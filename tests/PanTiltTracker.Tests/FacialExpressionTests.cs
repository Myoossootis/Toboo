using System.Security.Cryptography;
using OpenCvSharp;
using PanTiltTracker.Core;
using PanTiltTracker.Vision;
using Xunit;

namespace PanTiltTracker.Tests;

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
