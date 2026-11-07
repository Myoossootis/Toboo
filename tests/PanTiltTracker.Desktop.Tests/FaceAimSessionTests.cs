using System.Text.Json;
using OpenCvSharp;
using PanTiltTracker.Core;
using PanTiltTracker.Desktop.Services;
using Xunit;

namespace PanTiltTracker.Desktop.Tests;

public sealed class FaceAimSessionTests
{
    [Fact]
    public async Task BlankVideoUsesFaceAimAndDoesNotLogInventedAngleReferences()
    {
        var folder = NewDirectory();
        var video = Path.Combine(folder, "blank.avi");
        using (var writer = new VideoWriter(video, FourCC.MJPG, 30, new Size(640, 480)))
        using (var blank = new Mat(480, 640, MatType.CV_8UC3, Scalar.All(40)))
        {
            Assert.True(writer.IsOpened());
            for (var i = 0; i < 6; i++) writer.Write(blank);
        }
        var frames = await Run(video, folder);
        Assert.Equal(6, frames.Count);
        Assert.All(frames, frame =>
        {
            Assert.NotNull(frame.FaceAim);
            Assert.False(frame.FaceAim.HasReference);
            Assert.False(frame.FaceAim.RequestPermitted);
            Assert.False(frame.Observation.HasTarget);
        });
        var rows = ReadRows(folder);
        Assert.All(rows, row =>
        {
            Assert.Empty(row["pan_command_deg"]);
            Assert.Empty(row["tilt_command_deg"]);
            Assert.Empty(row["sight_pan_error_deg"]);
            Assert.Equal("0", row["aim_request_permitted"]);
            Assert.Equal("0", row["aim_has_reference"]);
        });
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "run.json")));
        Assert.Equal("disabled", metadata.RootElement.GetProperty("hardwareOutput").GetString());
        Assert.Equal("confirmed spatial lock; not identity recognition",
            metadata.RootElement.GetProperty("experiment").GetProperty("faceSelection").GetString());
    }

    [FaceAimSampleFact]
    public async Task RecordedFaceFramesConfirmThenClearOnLossWithNoActuatorOutput()
    {
        var folder = NewDirectory();
        var video = Path.Combine(folder, "face-and-loss.avi");
        using (var source = Cv2.ImRead(Environment.GetEnvironmentVariable("UAVTRACK_FACE_TEST_IMAGE")!))
        using (var resized = new Mat())
        using (var blank = new Mat(480, 640, MatType.CV_8UC3, Scalar.All(40)))
        using (var writer = new VideoWriter(video, FourCC.MJPG, 30, new Size(640, 480)))
        {
            Assert.False(source.Empty());
            Assert.True(writer.IsOpened());
            Cv2.Resize(source, resized, new Size(640, 480));
            for (var i = 0; i < 5; i++) writer.Write(resized);
            writer.Write(blank); writer.Write(blank);
        }
        var settings = new FaceAimSettings { Camera = CameraAngleModel.FromFieldOfView(640, 480, 60, 45) };
        var frames = await Run(video, folder, settings);
        Assert.Equal(7, frames.Count);
        Assert.All(frames.Take(2), f => Assert.Equal("face_confirming", f.VisionStatus));
        Assert.All(frames.Skip(2).Take(3), f =>
        {
            Assert.True(f.Observation.HasTarget);
            Assert.NotNull(f.FaceAim!.SightError);
            Assert.NotNull(f.FaceAim.FilteredX);
            Assert.False(f.FaceAim.RequestPermitted);
        });
        Assert.All(frames.Skip(5), f =>
        {
            Assert.False(f.Observation.HasTarget);
            Assert.Null(f.FaceAim!.FilteredX);
            Assert.Null(f.FaceAim.SightError);
        });
        var rows = ReadRows(folder);
        Assert.NotEmpty(rows[2]["face_filtered_u_px"]);
        Assert.Equal("1", rows[2]["face_lock_epoch"]);
        Assert.Empty(rows[5]["face_filtered_u_px"]);
        Assert.Empty(rows[5]["box_x"]);
        Assert.All(rows, r => Assert.Equal("0", r["aim_request_permitted"]));
    }

    private static async Task<List<SessionFrame>> Run(string video, string output, FaceAimSettings? settings = null)
    {
        var frames = new List<SessionFrame>();
        var options = new SessionOptions(InputSource.Video, video, 0, 7, new(), output,
            DetectFaces: true, FaceAim: settings ?? new FaceAimSettings());
        await TrackingSession.RunAsync(options, f => { frames.Add(f); return Task.CompletedTask; },
            () => false, CancellationToken.None);
        return frames;
    }

    private static Dictionary<string, string>[] ReadRows(string output)
    {
        var lines = File.ReadAllLines(Path.Combine(output, "telemetry.csv"));
        return lines.Skip(1).Select(line => lines[0].Split(',').Zip(line.Split(','))
            .ToDictionary(p => p.First, p => p.Second)).ToArray();
    }

    private static string NewDirectory()
    {
        var folder = Path.Combine(Path.GetTempPath(), "UAVTrack-face-aim", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}

public sealed class FaceAimSampleFactAttribute : FactAttribute
{
    public FaceAimSampleFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("UAVTRACK_FACE_TEST_IMAGE")))
            Skip = "Set UAVTRACK_FACE_TEST_IMAGE to a public face sample for recorded-frame integration verification.";
    }
}
