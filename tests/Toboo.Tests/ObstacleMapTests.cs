using Toboo.Core;
using Xunit;

namespace Toboo.Tests;

public sealed class ObstacleMapTests
{
    [Fact]
    public void TwoDifferentPosesPlaceTheSameObstacleInTheSameWorldRegion()
    {
        var map = new ObstacleMap(new(0, 0, 0.05), 350);
        map.ObservePose(Ms(0), At(1.55));
        map.ObservePose(Ms(50), At(1.55));
        map.ObserveRange(Ms(20), 975);
        Assert.Equal(1, map.MappedEchoes);
        Assert.All(map.Hits, hit => Assert.InRange(hit.Z, 5, 6));
        var central = new MapCell(0, 5);
        Assert.Contains(central, map.Hits);

        map.ObservePose(Ms(200), At(1.05));
        map.ObserveRange(Ms(220), 475);
        map.ObservePose(Ms(250), At(1.05));
        Assert.Equal(2, map.MappedEchoes);
        Assert.All(map.Hits, hit => Assert.InRange(hit.Z, 5, 6));
        Assert.Equal(2, map.Path.Count);
        Assert.Equal(ObstacleAdvice.NoNearEcho, map.Advice(Ms(255)));
    }

    [Fact]
    public void NullStaleOrUnpairedObservationsDoNotDrawGhostObstaclesOrJoinTrajectories()
    {
        var map = new ObstacleMap(default, 350);
        map.ObservePose(Ms(0), At(1.55));
        map.ObserveRange(Ms(20), 500);
        map.ObservePose(Ms(40), null);
        map.ObservePose(Ms(95), At(1.45));
        map.ObserveRange(Ms(105), 500);
        map.ObservePose(Ms(400), At(1.35));
        Assert.Equal(0, map.MappedEchoes);
        Assert.All(map.Path, pose => Assert.True(pose.StartsSegment));
        Assert.Equal(ObstacleAdvice.Unknown, map.Advice(Ms(600)));
        map.ObserveRange(Ms(610), 210);
        Assert.Equal(ObstacleAdvice.Stop, map.Advice(Ms(615)));
        map.ObserveRange(Ms(620), null);
        Assert.Equal(ObstacleAdvice.Unknown, map.Advice(Ms(622)));
        map.ObservePose(Ms(630), At(1.31));
        Assert.Empty(map.Hits);
    }

    [Fact]
    public void NearWarningRequiresTwoDistinctClearReadingsBeforeClearing()
    {
        var map = new ObstacleMap(default, 350);
        map.ObservePose(Ms(0), At(1));
        map.ObserveRange(Ms(0), 300);
        map.ObservePose(Ms(100), At(1));
        map.ObserveRange(Ms(100), 500);
        map.ObserveRange(Ms(100), 500);
        Assert.Equal(ObstacleAdvice.Stop, map.Advice(Ms(100)));
        map.ObservePose(Ms(200), At(1));
        map.ObserveRange(Ms(200), 500);
        Assert.Equal(ObstacleAdvice.NoNearEcho, map.Advice(Ms(200)));
    }

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);
    private static MapPose At(double z) => new(new(0, z), new(1, 0), default, new(0, -1));
}
