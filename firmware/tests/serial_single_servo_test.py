"""Hardware test: disconnect driver and servos; this test emits real UART1 bytes."""
import argparse
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from single_servo import Link  # noqa: E402


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def run(port, log):
    link = Link(port, log)
    identified = False
    try:
        link.hello()
        identified = True
        info = link.command("INFO", "INFO,ESP32-S3,")
        require("FLASH=16777216" in info and "PSRAM=8388608" in info, info)
        initial = link.status()
        require(initial["state"] == "DISARMED" and initial["last"] == -1 and initial["frames"] == 0,
                f"Not a fresh safe boot: {initial}")
        link.request("CENTER_S1", initial, 1, expected="ERR,STATE,")
        link.command("ARM_BENCH,1", "ERR,FORMAT,")
        link.command("$B090#", "ERR,FORMAT,")
        link.request("ARM_S1", initial, 1)
        require(link.status()["frames"] == 0, "ARM emitted a movement command")
        link.request("MOVE_S1", initial, 2, 90, "ERR,REFERENCE,")
        link.request("CENTER_S1", initial, 2)
        centered = link.status()
        require(centered["last"] == 90 and centered["frames"] == 1, "Explicit center failed")
        link.request("CENTER_S1", initial, 3, expected="ERR,REFERENCE,")
        low, high = link.angle_limits
        for value, error in ((low - 1, "RANGE"), (high + 1, "RANGE"), ("-90", "NUMBER"),
                             ("90.0", "NUMBER"), ("", "NUMBER"), ("4294967296", "NUMBER")):
            link.request("MOVE_S1", initial, 3, value, f"ERR,{error},")
        require(link.status()["frames"] == 1, "Rejected input emitted a UART frame")
        seq = 3
        link.request("MOVE_S1", initial, seq, 92)
        started = time.monotonic()
        reached = None
        while time.monotonic() - started < 1:
            seq += 1
            link.request("KEEP_S1", initial, seq)
            current = link.status()
            if current["last"] == 92:
                reached = current
                break
            time.sleep(0.06)
        require(reached and reached["frames"] == 3, "Expected only 90/91/92 UART frames")
        # Long enough target for the watchdog to cancel before it can finish.
        seq += 1
        link.request("MOVE_S1", initial, seq, 88)
        started = time.monotonic()
        link.request("MOVE_S1", initial, seq, 90, "ERR,SEQUENCE,")
        link.request("ARM_S1", initial, seq + 1, expected="ERR,STATE,")
        while time.monotonic() - started < 0.62:
            link.command("PING", "PONG,")
            time.sleep(0.04)
        stopped = link.status()
        require(stopped["state"] == "TIMED_OUT" and stopped["last"] > 88 and
                stopped["target"] == stopped["last"], f"Watchdog did not cancel target: {stopped}")
        require(stopped["session"] != initial["session"], "Timeout did not invalidate old session")
        time.sleep(0.12)
        require(link.status() == stopped, "New UART frames after timeout")
        link.request("ARM_S1", initial, seq + 1, expected="ERR,SESSION,")
        link.request("MOVE_S1", stopped, seq + 1, 90, "ERR,STATE,")
        link.disarm()

        for invalid in (b"X" * 130 + b"\n", b"HE\x00LLO\n"):
            link.write(invalid)
            link.response("ERR,FRAMING,")
        link.write(b"ARM_S1,")
        time.sleep(0.22)
        link.write(b"incomplete\n")
        link.response("ERR,FRAMING,")
        link.write(b"STA")
        time.sleep(0.02)
        link.write(b"TUS\r\n")
        link.response("STATUS,DISARMED,")
        before_reconnect = link.status()
        seq = before_reconnect["sequence"] + 1
        link.request("ARM_S1", before_reconnect, seq)
        require(link.status()["frames"] == before_reconnect["frames"], "Re-arm emitted a command")
        link.io.close()
        time.sleep(0.75)
        link.open()
        link.hello()
        reconnected = link.status()
        require(reconnected["state"] == "TIMED_OUT" and
                reconnected["frames"] == before_reconnect["frames"], "Reconnect auto-resumed output")
        link.request("KEEP_S1", before_reconnect, seq + 1, expected="ERR,SESSION,")
        link.disarm()

        before_reset = link.status()
        link.command("REBOOT_SAFE," + before_reset["session"], "ACK,REBOOT_SAFE,TX_DISABLED")
        link.io.close()
        time.sleep(0.9)
        link.open()
        link.hello()
        reset = link.status()
        require(reset["state"] == "DISARMED" and reset["last"] == -1 and reset["frames"] == 0,
                f"Reset restored old command: {reset}")
        require(reset["session"].split("-")[0] != initial["session"].split("-")[0],
                "Boot session was reused")
        link.request("ARM_S1", before_reset, 1, expected="ERR,SESSION,")
        link.disarm()
        return {"result": "PASS", "port": port, "firmware": f"{link.identity} {link.version}",
                "conditions": "driver, servos and external power disconnected",
                "uart_frames_before_reset": before_reset["frames"],
                "verified": ["startup and ARM emit no motion frame", "explicit center, S1-only bounded commands",
                             "range/overflow/replay/framing rejection", "watchdog cancels target despite PING",
                             "timeout and DISARM invalidate session", "serial close/reopen cannot resume",
                             "software reboot clears reference and changes boot session"],
                "not_verified": ["external GPIO waveform", "driver reception", "servo motion", "power-cycle behavior"],
                "final_status": link.status(), "info": info}
    finally:
        if link.io.is_open:
            if identified:
                try:
                    link.disarm()
                except (OSError, RuntimeError, TimeoutError):
                    pass
            link.io.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", required=True)
    parser.add_argument("--outputs-disconnected", action="store_true", required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    with (args.out / "serial.jsonl").open("w", encoding="utf-8") as log:
        result = run(args.port, log)
    (args.out / "result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result, indent=2))
