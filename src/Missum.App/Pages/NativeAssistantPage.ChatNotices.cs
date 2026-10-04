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
        var hasActiveHeader = DisplayMessages.Values.Any(message => S(message, "role") == "assistant" && S(message, "status") is "streaming" or "pending");
        var preparing = DisplayChatStatus.StartsWith("AI-Modell und Dienste werden vorbereitet", StringComparison.Ordinal)
            || DisplayChatStatus.StartsWith("Modell wird geladen", StringComparison.Ordinal)
            || DisplayChatStatus.StartsWith("Coding-Modell wird geladen", StringComparison.Ordinal);
        StatusText.Text = DisplayChatStatus;
        StatusText.Visibility = string.IsNullOrWhiteSpace(DisplayChatStatus) || (DisplayRunning && hasActiveHeader && !preparing) ? Visibility.Collapsed : Visibility.Visible;
        _refreshingChatNotices = true;
        try
        {
            ErrorBar.Message = ActiveSubagent is { } child ? S(child.Snapshot, "error") : _chatErrors.GetValueOrDefault(_session, "");
            ErrorBar.IsOpen = !string.IsNullOrEmpty(ErrorBar.Message);
            if (StatusText.Visibility == Visibility.Visible || ErrorBar.IsOpen) WelcomePanel.Visibility = Visibility.Collapsed;
        }
        finally { _refreshingChatNotices = false; }
    }

    private void OnChatErrorClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (!_refreshingChatNotices && ActiveSubagent is null) _chatErrors.Remove(_session);
    }
}
