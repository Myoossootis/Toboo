using System.Text.Json;
using PanTiltTracker.Core;
using PanTiltTracker.Shared;

namespace PanTiltTracker.App;

internal static class SensorCommands
{
    public static int Run(string[] args)
    {
        if (args.Length < 1 || args[0] is not ("demo" or "replay")) throw new ArgumentException("sensors demo|replay [--input file.jsonl] --out directory");
        var values = new Dictionary<string, string>();
        for (var i = 1; i < args.Length; i += 2)
            if (args[i] is not ("--out" or "--input") || i + 1 >= args.Length || !values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Invalid sensor command options.");
        var synthetic = args[0] == "demo";
        if (!synthetic && !values.ContainsKey("--input")) throw new ArgumentException("replay requires --input.");
        var directory = Path.GetFullPath(values.GetValueOrDefault("--out", "artifacts/sensors-" + args[0]));
        var output = Path.Combine(directory, "events.jsonl");
        var input = synthetic ? null : Path.GetFullPath(values["--input"]);
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Input cannot be overwritten.");
        var events = synthetic ? SensorExperiment.Demo() : SensorExperiment.Read(input!);
        Directory.CreateDirectory(directory);
        using var raw = new StreamWriter(output);
        using var results = new StreamWriter(Path.Combine(directory, "fusion.jsonl"));
        var telemetry = new SensorTelemetry();
        var fusion = new CompanionFusionPreview();
        var states = new HashSet<CompanionState>();
        var count = 0;
        foreach (var item in events)
        {
            var state = SensorExperiment.Apply(item, telemetry, fusion);
            raw.WriteLine(JsonSerializer.Serialize(item));
            results.WriteLine(JsonSerializer.Serialize(new { item.AtMilliseconds, Preview = state }));
            states.Add(state.State); count++;
        }
        if (count == 0) throw new InvalidDataException("Empty telemetry replay.");
        var summary = new { source = synthetic ? "synthetic" : "recorded-replay", hardwareOutput = "disabled",
            thresholds = "preliminary-unvalidated", records = count, telemetry.Rejected, telemetry.MissingPackets,
            states = states.Select(s => s.ToString()).ToArray() };
        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(directory, "run.json"), json);
        Console.WriteLine(json);
        return 0;
    }
}
