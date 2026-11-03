# Active face detector: YuNet 2023mar

Active file: `face_detection_yunet_2023mar.onnx` (232589 bytes).

- Source: https://github.com/opencv/opencv_zoo/blob/f12e12798e8314f7c074a6656816c048dcc95b7a/models/face_detection_yunet/face_detection_yunet_2023mar.onnx
- SHA256: `8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4` (verified against the upstream Git LFS pointer).
- Model license: MIT, retained in `licenses/YuNet-MIT.txt`.
- CPU inference uses existing OpenCvSharp / OpenCV 4.10 DNN; no new NuGet or GPU dependency.
- Output decoding follows OpenCV 4.10 `FaceDetectorYN`: https://github.com/opencv/opencv/blob/4.10.0/modules/objdetect/src/face_detect.cpp (Apache-2.0, retained in `licenses/OpenCV-Apache-2.0.txt`). The older wrapper has no FaceDetectorYN binding, so the existing DNN API is used directly.

The input is BGR, resized down to at most 640 pixels wide with its aspect ratio preserved, then padded on the bottom/right to multiples of 32. No histogram equalization or image mirroring is applied. The network is run once for frontal and profile views, using score threshold 0.8 and NMS IoU threshold 0.3. Boxes are clipped and mapped back to original unmirrored coordinates; the largest retained box supplies the center observation. This does not identify or persist the identity of a person.

Extreme poses, occlusion, poor lighting or unusual image quality can still cause misses or false positives. Landmarks from the upstream network are not exposed by this application. All inference is local and requires no network access at runtime.

## Retained v0.1.2 model (not used by current main)

File: `haarcascade_frontalface_default.xml` (930127 bytes).

- Source: https://github.com/opencv/opencv/blob/4.10.0/data/haarcascades/haarcascade_frontalface_default.xml
- SHA256: `0f7d4527844eb514d4a4948e822da90fbb16a34a0bbbbc6adc6498747a5aafb0`
- Original model by Rainer Lienhart, distributed with OpenCV. The original Intel permissive license is retained in the XML header; it is also reproduced in `licenses/OpenCV-Haar-model.txt`.
- Unmodified upstream data, intentionally tracked as a small offline runtime dependency. No face photographs or personal camera recordings are included.

This frontal Haar model is retained for comparing the already-released v0.1.2 baseline and keeping future overlay updates additive. It is not a fallback: current main requires the ONNX model and fails clearly if it is missing. No photographs or personal recordings are included.
