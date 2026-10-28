# Project working agreements

Follow the user's global Karpathy Guidelines, including reading the installed skill when required. Keep this project small and preserve unrelated files and the original EyeTracking project.

- Build with the installed .NET 8 SDK and `dotnet` CLI. Do not add a Visual Studio or C++/CLI build dependency.
- Core contains no OpenCV, UI or serial-port dependencies. Vision owns native image buffers; App connects acquisition, detection and logging.
- Use DICT_4X4_50 / target ID 7 unless an experiment explicitly changes it. Do not mirror algorithm coordinates implicitly.
- Every command angle is a request, not measured shaft position. Distinguish synthetic tests, recorded-video analysis and actual hardware verification in logs and reports.
- Use a monotonic clock for control. On missing/stale frames, hold position; do not invent detections or integrate a large elapsed-time jump.
- `config/tracker.json` contains preliminary simulation settings. Actual servo limits and directions need hardware measurements.
- Raw images, videos, calibration results and generated outputs belong in ignored data/artifact directories. Do not commit personal eye-tracking data or temporary vendor downloads.
- Dependencies are pinned and lock files are tracked. Use `scripts/verify.ps1` before completing behavior changes; add focused tests when control or frame ownership behavior changes.
- No physical actuator output exists in v0.1. Add transport only with the documented protocol and independently tested firmware timeout handling.
- Prefer short feature branches from main and reviewable commits. Do not rewrite shared history or publish local/private data accidentally.
