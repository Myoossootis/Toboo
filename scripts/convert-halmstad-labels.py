"""Convert the actual DRONE field of Halmstad MATLAB groundTruth tables.

Uses the MIT mcos-decoder MatStream reader, not its depth/size bbox heuristic:
the latter includes an extra non-frame empty element in the visible sequences.
No prediction is used as a label; no interpolation or dropped frame slots.
"""
import argparse
import csv
import hashlib
import importlib.metadata
import json
from pathlib import Path

import numpy as np
import scipy.io
from mcos_decoder import MatStream


def walk(element):
    yield element
    for child in element.children:
        yield from walk(child)


def decode_drone_table(path, expected_frames):
    workspace = scipy.io.loadmat(str(path))["__function_workspace__"].tobytes()
    tables = []
    for top in MatStream(workspace, skip_header=8).walk():
        for element in walk(top):
            children = element.children
            if len(children) < 5 or children[0].type_id != 6:
                continue
            if int(children[0].as_array()[0]) & 255 != 2:  # MATLAB mxSTRUCT
                continue
            if children[3].type_id != 5 or children[3].payload_size != 4 or children[4].type_id != 1:
                continue
            length = int(children[3].as_array()[0])
            if length <= 0:
                continue
            raw = children[4].as_array().tobytes()
            names = [raw[i:i + length].split(b"\0")[0].decode("ascii") for i in range(0, len(raw), length)]
            if "Time" not in names or "DRONE" not in names:
                continue
            rows = int(np.prod(children[1].as_array()))
            if rows != expected_frames or len(children) - 5 != rows * len(names):
                raise ValueError(f"Annotation rows {rows} do not match expected video frames {expected_frames}")
            boxes = []
            for row in range(rows):
                cell = children[5 + row * len(names) + names.index("DRONE")]
                arrays = [part.as_array() for part in walk(cell) if part.type_id == 9 and part.payload_size == 32]
                if len(arrays) > 1:
                    raise ValueError("Multiple boxes per frame are not supported by this single-target evaluation")
                if not arrays:
                    boxes.append(None)
                    continue
                x, y, width, height = map(float, arrays[0])
                if not all(np.isfinite([x, y, width, height])) or width <= 0 or height <= 0:
                    raise ValueError(f"Invalid reference box at frame {row}")
                boxes.append((x - 1, y - 1, width, height))  # MATLAB 1-based image coordinates -> OpenCV
            tables.append(boxes)
    if len(tables) != 1:
        raise ValueError(f"Expected one groundTruth table, found {len(tables)}")
    return tables[0]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--labels", required=True, type=Path)
    parser.add_argument("--frames", required=True, type=int)
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args()
    if args.frames <= 0 or args.out.exists() or args.out.with_suffix(".source.json").exists():
        raise ValueError("Use positive --frames and fresh output paths")
    boxes = decode_drone_table(args.labels, args.frames)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    with args.out.open("w", newline="", encoding="utf-8") as output:
        writer = csv.writer(output)
        writer.writerow(["frame", "present", "x", "y", "width", "height"])
        for number, box in enumerate(boxes):
            writer.writerow([number, int(box is not None), *(box or ["", "", "", ""])])
    metadata = dict(source=str(args.labels.resolve()), sourceSha256=hashlib.sha256(args.labels.read_bytes()).hexdigest(),
                    frames=len(boxes), positiveFrames=sum(b is not None for b in boxes), className="DRONE",
                    coordinateConvention="x,y minus 1; widths/heights unchanged; zero-based frame index",
                    decoder="struct table fields; no depth/size heuristic, interpolation, or prediction labels",
                    packages={p: importlib.metadata.version(p) for p in ("mcos-decoder", "numpy", "scipy")})
    args.out.with_suffix(".source.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    print(json.dumps(metadata, indent=2))


if __name__ == "__main__":
    main()
