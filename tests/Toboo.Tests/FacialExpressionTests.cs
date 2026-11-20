using System.Security.Cryptography;
using OpenCvSharp;
using Toboo.Core;
using Toboo.Vision;
using Xunit;

namespace Toboo.Tests;

public sealed class FacialExpressionTests
{
    [Fact]
    public void ModelMatchesPinnedWeightsAndRunsOnNativeOpenCv()
    {
        Assert.Equal(FacialExpressionRecognizer.ModelSha256,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(FacialExpressionRecognizer.DefaultModelPath))).ToLowerInvariant());
        using var model = new FacialExpressionRecognizer();
        using var image = new Mat(112, 112, MatType.CV_8UC3, new Scalar(10, 60, 180));
        var scores = model.PredictAligned(image);
        Assert.Equal(7, scores.Length); Assert.Equal(1, scores.Sum(), 6);
        Assert.All(scores, s => Assert.InRange(s, 0, 1));
        // Cross-language fixture from the pinned upstream Python preprocessor on constant BGR(10,60,180).
        double[] reference = [0.01298389, 0.00065828, 0.00159395, 0.01930408, 0.91325051, 0.01898624, 0.03322306];
        for (var i = 0; i < 7; i++) Assert.InRange(Math.Abs(scores[i] - reference[i]), 0, 0.0001);
    }
    [Fact]
    public void SimilarityAlignmentUsesAllFivePointsAndDoesNotMirrorTheImage()
    {
        using var image = new Mat(112, 112, MatType.CV_8UC3, Scalar.Black);
        Cv2.Rectangle(image, new Rect(2, 2, 30, 30), Scalar.White, -1);
        using var original = image.Clone();
        using var aligned = FacialExpressionRecognizer.Align(image, FacialExpressionRecognizer.Reference);
        Assert.Equal(0, Cv2.Norm(image, aligned));
        Assert.Equal(0, Cv2.Norm(image, original));
        Assert.Throws<ArgumentException>(() => FacialExpressionRecognizer.Align(image, Enumerable.Repeat(new Point2f(0, 0), 5).ToArray()));
    }
    [Fact]
    public void AlignmentMatchesUpstreamInverseFitForImperfectLandmarks()
    {
        Point2f[] points = [new(60, 70), new(112, 68), new(88, 98), new(62, 130), new(108, 126)];
        // Frozen output from the pinned Zoo FaceAlignment, not a second implementation of our solver.
        double[,] expected = { { 0.6774774977016951, -0.005158049790514695, -1.7293527029591813 },
            { 0.005158049790514693, 0.6774774977016952, 4.793401944168892 } };
        using var actual = FacialExpressionRecognizer.AlignmentTransform(points);
        for (var row = 0; row < 2; row++)
            for (var column = 0; column < 3; column++)
                Assert.InRange(Math.Abs(expected[row, column] - actual.At<double>(row, column)), 0, 0.000000001);
        points[0] = new(float.NaN, 70);
        Assert.Throws<ArgumentException>(() => FacialExpressionRecognizer.AlignmentTransform(points));
    }
    [Fact]
    public void SessionKeepsRawScoresSeparateAndClearsThemWhenTheFaceIsLost()
    {
        using var image = new Mat(160, 160, MatType.CV_8UC3, Scalar.All(80));
        for (var y = 0; y < 160; y += 8)
            for (var x = 0; x < 160; x += 8)
                if ((x / 8 + y / 8) % 2 == 0) Cv2.Rectangle(image, new Rect(x, y, 8, 8), Scalar.All(180), -1);
        var box = new Rect(10, 10, 110, 110);
        var faces = new FaceDetectionResult(new(0, TimeSpan.Zero, 160, 160, 65, 65), [box],
            [new(box, FacialExpressionRecognizer.Reference, 1)]);
        using var session = new FaceExpressionSession();
        var inferred = session.Analyze(image, faces, null, TimeSpan.Zero, singleImage: true);
        Assert.True(inferred.WasInferred);
        Assert.NotNull(inferred.RawScores);
        Assert.Equal(7, inferred.RawScores.Length);
        Assert.Equal(1, inferred.RawScores.Sum(), 6);
        Assert.NotSame(inferred.RawScores, inferred.Estimate.Scores);
        var lost = session.Analyze(image, new(new(1, TimeSpan.FromSeconds(0.1), 160, 160, null, null), []), null, TimeSpan.FromSeconds(0.1));
        Assert.Null(lost.RawScores);
        Assert.Equal(FacialExpression.Unknown, lost.Estimate.Label);
    }
    [Fact]
    public void SmallFacesAndDarkImagesAreRejectedInsteadOfInventingAnExpression()
    {
        using var model = new FacialExpressionRecognizer();
        using var image = new Mat(160, 160, MatType.CV_8UC3, Scalar.Black);
        Assert.Equal("face_too_small", model.Infer(image, new(new(10, 10, 40, 40), [], 1)).Reason);
        Assert.Equal("lighting_or_blur", model.Infer(image, new(new(10, 10, 110, 110), FacialExpressionRecognizer.Reference, 1)).Reason);
        Assert.Equal(0, Cv2.CountNonZero(image.Reshape(1)));
    }
    [Fact]
    public void MissingOrTamperedModelFailsClearlyAndTheOptionalSessionDegrades()
    {
        Assert.Throws<FileNotFoundException>(() => new FacialExpressionRecognizer("missing-expression.onnx"));
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "not the pinned model");
            Assert.Throws<InvalidDataException>(() => new FacialExpressionRecognizer(path));
            using var session = new FaceExpressionSession(path);
            using var blank = new Mat(112, 112, MatType.CV_8UC3, Scalar.Black);
            var result = session.Analyze(blank, new(new(0, TimeSpan.Zero, 112, 112, null, null), []), null, TimeSpan.Zero);
            Assert.Equal("model_unavailable", result.Estimate.Reason); Assert.Equal(FacialExpression.Unknown, result.Estimate.Label);
        }
        finally { File.Delete(path); }
    }
}
