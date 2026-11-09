using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.VisualTree;
using PanTiltTracker.Desktop.ViewModels;

namespace PanTiltTracker.Desktop;

public sealed partial class MainWindow : Window
{
    private bool closing;
    private AdvancedWindow? advancedWindow;
    private SensorWindow? sensorWindow;
    public MainViewModel ViewModel { get; }
    public CompanionShellViewModel Shell { get; }
    public MainWindow() : this(new MainViewModel()) { }
    public MainWindow(MainViewModel viewModel, ChatViewModel? chat = null)
    {
        ViewModel = viewModel;
        Shell = new(viewModel, chat);
        InitializeComponent();
        DataContext = Shell;
        Shell.AdvancedRequested += ShowAdvanced;
        Shell.SensorsRequested += ShowSensors;
        viewModel.FramePresented += RefreshPreview;
        SizeChanged += (_, _) => ApplyLayout();
        ApplyLayout();
    }
    private void ApplyLayout()
    {
        var compact = (Bounds.Width > 0 ? Bounds.Width : Width) < 1120;
        Classes.Set("compact", compact);
        ShellGrid.ColumnDefinitions[0].Width = new GridLength(compact ? 84 : 208);
        HomeView.Compact = compact;
        DevicesView.Compact = compact;
        PreferencesView.Compact = compact;
    }
    private void RefreshPreview()
    {
        foreach (var image in this.GetVisualDescendants().OfType<Image>()) image.InvalidateVisual();
        sensorWindow?.ViewModel.ObserveFace(ViewModel.IsRunning && ViewModel.IsCameraSource && ViewModel.IsFace && ViewModel.LiveFaceConfirmed);
    }
    internal void ShowAdvanced(int tab = 0)
    {
        Shell.PageIndex = 1;
        if (advancedWindow is null)
        {
            advancedWindow = new AdvancedWindow(ViewModel, ownsViewModel: false) { SharedSensorsRequested = ShowSensors };
            advancedWindow.Closed += (_, _) => advancedWindow = null;
            advancedWindow.Show(this);
        }
        advancedWindow.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = tab;
        advancedWindow.Activate();
    }
    internal void ShowSensors()
    {
        if (sensorWindow is not null) { sensorWindow.Activate(); return; }
        sensorWindow = new SensorWindow();
        sensorWindow.Closed += (_, _) => sensorWindow = null;
        sensorWindow.Show(this);
    }
    private void DragWindow(object? sender, PointerPressedEventArgs args)
    {
        if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(args);
    }
    private void Minimize(object? sender, RoutedEventArgs args) => WindowState = WindowState.Minimized;
    private void Maximize(object? sender, RoutedEventArgs args) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow(object? sender, RoutedEventArgs args) => Close();
    protected override async void OnClosing(WindowClosingEventArgs args)
    {
        base.OnClosing(args);
        if (closing) return;
        if (ViewModel.Servo.Busy && !ViewModel.IsFollowing) { args.Cancel = true; return; }
        if (ViewModel.IsRunning || Shell.Chat.RemoteRunning || Shell.Chat.HasPending || Shell.Chat.IsBusy)
        {
            args.Cancel = true; IsEnabled = false;
            await ViewModel.StopAsync();
            await Shell.Chat.ShutdownAsync();
            closing = true; Close();
        }
    }
    protected override void OnClosed(EventArgs args)
    {
        advancedWindow?.Close(); sensorWindow?.Close();
        ViewModel.FramePresented -= RefreshPreview;
        Shell.Dispose(); ViewModel.Dispose();
        base.OnClosed(args);
    }
}
