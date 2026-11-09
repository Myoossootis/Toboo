using PanTiltTracker.Core;
using Xunit;

namespace PanTiltTracker.Tests;

public sealed class ExpressionTrackerTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);
    private static double[] Happy => [0.02, 0.01, 0.01, 0.9, 0.03, 0.01, 0.02];
    private static ExpressionTracker Stable()
    {
        var tracker = new ExpressionTracker();
        for (var i = 0; i < 4; i++) tracker.Update(Happy, i, 1, S(i * 0.2), S(i * 0.2));
        Assert.Equal(FacialExpression.Happy, tracker.Current.Label); return tracker;
    }
    [Fact]
    public void ConfirmationUsesElapsedSampleTimeRatherThanFrameCount()
    {
        var tracker = new ExpressionTracker();
        for (var i = 0; i < 30; i++)
            Assert.Equal(FacialExpression.Unknown, tracker.Update(Happy, i, 1, S(i * 0.01), S(i * 0.01)).Label);
        Assert.Equal(FacialExpression.Happy, tracker.Update(Happy, 30, 1, S(0.4), S(0.4)).Label);
    }
    [Fact]
    public void LossAndTargetChangeDoNotTransferOldEvidence()
    {
        var tracker = Stable();
        Assert.Equal(FacialExpression.Unknown, tracker.Clear("no_selected_face").Label);
        Assert.Equal(FacialExpression.Unknown, tracker.Update(Happy, 4, 1, S(0.8), S(0.8)).Label);
        Assert.Equal(FacialExpression.Happy, tracker.Update(Happy, 5, 1, S(1.2), S(1.2)).Label);
        Assert.Equal(FacialExpression.Unknown, tracker.Update(Happy, 6, 2, S(1.4), S(1.4)).Label);
    }
    [Fact]
    public void LongGapAndStaleSamplesRequireNewConfirmation()
    {
        var tracker = Stable();
        Assert.Equal(FacialExpression.Unknown, tracker.Update(Happy, 4, 1, S(3), S(3)).Label);
        Assert.Equal("stale", tracker.At(S(3.51)).Reason);
        Assert.Empty(tracker.Current.Scores);
        Assert.Equal("stale", tracker.Update(Happy, 5, 1, S(3.8), S(4.4)).Reason);
    }
    [Fact]
    public void AmbiguousFreshScoresImmediatelySuppressAnOldConfidentLabel()
    {
        var tracker = Stable();
        double[] ambiguous = [0.01, 0.01, 0.01, 0.48, 0.47, 0.01, 0.01];
        Assert.Equal("uncertain", tracker.Update(ambiguous, 4, 1, S(0.61), S(0.61)).Reason);
        Assert.Equal(FacialExpression.Unknown, tracker.Current.Label);
    }
    [Fact]
    public void AConflictingNewWinnerDoesNotContinueShowingTheOldSmile()
    {
        var tracker = Stable();
        double[] sad = [0.02, 0.01, 0.01, 0.01, 0.03, 0.9, 0.02];
        Assert.Equal(FacialExpression.Unknown, tracker.Update(sad, 4, 1, S(0.61), S(0.61)).Label);
        for (var i = 5; i < 12; i++) tracker.Update(sad, i, 1, S(i * 0.2), S(i * 0.2));
        Assert.Equal(FacialExpression.Sad, tracker.Current.Label);
    }
    [Fact]
    public void RepeatedFutureAndInvalidScoresCannotKeepAStableLabel()
    {
        var tracker = Stable();
        Assert.Equal("repeated_frame", tracker.Update(Happy, 3, 1, S(0.7), S(0.7)).Reason);
        Assert.Equal("stale", tracker.Update(Happy, 4, 1, S(1), S(0.8)).Reason);
        Assert.Equal("invalid_scores", tracker.Update([double.NaN, 0, 0, 1, 0, 0, 0], 5, 1, S(0.9), S(0.9)).Reason);
        Assert.Throws<ArgumentException>(() => tracker.At(S(0.5)));
    }
    [Fact]
    public void SoftmaxHandlesLargeLogitsAndDoesNotPretendTheyAreAlreadyProbabilities()
    {
        var probabilities = ExpressionTracker.Softmax([1000, 1000, -1000, -1000, -1000, -1000, -1000]);
        Assert.Equal(1, probabilities.Sum(), 10);
        Assert.Equal(0.5, probabilities[0], 10); Assert.Equal(0.5, probabilities[1], 10);
        Assert.Throws<ArgumentException>(() => ExpressionTracker.Softmax([float.NaN, 0, 0, 0, 0, 0, 0]));
    }
    [Fact]
    public void SingleImageIsExplicitlyLabelledWithoutTemporalConfirmation()
    {
        var result = new ExpressionTracker().Update(Happy, 0, 0, S(0), S(0), singleImage: true);
        Assert.Equal(FacialExpression.Happy, result.Label); Assert.Equal("single_image", result.Reason);
    }
}
