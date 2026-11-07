"""Manual bench-servo requests over USB; defaults to read-only status."""
import argparse
import json
import time
from pathlib import Path

import serial

FIRMWARE_LIMITS = {
    ("UAVTRACK_SINGLE_S1", "0.2.0"): {"S1": (88, 92)},
    ("UAVTRACK_SINGLE_S1", "0.2.1"): {"S1": (80, 100)},
    ("UAVTRACK_PANTILT", "0.3.0"): {"S1": (80, 100), "S2": (88, 92)},
    ("UAVTRACK_PANTILT", "0.3.1"): {"S1": (80, 100), "S2": (45, 90)},
    ("UAVTRACK_PANTILT", "0.3.2"): {"S1": (45, 100), "S2": (45, 90)},
    ("UAVTRACK_PANTILT", "0.3.3"): {"S1": (0, 180), "S2": (40, 65)},
    ("UAVTRACK_PANTILT", "0.3.4"): {"S1": (30, 175), "S2": (35, 65)},
    ("UAVTRACK_PANTILT", "0.4.0"): {"S1": (30, 175), "S2": (35, 65)},
}
CALIBRATED_VERSIONS = {"0.3.1", "0.3.2", "0.3.3", "0.3.4", "0.4.0"}
PROTECTED_VERSIONS = {"0.3.3", "0.3.4", "0.4.0"}
FIRST_TRIAL_ANGLE = {"S1": 80, "S2": 88}


def first_angle(link, axis):
    return 45 if axis == "S2" and link.version in PROTECTED_VERSIONS else FIRST_TRIAL_ANGLE[axis]


class Link:
    def __init__(self, port, log=None, axis="S1"):
        self.port_name, self.log = port, log
        self.axis = axis
        self.buffer = bytearray()
        self.open()

    def open(self):
        self.io = serial.Serial(baudrate=115200, timeout=0.02, write_timeout=1)
        self.io.dtr = False
        self.io.rts = False
        self.io.port = self.port_name
        self.io.open()
        self.buffer.clear()

    def record(self, kind, text):
        if self.log:
            self.log.write(json.dumps({"monotonic": time.monotonic(), kind: text}) + "\n")
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
                    raise RuntimeError(line)
        raise TimeoutError(f"No response matching {prefix}")

    def command(self, line, expected):
        self.write(line.encode("ascii") + b"\n")
        return self.response(expected)

    def hello(self):
        for attempt in range(3):
            try:
                response = self.command("HELLO", "HELLO,")
                fields = response.split(",")
                limits = FIRMWARE_LIMITS.get(tuple(fields[1:3]))
                if fields[3:] != ["UART_CAPABLE", "DEFAULT_DISARMED"] or limits is None:
                    raise RuntimeError(f"Unexpected servo firmware: {response}")
                if self.axis not in limits:
                    raise RuntimeError(f"Firmware {fields[2]} does not support {self.axis}")
                self.identity = fields[1]
                self.version = fields[2]
                self.limits = limits
                return response
            except TimeoutError:
                if attempt == 2:
                    raise

    @property
    def angle_limits(self):
        return self.limits[self.axis]

    def status(self):
        command = "STATUS" if self.axis == "S1" else "STATUS_S2"
        f = self.command(command, "STATUS,").split(",")
        if len(f) != 8 or f[7] != f"{self.axis}_ONLY":
            raise RuntimeError(f"Unexpected firmware response: {f}")
        return dict(state=f[1], session=f[2], sequence=int(f[3]), last=int(f[4]),
                    target=int(f[5]), frames=int(f[6]))

    def request(self, verb, status, seq, angle=None, expected="ACK,"):
        line = f"{verb},{status['session']},{seq}"
        if angle is not None:
            line += f",{angle}"
        return self.command(line, expected)

    def disarm(self):
        return self.command("DISARM", "ACK,DISARM,TX_DISABLED")


