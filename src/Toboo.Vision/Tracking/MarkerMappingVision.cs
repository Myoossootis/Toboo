using OpenCvSharp;
using Toboo.Core;

namespace Toboo.Vision;

public sealed record MappingIntrinsics(int Width, int Height, double Fx, double Fy, double Cx, double Cy,
    double[] Distortion, double RmsPixels)
{
    public bool IsValid => Width > 0 && Height > 0 && Fx > Width * 0.2 && Fx < Width * 5 &&
        Fy > Height * 0.2 && Fy < Height * 5 && Cx > 0 && Cx < Width && Cy > 0 && Cy < Height &&
        double.IsFinite(RmsPixels) && RmsPixels is >= 0 and <= 1.2 &&
        Distortion is { Length: 5 } && Distortion.All(double.IsFinite);

    internal double[,] Matrix => new double[,] { { Fx, 0, Cx }, { 0, Fy, Cy }, { 0, 0, 1 } };
}

public sealed class MarkerMappingVision : IDisposable
{
    public const int CalibrationSamples = 12;
    public const int MarkerId = 7;
    private static readonly Size Chessboard = new(9, 6);
    private readonly ArucoTargetDetector detector = new(MarkerId);

    public MapPose? Locate(Mat gray, MappingIntrinsics? calibration, double sideMeters, out Point2f[]? corners)
    {
        corners = detector.FindCorners(gray);
        if (corners is null || calibration is null || gray.Width != calibration.Width || gray.Height != calibration.Height)
            return null;
        return SolvePose(corners, calibration, sideMeters);
    }

    internal static MapPose? SolvePose(Point2f[] corners, MappingIntrinsics calibration, double sideMeters)
    {
        if (corners.Length != 4 || !calibration.IsValid || !double.IsFinite(sideMeters) || sideMeters is < 0.04 or > 0.5)
            return null;
        for (var i = 0; i < 4; i++)
            if (!(PixelDistance(corners[i], corners[(i + 1) % 4]) >= 32)) return null;

        var half = (float)(sideMeters / 2);
        Point3f[] square = [new(-half, half, 0), new(half, half, 0), new(half, -half, 0), new(-half, -half, 0)];
        var matrix = calibration.Matrix;
        double[] rotation = [], translation = [];
        Cv2.SolvePnP(square, corners, matrix, calibration.Distortion, ref rotation, ref translation,
            flags: SolvePnPFlags.Iterative);
        if (translation.Length != 3 || rotation.Length != 3 || translation[2] <= 0.1 ||
            !translation.All(double.IsFinite) || !rotation.All(double.IsFinite)) return null;
        Cv2.ProjectPoints(square, rotation, translation, matrix, calibration.Distortion, out var reprojected, out _);
        if (corners.Zip(reprojected).Count(pair => PixelDistance(pair.First, pair.Second) <= 2.5) != 4) return null;
        Cv2.Rodrigues(rotation, out double[,] r, out _);
        // solvePnP returns world-to-camera. Each row supplies one camera axis in the tag frame.
        // The map is tag-world X/Z. The tag is upright with world Y pointing up.
        MapPoint Axis(int i) => new(r[i, 0], r[i, 2]);
        var right = Axis(0); var down = Axis(1); var forward = Axis(2);
        var position = right * -translation[0] + down * -translation[1] + forward * -translation[2];
        return position.Z > 0.05 && position.Length <= 10 ? new(position, right, down, forward) : null;
    }

    public static Point2f[]? FindChessboard(Mat gray)
    {
        if (!Cv2.FindChessboardCorners(gray, Chessboard, out Point2f[] corners,
                ChessboardFlags.AdaptiveThresh | ChessboardFlags.NormalizeImage | ChessboardFlags.FastCheck)) return null;
        return Cv2.CornerSubPix(gray, corners, new Size(5, 5), new Size(-1, -1),
            new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 30, 0.01));
    }

    public static bool IsNewCalibrationView(IReadOnlyList<Point2f[]> existing, Point2f[] candidate, int width)
    {
        var meanX = candidate.Average(point => point.X);
        var meanY = candidate.Average(point => point.Y);
        foreach (var frame in existing)
        {
            if (Math.Abs(frame.Average(point => point.X) - meanX) > width * 0.12 ||
                Math.Abs(frame.Average(point => point.Y) - meanY) > width * 0.12) continue;
            var same = frame.Zip(candidate).Average(pair => PixelDistance(pair.First, pair.Second));
            var reverse = frame.Zip(candidate.Reverse()).Average(pair => PixelDistance(pair.First, pair.Second));
            if (Math.Min(same, reverse) < width * 0.035) return false;
        }
        return true;
    }

    public static MappingIntrinsics Calibrate(IReadOnlyList<Point2f[]> samples, int width, int height)
    {
        if (samples.Count < CalibrationSamples) throw new ArgumentException("需要至少 12 个不同角度和位置的棋盘格画面。");
        var world = Enumerable.Range(0, Chessboard.Width * Chessboard.Height)
            .Select(i => new Point3f(i % Chessboard.Width, i / Chessboard.Width, 0)).ToArray();
        var k = new double[3, 3];
        var d = new double[5];
        var rms = Cv2.CalibrateCamera(samples.Select(_ => world), samples, new Size(width, height), k, d,
            out _, out _, CalibrationFlags.None);
        var result = new MappingIntrinsics(width, height, k[0, 0], k[1, 1], k[0, 2], k[1, 2], d, rms);
        if (!result.IsValid) throw new InvalidDataException($"标定误差或内参超出范围（RMS {rms:0.00} px）。请从不同角度和位置重采。");
        return result;
    }

    public static void WritePatterns(string directory)
    {
        const int cell = 100;
        using var board = new Mat(9 * cell, 12 * cell, MatType.CV_8UC1, Scalar.White);
        for (var row = 0; row < 7; row++)
            for (var column = 0; column < 10; column++)
                if ((row + column) % 2 == 0)
                    Cv2.Rectangle(board, new Rect((column + 1) * cell, (row + 1) * cell, cell, cell), Scalar.Black, -1);
        using var marker = ArucoTargetDetector.CreateMarker(MarkerId);
        if (!Cv2.ImWrite(Path.Combine(directory, "toboo-chessboard-9x6.png"), board) ||
            !Cv2.ImWrite(Path.Combine(directory, "toboo-aruco-7.png"), marker))
            throw new IOException("无法写入标定图，请检查目录权限。");
    }

    private static double PixelDistance(Point2f a, Point2f b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    public void Dispose() => detector.Dispose();
}
