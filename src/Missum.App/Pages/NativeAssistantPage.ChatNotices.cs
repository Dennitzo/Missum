using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private readonly Dictionary<Guid, string> _chatStatuses = [];
    private readonly Dictionary<Guid, string> _chatErrors = [];
    private bool _refreshingChatNotices;
    private string ChatStatus
    {
        get => _chatStatuses.GetValueOrDefault(_session, "");
        set { _chatStatuses[_session] = value; RefreshChatNotices(); }
    }

    private void RefreshChatNotices()
    {
        if (StatusText is null || ErrorBar is null) return;
        var hasActiveHeader = _messages.Values.Any(message => S(message, "role") == "assistant" && S(message, "status") is "streaming" or "pending");
        StatusText.Text = ChatStatus;
        StatusText.Visibility = string.IsNullOrWhiteSpace(ChatStatus) || (_running && hasActiveHeader) ? Visibility.Collapsed : Visibility.Visible;
        _refreshingChatNotices = true;
        try
        {
            ErrorBar.Message = _chatErrors.GetValueOrDefault(_session, "");
            ErrorBar.IsOpen = !string.IsNullOrEmpty(ErrorBar.Message);
            if (StatusText.Visibility == Visibility.Visible || ErrorBar.IsOpen) WelcomePanel.Visibility = Visibility.Collapsed;
        }
        finally { _refreshingChatNotices = false; }
    }

    private void OnChatErrorClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (!_refreshingChatNotices) _chatErrors.Remove(_session);
    }
}
