import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("lcd_expression", Path(__file__).parents[1] / "tools/lcd_expression.py")
lcd = importlib.util.module_from_spec(spec)
spec.loader.exec_module(lcd)


class Port:
    def __init__(self, responses):
        self.responses = bytearray(responses)
        self.writes = []

    def reset_input_buffer(self):
        pass

    def write(self, data):
        self.writes.append(data)

    def read(self, size):
        value = self.responses[:size]
        del self.responses[:size]
        return bytes(value)


class LcdHostTests(unittest.TestCase):
    def test_handshake_before_any_display_request_and_clear_on_end(self):
        port = Port(b"TOBOO_LCD,1,UNO,16,2\nOK,happy\nOK,unknown\n")
        output = []
        lcd.run(port, ["E,happy,300\n", "E,unknown,1\n"], output.append, lambda _: None)
        self.assertEqual(["READY", "OK,happy", "OK,unknown"], output)
        self.assertEqual([b"TOBOO_LCD?\n", b"E,happy,300\n", b"E,unknown,1\n", b"E,unknown,1\n"], port.writes)

    def test_wrong_device_or_missing_lcd_never_receives_display_commands(self):
        for response in (b"TOBOO_SENSOR,1,UNO\n", b"TOBOO_LCD_ERROR,-1\n"):
            port = Port(response)
            with self.assertRaises(RuntimeError):
                lcd.run(port, ["E,happy,300\n"], lambda _: None, lambda _: None)
            self.assertEqual([b"TOBOO_LCD?\n"], port.writes)

    def test_bad_ack_aborts_without_replaying_observation(self):
        port = Port(b"TOBOO_LCD,1,UNO,16,2\nOK,sad\n")
        with self.assertRaises(RuntimeError):
            lcd.run(port, ["E,happy,300\n", "E,happy,300\n"], lambda _: None, lambda _: None)
        self.assertEqual([b"TOBOO_LCD?\n", b"E,happy,300\n", b"E,unknown,1\n"], port.writes)

    def test_invalid_input_and_bounded_serial_lines(self):
        for line in ("E,happy,501", "E,happy,0", "E,happy,-1", "E,Happy,1", "E,happy,1,1", "E,happy,１２", "MOVE,180"):
            with self.assertRaises(ValueError, msg=line):
                lcd.validate(line)
        with self.assertRaises(RuntimeError):
            lcd.read_line(Port(b"x" * 81), 1)
        with self.assertRaises(RuntimeError):
            lcd.read_line(Port(b"OK,happy"), 0.001)


if __name__ == "__main__":
    unittest.main()