def once(link, action, angle, *, status=None):
    if status is None:
        status = link.status()
    if action == "status":
        return status
    if action == "disarm":
        link.disarm()
        return link.status()
    if status["state"].startswith("ARMED_"):
        raise RuntimeError("Already armed; explicitly disarm the other session first.")
    if action == "center" and status["last"] != -1:
        raise RuntimeError("Already has a command reference. Use move --angle 90, or reset after driver power cycling.")
    if action == "move" and status["last"] == -1:
        raise RuntimeError("No reference. An explicit first center request is required.")
    desired = (90 if angle is None else angle) if action == "center" else angle
    if action == "center" and angle is not None and link.version not in CALIBRATED_VERSIONS:
        raise RuntimeError("An explicit first angle requires firmware 0.3.1 or 0.3.2.")
    if action in ("center", "move"):
        low, high = link.angle_limits
        if action == "center" and link.version in CALIBRATED_VERSIONS:
            low = first_angle(link, link.axis)
            if link.version in PROTECTED_VERSIONS and link.axis == "S2":
                high = 45
        if desired is None or not low <= desired <= high:
            raise ValueError(f"{link.axis} firmware {link.version} accepts targets from {low} to {high} degrees")
    if action == "move":
        # At most one degree every 200 ms, plus one second for USB and polling overhead.
        time_budget = abs(desired - status["last"]) * 0.2 + 1.0
    else:
        time_budget = 1.5
    seq = status["sequence"] + 1
    if link.version in PROTECTED_VERSIONS and link.axis == "S2" and status["last"] > 65:
        raise RuntimeError("S2 is above the permitted region; inspect the physical position before restarting.")
    try:
        link.request(f"ARM_{link.axis}", status, seq)
        seq += 1
        link.request(f"CENTER_{link.axis}" if action == "center" else f"MOVE_{link.axis}", status, seq,
                     angle)
        deadline = time.monotonic() + time_budget
        while time.monotonic() < deadline:
            current = link.status()
            if current["state"] != f"ARMED_{link.axis}":
                raise RuntimeError(f"Request interrupted: {current}")
            if current["last"] == desired and current["target"] == desired:
                return current
            seq += 1
            link.request(f"KEEP_{link.axis}", status, seq)
            time.sleep(0.08)
        raise TimeoutError(f"UART request did not complete within {time_budget:.1f} seconds")
    finally:
        link.disarm()  # Stops future commands, does NOT switch off servo power/PWM.


def inspect_axes(link):
    states = {}
    for axis in link.limits:
        link.axis = axis
        states[axis] = link.status()
    if len({status["session"] for status in states.values()}) != 1:
        raise RuntimeError("ESP32 session changed while reading axis status.")
    status = next(iter(states.values()))
    return {"firmware": link.version, "final_state": status["state"], "uart_frames_total": status["frames"],
            "axes": {axis: {"last_requested": status["last"]} for axis, status in states.items()},
            "shaft_position": "not measured", "power_cutoff": False}


