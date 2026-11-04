using OpenCvSharp;
using PanTiltTracker.Core;
using PanTiltTracker.Vision;
using Xunit;

namespace PanTiltTracker.Tests;

public sealed class DroneTrackingTests
{
    private static readonly Rect Region = new(120,100,96,64);

    [Fact]
    public void AutomaticInitializationThenFreshVisualUpdatesDoNotReuseDetectionScoresOrMutateInput()
    {
        var calls = 0;
        using var tracker = new DroneTargetTracker((image,number,time) =>
        {
            calls++;
            return Detection(image,number,time,new DroneDetection(Region,.9f));
        });
        using var scene = Scene();
        using var before = scene.Clone();
        var first = tracker.Process(scene,0,TimeSpan.Zero);
        var next = tracker.Process(scene,1,TimeSpan.FromSeconds(.03));
        Assert.Equal("drone_acquired", first.Status); Assert.Equal("detector",first.BoxSource);
        Assert.Equal(.9f,first.DetectionScore); Assert.Equal(1,first.TrackEpoch);
        Assert.True(next.Observation.HasTarget); Assert.Equal("drone_tracked",next.Status);
        Assert.Equal("csrt",next.BoxSource); Assert.Null(next.DetectionScore); Assert.Null(next.CandidateCount);
        Assert.Equal(1,calls); Assert.Equal(1,next.Observation.FrameNumber);
        Assert.Equal(TimeSpan.FromSeconds(.03),next.Observation.CapturedAt);
        Assert.Equal(0,Cv2.Norm(scene,before));
    }

    [Fact]
    public void ExpiredClassificationDropsEvenASuccessfulTrackerAndReacquiresWithANewEpoch()
    {
        using var tracker = new DroneTargetTracker((image,number,time) => number is 0 or 40
            ? Detection(image,number,time,new DroneDetection(Region,.9f)) : Detection(image,number,time));
        using var scene = Scene();
        var first = tracker.Process(scene,0,TimeSpan.Zero);
        DroneTrackingResult? result = null;
        for (var n=1;n<=36;n++) result = tracker.Process(scene,n,TimeSpan.FromSeconds(n*.03));
        Assert.NotNull(result); Assert.False(result.Observation.HasTarget); Assert.Null(result.Box);
        Assert.Equal("drone_searching",result.Status); Assert.Null(result.DetectionScore);
        var controller = new PanTiltController(new());
        var previous = controller.Step(first.Observation,TimeSpan.Zero);
        var held = controller.Step(result.Observation,result.Observation.CapturedAt);
        Assert.Equal(previous.PanDegrees,held.PanDegrees); Assert.Equal(previous.TiltDegrees,held.TiltDegrees);
        for(var n=37;n<=40;n++) result=tracker.Process(scene,n,TimeSpan.FromSeconds(n*.03));
        Assert.True(result.Observation.HasTarget); Assert.Equal("drone_acquired",result.Status);
        Assert.Equal(2,result.TrackEpoch);
    }

    [Fact]
    public void HigherScoreDistantCandidateCannotSwitchAnActiveLock()
    {
        var distant = new Rect(400,280,96,64);
        using var tracker = new DroneTargetTracker((image,number,time) => number==0
            ? Detection(image,number,time,new DroneDetection(Region,.7f))
            : Detection(image,number,time,new DroneDetection(distant,.99f),new DroneDetection(Region,.6f)));
        using var scene = Scene();
        tracker.Process(scene,0,TimeSpan.Zero);
        DroneTrackingResult? result=null;
        for(var n=1;n<=7;n++) result=tracker.Process(scene,n,TimeSpan.FromSeconds(n*.03));
        Assert.NotNull(result); Assert.Equal("drone_verified",result.Status);
        Assert.Equal(Region,result.Box); Assert.Equal(.6f,result.DetectionScore); Assert.Equal(1,result.TrackEpoch);
    }

    [Fact]
    public void BlankSceneLossLargeGapAndBackwardFramesDoNotReturnOldCoordinates()
    {
        using var tracker = new DroneTargetTracker((image,number,time) => number==0
            ? Detection(image,number,time,new DroneDetection(Region,.9f)) : Detection(image,number,time));
        using var scene = Scene();
        using var blank = new Mat(480,640,MatType.CV_8UC3,Scalar.Black);
        tracker.Process(scene,0,TimeSpan.Zero);
        Assert.False(tracker.Process(blank,1,TimeSpan.FromSeconds(.03)).Observation.HasTarget);
        Assert.False(tracker.Process(scene,2,TimeSpan.FromSeconds(1)).Observation.HasTarget);
        Assert.Throws<ArgumentException>(()=>tracker.Process(scene,2,TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(()=>tracker.Process(scene,3,TimeSpan.FromSeconds(.1)));
        tracker.Dispose();
        Assert.Throws<ObjectDisposedException>(()=>tracker.Process(scene,3,TimeSpan.FromSeconds(1.1)));
        Assert.False(scene.Empty());
    }

    private static DroneDetectionResult Detection(Mat image,long n,TimeSpan t,params DroneDetection[] boxes) =>
        new(new(n,t,image.Width,image.Height,boxes.Length>0?boxes[0].Box.X+boxes[0].Box.Width/2.0:null,
            boxes.Length>0?boxes[0].Box.Y+boxes[0].Box.Height/2.0:null),boxes);

    private static Mat Scene()
    {
        var image=new Mat(480,640,MatType.CV_8UC3,Scalar.Black);
        var random=new Random(84);
        for(var y=Region.Y;y<Region.Bottom;y+=8)
            for(var x=Region.X;x<Region.Right;x+=8)
                Cv2.Rectangle(image,new Rect(x,y,8,8),new Scalar(random.Next(60,256),random.Next(60,256),random.Next(60,256)),-1);
        return image;
    }
}
