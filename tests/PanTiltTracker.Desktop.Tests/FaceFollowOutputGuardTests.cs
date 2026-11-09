using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PanTiltTracker.Core;
using PanTiltTracker.Desktop.Services;
using PanTiltTracker.Desktop.ViewModels;
using Xunit;

namespace PanTiltTracker.Desktop.Tests;

public sealed class FaceFollowOutputGuardTests
{
    private static FaceAimSettings Settings => new() {
        Camera=CameraAngleModel.FromFieldOfView(640,480,60,45),DirectionsConfirmed=true,
        PanDirection=-1,TiltDirection=-1,PanMinDegrees=30,PanMaxDegrees=175,TiltMinDegrees=50,TiltMaxDegrees=150,
        ConfirmationFrames=1 };

    [Fact]
    public async Task CommunicationFailureLatchesOutputOffButKeepsSubsequentFramesUsable()
    {
        var sends=0; var stops=0;
        var guard=new FaceFollowOutputGuard((_,_,_)=> {
            sends++; return Task.FromException<AimReference?>(new TimeoutException("missing STATUS response"));
        },()=> {stops++;return ValueTask.CompletedTask;},()=>new(61,56),()=>null);
        var controller=new FaceAimController(Settings,new(61,56));
        for(var number=0;number<30;number++)
        {
            var time=TimeSpan.FromSeconds(number/30.0);
            var aim=controller.Step([new(280,320,80,80)],number,time,640,480,time);
            var feedback=await guard.UpdateAsync(aim,TimeSpan.Zero,CancellationToken.None);
            var shown=FaceFollowOutputGuard.ApplyFeedback(controller,aim,feedback);
            Assert.True(shown.Observation.HasTarget);
            Assert.False(shown.RequestPermitted);
            Assert.Equal("servo_output_suspended",shown.Readiness);
            Assert.Equal(56,shown.Command.TiltDegrees);
        }
        Assert.Equal(1,sends); Assert.Equal(1,stops);
        Assert.True(guard.Suspended);
        Assert.Contains("画面继续",guard.Notice);
    }

    [Fact]
    public async Task BoundaryHoldDoesNotSuspendOutputAndMovingBackResumesNormally()
    {
        var sends=0;var stops=0;
        var reference=new AimReference(103,51);
        var guard=new FaceFollowOutputGuard((aim,_,_)=> {
            sends++; reference=new(aim.Command.PanDegrees,aim.Command.TiltDegrees);
            Assert.InRange(reference.TiltDegrees,50,150);
            return Task.FromResult<AimReference?>(null);
        },()=> {stops++;return ValueTask.CompletedTask;},()=>reference,()=>null);
        var controller=new FaceAimController(Settings,reference);
        FaceAimResult result=null!;
        for(var frame=0;frame<200;frame++)
        {
            var time=TimeSpan.FromSeconds(frame/30.0);
            result=controller.Step([new(440,320,80,80)],frame,time,640,480,time);
            await guard.UpdateAsync(result,TimeSpan.Zero,CancellationToken.None);
        }
        Assert.Equal(50,result.Command.TiltDegrees);
        Assert.True(result.Command.PanDegrees<103);
        Assert.Contains("50° 下限",MainViewModel.BoundaryMessage(result,Settings));
        for(var frame=200;frame<400;frame++)
        {
            var time=TimeSpan.FromSeconds(frame/30.0);
            result=controller.Step([new(440,60,80,80)],frame,time,640,480,time);
            await guard.UpdateAsync(result,TimeSpan.Zero,CancellationToken.None);
        }
        Assert.True(result.Command.TiltDegrees>50);
        Assert.Equal(400,sends);Assert.Equal(0,stops);Assert.False(guard.Suspended);
    }

    [Fact]
    public async Task UserCancellationIsNotMisreportedAsAConnectionFault()
    {
        using var cancellation=new CancellationTokenSource();
        var guard=new FaceFollowOutputGuard((_,_,token)=> {
            cancellation.Cancel();return Task.FromCanceled<AimReference?>(token);
        },()=>throw new InvalidOperationException("No fault cleanup expected"),()=>new(103,100),()=>null);
        var controller=new FaceAimController(Settings,new(103,100));
        var aim=controller.Step([],0,TimeSpan.Zero,640,480,TimeSpan.Zero);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>guard.UpdateAsync(aim,TimeSpan.Zero,cancellation.Token));
        Assert.False(guard.Suspended);
    }

    [AvaloniaTheory]
    [InlineData(1280,860)]
    [InlineData(980,700)]
    public async Task OutputFaultLeavesTheWindowRunningWithAnAmberNoticeAndFreshFrames(int width,int height)
    {
        using var model=new MainViewModel { IsRunning=true,IsFollowing=true };
        var window=new AdvancedWindow(model) { Width=width,Height=height };
        try
        {
            window.Show();
            var controller=new FaceAimController(Settings,new(103,56));
            for(var number=0;number<2;number++)
            {
                var time=TimeSpan.FromSeconds(number/30.0);
                var aim=controller.Step([new(280,320,80,80)],number,time,640,480,time);
                aim=FaceFollowOutputGuard.ApplyFeedback(controller,aim,new(new(103,56),"通信中断，舵机停发；画面继续",true));
                model.Present(new(new byte[640*480*4],640,480,aim.Observation,aim.Command,time,1,
                    VisionStatus:aim.Status,FaceAim:aim,OutputNotice:"通信中断，舵机停发；画面继续",OutputSuspended:true));
            }
            Assert.True(model.IsRunning);Assert.False(model.IsFollowing);Assert.False(model.HasError);
            Assert.Equal(2,model.ProcessedFrames);
            Assert.Contains("画面继续",model.ServoNotice);
            Assert.True(window.FindControl<TextBlock>("ServoNoticeBanner")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<Button>("StopButton")!.IsEffectivelyEnabled);
            Assert.False(model.Servo.Ready);
            await Task.Delay(80);
            using var image=window.CaptureRenderedFrame();
            Assert.NotNull(image);
            var output=Path.Combine(Workspace(),"artifacts","follow-limit-fix");
            Directory.CreateDirectory(output);
            image.Save(Path.Combine(output,$"warning-{width}.png"));
        }
        finally {model.IsRunning=false;window.Close();}
    }

    private static string Workspace()
    {
        for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent)
            if(File.Exists(Path.Combine(directory.FullName,"PanTiltTracker.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Workspace not found");
    }
}
