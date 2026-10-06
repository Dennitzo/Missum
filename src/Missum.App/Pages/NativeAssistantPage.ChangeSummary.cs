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

    /// <summary>Updates the outputs summary. False means a foreign or stale receipt was ignored.</summary>
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
        // current output receipt with a different assistant turn or run.
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

        RenderChangesSummaryContent(files, partial, notice);
        return true;
    }

    private void RenderChangesSummaryContent(JsonElement[] files, bool partial, string notice)
    {
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

        var label = files.Length == 0 ? (partial ? "Änderungsübersicht unvollständig" : "Keine Dateiänderungen")
            : files.Length == 1 ? "1 Datei geändert" : $"{files.Length:N0} Dateien geändert";
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        var layout = new Grid { ColumnSpacing = 10 };
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(16) });
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        layout.Children.Add((UIElement)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
            <Path xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  Width="14" Height="14" Stretch="Uniform" HorizontalAlignment="Center" VerticalAlignment="Center"
                  Stroke="{ThemeResource MissumIconCodeBrush}" StrokeThickness="1.2" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
                  Data="M3,1 L11,1 Q13,1 13,3 L13,11 Q13,13 11,13 L3,13 Q1,13 1,11 L1,3 Q1,1 3,1 Z M5,5 L9,5 M7,3 L7,7 M5,10 L9,10"/>
            """));
        var title = Label("Änderungen", Brush(235));
        Grid.SetColumn(title, 1); layout.Children.Add(title);
        Grid.SetColumn(row, 2); layout.Children.Add(row);
        var description = label;
        if (countsAvailable)
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

        ChangesSummaryButton.Content = layout;
        ChangesSummaryButton.IsEnabled = true;
        ChangesSummaryButton.Visibility = Visibility.Visible;
        AutomationProperties.SetName(ChangesSummaryButton, description);
        ToolTipService.SetToolTip(ChangesSummaryButton, notice.Length > 0 ? description + "\n" + notice : description + "\nGit-Diff anzeigen");

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
        RenderChangesSummaryContent([], false, "");
    }

    private void OnChangesSummaryClick(object sender, RoutedEventArgs e)
    {
        if (_currentChangesSummary.ValueKind == JsonValueKind.Object) OpenChangesReview(_currentChangesSummary);
        else OpenEmptyChangesReview();
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
