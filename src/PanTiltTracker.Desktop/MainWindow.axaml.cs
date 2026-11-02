using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using OpenCvSharp;
using PanTiltTracker.Desktop.ViewModels;
using PanTiltTracker.Vision;
using PanTiltTracker.Desktop.Controls;

namespace PanTiltTracker.Desktop;

public sealed partial class MainWindow : Avalonia.Controls.Window
{
    private bool closing;
    private Avalonia.Point? regionStart;
    public MainViewModel ViewModel { get; }

    public MainWindow() : this(new MainViewModel()) { }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        viewModel.FramePresented += RefreshPreview;
    }

    private void RefreshPreview()
    {
        if (!ViewModel.HasRun) AnalysisExpander.IsExpanded = false;
        PreviewImage.InvalidateVisual();
        DrawSelection(ViewModel.CanSelectRegion ? ViewModel.InitialRegion : null);
    }

    private void BeginRegion(object? sender, PointerPressedEventArgs args)
    {
        if (!ViewModel.CanSelectRegion || !args.GetCurrentPoint(SelectionCanvas).Properties.IsLeftButtonPressed) return;
        var point = args.GetPosition(SelectionCanvas);
        var image = ImageSelectionGeometry.ImageBounds(SelectionCanvas.Bounds.Size, ViewModel.PreviewWidth, ViewModel.PreviewHeight);
        if (!image.Contains(point)) return;
        regionStart = point;
        args.Pointer.Capture(SelectionCanvas);
        args.Handled = true;
    }

    private void MoveRegion(object? sender, PointerEventArgs args)
    {
        if (regionStart is not { } start || !ViewModel.CanSelectRegion) return;
        DrawSelection(ImageSelectionGeometry.Region(start, args.GetPosition(SelectionCanvas),
            SelectionCanvas.Bounds.Size, ViewModel.PreviewWidth, ViewModel.PreviewHeight));
    }

    private void EndRegion(object? sender, PointerReleasedEventArgs args)
    {
        if (regionStart is not { } start) return;
        var region = ImageSelectionGeometry.Region(start, args.GetPosition(SelectionCanvas),
            SelectionCanvas.Bounds.Size, ViewModel.PreviewWidth, ViewModel.PreviewHeight);
        regionStart = null;
        args.Pointer.Capture(null);
        if (region is { } selected && ViewModel.CanSelectRegion) ViewModel.SetInitialRegion(selected);
        DrawSelection(ViewModel.CanSelectRegion ? ViewModel.InitialRegion : null);
        args.Handled = true;
    }

    private void CancelRegion(object? sender, PointerCaptureLostEventArgs args)
    {
        regionStart = null;
        DrawSelection(ViewModel.CanSelectRegion ? ViewModel.InitialRegion : null);
    }

    private void SelectionSizeChanged(object? sender, SizeChangedEventArgs args) => RefreshPreview();

    private void DrawSelection(Rect? region)
    {
        SelectionBox.IsVisible = region is not null;
        if (region is not { } r) return;
        var image = ImageSelectionGeometry.ImageBounds(SelectionCanvas.Bounds.Size, ViewModel.PreviewWidth, ViewModel.PreviewHeight);
        Canvas.SetLeft(SelectionBox, image.X + r.X * image.Width / ViewModel.PreviewWidth);
        Canvas.SetTop(SelectionBox, image.Y + r.Y * image.Height / ViewModel.PreviewHeight);
        SelectionBox.Width = r.Width * image.Width / ViewModel.PreviewWidth;
        SelectionBox.Height = r.Height * image.Height / ViewModel.PreviewHeight;
    }

    private async void PickInput(object? sender, RoutedEventArgs args)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = ViewModel.SourceIndex == 1 ? "选择目标图片" : "选择实验录像",
            AllowMultiple = false,
            FileTypeFilter = ViewModel.SourceIndex == 1
                ? [FilePickerFileTypes.ImageAll] : [new FilePickerFileType("视频") { Patterns = ["*.mp4", "*.avi", "*.mkv", "*.mov"] }]
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) ViewModel.InputPath = path;
    }

    private async void SaveMarker(object? sender, RoutedEventArgs args)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存 ArUco 标记", SuggestedFileName = $"aruco-4x4-50-id-{ViewModel.MarkerId}.png",
            DefaultExtension = "png", FileTypeChoices = [FilePickerFileTypes.ImagePng]
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            using var image = ArucoTargetDetector.CreateMarker(ViewModel.MarkerId);
            if (!Cv2.ImWrite(path, image)) throw new IOException("图片保存失败。");
            ViewModel.AddEvent($"已保存 ID {ViewModel.MarkerId} 标记；打印后测量黑色方块边长。");
        }
        catch (Exception error) { ViewModel.ReportError(error.Message); }
    }

    private async void ExportCsv(object? sender, RoutedEventArgs args)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出实验日志", SuggestedFileName = "tracking-telemetry.csv", DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV 日志") { Patterns = ["*.csv"] }]
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try { ViewModel.ExportCsvTo(path); }
        catch (Exception error) { ViewModel.ReportError(error.Message); }
    }

    protected override async void OnClosing(WindowClosingEventArgs args)
    {
        base.OnClosing(args);
        if (closing) return;
        if (ViewModel.IsRunning)
        {
            args.Cancel = true;
            IsEnabled = false;
            await ViewModel.StopAsync();
            closing = true;
            Close();
        }
        else
        {
            closing = true;
        }
    }

    protected override void OnClosed(EventArgs args)
    {
        ViewModel.FramePresented -= RefreshPreview;
        ViewModel.Dispose();
        base.OnClosed(args);
    }
}