def home(link, pan=None, tilt=None):
    if link.version == "0.4.0":
        return coordinated(link, [(90 if pan is None else pan, 45 if tilt is None else tilt)])
    axes = ("S1", "S2")
    if not all(axis in link.limits for axis in axes):
        raise RuntimeError("Home requires dual-axis firmware on S1 and S2.")
    adjustable = link.version in CALIBRATED_VERSIONS
    if (pan is not None or tilt is not None) and not adjustable:
        raise RuntimeError("Calibrated home requires firmware 0.3.1.")
    goals = {"S1": 90 if pan is None else pan, "S2": (first_angle(link, "S2") if adjustable else 90) if tilt is None else tilt}
    for axis, goal in goals.items():
        low, high = link.limits[axis]
        if not low <= goal <= high:
            raise ValueError(f"{axis} home target must be within {low}–{high} degrees.")
    results = {}
    boot = None
    for axis in axes:
        link.axis = axis
        status = link.status()
        current_boot = status["session"].split("-")[0]
        if status["state"] != "DISARMED" or (boot is not None and current_boot != boot):
            raise RuntimeError(f"Home interrupted before {axis}: {status}")
        boot = current_boot
        goal = goals[axis]
        initial_frames = status["frames"]
        if status["last"] == -1:
            first = 45 if axis == "S2" and link.version in PROTECTED_VERSIONS else max(first_angle(link, axis), goal)
            once(link, "center", first if adjustable else None, status=status)
            status = link.status()
            if (status["state"], status["last"], status["session"].split("-")[0]) != ("DISARMED", first, boot):
                raise RuntimeError(f"Home interrupted after initializing {axis}: {status}")
            if first != goal:
                time.sleep(0.5)
        if status["last"] != goal:
            once(link, "move", goal, status=status)
        final = link.status()
        if (final["state"], final["last"], final["target"], final["session"].split("-")[0]) != (
                "DISARMED", goal, goal, boot):
            raise RuntimeError(f"Home did not complete for {axis}: {final}")
        results[axis] = {"last_requested": final["last"], "uart_frames_sent": final["frames"] - initial_frames}
        if axis == "S1" and results[axis]["uart_frames_sent"]:
            time.sleep(1.0)
    return {"firmware": link.version, "axes": results, "final_state": "DISARMED", "shaft_position": "not measured", "power_cutoff": False}


def nudge(link, delta, floor):
    if link.version not in CALIBRATED_VERSIONS:
        raise RuntimeError("Micro-adjustment requires firmware 0.3.1 or 0.3.2.")
    status = link.status()
    if status["state"] != "DISARMED" or status["last"] == -1:
        raise RuntimeError("Run home first to establish both command references.")
    low, high = link.angle_limits
    low = max(low, floor) if link.axis == "S2" else low
    goal = status["last"] + delta
    if not low <= goal <= high:
        raise ValueError(f"{link.axis} target {goal} is outside the current {low}–{high} degree range.")
    axis = link.axis
    sent = once(link, "move", goal, status=status)
    expected_frames = (status["frames"] + 1) & 0xFFFFFFFF
    if sent["session"] != status["session"] or sent["frames"] != expected_frames:
        raise RuntimeError("ESP32 did not confirm exactly one UART frame for this step.")
    report = inspect_axes(link)
    if (report["final_state"] != "DISARMED" or report["uart_frames_total"] != expected_frames
            or report["axes"][axis]["last_requested"] != goal):
        raise RuntimeError("ESP32 state changed before the step could be confirmed.")
    report["nudge"] = {"axis": axis, "from": status["last"], "to": goal, "uart_frames_sent": 1}
    return report


def sweep(link):
    axis = link.axis
    start, end = (40, 65) if axis == "S2" and link.version in PROTECTED_VERSIONS else (45, 90)
    if link.version not in CALIBRATED_VERSIONS or link.angle_limits[0] > 45:
        raise RuntimeError(f"The {axis} 45–90 degree sweep requires newer firmware.")
    status = link.status()
    if status["state"] != "DISARMED":
        raise RuntimeError("Read the ESP32 status and reset it before starting a sweep.")
    boot = status["session"].split("-")[0]
    if status["last"] == -1:
        first = first_angle(link, axis)
        once(link, "center", first, status=status)
        status = link.status()
        if (status["state"], status["last"], status["session"].split("-")[0]) != ("DISARMED", first, boot):
            raise RuntimeError(f"Sweep interrupted while initializing {axis}.")
        time.sleep(0.5)
    if status["last"] != start:
        once(link, "move", start, status=status)
    status = link.status()
    if (status["state"], status["last"], status["session"].split("-")[0]) != ("DISARMED", start, boot):
        raise RuntimeError(f"Sweep did not establish the 45-degree starting request for {axis}.")
    time.sleep(0.5)
    sent = once(link, "move", end, status=status)
    if (sent["session"], sent["frames"]) != (status["session"], (status["frames"] + end - start) & 0xFFFFFFFF):
        raise RuntimeError(f"ESP32 did not confirm all 45 sweep steps for {axis}.")
    report = inspect_axes(link)
    if (report["final_state"] != "DISARMED" or report["uart_frames_total"] != sent["frames"]
            or report["axes"][axis]["last_requested"] != end):
        raise RuntimeError(f"ESP32 state changed before completion of the {axis} sweep.")
    report["sweep"] = {"axis": axis, "from": start, "to": end, "uart_frames_sent": end - start}
    return report


