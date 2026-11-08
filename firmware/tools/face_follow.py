"""Explicit interactive face-follow transport. stdin/stdout are one JSON object per line.

Opening only inspects. Motion requires --execute AND a start message, with existing
command references. No initialization or home is performed by this program.
"""
import argparse
import json
import math
import queue
import sys
import threading
import time
from pathlib import Path
from single_servo import Link, read_pair


def validate_start(message, pair, tilt_limits=(35, 65)):
    bounds = message.get("bounds")
    reference = message.get("reference")
    axes = message.get("axes")
    if (message.get("type") != "start" or axes not in ("S1", "S2", "both") or
            not isinstance(bounds, list) or len(bounds) != 4 or any(type(v) is not int for v in bounds) or
            not 30 <= bounds[0] < bounds[1] <= 175 or not tilt_limits[0] <= bounds[2] < bounds[3] <= tilt_limits[1]):
        raise ValueError("Invalid follow start, axis selection or bounds")
    current = [state["last"] for state in pair]
    if reference != current or not bounds[0] <= current[0] <= bounds[1] or not bounds[2] <= current[1] <= bounds[3]:
        raise ValueError("Home/read the current command references inside the selected bounds first")
    return bounds, current, axes


def validate_frame(message, bounds, reference, axes):
    if message.get("type") != "frame" or type(message.get("permit")) is not bool:
        raise ValueError("Expected frame with explicit permit")
    age = message.get("age_ms")
    if type(age) not in (int, float) or not math.isfinite(age) or age < 0:
        raise ValueError("Invalid frame age")
    if not message["permit"] or age > 200:
        return None
    values = [message.get("pan"), message.get("tilt")]
    for axis, value in enumerate(values):
        if type(value) not in (int, float) or not math.isfinite(value):
            raise ValueError("Follow target must be finite and numeric")
    if axes == "S1": values[1] = reference[1]
    if axes == "S2": values[0] = reference[0]
    values = [max(bounds[axis * 2], min(bounds[axis * 2 + 1], value)) for axis, value in enumerate(values)]
    return [int(math.floor(value * 100 + 0.5)) for value in values]


def follow(link, inbox, emit):
    if link.version not in {"0.5.0", "0.5.1", "0.5.2", "0.5.3"}:
        raise RuntimeError("Face follow needs firmware 0.5.0; existing firmware was not modified")
    initial = read_pair(link)
    if initial[0]["state"] != "DISARMED":
        raise RuntimeError("Read/reset the ESP32 before follow; another session is active")
    emit({"type": "ready", "firmware": link.version, "reference": [s["last"] for s in initial]})
    message, received = inbox.get(timeout=30)
    if time.monotonic() - received > 0.2:
        raise TimeoutError("Start request expired")
    if message is None or message.get("type") == "stop": return
    bounds, reference, axes = validate_start(message, initial, (0, 180) if link.version == "0.5.3" else (35, 65))
    session, sequence = initial[0]["session"], initial[0]["sequence"]

    def request(verb, values=()):
        nonlocal sequence
        sequence += 1
        text = f"{verb},{session},{sequence}" + "".join(f",{v}" for v in values)
        link.command(text, f"ACK,{sequence},{verb},REQUEST_ONLY")

    def report(held=False, limited=()):
        pair = read_pair(link)
        if any(s["session"] != session or s["state"] != "ARMED_FOLLOW" or s["sequence"] != sequence for s in pair):
            raise RuntimeError("Follow session interrupted by reset, timeout or another controller")
        emit({"type": "state", "reference": [s["last"] for s in pair], "uart_frames": pair[-1]["frames"], "held": held,
              "limited": list(limited)})

    armed = False
    try:
        # Only a valid explicit start may enable UART. ARM itself sends no angle.
        armed = True  # Also disarm if the ARM acknowledgement is lost.
        request("ARM_FOLLOW", bounds)
        report()
        while True:
            try: message, received = inbox.get(timeout=0.25)
            except queue.Empty: raise TimeoutError("No fresh host frame for 250 ms")
            if message is None or message.get("type") == "stop": break
            if time.monotonic() - received > 0.15:
                request("HOLD_BOTH")
                raise TimeoutError("Queued host frame expired")
            target = validate_frame(message, bounds, reference, axes)
            limited = []
            if target is not None:
                for index, (axis, key) in enumerate((("S1", "pan"), ("S2", "tilt"))):
                    if axes not in (axis, "both"): continue
                    if message[key] < bounds[index * 2]: limited.append(axis + ":lower")
                    elif message[key] > bounds[index * 2 + 1]: limited.append(axis + ":upper")
            request("HOLD_BOTH" if target is None else "FOLLOW_BOTH", () if target is None else target)
            report(held=target is None, limited=limited)
    finally:
        if armed: link.disarm()
    emit({"type": "stopped", "shaft_position": "not measured", "power_cutoff": False})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", required=True)
    parser.add_argument("--log", type=Path, required=True)
    parser.add_argument("--execute", action="store_true")
    args = parser.parse_args()
    if not args.execute: parser.error("Interactive follow requires explicit --execute")
    args.log.parent.mkdir(parents=True, exist_ok=True)
    inbox = queue.Queue(maxsize=1)

    def read_input():
        try:
            for line in sys.stdin:
                message = json.loads(line)
                if not isinstance(message, dict): break
                # Bounded inbox: backpressure; never accumulate an old trajectory.
                inbox.put((message, time.monotonic()), timeout=0.3)
        except (ValueError, queue.Full): pass
        finally:
            try: inbox.put((None, time.monotonic()), timeout=0.3)
            except queue.Full: pass

    threading.Thread(target=read_input, daemon=True).start()
    link = None
    try:
        with args.log.open("w", encoding="utf-8") as log:
            try:
                link = Link(args.port, log)
                link.hello()
                follow(link, inbox, lambda value: print(json.dumps(value), flush=True))
            finally:
                if link: link.io.close()
    except (OSError, RuntimeError, ValueError, queue.Empty) as error:
        parser.exit(1, f"Follow stopped: {error}\n")


if __name__ == "__main__": main()
