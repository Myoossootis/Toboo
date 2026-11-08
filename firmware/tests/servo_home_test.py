"""Exercise the host home sequence without opening a serial port."""
import sys
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from single_servo import FIRMWARE_LIMITS, home, nudge, sweep, coordinated, rectangle_center, rectangle_path, once, reference  # noqa: E402


class FakeLink:
    version = "0.3.1"
    limits = FIRMWARE_LIMITS[("UAVTRACK_PANTILT", version)]

    def __init__(self, pan, tilt):
        self.axis = "S1"
        self.positions = {"S1": pan, "S2": tilt, "S3": -1}
        self.frames = 0
        self.state = "DISARMED"
        self.boot = "boot"
        self.commands = []

    def status(self):
        last = self.positions[self.axis]
        return {"state": self.state, "session": f"{self.boot}-1", "last": last, "target": last,
                "frames": self.frames}

    @property
    def angle_limits(self):
        return self.limits[self.axis]

    def request_home(self, link, action, angle, *, status):
        self.commands.append((link.axis, action, angle))
        self.frames += 1 if action == "center" else abs(status["last"] - angle)
        self.positions[link.axis] = angle
        return self.status()


class ServoHomeTest(unittest.TestCase):
    def test_new_rectangle_and_both_diagonals_use_verified_50_to_150_window(self):
        link = PairLink(-1,-1)
        link.version="0.5.3"; link.limits=FIRMWARE_LIMITS[("UAVTRACK_PANTILT",link.version)]
        path=rectangle_path(rectangle_center(30,175,50,150),30,175,50,150,tilt_limits=(0,180))
        self.assertEqual(path,[(103,100),(30,150),(30,50),(175,50),(175,150),(30,150),
                              (175,50),(175,150),(30,50),(30,150),(103,100)])
        with patch("single_servo.time.sleep"):
            report=coordinated(link,path)
        self.assertEqual(report["path"],[list(point) for point in path])
        self.assertEqual(report["axes"]["S2"]["last_requested"],100)
        self.assertEqual(report["axes"]["S3"]["last_requested"],-1)

    def test_missing_s2_reference_never_initializes_at_45_outside_new_window(self):
        link=PairLink(103,-1)
        link.version="0.5.3"; link.limits=FIRMWARE_LIMITS[("UAVTRACK_PANTILT",link.version)]
        with patch("single_servo.once") as output:
            with self.assertRaisesRegex(RuntimeError,"Reset ESP32"):
                coordinated(link,[(103,100)])
        output.assert_not_called()
        self.assertEqual(link.commands,[])

    def test_calibration_cannot_jump_beyond_observed_interval(self):
        link = Mock(version="0.5.3", axis="S2", angle_limits=(0, 180))
        link.status.return_value = dict(state="DISARMED", last=90)
        with self.assertRaisesRegex(ValueError, "one-degree"):
            once(link, "move", 95)
        link.request.assert_not_called()

    def test_s2_reference_does_not_initialize_other_channels(self):
        link = FakeLink(-1, -1)
        link.version = "0.5.3"; link.limits = FIRMWARE_LIMITS[("UAVTRACK_PANTILT", link.version)]
        link.axis = "S2"
        with patch("single_servo.once", link.request_home):
            result = reference(link)
        self.assertEqual(link.commands, [("S2", "center", 45)])
        self.assertEqual(result["axes"]["S1"]["last_requested"], -1)
        self.assertEqual(result["axes"]["S3"]["last_requested"], -1)

    def test_explicit_home_initializes_only_missing_pan_reference_after_s2_calibration(self):
        link = PairLink(-1, 90)
        link.version = "0.5.3"; link.limits = FIRMWARE_LIMITS[("UAVTRACK_PANTILT", link.version)]
        with patch("single_servo.once", link.request_home), patch("single_servo.time.sleep"):
            report = coordinated(link, [(103, 68)])
        self.assertIn(("S1", "center", 103), link.commands)
        self.assertFalse(any(isinstance(c, tuple) and c[0] == "S2" for c in link.commands))
        self.assertEqual(report["axes"]["S2"]["last_requested"], 68)
    def test_manual_s2_can_return_from_90_on_new_firmware(self):
        link = Mock(version="0.5.2", axis="S2", angle_limits=(35, 90))
        initial = dict(state="DISARMED", session="boot-1", sequence=0, last=90, target=90, frames=1)
        done = initial | dict(state="ARMED_S2", last=89, target=89, frames=2)
        link.status.side_effect = [initial, done]
        self.assertEqual(once(link, "move", 89)["last"], 89)
        self.assertEqual(link.request.call_args.args[:4], ("MOVE_S2", initial, 2, 89))
        link.disarm.assert_called_once()

    def test_manual_s2_rejects_request_above_90_before_arming(self):
        link = Mock(version="0.5.2", axis="S2", angle_limits=(35, 90))
        link.status.return_value = dict(state="DISARMED", last=50)
        with self.assertRaises(ValueError):
            once(link, "move", 91)
        link.request.assert_not_called()

    def test_s2_extension_does_not_expand_rectangle_tracking_bounds(self):
        self.assertEqual(FIRMWARE_LIMITS[("UAVTRACK_PANTILT", "0.5.2")]["S2"], (35, 90))
        with self.assertRaises(ValueError):
            rectangle_path((103, 60), 30, 175, 35, 90)

    def test_s3_replacement_sweep_is_isolated_and_uses_30_to_90(self):
        link = FakeLink(-1, -1)
        link.version = "0.5.1"
        link.limits = FIRMWARE_LIMITS[("UAVTRACK_PANTILT", link.version)]
        link.positions["S3"] = -1
        link.axis = "S3"
        with patch("single_servo.once", link.request_home), patch("single_servo.time.sleep"):
            result = sweep(link)
        self.assertEqual(link.commands, [("S3", "center", 60), ("S3", "move", 30), ("S3", "move", 90)])
        self.assertEqual(link.positions["S1"], -1)
        self.assertEqual(link.positions["S2"], -1)
        self.assertEqual(result["sweep"]["uart_frames_sent"], 60)

    def test_fresh_boot_and_referenced_axis_use_different_commands(self):
        link = FakeLink(-1, 90)
        with patch("single_servo.once", link.request_home), patch("single_servo.time.sleep") as pause:
            result = home(link)
        self.assertEqual(link.commands, [("S1", "center", 90), ("S2", "move", 88)])
        pause.assert_called_once_with(1.0)
        self.assertEqual(result["axes"]["S1"]["uart_frames_sent"], 1)
        self.assertEqual(result["axes"]["S2"]["uart_frames_sent"], 2)

    def test_already_requested_home_does_not_rearm(self):
        link = FakeLink(90, 90)
        with patch("single_servo.once") as request:
            result = home(link, 90, 90)
        request.assert_not_called()
        self.assertEqual(result["axes"]["S1"]["uart_frames_sent"], 0)
        self.assertEqual(result["axes"]["S2"]["uart_frames_sent"], 0)

    def test_incompatible_or_armed_firmware_cannot_start_home(self):
        old = FakeLink(-1, -1)
        old.limits = {"S1": (80, 100)}
        armed = FakeLink(-1, -1)
        armed.state = "ARMED_S2"
        with patch("single_servo.once") as request:
            with self.assertRaisesRegex(RuntimeError, "dual-axis firmware"):
                home(old)
            with self.assertRaisesRegex(RuntimeError, "before S1"):
                home(armed)
        request.assert_not_called()

    def test_reset_between_axes_cancels_second_request(self):
        link = FakeLink(-1, -1)

        def reboot(_):
            link.boot = "reboot"

        with patch("single_servo.once", link.request_home), patch("single_servo.time.sleep", reboot):
            with self.assertRaisesRegex(RuntimeError, "before S2"):
                home(link)
        self.assertEqual(link.commands, [("S1", "center", 90)])

    def test_calibrated_tilt_starts_at_88_before_ramping_lower(self):
        link = FakeLink(-1, -1)
        with patch("single_servo.once", link.request_home), patch("single_servo.time.sleep"):
            result = home(link, 87, 85)
        self.assertEqual(link.commands, [("S1", "center", 87), ("S2", "center", 88), ("S2", "move", 85)])
        self.assertEqual(result["axes"]["S2"]["uart_frames_sent"], 4)

    def test_nudge_uses_firmware_reference_and_honors_trial_floor(self):
        link = FakeLink(90, 85)
        link.axis = "S2"
        with patch("single_servo.once", link.request_home):
            with self.assertRaisesRegex(ValueError, "85–90"):
                nudge(link, -1, 85)
            self.assertFalse(link.commands)
            result = nudge(link, -1, 80)
        self.assertEqual(link.commands, [("S2", "move", 84)])
        self.assertEqual(result["axes"]["S2"]["last_requested"], 84)

    def test_rectangle_route_matches_requested_order_and_rounded_center(self):
        center = rectangle_center(30, 175, 35, 65)
        self.assertEqual(center, (103, 50))
        self.assertEqual(rectangle_path(center, 30, 175, 35, 65), [
            (103, 50), (30, 65), (30, 35), (175, 35), (175, 65), (30, 65),
            (175, 35), (175, 65), (30, 35), (30, 65), (103, 50)])
        with self.assertRaises(ValueError):
            rectangle_path(center, 30, 175, 35, 70)

    def test_coordinated_route_uses_paired_targets_and_stops_at_center(self):
        link = PairLink(-1, -1)
        path = rectangle_path(rectangle_center(30, 175, 35, 65), 30, 175, 35, 65)
        with patch("single_servo.time.sleep"):
            result = coordinated(link, path)
        moves = [command for command in link.commands if command.startswith("MOVE_BOTH,")]
        self.assertEqual([tuple(map(int, command.split(",")[-2:])) for command in moves], path)
        self.assertEqual(result["axes"]["S1"]["last_requested"], 103)
        self.assertEqual(result["axes"]["S2"]["last_requested"], 50)
        self.assertEqual(link.state, "DISARMED")

    def test_coordinated_failure_cancels_remaining_waypoints(self):
        link = PairLink(103, 50)
        link.fail_move = True
        with self.assertRaisesRegex(RuntimeError, "injected"):
            coordinated(link, [(30, 65), (175, 35)])
        self.assertEqual(link.state, "DISARMED")
        self.assertEqual(len([command for command in link.commands if command.startswith("MOVE_BOTH,")]), 1)

    def test_reverse_step_reports_its_own_uart_frame(self):
        link = FakeLink(90, 50)
        link.axis = "S2"
        with patch("single_servo.once", link.request_home):
            forward = nudge(link, 1, 45)
            reverse = nudge(link, -1, 45)
        self.assertEqual(forward["nudge"], {"axis": "S2", "from": 50, "to": 51, "uart_frames_sent": 1})
        self.assertEqual(reverse["nudge"], {"axis": "S2", "from": 51, "to": 50, "uart_frames_sent": 1})
        self.assertEqual(reverse["uart_frames_total"], 2)

    def test_step_without_frame_increment_cannot_report_success(self):
        link = FakeLink(90, 50)
        link.axis = "S2"
        with patch("single_servo.once", return_value=link.status()):
            with self.assertRaisesRegex(RuntimeError, "exactly one UART frame"):
                nudge(link, 1, 45)

    def test_sweep_only_commands_the_selected_axis(self):
        for axis, first in (("S1", 80), ("S2", 88)):
            with self.subTest(axis=axis):
                link = FakeLink(-1, -1)
                link.version = "0.3.2"
                link.limits = FIRMWARE_LIMITS[("UAVTRACK_PANTILT", link.version)]
                link.axis = axis
                with patch("single_servo.once", link.request_home), patch("single_servo.time.sleep"):
                    result = sweep(link)
                self.assertEqual(link.commands, [(axis, "center", first), (axis, "move", 45), (axis, "move", 90)])
                self.assertEqual(result["sweep"], {"axis": axis, "from": 45, "to": 90, "uart_frames_sent": 45})

    def test_sweep_cannot_start_ascending_after_interrupted_approach(self):
        link = FakeLink(80, 88)
        link.version = "0.3.2"
        link.limits = FIRMWARE_LIMITS[("UAVTRACK_PANTILT", link.version)]
        with patch("single_servo.once", side_effect=TimeoutError("cancelled")) as request:
            with self.assertRaises(TimeoutError):
                sweep(link)
        self.assertEqual(request.call_count, 1)
        self.assertEqual(request.call_args.args[1:], ("move", 45))

    def test_previous_firmware_can_sweep_tilt_but_cannot_start_pan(self):
        link = FakeLink(80, 45)
        with patch("single_servo.once", link.request_home), patch("single_servo.time.sleep"):
            with self.assertRaisesRegex(RuntimeError, "S1.*requires newer firmware"):
                sweep(link)
            self.assertFalse(link.commands)
            link.axis = "S2"
            sweep(link)
        self.assertEqual(link.commands, [("S2", "move", 90)])

    def test_protected_firmware_initializes_tilt_at_45_and_only_sweeps_below_65(self):
        link = FakeLink(90, -1)
        link.version = "0.3.3"
        link.limits = FIRMWARE_LIMITS[("UAVTRACK_PANTILT", link.version)]
        link.axis = "S2"
        with patch("single_servo.once", link.request_home), patch("single_servo.time.sleep"):
            report = sweep(link)
        self.assertEqual(link.commands, [("S2", "center", 45), ("S2", "move", 40), ("S2", "move", 65)])
        self.assertEqual(report["sweep"]["uart_frames_sent"], 25)

    def test_new_home_rejects_old_88_degree_calibration_before_moving(self):
        link = FakeLink(90, -1)
        link.version = "0.3.3"
        link.limits = FIRMWARE_LIMITS[("UAVTRACK_PANTILT", link.version)]
        with patch("single_servo.once") as request:
            with self.assertRaises(ValueError):
                home(link, 90, 88)
        request.assert_not_called()

    def test_measured_limits_reject_nudges_beyond_both_axes(self):
        for axis, pan, tilt, delta in (("S1", 30, 45, -1), ("S1", 175, 45, 1),
                                       ("S2", 90, 35, -1), ("S2", 90, 65, 1)):
            with self.subTest(axis=axis, delta=delta):
                link = FakeLink(pan, tilt)
                link.version = "0.3.4"
                link.limits = FIRMWARE_LIMITS[("UAVTRACK_PANTILT", link.version)]
                link.axis = axis
                with patch("single_servo.once") as request:
                    with self.assertRaises(ValueError):
                        nudge(link, delta, 35)
                request.assert_not_called()

    def test_new_tilt_lower_segment_can_be_requested_one_degree_at_a_time(self):
        link = FakeLink(90, 40)
        link.version = "0.3.4"
        link.limits = FIRMWARE_LIMITS[("UAVTRACK_PANTILT", link.version)]
        link.axis = "S2"
        with patch("single_servo.once", link.request_home):
            for angle in range(39, 34, -1):
                result = nudge(link, -1, 35)
                self.assertEqual(result["axes"]["S2"]["last_requested"], angle)


class PairLink(FakeLink):
    version = "0.4.0"
    limits = FIRMWARE_LIMITS[("UAVTRACK_PANTILT", version)]
    fail_move = False

    def __init__(self, pan, tilt):
        super().__init__(pan, tilt)
        self.sequence = 0

    def status(self):
        return {**super().status(), "sequence": self.sequence}

    def command(self, line, expected):
        self.commands.append(line)
        if line == "MOTION":
            return "MOTION,IDLE"
        fields = line.split(",")
        self.sequence = int(fields[2])
        if fields[0] == "ARM_BOTH":
            self.state = "ARMED_BOTH"
        elif fields[0] in ("INIT_BOTH", "MOVE_BOTH"):
            if self.fail_move:
                raise RuntimeError("injected send failure")
            self.positions.update(zip(("S1", "S2"), map(int, fields[3:])))
            self.frames += 2
        return f"ACK,{self.sequence},{fields[0]},REQUEST_ONLY"

    def record(self, kind, value):
        pass

    def disarm(self):
        self.state = "DISARMED"


if __name__ == "__main__":
    unittest.main()
