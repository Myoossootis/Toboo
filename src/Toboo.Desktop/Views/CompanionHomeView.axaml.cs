using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Toboo.Desktop.ViewModels;

namespace Toboo.Desktop.Views;

public sealed partial class CompanionHomeView : UserControl
{
    private ChatViewModel? chat;
    private bool voiceHeld;
    private IPointer? heldPointer;
    public CompanionHomeView()
    {
        InitializeComponent();
        VoiceHoldButton.AddHandler(PointerPressedEvent, VoicePressed, RoutingStrategies.Tunnel);
        VoiceHoldButton.AddHandler(PointerReleasedEvent, VoiceReleased, RoutingStrategies.Tunnel);
        VoiceHoldButton.PointerCaptureLost += (_, _) => CancelHeldVoice();
        VoiceHoldButton.KeyDown += VoiceKeyDown;
        VoiceHoldButton.KeyUp += VoiceKeyUp;
        VoiceHoldButton.LostFocus += (_, _) => CancelHeldVoice();
        AddHandler(KeyDownEvent, VoiceEscape, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => BindChat();
        AttachedToVisualTree += (_, _) => BindChat();
        DetachedFromVisualTree += (_, _) => { CancelHeldVoice(); if (chat is not null) chat.Updated -= ChatUpdated; chat = null; };
    }
    private void BindChat()
    {
        if (chat is not null) chat.Updated -= ChatUpdated;
        chat = (DataContext as CompanionShellViewModel)?.Chat;
        if (chat is not null) chat.Updated += ChatUpdated;
    }
    private void ChatUpdated(bool force)
    {
        if (force || ConversationScroll.Offset.Y + ConversationScroll.Viewport.Height >= ConversationScroll.Extent.Height - 64)
            Dispatcher.UIThread.Post(() => ConversationScroll.ScrollToEnd(), DispatcherPriority.Loaded);
    }
    private void ComposerKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || args.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        args.Handled = true;
        if (chat?.CanSend == true) chat.SendCommand.Execute(null);
    }
    private async void VoicePressed(object? sender, PointerPressedEventArgs args)
    {
        if (!args.GetCurrentPoint(VoiceHoldButton).Properties.IsLeftButtonPressed || voiceHeld
            || DataContext is not CompanionShellViewModel shell || !shell.Voice.CanRecord) return;
        args.Handled = true; voiceHeld = true;
        VoiceHoldButton.Focus();
        heldPointer = args.Pointer;
        heldPointer.Capture(VoiceHoldButton);
        await shell.Voice.BeginListeningAsync();
    }
    private void VoiceReleased(object? sender, PointerReleasedEventArgs args)
    {
        if (!voiceHeld || !ReferenceEquals(heldPointer, args.Pointer) || args.InitialPressMouseButton != MouseButton.Left) return;
        args.Handled = true; voiceHeld = false;
        (DataContext as CompanionShellViewModel)?.Voice.FinishListening();
        ReleaseHeldPointer();
    }
    private async void VoiceKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key is not (Key.Space or Key.Enter)) return;
        args.Handled = true;
        if (voiceHeld || DataContext is not CompanionShellViewModel shell || !shell.Voice.CanRecord) return;
        voiceHeld = true;
        await shell.Voice.BeginListeningAsync();
    }
    private void VoiceKeyUp(object? sender, KeyEventArgs args)
    {
        if (!voiceHeld || heldPointer is not null || args.Key is not (Key.Space or Key.Enter)) return;
        args.Handled = true; voiceHeld = false;
        (DataContext as CompanionShellViewModel)?.Voice.FinishListening();
    }
    private void VoiceEscape(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape || DataContext is not CompanionShellViewModel shell || !shell.Voice.CanStop) return;
        args.Handled = true;
        CancelComposerVoice();
    }
    private void CancelComposerVoice(object? sender, RoutedEventArgs args) => CancelComposerVoice();

    private void CancelHeldVoice()
    {
        if (voiceHeld) CancelComposerVoice();
    }
    private void CancelComposerVoice()
    {
        voiceHeld = false;
        ReleaseHeldPointer();
        (DataContext as CompanionShellViewModel)?.Voice.Stop();
    }
    private void ReleaseHeldPointer()
    {
        var pointer = heldPointer;
        heldPointer = null;
        if (pointer is not null && ReferenceEquals(pointer.Captured, VoiceHoldButton))
            pointer.Capture(null);
    }
    public bool Compact { set { Classes.Set("compact", value); HomeGrid.ColumnDefinitions[1].Width = new(value ? 264 : 320); } }
}
