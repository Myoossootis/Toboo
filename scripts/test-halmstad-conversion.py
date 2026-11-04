"""Synthetic table-structure tests; no public videos or model weights required."""
import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch

import numpy as np

spec = importlib.util.spec_from_file_location("converter", Path(__file__).with_name("convert-halmstad-labels.py"))
converter = importlib.util.module_from_spec(spec)
spec.loader.exec_module(converter)


class Element:
    def __init__(self, kind, data=None, children=None, size=0):
        self.type_id, self.data, self.children, self.payload_size = kind, data, children or [], size

    def as_array(self):
        return self.data


def table(boxes):
    names = ["Time", "AIRPLANE", "BIRD", "DRONE", "HELICOPTER"]
    packed = b"".join(n.encode().ljust(11, b"\0") for n in names)
    children = [Element(6, np.array([2, 0])), Element(5, np.array([len(boxes), 1])), Element(1),
                Element(5, np.array([11]), size=4), Element(1, np.frombuffer(packed, dtype=np.uint8))]
    for box in boxes:
        for name in names:
            # A non-drone box must never become a DRONE reference.
            value = box if name == "DRONE" else ([100, 100, 30, 20] if name == "BIRD" else None)
            arrays = [] if value is None else [Element(9, np.array(value), size=32)]
            children.append(Element(14, children=arrays))
    return Element(14, children=children)


class ConversionTests(unittest.TestCase):
    def decode(self, tables, frames):
        with patch.object(converter.scipy.io, "loadmat", return_value={"__function_workspace__": np.zeros(8, dtype=np.uint8)}), \
             patch.object(converter, "MatStream") as stream:
            stream.return_value.walk.return_value = tables
            return converter.decode_drone_table(Path("synthetic.mat"), frames)

    def test_exact_rows_field_selection_and_coordinate_conversion(self):
        self.assertEqual([None, (9, 19, 30, 40)], self.decode([table([None, [10, 20, 30, 40]])], 2))

    def test_no_silent_off_by_one_repair(self):
        with self.assertRaises(ValueError):
            self.decode([table([None, [10, 20, 30, 40]])], 1)

    def test_missing_or_duplicate_tables_fail(self):
        for tables in ([], [table([None]), table([None])]):
            with self.assertRaises(ValueError):
                self.decode(tables, 1)

    def test_nonfinite_and_empty_size_boxes_fail(self):
        for box in ([float("nan"), 0, 30, 40], [10, 20, 0, 40]):
            with self.assertRaises(ValueError):
                self.decode([table([box])], 1)

    def test_multiple_boxes_are_not_silently_reduced(self):
        value = table([[10, 20, 30, 40]])
        value.children[8].children.append(Element(9, np.array([50, 20, 30, 40]), size=32))
        with self.assertRaises(ValueError):
            self.decode([value], 1)


if __name__ == "__main__":
    unittest.main()
