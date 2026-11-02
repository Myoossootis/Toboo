# Frontal face cascade

File: `haarcascade_frontalface_default.xml` (930127 bytes).

- Source: https://github.com/opencv/opencv/blob/4.10.0/data/haarcascades/haarcascade_frontalface_default.xml
- SHA256: `0f7d4527844eb514d4a4948e822da90fbb16a34a0bbbbc6adc6498747a5aafb0`
- Original model by Rainer Lienhart, distributed with OpenCV. The original Intel permissive license is retained in the XML header; it is also reproduced in `licenses/OpenCV-Haar-model.txt`.
- Unmodified upstream data, intentionally tracked as a small offline runtime dependency. No face photographs or personal camera recordings are included.

The application uses grayscale, histogram equalization, a maximum working width of 640 pixels, scale factor 1.1, five neighbors and a minimum 30×30 working-image face size. Coordinates are mapped back to the unmirrored input frame. All faces are boxed; the largest box supplies the center observation. This selection is repeated per frame and does not preserve a person's identity.

This is a frontal-face baseline. Profile views, occlusion, small faces and poor lighting can cause misses or false positives. It does not perform identity recognition, liveness detection, landmark estimation or camera calibration. The model is local and requires no network access at runtime.
