"""Verify ONLY the OUTPUT_DISABLED bench firmware over a real USB serial port."""
import argparse
import json
import time
from pathlib import Path

import serial


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


class BenchPort:
    def __init__(self, port, log):
        self.port_name = port
        self.log = log
        self.buffer = bytearray()
        self.open()

    def open(self):
        self.io = serial.Serial(baudrate=115200, timeout=0.02, write_timeout=1)
        self.io.dtr = False
        self.io.rts = False
        self.io.port = self.port_name
        self.io.open()
        self.buffer.clear()

    def record(self, direction, data):
        self.log.write(json.dumps({"monotonic": time.monotonic(), direction: data}) + "\n")
        self.log.flush()

    def write(self, data):
        self.record("tx", repr(data))
        self.io.write(data)

    def response(self, prefix, timeout=2):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            self.buffer.extend(self.io.read(max(1, self.io.in_waiting)))
            while b"\n" in self.buffer:
                raw, _, rest = self.buffer.partition(b"\n")
                self.buffer = bytearray(rest)
                line = raw.decode("ascii", errors="replace").strip()
                self.record("rx", line)
                if line.startswith(prefix):
                    return line
                if line.startswith("ERR,") and not prefix.startswith("ERR,"):
                    raise RuntimeError(f"Unexpected device rejection: {line}")
        raise RuntimeError(f"Timed out waiting for {prefix}")

    def command(self, text, prefix):
        self.write(text.encode("ascii") + b"\n")
        return self.response(prefix)

    def status(self):
        fields = self.command("STATUS", "STATUS,").split(",")
        require(fields[-1] == "OUTPUT_DISABLED", "Unexpected actuator firmware")
        return {"state": fields[1], "pan": int(fields[2]), "tilt": int(fields[3]),
                "target_pan": int(fields[4]), "target_tilt": int(fields[5]),
                "sequence": int(fields[6])}


def run(port, log):
    link = BenchPort(port, log)
    passed = []
    try:
        # Never send ARM or SET until the firmware has identified itself.
        link.command("HELLO", "HELLO,UAVTRACK_BENCH,0.1.0,OUTPUT_DISABLED")
        info = link.command("INFO", "INFO,ESP32-S3,")
        require(all(s in info for s in ("FLASH=16777216", "PSRAM=8388608",
                    "PSRAM_SAMPLE=PASS", "OUTPUT_DISABLED")), info)
        passed.append("real ESP32-S3 identity, 16 MB flash, 8 MB PSRAM, 64 KiB memory sample")
        require(link.status()["state"] == "DISARMED", "Startup must be DISARMED")
        link.command("SET,1,900,900", "ERR,STATE,")
        passed.append("startup disarmed; SET rejected")

        link.write(b"STA")
        time.sleep(0.03)
        link.write(b"TUS\r\n")
        link.response("STATUS,DISARMED,")
        for bad in (b"A" * 130 + b"\n", b"HE\x00LLO\n"):
            link.write(bad)
            link.response("ERR,FRAMING,")
        link.write(b"ARM_BENCH,")
        time.sleep(0.25)
        link.write(b"1\n")
        link.response("ERR,FRAMING,")
        require(link.status()["state"] == "DISARMED", "Stale fragment enabled state")
        passed.append("fragmentation, CRLF, oversized/non-ASCII/stale lines, parser recovery")

        link.command("ARM_BENCH,1", "ACK,1,ARM_BENCH,")
        for command, reason in (("SET,2,849,900", "RANGE"),
                                ("SET,2,900,951", "RANGE"),
                                ("SET,2,,900", "NUMBER"),
                                ("SET,4294967296,900,900", "NUMBER")):
            link.command(command, f"ERR,{reason},")
        require(link.status()["sequence"] == 1, "Rejected command advanced sequence")
        link.command("SET,2,950,850", "ACK,2,SET,")
        started = time.monotonic()
        time.sleep(0.12)
        moving = link.status()
        elapsed = time.monotonic() - started
        require(900 < moving["pan"] <= 900 + 50 * elapsed + 2, f"Slew failure: {moving}")
        require(moving["tilt"] < 900, "Tilt simulation did not move")
        link.command("SET,2,900,900", "ERR,SEQUENCE,")
        link.command("ARM_BENCH,3", "ERR,STATE,")
        # PING remains continuous: timeout must depend on accepted motion requests.
        while time.monotonic() - started < 0.62:
            link.command("PING", "PONG,")
            time.sleep(0.04)
        stopped = link.status()
        require(stopped["state"] == "TIMED_OUT", f"Watchdog failure: {stopped}")
        require(stopped["target_pan"] == stopped["pan"] and
                stopped["target_tilt"] == stopped["tilt"], "Old target not cancelled")
        time.sleep(0.1)
        require(link.status() == stopped, "Virtual position moved after timeout")
        link.command("SET,3,950,950", "ERR,STATE,")
        passed.append("range/overflow rejection, sequence replay, virtual slew, PING cannot feed watchdog")

        link.command("ARM_BENCH,3", "ACK,3,ARM_BENCH,")
        require(link.status()["target_pan"] == stopped["pan"], "Re-arm restored old target")
        link.command("SET,4,850,950", "ACK,4,SET,")
        link.command("DISARM", "ACK,DISARM,")
        disarmed = link.status()
        require(disarmed["state"] == "DISARMED" and
                disarmed["target_pan"] == disarmed["pan"], "Disarm did not hold")
        passed.append("explicit re-arm, old target discarded, DISARM holds")

        link.command("ARM_BENCH,5", "ACK,5,ARM_BENCH,")
        link.command("SET,6,950,950", "ACK,6,SET,")
        link.io.close()
        time.sleep(0.8)  # Serial session disconnection, not a USB power-removal test.
        link.open()
        link.command("HELLO", "HELLO,UAVTRACK_BENCH,0.1.0,OUTPUT_DISABLED")
        require(link.status()["state"] == "TIMED_OUT", "Port reconnect auto-armed/reset state")
        link.command("SET,7,950,950", "ERR,STATE,")
        link.command("DISARM", "ACK,DISARM,")
        passed.append("serial port close/reopen causes timeout; reconnect cannot resume")
        return {"result": "PASS", "port": port, "physicalActuatorOutput": "disabled",
                "angles": "virtual commands only", "checks": passed, "info": info}
    finally:
        if link.io.is_open:
            link.io.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    with (args.out / "serial.jsonl").open("w", encoding="utf-8") as log:
        result = run(args.port, log)
    (args.out / "result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result, indent=2))
