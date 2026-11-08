"""Synthetic transport tests; no serial device is opened."""
import queue
import sys
import time
import unittest
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from face_follow import follow, validate_frame, validate_start


class FakeLink:
    version = "0.5.0"
    axis = "S1"
    def __init__(self):
        self.commands = []
        self.state = "DISARMED"
        self.sequence = 0
        self.last = [103, 50]
        self.stop_count = 0
    def status(self):
        return dict(state=self.state, session="boot-1", sequence=self.sequence,
                    last=self.last[0 if self.axis == "S1" else 1], target=0, frames=0)
    def command(self, text, expected):
        self.commands.append(text)
        parts = text.split(",")
        if parts[0] == "ARM_FOLLOW": self.state = "ARMED_FOLLOW"
        self.sequence = int(parts[2])
        return expected
    def disarm(self):
        self.state = "DISARMED"
        self.stop_count += 1


def start(**changes):
    return dict(type="start", bounds=[95, 110, 45, 55], reference=[103, 50], axes="S1") | changes


class FollowTests(unittest.TestCase):
    def test_both_axis_follow_uses_50_to_150_and_holds_on_missing_frame(self):
        link=FakeLink(); link.version="0.5.3"; link.last=[103,100]
        inbox=queue.Queue()
        for message in (start(bounds=[30,175,50,150],reference=[103,100],axes="both"),
                        dict(type="frame",permit=True,age_ms=10,pan=140,tilt=125.5),
                        dict(type="frame",permit=False,age_ms=0),dict(type="stop")):
            inbox.put((message,time.monotonic()))
        follow(link,inbox,lambda _:None)
        self.assertIn("ARM_FOLLOW,boot-1,1,30,175,50,150",link.commands)
        self.assertIn("FOLLOW_BOTH,boot-1,2,14000,12550",link.commands)
        self.assertIn("HOLD_BOTH,boot-1,3",link.commands)
        self.assertEqual(link.stop_count,1)

    def test_follow_clamps_requests_outside_saved_window_and_continues_after_return(self):
        for tilt in (49.99,150.01):
            link=FakeLink(); link.version="0.5.3"; link.last=[103,100]
            inbox=queue.Queue()
            for message in (start(bounds=[30,175,50,150],reference=[103,100],axes="both"),
                            dict(type="frame",permit=True,age_ms=10,pan=103,tilt=tilt),
                            dict(type="frame",permit=True,age_ms=10,pan=103,tilt=100),dict(type="stop")):
                inbox.put((message,time.monotonic()))
            reports=[]
            follow(link,inbox,reports.append)
            self.assertEqual(link.stop_count,1)
            clipped=5000 if tilt<50 else 15000
            self.assertIn(f"FOLLOW_BOTH,boot-1,2,10300,{clipped}",link.commands)
            self.assertIn("FOLLOW_BOTH,boot-1,3,10300,10000",link.commands)
            self.assertEqual(reports[2]["limited"],["S2:lower" if tilt<50 else "S2:upper"])
            self.assertEqual(reports[3]["limited"],[])

    def test_new_profile_accepts_explicitly_calibrated_bounds_above_old_65_limit(self):
        link = FakeLink(); link.version = "0.5.3"
        inbox = queue.Queue()
        for message in (start(bounds=[95, 110, 45, 100], axes="both"),
                        dict(type="frame", permit=True, age_ms=10, pan=104, tilt=90), dict(type="stop")):
            inbox.put((message, time.monotonic()))
        follow(link, inbox, lambda _: None)
        self.assertIn("FOLLOW_BOTH,boot-1,2,10400,9000", link.commands)
    def test_s3_capable_firmware_retains_s1_s2_follow_protocol(self):
        link = FakeLink()
        link.version = "0.5.1"
        inbox = queue.Queue()
        for message in (start(), dict(type="frame", permit=True, age_ms=10, pan=104, tilt=50), dict(type="stop")):
            inbox.put((message, time.monotonic()))
        follow(link, inbox, lambda _: None)
        self.assertIn("FOLLOW_BOTH,boot-1,2,10400,5000", link.commands)
        self.assertFalse(any("_S3" in command for command in link.commands))

    def test_replacement_firmware_still_rejects_follow_bounds_above_65(self):
        link = FakeLink()
        link.version = "0.5.2"
        inbox = queue.Queue()
        inbox.put((start(bounds=[95, 110, 45, 90]), time.monotonic()))
        with self.assertRaises(ValueError):
            follow(link, inbox, lambda _: None)
        self.assertEqual(link.commands, [])

    def test_bad_reference_and_limits_do_not_arm(self):
        for message in (start(reference=[-1, 50]), start(bounds=[30, 175, 35, 66]), start(axes="unknown")):
            link = FakeLink()
            inbox = queue.Queue()
            inbox.put((message, time.monotonic()))
            with self.assertRaises(ValueError): follow(link, inbox, lambda _: None)
            self.assertEqual(link.commands, [])
            self.assertEqual(link.stop_count, 0)

    def test_single_axis_stale_frame_hold_and_clean_stop(self):
        link = FakeLink()
        inbox = queue.Queue()
        for message in [start(), dict(type="frame", permit=True, age_ms=20, pan=104.55, tilt=54),
                        dict(type="frame", permit=False, age_ms=20),
                        dict(type="frame", permit=True, age_ms=201, pan=105, tilt=54), dict(type="stop")]:
            inbox.put((message, time.monotonic()))
        reports = []
        follow(link, inbox, reports.append)
        self.assertIn("FOLLOW_BOTH,boot-1,2,10455,5000", link.commands)
        self.assertEqual(sum(text.startswith("HOLD_BOTH,") for text in link.commands), 2)
        self.assertEqual(link.stop_count, 1)
        self.assertTrue(reports[-2]["held"])
        self.assertEqual(reports[-1]["type"], "stopped")

    def test_eof_and_idle_timeout_disarm(self):
        for eof in (True, False):
            link, inbox = FakeLink(), queue.Queue()
            inbox.put((start(), time.monotonic()))
            if eof: inbox.put((None, time.monotonic()))
            if eof: follow(link, inbox, lambda _: None)
            else:
                with self.assertRaises(TimeoutError): follow(link, inbox, lambda _: None)
            self.assertEqual(link.stop_count, 1)

    def test_invalid_target_and_delayed_message_disarm(self):
        for age, value in ((0, float("nan")), (1, 104)):
            link, inbox = FakeLink(), queue.Queue()
            inbox.put((start(), time.monotonic()))
            inbox.put((dict(type="frame", permit=True, age_ms=0, pan=value, tilt=50), time.monotonic() - age))
            with self.assertRaises((TimeoutError, ValueError)): follow(link, inbox, lambda _: None)
            self.assertEqual(link.stop_count, 1)
            self.assertFalse(any(text.startswith("FOLLOW_BOTH,") for text in link.commands))

    def test_nonfinite_and_non_numeric_values_rejected(self):
        for value in (float("nan"), float("inf"), True, "104"):
            with self.assertRaises(ValueError):
                validate_frame(dict(type="frame", permit=True, age_ms=0, pan=value, tilt=50), [95, 110, 45, 55], [103, 50], "both")

    def test_old_firmware_rejected_without_any_output(self):
        link = FakeLink()
        link.version = "0.4.0"
        with self.assertRaises(RuntimeError): follow(link, queue.Queue(), lambda _: None)
        self.assertEqual(link.commands, [])


if __name__ == "__main__": unittest.main()
