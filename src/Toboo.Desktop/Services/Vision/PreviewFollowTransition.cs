namespace Toboo.Desktop.Services;

/// <summary>Release the preview owner before rechecking permission and acquiring follow output.</summary>
internal static class PreviewFollowTransition
{
    internal static async Task RunAsync(Func<Task> stopPreview, Func<bool> ready, Func<Task> startFollow)
    {
        await stopPreview();
        if (ready()) await startFollow();
    }
}
