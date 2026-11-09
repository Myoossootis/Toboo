using System.Text.Json;
using Toboo.Core;

namespace Toboo.Runtime;

public sealed record SensorEvent(double AtMilliseconds, string? Line = null, bool FaceConfirmed = false, bool Resume = false, bool Disconnected = false);

/// <summary>Portable, deterministic test inputs. No serial or actuator output.</summary>
public static class SensorExperiment
{
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    public static IEnumerable<SensorEvent> Demo()
    {
        yield return new(0, SensorPacket.Hello);
        uint sequence = 0;
        for (uint ms = 0; ms <= 18000; ms += 50)
        {
            // Distinct faults: close object, tilted base, missing packets, no echo, then recovery.
            if (ms is < 11000 or >= 12000)
            {
                var tilted = ms is >= 8000 and < 9000;
                yield return new(ms, new SensorPacket(sequence++, ms, SensorChannel.Acceleration,
                    true, tilted ? 800 : 0, 0, tilted ? 600 : 1000).Encode());
                if (ms % 100 == 0)
                    yield return new(ms, new SensorPacket(sequence++, ms, SensorChannel.Distance,
                        ms is < 13000 or >= 13500, ms is >= 13000 and < 13500 ? 0 : ms is >= 4000 and < 4500 ? 100 : 500).Encode());
            }
            yield return new(ms, FaceConfirmed: ms is >= 500 and < 16000,
                Resume: ms is 2200 or 6700 or 15750);
        }
    }

    public static IEnumerable<SensorEvent> Read(string path)
    {
        var previous = 0.0;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length > 1024) throw new InvalidDataException("Telemetry record is too long.");
            var item = JsonSerializer.Deserialize<SensorEvent>(line, JsonOptions) ?? throw new InvalidDataException("Empty telemetry record.");
            if (!double.IsFinite(item.AtMilliseconds) || item.AtMilliseconds < previous || item.AtMilliseconds > TimeSpan.MaxValue.TotalMilliseconds / 2)
                throw new InvalidDataException("Replay timestamps must be finite, nonnegative and monotonic.");
            previous = item.AtMilliseconds;
            yield return item;
        }
    }

    public static FusionPreview Apply(SensorEvent item, SensorTelemetry telemetry, CompanionFusionPreview fusion)
    {
        var time = TimeSpan.FromMilliseconds(item.AtMilliseconds);
        if (item.Disconnected) telemetry.Disconnect();
        if (item.Line is { } line) telemetry.Accept(line, time);
        fusion.ObserveFace(item.FaceConfirmed, time);
        return fusion.Step(telemetry, time, item.Resume);
    }
}
