# Visible facial-expression model

Pinned OpenCV Zoo Progressive Teacher / MobileFaceNet model, 4,791,892 bytes. Exact source, commit and SHA256 are in `model.json`. This unmodified, small offline runtime dependency is tracked alongside YuNet. No images or personal recordings are bundled.

- Upstream: https://github.com/opencv/opencv_zoo/tree/25f423d0e04c31a17254620e58febd7386da523b/models/facial_expression_recognition
- License: Apache-2.0 (`licenses/OpenCV-Zoo-Apache-2.0.txt`). Copyright (C) 2022, Shenzhen Institute of Artificial Intelligence and Robotics for Society.
- SHA256: `4f61307602fc089ce20488a31d4e4614e3c9753a7d6c41578c854858b183e1a9`, verified against upstream Git LFS.
- Alignment: non-reflecting least-squares similarity transform of all five YuNet landmarks to the upstream 112x112 template.
- Input: BGR image warped to 112x112, converted to RGB/NCHW, `(pixel - 127.5) / 127.5`.
- Output `label`: seven logits in order angry, disgust, fearful, happy, neutral, sad, surprised. Stable softmax is applied once; scores are not calibrated psychological probabilities.
- Model loading verifies the hash. Missing/corrupt expression weights produce Unknown in the optional expression session; face tracking continues. The distribution self-test also checks that the expression model loads.

Upstream benchmark accuracy is not this prototype's measured accuracy. This predicts visible-expression categories; it does not identify people or determine their true emotional or medical state.

See [expression analysis](../../docs/expressions.md) for temporal confirmation, rejection rules and runtime limits.