def read_pair(link):
    states = []
    for axis in ("S1", "S2"):
        link.axis = axis
        states.append(link.status())
    if any(states[0][key] != states[1][key] for key in ("session", "sequence", "state")):
        raise RuntimeError("ESP32 changed session or state while reading both axes.")
    return states


def coordinated(link, points):
    if link.version != "0.4.0":
        raise RuntimeError("Coordinated motion requires firmware 0.4.0.")
    if not points:
        raise ValueError("A motion path must include a center point.")
    for point in points:
        for axis, angle in zip(("S1", "S2"), point):
            low, high = link.limits[axis]
            if type(angle) is not int or not low <= angle <= high:
                raise ValueError(f"{axis} path point is outside {low}–{high} degrees.")
    initial = read_pair(link)
    if initial[0]["state"] != "DISARMED":
        raise RuntimeError("Stop the active controller before coordinated motion.")
    unknown = [state["last"] == -1 for state in initial]
    if any(unknown) and not all(unknown):
        raise RuntimeError("Both references are required. Reset ESP32 after inspecting the physical position.")
    session = initial[0]["session"]
    sequence = initial[0]["sequence"]

    def request(verb, point=None):
        nonlocal sequence
        sequence += 1
        line = f"{verb},{session},{sequence}"
        if point is not None:
            line += f",{point[0]},{point[1]}"
        link.command(line, f"ACK,{sequence},{verb},REQUEST_ONLY")

    def states():
        pair = read_pair(link)
        if pair[0]["state"] != "ARMED_BOTH" or pair[0]["session"] != session:
            raise RuntimeError("Coordinated motion was interrupted by a stop, timeout or reset.")
        return pair

    completed = []
    try:
        request("ARM_BOTH")
        if all(unknown):
            request("INIT_BOTH", points[0])
            pair = states()
            if tuple(state["last"] for state in pair) != points[0]:
                raise RuntimeError("Initial center request was not confirmed.")
            # Allow initial physical settling while continuing the independent watchdog.
            for _ in range(10):
                request("KEEP_BOTH")
                time.sleep(0.05)
        for index, point in enumerate(points):
            pair = states()
            distance = max(abs(state["last"] - goal) for state, goal in zip(pair, point))
            request("MOVE_BOTH", point)
            deadline = time.monotonic() + max(0.4, distance * 1.875 / 30) + 1.5
            while True:
                pair = states()
                if (all(state["last"] == goal and state["target"] == goal for state, goal in zip(pair, point))
                        and link.command("MOTION", "MOTION,") == "MOTION,IDLE"):
                    break
                if time.monotonic() >= deadline:
                    raise TimeoutError("Coordinated segment did not complete in time.")
                request("KEEP_BOTH")
                time.sleep(0.04)
            completed.append(list(point))
            link.record("waypoint", {"index": index, "pan": point[0], "tilt": point[1]})
            request("KEEP_BOTH")
    finally:
        link.disarm()
    final_pair = read_pair(link)
    if final_pair[0]["session"].split("-")[0] != session.split("-")[0]:
        raise RuntimeError("ESP32 restarted before completion could be confirmed.")
    report = inspect_axes(link)
    if report["final_state"] != "DISARMED" or any(
            report["axes"][axis]["last_requested"] != goal for axis, goal in zip(("S1", "S2"), points[-1])):
        raise RuntimeError("Final center request could not be confirmed.")
    report["path"] = completed
    report["initialized_from_unknown"] = all(unknown)
    return report


