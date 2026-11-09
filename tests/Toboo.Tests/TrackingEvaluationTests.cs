using Toboo.Core;
using Xunit;

namespace Toboo.Tests;

public sealed class TrackingEvaluationTests
{
    private static readonly EvaluationBox Box = new(10,20,30,40);

    [Fact]
    public void PerfectTrackHasUnitPrecisionRecallAndIoU()
    {
        var m = TrackingEvaluation.Evaluate([new(0,Box,Box), new(1,Box,Box)], 30);
        Assert.Equal(1, m.Precision); Assert.Equal(1, m.Recall); Assert.Equal(1, m.MeanIoUOnVisibleFrames);
        Assert.Equal(0, m.MeanCenterErrorOnReportedVisibleFramesPx);
        Assert.Equal(0, m.LostEpisodes); Assert.Equal(0, m.InitialAcquisitionFrame);
    }

    [Fact]
    public void WrongLocationCountsAsFalsePositiveAndFalseNegativeAndMissingIoUIsZero()
    {
        var m = TrackingEvaluation.Evaluate([new(0,Box,Box), new(1,Box,new(100,100,30,40)), new(2,Box,null)], 30);
        Assert.Equal(1, m.TruePositives); Assert.Equal(1, m.FalsePositives); Assert.Equal(2, m.FalseNegatives);
        Assert.Equal(.5, m.Precision); Assert.Equal(1.0 / 3, m.Recall);
        Assert.Equal(1.0 / 3, m.MeanIoUOnVisibleFrames);
        Assert.Equal(2, m.LongestMissingRunFrames); Assert.Equal(1, m.UnrecoveredEpisodes);
    }

    [Fact]
    public void ConsecutiveFalseAlarmFramesAreOneEventAndNoPositivesHaveUndefinedRecall()
    {
        var m = TrackingEvaluation.Evaluate([new(0,null,Box),new(1,null,Box),new(2,null,null),new(3,null,Box)], 2);
        Assert.Equal(3, m.FalsePositiveFramesOnAbsentTarget); Assert.Equal(2, m.FalseAlarmEvents);
        Assert.Equal(60, m.FalseAlarmEventsPerMinute); Assert.Null(m.Recall);
        Assert.Equal(0, m.Precision); Assert.Null(m.MeanIoUOnVisibleFrames);
        var empty = TrackingEvaluation.Evaluate([new(0,null,null)], 30);
        Assert.Null(empty.Precision); Assert.Null(empty.Recall);
    }

    [Fact]
    public void ReacquisitionUsesReferenceMatchesNotTrackerSuccessFlags()
    {
        var m = TrackingEvaluation.Evaluate([new(0,Box,Box),new(1,Box,null),new(2,Box,null),new(3,Box,Box)], 10);
        Assert.Equal(1, m.LostEpisodes); Assert.Equal(0, m.UnrecoveredEpisodes);
        Assert.Equal(.2, m.MeanReacquisitionSeconds); Assert.Equal(2, m.LongestMissingRunFrames);
    }

    [Fact]
    public void InvalidTimingBoxesAndFrameAlignmentAreRejected()
    {
        Assert.Throws<ArgumentException>(() => TrackingEvaluation.Evaluate([],30));
        Assert.Throws<ArgumentException>(() => TrackingEvaluation.Evaluate([new(1,Box,Box)],30));
        Assert.Throws<ArgumentException>(() => TrackingEvaluation.Evaluate([new(0,Box,Box)],0));
        Assert.Throws<ArgumentException>(() => TrackingEvaluation.Evaluate([new(0,Box,Box)],30,0));
        Assert.Throws<ArgumentException>(() => TrackingEvaluation.Evaluate([new(0,new(double.NaN,0,1,1),Box)],30));
    }
}
