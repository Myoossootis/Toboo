using System.Text.Json;
using OpenCvSharp;
using Toboo.Core;
using Toboo.Vision;

namespace Toboo.Desktop.Services;

internal sealed class ExpressionSession : IDisposable
{
    private readonly FaceExpressionSession pipeline = new();
    private readonly StreamWriter log;
    private readonly bool singleImage;
    public ExpressionSession(string directory, bool singleImage)
    {
        this.singleImage = singleImage;
        try { log = new StreamWriter(Path.Combine(directory, "expression.jsonl")) { AutoFlush = true }; }
        catch { pipeline.Dispose(); throw; }
    }
    public ExpressionAnalysis Analyze(Mat image, FaceDetectionResult faces, FaceAimResult? aim, TimeSpan now, bool realTime)
    {
        var analysis = pipeline.Analyze(image, faces, aim, now, singleImage, realTime);
        log.WriteLine(JsonSerializer.Serialize(new {
            frame = faces.Observation.FrameNumber, capturedSeconds = faces.Observation.CapturedAt.TotalSeconds,
            nowSeconds = now.TotalSeconds, label = analysis.Estimate.Label.ToString(),
            analysis.Estimate.Score, analysis.Estimate.Margin, analysis.Estimate.Reason,
            sampleCapturedSeconds = analysis.Estimate.CapturedAt?.TotalSeconds,
            modelScores = analysis.Estimate.Scores, analysis.InferenceMilliseconds, analysis.Brightness, analysis.Sharpness,
            analysis.TargetEpoch, analysis.WasInferred, pipeline.Diagnostic,
            scoreMeaning = "uncalibrated model score; visible expression, not psychological state" }));
        return analysis;
    }
    public void Dispose() { pipeline.Dispose(); log.Dispose(); }
}
