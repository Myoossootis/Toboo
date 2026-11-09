using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using PanTiltTracker.Desktop.ViewModels;

namespace PanTiltTracker.Desktop.Views;

public sealed partial class CompanionHomeView : UserControl
{
    private ChatViewModel? chat;
    public CompanionHomeView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindChat();
        AttachedToVisualTree += (_, _) => BindChat();
        DetachedFromVisualTree += (_, _) => { if (chat is not null) chat.Updated -= ChatUpdated; chat = null; };
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
    public bool Compact { set { Classes.Set("compact", value); HomeGrid.ColumnDefinitions[1].Width = new(value ? 264 : 320); } }
}
