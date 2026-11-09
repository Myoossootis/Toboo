"""Explicit UNO/LCD hardware protocol check. Visible glyphs still need a person's observation."""
import argparse
import json
from pathlib import Path
import sys
import time
import serial

sys.path.insert(0, str(Path(__file__).parents[1] / "tools"))
from lcd_expression import read_line


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()
    report = {"source": "actual-uno-usb-protocol", "port": args.port,
              "actuatorOutput": "none", "opticalDisplayVerified": False, "events": []}
    with serial.Serial(args.port, 115200, timeout=0.05, write_timeout=0.2) as port:
        time.sleep(2)
        port.reset_input_buffer()

        def exchange(command, expected):
            port.write((command + "\n").encode("ascii"))
            response = read_line(port, 4)
            report["events"].append({"request": command, "response": response})
            if response != expected:
                raise RuntimeError(f"Expected {expected}, received {response}")

        exchange("TOBOO_LCD?", "TOBOO_LCD,1,UNO,16,2")
        try:
            for label in ("angry", "disgust", "fearful", "happy", "neutral", "sad", "surprised"):
                exchange(f"E,{label},500", f"OK,{label}")
                exchange("LCD_STATUS?", f"LCD_STATE,{label}")
                time.sleep(0.25)
            exchange("E,happy,200", "OK,happy")
            exchange("E,sad,501", "ERR")
            time.sleep(0.3)
            exchange("LCD_STATUS?", "LCD_STATE,unknown")
            exchange("E,unknown,1", "OK,unknown")
            report["expiryVerified"] = True
            report["passed"] = True
        finally:
            port.write(b"E,unknown,1\n")
    path = Path(args.out)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print("PASS: real UNO/LCD handshake, seven ACKs, invalid TTL rejection and MCU expiry; no optical measurement.")


if __name__ == "__main__":
    main()
