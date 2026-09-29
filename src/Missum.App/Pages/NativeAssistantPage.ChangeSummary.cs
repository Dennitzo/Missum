using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private Guid _changesSummarySession;
    private long _changesSummaryRevision;
    private string _changesSummaryRun = "";
    private string _changesSummarySignature = "";
    private JsonElement _currentChangesSummary;
    private readonly NativeChangeReceiptState _changeReceiptState = new();

    /// <summary>Updates the composer summary. False means a foreign or stale receipt was ignored.</summary>
    private bool UpdateChangesSummary(JsonElement summary)
    {
        if (_changesSummarySession != _session) ResetChangesSummary();
        if (summary.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            ResetChangesSummary();
            return true;
        }
        if (summary.ValueKind != JsonValueKind.Object) return false;

        // Historical tabs can refresh their own receipt, but never replace the
        // current composer's receipt with a different assistant turn or run.
        RefreshChangesReview(summary);

        var sessionId = S(summary, "sessionId");
        if (!Guid.TryParse(sessionId, out var parsedSession) || parsedSession != _session) return false;
        var runId = S(summary, "runId");
        var revision = ReadConversationRevision(summary, "revision");
        if (!Guid.TryParse(S(summary, "messageId"), out var messageId) || !Guid.TryParse(runId, out var parsedRun)
            || !_changeReceiptState.AcceptReceipt(parsedSession, messageId, parsedRun, revision)) return false;
        if (_changesSummaryRun != runId)
        {
            _changesSummaryRun = runId;
            _changesSummaryRevision = 0;
        }
        if (revision > 0 && revision < _changesSummaryRevision) return false;
        if (revision > 0) _changesSummaryRevision = revision;
        _currentChangesSummary = summary.Clone();

        var files = Items(summary, "files");
        var partial = summary.TryGetProperty("isPartial", out var partialValue) && partialValue.ValueKind == JsonValueKind.True;
        var notice = S(summary, "notice");
        var signature = files.Length.ToString(CultureInfo.InvariantCulture) + ":" + partial + ":" + notice + ":"
            + string.Join("|", files.Select(file => S(file, "path") + ":" + S(file, "addedLines") + ":" + S(file, "removedLines") + ":" + S(file, "isBinary")));
        if (_changesSummarySignature == signature) return true;
        _changesSummarySignature = signature;

        if (files.Length == 0 && !partial)
        {
            ChangesSummaryButton.Visibility = Visibility.Collapsed;
            ChangesSummaryButton.Content = null;
            ChangesSummaryButton.IsEnabled = false;
            return true;
        }

        long added = 0;
        long removed = 0;
        var textFiles = 0;
        var countsAvailable = true;
        foreach (var file in files)
        {
            if (file.TryGetProperty("isBinary", out var binary) && binary.ValueKind == JsonValueKind.True) continue;
            textFiles++;
            if (!ReadCount(file, "addedLines", out var additions) || !ReadCount(file, "removedLines", out var removals))
            {
                countsAvailable = false;
                continue;
            }
            added += additions;
            removed += removals;
        }

        var label = files.Length == 0 ? "Änderungsübersicht unvollständig"
            : files.Length == 1 ? "1 Datei geändert" : $"{files.Length:N0} Dateien geändert";
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        row.Children.Add(Label(label, Brush(205)));
        var description = label;
        if (textFiles > 0 && countsAvailable)
        {
            var additions = $"+{added:N0}";
            var removals = $"−{removed:N0}";
            row.Children.Add(Label(additions, new SolidColorBrush(Color.FromArgb(255, 0x31, 0xC7, 0x7D))));
            row.Children.Add(Label(removals, new SolidColorBrush(Color.FromArgb(255, 0xFF, 0x62, 0x5A))));
            description += $", {added:N0} Zeilen hinzugefügt, {removed:N0} Zeilen entfernt";
        }
        else if (textFiles > 0)
        {
            row.Children.Add(Label("Zeilenzahlen nicht verfügbar", Brush(145)));
            description += ", Zeilenzahlen nicht verfügbar";
        }
        if (partial && files.Length > 0)
        {
            row.Children.Add(Label("teilweise", Brush(150)));
            description += ", teilweise erfasst";
        }

        ChangesSummaryButton.Content = row;
        ChangesSummaryButton.IsEnabled = files.Length > 0;
        ChangesSummaryButton.Visibility = Visibility.Visible;
        AutomationProperties.SetName(ChangesSummaryButton, description);
        ToolTipService.SetToolTip(ChangesSummaryButton, notice.Length > 0 ? description + "\n" + notice : description + "\nGit-Diff anzeigen");
        return true;

        static bool ReadCount(JsonElement file, string property, out long count)
        {
            count = 0;
            return file.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.Number
                && field.TryGetInt64(out count) && count >= 0;
        }

        static TextBlock Label(string text, SolidColorBrush foreground) => new()
        {
            Text = text,
            FontSize = 13,
            Foreground = foreground,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private void ResetChangesSummary()
    {
        _changesSummarySession = _session;
        _changesSummaryRevision = 0;
        _changesSummaryRun = "";
        _changesSummarySignature = "";
        _currentChangesSummary = default;
        ChangesSummaryButton.Visibility = Visibility.Collapsed;
        ChangesSummaryButton.Content = null;
        ChangesSummaryButton.IsEnabled = false;
        AutomationProperties.SetName(ChangesSummaryButton, "Dateiänderungen");
        ToolTipService.SetToolTip(ChangesSummaryButton, null);
    }

    private void OnChangesSummaryClick(object sender, RoutedEventArgs e)
    {
        if (_currentChangesSummary.ValueKind == JsonValueKind.Object) OpenChangesReview(_currentChangesSummary);
    }

    private bool ObserveChangesConversation(Guid sessionId, JsonElement data)
    {
        var lastAssistant = Items(data, "messages").LastOrDefault(message => S(message, "role") == "assistant");
        var messageId = Guid.TryParse(S(lastAssistant, "id"), out var parsed) ? parsed : Guid.Empty;
        return _changeReceiptState.ObserveConversation(sessionId, messageId, ReadConversationRevision(data));
    }

    private static long ReadConversationRevision(JsonElement value, string field = "conversationRevision") =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(field, out var revision)
            && revision.ValueKind == JsonValueKind.Number && revision.TryGetInt64(out var number) ? number : 0;
}
