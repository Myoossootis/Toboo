using Toboo.Vision;
using Xunit;

namespace Toboo.Tests;

public sealed class RelativeDepthPreviewTests
{
    [Fact]
    public void FlatAndNonfiniteValuesHaveBoundedDisplayDepth()
    {
        Assert.Equal([0.5f, 0.5f, 0.5f], RelativeDepthPreview.Normalize([4, float.NaN, 4]));
        Assert.Throws<InvalidDataException>(() => RelativeDepthPreview.Normalize([float.NaN]));
        var normalized = RelativeDepthPreview.Normalize(Enumerable.Range(0, 100).Select(i => (float)i).ToArray());
        Assert.All(normalized, p => Assert.InRange(p, 0, 1));
        Assert.True(normalized[10] < normalized[90]);
    }

    [Fact]
    public void SyntheticSceneHasFiniteColoredGeometryWithPerspectiveAndRelief()
    {
        var frame = RelativeDepthPreview.CreateDemo();
        Assert.NotEmpty(frame.ColorJpeg);
        Assert.NotEmpty(frame.DepthPng);
        Assert.InRange(frame.Points.Length, 8000, 20480);
        Assert.All(frame.Points, p =>
        {
            Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z));
            Assert.InRange(p.Z, .74f, 2.86f);
        });
        Assert.True(frame.Points.Max(p => p.Z) - frame.Points.Min(p => p.Z) > 1);
    }
}
