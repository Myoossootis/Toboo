using System.Text.Json;
using PanTiltTracker.Core;

namespace PanTiltTracker.Desktop.Services;

public sealed record FollowOutputFeedback(AimReference? HeldReference = null, string? Notice = null, bool Suspended = false);

/// <summary>Latch a failed output off while allowing the independent vision session to continue.</summary>
internal sealed class FaceFollowOutputGuard(
    Func<FaceAimResult, TimeSpan, CancellationToken, Task<AimReference?>> send,
    Func<ValueTask> stop, Func<AimReference> reference, Func<string?> notice)
{
    private AimReference lastConfirmed = reference();
    public bool Suspended { get; private set; }
    public string? Notice { get; private set; }

    public async Task<FollowOutputFeedback> UpdateAsync(FaceAimResult aim, TimeSpan age, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Suspended) return new(lastConfirmed, Notice, true);
        try
        {
            var held = await send(aim, age, token);
            lastConfirmed = reference();
            return new(held, notice());
        }
        catch (Exception error) when (!token.IsCancellationRequested &&
            error is IOException or TimeoutException or JsonException or InvalidOperationException or FormatException)
        {
            Suspended = true;
            Notice = $"舵机通信中断，已暂停输出；画面继续。{error.Message} 停止预览后重新读状态，再启动跟随。";
            try { await stop(); }
            catch (Exception stopError) { Notice += $" 停发清理：{stopError.Message}"; }
            return new(lastConfirmed, Notice, true);
        }
    }

    internal static FaceAimResult ApplyFeedback(FaceAimController controller, FaceAimResult aim, FollowOutputFeedback feedback)
    {
        if (feedback.HeldReference is { } held)
        {
            controller.SynchronizeHeldRequest(held);
            aim = aim with { Command = aim.Command with { PanDegrees = held.PanDegrees, TiltDegrees = held.TiltDegrees },
                PanVelocity = 0, TiltVelocity = 0 };
        }
        return feedback.Suspended ? aim with { RequestPermitted = false, Readiness = "servo_output_suspended" } : aim;
    }
}
