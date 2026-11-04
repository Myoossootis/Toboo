using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using PanTiltTracker.Core;

namespace PanTiltTracker.App;

internal static class DroneEvaluation
{
    public static void Run(string referencePath, string telemetryPath, double fps, string directory)
    {
        var reference = Read(referencePath); var predicted = Read(telemetryPath);
        if (reference.Count != predicted.Count || reference.Count == 0)
            throw new InvalidDataException("Reference and telemetry must contain the same nonzero number of frames. No missing rows are filled.");
        var frames = reference.Zip(predicted).Select(pair =>
        {
            var (r, p) = pair;
            var number = long.Parse(r["frame"], CultureInfo.InvariantCulture);
            if (p["frame"] != r["frame"]) throw new InvalidDataException("Reference/telemetry frame numbers differ.");
            return new EvaluationFrame(number, Box(r, "present", ["x", "y", "width", "height"]),
                Box(p, "detected", ["box_x", "box_y", "box_width", "box_height"]));
        }).ToArray();
        var metrics = TrackingEvaluation.Evaluate(frames, fps);
        var times = predicted.Select(row => double.Parse(row["algorithm_ms"], CultureInfo.InvariantCulture)).Order().ToArray();
        if (times.Any(t => !double.IsFinite(t) || t < 0)) throw new InvalidDataException("Invalid algorithm timing.");
        var report = new { metrics, meanAlgorithmMs = times.Average(), p95AlgorithmMs = times[(int)Math.Ceiling(times.Length * .95) - 1],
            referenceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(referencePath))).ToLowerInvariant(),
            telemetrySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(telemetryPath))).ToLowerInvariant(),
            evaluation = "selected target only, IoU >= 0.5; localization errors count as both FP and FN; no multi-object AP",
            timing = "algorithm only; excludes decoding/display/USB latency", hardwareOutput = "disabled" };
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(directory, "evaluation.json"), json); Console.WriteLine(json);
    }

    private static EvaluationBox? Box(Dictionary<string, string> row, string presence, string[] names)
    {
        if (row[presence] == "0")
        {
            if (names.Any(n => row[n].Length > 0)) throw new InvalidDataException("Absent targets must have empty box fields.");
            return null;
        }
        if (row[presence] != "1") throw new InvalidDataException("Presence must be 0 or 1.");
        var v = names.Select(n => double.Parse(row[n], CultureInfo.InvariantCulture)).ToArray();
        return new(v[0], v[1], v[2], v[3]);
    }

    private static List<Dictionary<string, string>> Read(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length < 2) throw new InvalidDataException("CSV is empty.");
        var header = lines[0].TrimStart('\uFEFF').Split(',');
        if (header.Distinct().Count() != header.Length) throw new InvalidDataException("Duplicate CSV columns.");
        return lines.Skip(1).Select(line =>
        {
            var fields = line.Split(',');
            if (fields.Length != header.Length) throw new InvalidDataException("Expected numeric CSV rows with the declared field count.");
            return header.Zip(fields).ToDictionary(p => p.First, p => p.Second);
        }).ToList();
    }
}
