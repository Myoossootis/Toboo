using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Toboo.Core;
using Toboo.Desktop.Services;

namespace Toboo.Desktop.ViewModels;

public sealed record ExpressionScoreRow(string Name, double Score);

public sealed partial class MainViewModel
{
    [ObservableProperty] private bool expressionEnabled = true;
    [ObservableProperty] private string expressionLabel = "表情线索 · 等待人脸";
    [ObservableProperty] private string expressionEmoticon = "(?)";
    [ObservableProperty] private string expressionDetail = "本地识别可见表情，不代表真实心理情绪。";
    [ObservableProperty] private string expressionTiming = "尚未推理";
    [ObservableProperty] private IReadOnlyList<ExpressionScoreRow> expressionScores = Array.Empty<ExpressionScoreRow>();
    private readonly Stopwatch expressionClock = Stopwatch.StartNew();
    private DispatcherTimer? expressionTimer;
    private TimeSpan? expressionExpires;
    private ExpressionEstimate? latestExpression;
    internal ChatExpression? CaptureExpressionCue()
    {
        if (!IsRunning || !IsCameraSource || !IsFace || !ExpressionEnabled || latestExpression is not { Reason: "stable" } cue
            || cue.Label == FacialExpression.Unknown || !double.IsFinite(cue.Score) || cue.Score is < 0.6 or > 1
            || expressionExpires is not { } expires || expressionClock.Elapsed >= expires) return null;
        var age = Math.Clamp(500 - (expires - expressionClock.Elapsed).TotalMilliseconds, 0, 500);
        return new(cue.Label.ToString().ToLowerInvariant(), cue.Score, age, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - age);
    }

    private void InitializeExpressionPresentation()
    {
        expressionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        expressionTimer.Tick += ExpireExpression;
        expressionTimer.Start();
    }
    partial void OnExpressionEnabledChanged(bool value) => ClearExpression(value ? "等待人脸" : "已关闭");
    private void ExpireExpression(object? sender, EventArgs args)
    {
        if (IsRunning && SourceIndex != 1 && expressionExpires is { } until && expressionClock.Elapsed >= until)
            ClearExpression("数据过期");
    }
    private void ClearExpression(string reason)
    {
        ExpressionLabel = "表情线索 · " + reason; ExpressionEmoticon = "(?)";
        ExpressionDetail = "本地识别可见表情，不代表真实心理情绪。";
        ExpressionScores = []; ExpressionTiming = "无有效推理结果"; expressionExpires = null;
        latestExpression = null;
    }
    private void PresentExpression(ExpressionAnalysis? analysis, TimeSpan now)
    {
        if (analysis is null) { ClearExpression(ExpressionEnabled ? "等待人脸" : "已关闭"); return; }
        var result = analysis.Estimate;
        latestExpression = result;
        ExpressionLabel = "表情线索 · " + result.DisplayName;
        ExpressionEmoticon = result.Emoticon;
        var description = result.Reason switch {
            "stable" => "连续确认", "single_image" => "单张图像，未连续确认", "confirming" => "连续确认中",
            "uncertain" => "分数或类别差距不足", "no_selected_face" => "没有已确认的人脸目标",
            "stale" => "数据已过期", "face_too_small" => "人脸太小，请靠近相机",
            "face_clipped" => "人脸超出画面", "pose_or_occlusion" => "姿态或遮挡不适合判断",
            "lighting_or_blur" => "亮度或清晰度不足", "landmarks_invalid" => "人脸关键点不可用",
            "model_unavailable" => "表情模型缺失或损坏；视觉跟随仍可用", "inference_error" => "表情推理失败",
            _ => "等待有效数据" };
        ExpressionDetail = result.Scores.Length == 7
            ? $"{description} · 模型分数 {result.Score:0.00} · 首二类差 {result.Margin:0.00}（未校准）" : description;
        ExpressionTiming = $"表情推理 {analysis.InferenceMilliseconds:0.0}ms · 最高5次/秒";
        ExpressionScores = result.Scores.Select((score, index) => new ExpressionScoreRow(
            new ExpressionEstimate((FacialExpression)index, 0, 0, "", null, []).DisplayName, score)).ToArray();
        expressionExpires = result.CapturedAt is { } captured
            ? expressionClock.Elapsed + TimeSpan.FromSeconds(0.5) - (now - captured) : null;
    }
}
