"""Bounded request/ACK bridge for the dedicated UNO LCD firmware. No actuator commands."""
import argparse
import sys
import time
import serial

LABELS = frozenset(("angry", "disgust", "fearful", "happy", "neutral", "sad", "surprised", "unknown"))


def validate(line):
    fields = line.split(",")
    if (len(fields) != 3 or fields[0] != "E" or fields[1] not in LABELS
            or not fields[2].isascii() or not fields[2].isdigit() or not 1 <= int(fields[2]) <= 500):
        raise ValueError("Invalid LCD request")
    return fields[1]


def read_line(port, timeout):
    deadline = time.monotonic() + timeout
    data = bytearray()
    while time.monotonic() < deadline:
        data.extend(port.read(1))
        if len(data) > 80:
            raise RuntimeError("Oversized LCD response")
        if data.endswith(b"\n"):
            return data.decode("ascii").rstrip("\r\n")
    raise RuntimeError("LCD response timed out")


def run(port, lines, emit, wait=time.sleep):
    wait(2)  # UNO USB serial commonly resets on open.
    port.reset_input_buffer()
    port.write(b"TOBOO_LCD?\n")
    response = read_line(port, 4)
    if response != "TOBOO_LCD,1,UNO,16,2":
        raise RuntimeError("UNO LCD handshake failed: " + response)
    emit("READY")
    try:
        # One outstanding request: the desktop awaits each ACK before sampling again.
        for raw in lines:
            line = raw.rstrip("\r\n")
            label = validate(line)
            port.write((line + "\n").encode("ascii"))
            response = read_line(port, 0.6)
            if response != "OK," + label:
                raise RuntimeError("LCD acknowledgement failed: " + response)
            emit(response)
    finally:
        port.write(b"E,unknown,1\n")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", required=True)
    args = parser.parse_args()
    with serial.Serial(args.port, 115200, timeout=0.05, write_timeout=0.2) as port:
        run(port, sys.stdin, lambda line: print(line, flush=True))


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError, serial.SerialException) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
