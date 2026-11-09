namespace Toboo.Core;

public sealed record SightAngles(double HorizontalDegrees, double VerticalDegrees);

/// <summary>Pinhole sight direction, not servo shaft angle. Coordinates are never mirrored.</summary>
public sealed record CameraAngleModel(int Width, int Height, double Fx, double Fy,
    double Cx, double Cy, bool Approximate = true)
{
    public void Validate()
    {
        if (Width <= 0 || Height <= 0 || !double.IsFinite(Fx) || !double.IsFinite(Fy) ||
            Fx <= 0 || Fy <= 0 || !double.IsFinite(Cx) || !double.IsFinite(Cy) ||
            Cx < 0 || Cx >= Width || Cy < 0 || Cy >= Height)
            throw new ArgumentException("Invalid camera intrinsics.");
    }

    public static CameraAngleModel FromFieldOfView(int width, int height,
        double horizontalDegrees, double verticalDegrees)
    {
        if (!double.IsFinite(horizontalDegrees) || !double.IsFinite(verticalDegrees) ||
            horizontalDegrees <= 0 || horizontalDegrees >= 170 || verticalDegrees <= 0 || verticalDegrees >= 170)
            throw new ArgumentException("Specify horizontal and vertical FOV, not diagonal FOV.");
        var model = new CameraAngleModel(width, height,
            width / (2 * Math.Tan(horizontalDegrees * Math.PI / 360)),
            height / (2 * Math.Tan(verticalDegrees * Math.PI / 360)), width / 2.0, height / 2.0);
        model.Validate();
        return model;
    }

    public SightAngles Project(double x, double y, int frameWidth, int frameHeight)
    {
        Validate();
        if (frameWidth <= 0 || frameHeight <= 0 || !double.IsFinite(x) || !double.IsFinite(y) ||
            x < 0 || x >= frameWidth || y < 0 || y >= frameHeight)
            throw new ArgumentException("Point must be inside the frame.");
        // Resizing is allowed; a different crop/aspect ratio needs a new calibration.
        if (Math.Abs((double)frameWidth / frameHeight / ((double)Width / Height) - 1) > 0.01)
            throw new ArgumentException("Camera calibration aspect ratio does not match the frame.");
        var nx = (x * Width / frameWidth - Cx) / Fx;
        var ny = (y * Height / frameHeight - Cy) / Fy;
        return new(Math.Atan(nx) * 180 / Math.PI,
            Math.Atan2(ny, Math.Sqrt(1 + nx * nx)) * 180 / Math.PI);
    }
}
