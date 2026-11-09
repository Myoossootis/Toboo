"""Read only toboo UNO telemetry. No actuator commands. Stdout is a bounded raw-line stream."""
import argparse
import sys
import time
import serial


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", required=True)
    args = parser.parse_args()
    # Opening a UNO USB serial port may reset that board. The user must identify its port first.
    with serial.Serial(args.port, 115200, timeout=0.2, write_timeout=1) as port:
        time.sleep(2)
        port.reset_input_buffer()
        port.write(b"TOBOO?\n")
        deadline = time.monotonic() + 4
        ready = False
        last = time.monotonic()
        while True:
            raw = port.read_until(b"\n", size=100)
            if raw:
                if not raw.endswith(b"\n"):
                    raise RuntimeError("UNO line is oversized or incomplete; reconnect required")
                line = raw.decode("ascii").rstrip("\r\n")
                if not ready:
                    if line != "TOBOO_SENSOR,1,UNO":
                        if time.monotonic() > deadline:
                            raise RuntimeError("Not a toboo UNO acquisition device")
                        continue
                    ready = True
                print(line, flush=True)
                last = time.monotonic()
            if not ready and time.monotonic() > deadline:
                raise RuntimeError("UNO handshake timed out; verify board, firmware and port")
            if ready and time.monotonic() - last > 2:
                raise RuntimeError("UNO telemetry lost for two seconds")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError, serial.SerialException) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