def rectangle_path(center, left, right, bottom, top):
    if not 30 <= left < right <= 175 or not 35 <= bottom < top <= 65:
        raise ValueError("Rectangle bounds must lie inside S1 30–175 and S2 35–65.")
    if not left <= center[0] <= right or not bottom <= center[1] <= top:
        raise ValueError("The fixed center must lie inside the configured rectangle.")
    upper_left, lower_left = (left, top), (left, bottom)
    lower_right, upper_right = (right, bottom), (right, top)
    return [center, upper_left, lower_left, lower_right, upper_right, upper_left,
            lower_right, upper_right, lower_left, upper_left, center]


def rectangle_center(left, right, bottom, top):
    return ((left + right + 1) // 2, (bottom + top + 1) // 2)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", required=True)
    parser.add_argument("--axis", choices=("S1", "S2"), help="Defaults to S1 for single-axis actions")
    parser.add_argument("--action", choices=("status", "center", "move", "disarm", "home", "inspect", "nudge", "sweep", "pattern"), default="status")
    parser.add_argument("--angle", type=int, metavar="DEGREES")
    parser.add_argument("--pan-home", type=int)
    parser.add_argument("--tilt-home", type=int)
    parser.add_argument("--left", type=int, default=30)
    parser.add_argument("--right", type=int, default=175)
    parser.add_argument("--bottom", type=int, default=35)
    parser.add_argument("--top", type=int, default=65)
    parser.add_argument("--delta", type=int, choices=(-1, 1))
    parser.add_argument("--tilt-floor", type=int, default=35)
    parser.add_argument("--execute", action="store_true", help="Required for any motion request")
    parser.add_argument("--log", type=Path)
    args = parser.parse_args()
    if args.action in ("center", "move", "home", "nudge", "sweep", "pattern") and not args.execute:
        parser.error("Motion requires --execute; check mechanical clearance and power wiring first")
    if args.action == "sweep" and args.axis is None:
        parser.error("Sweep requires an explicit --axis S1 or S2")
    if args.action == "home" and (args.axis is not None or args.angle is not None):
        parser.error("Home uses --pan-home and --tilt-home; omit --axis and --angle")
    if args.action == "nudge" and args.delta is None:
        parser.error("Nudge requires --delta -1 or 1")
    if args.action == "move" and args.angle is None:
        parser.error("move requires --angle")
    if args.log:
        args.log.parent.mkdir(parents=True, exist_ok=True)
    log = args.log.open("w", encoding="utf-8") if args.log else None
    link = None
    try:
        link = Link(args.port, log, args.axis or "S1")
        link.hello()
        if args.action == "home":
            if link.version == "0.4.0":
                center = rectangle_center(args.left, args.right, args.bottom, args.top)
                rectangle_path(center, args.left, args.right, args.bottom, args.top)
                report = coordinated(link, [center])
            else:
                report = home(link, args.pan_home, args.tilt_home)
        elif args.action == "pattern":
            points = rectangle_path(rectangle_center(args.left, args.right, args.bottom, args.top),
                                    args.left, args.right, args.bottom, args.top)
            report = coordinated(link, points)
        elif args.action == "inspect":
            report = inspect_axes(link)
        elif args.action == "nudge":
            report = nudge(link, args.delta, args.tilt_floor)
        elif args.action == "sweep":
            report = sweep(link)
        else:
            report = {"request_result": once(link, args.action, args.angle), "final_status": link.status(),
                      "shaft_position": "not measured", "power_cutoff": False}
        print(json.dumps(report, indent=2))
    except (OSError, RuntimeError, ValueError) as error:
        parser.exit(1, f"Error: {error}\n")
    finally:
        if link:
            link.io.close()
        if log:
            log.close()
