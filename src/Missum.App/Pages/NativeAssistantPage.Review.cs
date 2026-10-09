using System.Text.Json;
using Missum.App.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private readonly List<ChangesReviewTab> _reviewTabs = [];
    private Guid? _activeReviewRunId;
    private const int ReviewFilesPerPage = 50;

    private void OpenEmptyChangesReview()
    {
        if (_disposed || _sessionTabNavigationBusy || _session == Guid.Empty) return;
        var tab = _reviewTabs.FirstOrDefault(item => item.SessionId == _session && S(item.Summary, "emptyOverview") == "True");
        if (tab is null)
        {
            var summary = JsonSerializer.SerializeToElement(new { emptyOverview = true, files = Array.Empty<object>() });
            tab = CreateReviewTab(_session, Guid.NewGuid(), summary);
            _reviewTabs.Add(tab);
        }
        ShowReviewTab(tab);
        RenderSessionTabs();
    }

    /// <summary>Opens one native review tab per run, without modifying files or chat history.</summary>
    private void OpenChangesReview(JsonElement summary)
    {
        if (_disposed || _sessionTabNavigationBusy) return;
        if (!TryGetReviewIdentity(summary, out var sessionId, out var runId) || sessionId != _session)
        {
            ShowError("Die Änderungsübersicht enthält keine gültige Zuordnung zu diesem Chat und Lauf.");
            return;
        }
        var tab = _reviewTabs.FirstOrDefault(item => item.RunId == runId);
        if (tab is null)
        {
            tab = CreateReviewTab(sessionId, runId, summary);
            _reviewTabs.Add(tab);
        }
        else if (tab.SessionId != sessionId) return;
        else UpdateReviewReceipt(tab, summary);
        ShowReviewTab(tab);
        RenderSessionTabs();
    }

    /// <summary>Refreshes an already opened run only; a later run cannot overwrite an earlier review.</summary>
    private void RefreshChangesReview(JsonElement summary)
    {
        if (_disposed || !TryGetReviewIdentity(summary, out var sessionId, out var runId)) return;
        var tab = _reviewTabs.FirstOrDefault(item => item.RunId == runId && item.SessionId == sessionId);
        if (tab is null || !UpdateReviewReceipt(tab, summary)) return;
        if (_activeReviewRunId == runId && _session == sessionId)
        {
            tab.ScrollOffset = ReviewScroll.VerticalOffset;
            RenderReviewContent(tab);
        }
        RenderSessionTabs();
    }

    private static bool TryGetReviewIdentity(JsonElement summary, out Guid sessionId, out Guid runId)
    {
        sessionId = runId = Guid.Empty;
        return summary.ValueKind == JsonValueKind.Object
            && Guid.TryParse(S(summary, "sessionId"), out sessionId) && sessionId != Guid.Empty
            && Guid.TryParse(S(summary, "runId"), out runId) && runId != Guid.Empty;
    }

    private static long ReviewRevision(JsonElement summary) =>
        summary.TryGetProperty("revision", out var revision) && revision.ValueKind == JsonValueKind.Number
            && revision.TryGetInt64(out var value) ? value : 0;

    private static string ReviewTitle(JsonElement summary)
    {
        var count = Items(summary, "files").Length;
        return $"Änderungen · {count:N0} " + (count == 1 ? "Datei" : "Dateien");
    }

    private static bool UpdateReviewReceipt(ChangesReviewTab tab, JsonElement summary)
    {
        var revision = ReviewRevision(summary);
        if (tab.Revision > 0 && revision < tab.Revision) return false;
        if (revision > 0 && revision == tab.Revision) return false;
        tab.Summary = summary.Clone();
        tab.Revision = revision;
        tab.Title = ReviewTitle(summary);
        return true;
    }

    private ChangesReviewTab CreateReviewTab(Guid sessionId, Guid runId, JsonElement summary)
    {
        var label = new TextBlock { FontSize = 13, MaxWidth = 184, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        content.Children.Add(new FontIcon { Glyph = "\uE8A5", FontSize = 13, Foreground = NativeIconPalette.BrushFor("code") }); content.Children.Add(label);
        var select = TabButton(); select.Content = content; select.Height = 30; select.MinWidth = 110;
        select.Padding = new Thickness(10, 0, 5, 0);
        var close = TabButton(); close.Content = new FontIcon { Glyph = "\uE711", FontSize = 11 };
        close.Width = 24; close.Height = 24; close.Margin = new Thickness(0, 0, 3, 0);
        close.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(close, "Änderungs-Tab schließen");
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(select); Grid.SetColumn(close, 1); row.Children.Add(close);
        var container = new Border { Height = 32, MaxWidth = 258, CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1), Child = row };
        var tab = new ChangesReviewTab(sessionId, runId, container, label, select, close);
        UpdateReviewReceipt(tab, summary);
        select.Click += async (_, _) => await ActivateReviewTabAsync(tab);
        close.Click += (_, _) => CloseReviewTab(tab);
        return tab;
    }

    private async Task ActivateReviewTabAsync(ChangesReviewTab tab)
    {
        if (_disposed || _sessionTabNavigationBusy) return;
        if (_session == tab.SessionId) { ShowReviewTab(tab); RenderSessionTabs(); return; }
        _sessionTabNavigationBusy = true;
        RenderSessionTabs();
        try
        {
            if (await NavigateAsync("session.open", new { sessionId = tab.SessionId }) && _session == tab.SessionId)
                ShowReviewTab(tab);
            else ShowChatView();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { ShowChatView(); ShowError(exception.Message); }
        finally
        {
            _sessionTabNavigationBusy = false;
            if (!_disposed) SyncSessionTabs();
        }
    }

    private void CloseReviewTab(ChangesReviewTab tab)
    {
        if (_sessionTabNavigationBusy || _disposed) return;
        if (_activeReviewRunId == tab.RunId) ShowChatView();
        foreach (var card in tab.Files.Values) card.Diff?.Dispose();
        _reviewTabs.Remove(tab);
        SessionTabsPanel.Children.Remove(tab.Container);
        RenderSessionTabs();
    }

    private void ShowReviewTab(ChangesReviewTab tab)
    {
        HideSubagentOverview();
        _activeSourcesSession = null; _sourcesHost.Visibility = Visibility.Collapsed;
        HideResearchView();
        SaveReviewScrollOffset();
        _activeReviewRunId = tab.RunId;
        BodyGrid.Visibility = Visibility.Collapsed;
        ReviewScroll.Visibility = Visibility.Visible;
        RenderReviewContent(tab);
    }

    private void ShowChatView()
    {
        HideSubagentOverview();
        _activeSourcesSession = null; _sourcesHost.Visibility = Visibility.Collapsed;
        HideResearchView();
        SaveReviewScrollOffset();
        _activeReviewRunId = null;
        ReviewScroll.Visibility = Visibility.Collapsed;
        BodyGrid.Visibility = Visibility.Visible;
    }

    private void SaveReviewScrollOffset()
    {
        if (_activeReviewRunId is { } runId && _reviewTabs.FirstOrDefault(tab => tab.RunId == runId) is { } tab)
            tab.ScrollOffset = ReviewScroll.VerticalOffset;
    }

    private void SyncReviewSession()
    {
        if (_activeSubagentOverviewSession is { } overviewSession && overviewSession != _session) ShowChatView();
        if (_activeSourcesSession is { } sourceSession && sourceSession != _session) ShowChatView();
        // Same-session status/snapshot updates preserve the selected review tab.
        if (_activeReviewRunId is { } runId && _reviewTabs.FirstOrDefault(tab => tab.RunId == runId)?.SessionId != _session)
            ShowChatView();
    }

    private int RenderReviewTabs(int firstIndex)
    {
        var visibleTabs = _reviewTabs.Where(tab => tab.SessionId == _session).ToArray();
        foreach (var hidden in _reviewTabs.Where(tab => tab.SessionId != _session))
            SessionTabsPanel.Children.Remove(hidden.Container);
        for (var index = 0; index < visibleTabs.Length; index++)
        {
            var tab = visibleTabs[index];
            var active = _activeReviewRunId == tab.RunId && _session == tab.SessionId;
            SetTabText(tab.Label, tab.Title);
            ApplyTabAppearance(tab.Container, tab.Select, active);
            SetTabCloseForeground(tab.Close);
            SetTabEnabled(tab.Select, !_sessionTabNavigationBusy);
            SetTabEnabled(tab.Close, !_sessionTabNavigationBusy);
            SetTabToolTip(tab.Select, tab.Title + (S(tab.Summary, "emptyOverview") == "True" ? "" : "\nLauf " + tab.RunId.ToString("N")[..8]));
            SetTabName(tab.Select, "Änderungs-Tab: " + tab.Title);
            SetTabName(tab.Close, "Änderungs-Tab schließen: " + tab.Title);
            SetTabHelp(tab.Select, active ? "Aktive Änderungsübersicht" : "Dateiänderungen dieses Laufs öffnen");
            PositionTab(tab.Container, firstIndex + index);
        }
        return visibleTabs.Length;
    }

    private void RenderReviewContent(ChangesReviewTab tab)
    {
        ReviewChangesPanel.Width = Math.Max(0, ReviewScroll.ActualWidth - ReviewScroll.Padding.Left - ReviewScroll.Padding.Right);
        ReviewChangesPanel.Children.Clear();
        ReviewChangesPanel.Spacing = 16;
        ReviewChangesPanel.Children.Add(new TextBlock { Text = tab.Title, FontSize = 25,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var workspace = S(tab.Summary, "workspacePath");
        ReviewChangesPanel.Children.Add(new TextBlock
        {
            Text = S(tab.Summary, "emptyOverview") == "True" ? "Noch keine Änderungen in dieser Sitzung." : (workspace.Length > 0 ? workspace + "\n" : "") + "Lauf " + tab.RunId.ToString("N")[..8],
            FontSize = 13, Foreground = ThemeBrush("MissumMutedTextBrush", 150), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
        });
        var partial = S(tab.Summary, "isPartial") == "True";
        var notice = S(tab.Summary, "notice");
        if (partial || notice.Length > 0)
        {
            var noticeBar = new InfoBar
            {
                IsOpen = true, IsClosable = false, Severity = partial ? InfoBarSeverity.Warning : InfoBarSeverity.Informational,
                Title = partial ? "Änderungen nur teilweise erfasst" : "Hinweis",
                Message = notice.Length > 0 ? notice : "Die vorhandene Quittung enthält möglicherweise nicht alle Dateiänderungen dieses Laufs.",
            };
            NativeNotice.Attach(noticeBar);
            ReviewChangesPanel.Children.Add(noticeBar);
        }

        var files = Items(tab.Summary, "files");
        if (files.Length == 0)
            ReviewChangesPanel.Children.Add(new TextBlock { Text = partial ? "Noch keine Datei-Diffs verfügbar." : "Keine erfassten Dateiänderungen vorhanden.",
                Foreground = ThemeBrush("MissumMutedTextBrush", 170), TextWrapping = TextWrapping.Wrap });
        var pageCount = Math.Max(1, (files.Length + ReviewFilesPerPage - 1) / ReviewFilesPerPage);
        tab.FilePage = Math.Clamp(tab.FilePage, 0, pageCount - 1);
        if (pageCount > 1)
        {
            var pager = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var previous = new Button { Content = "Zurück", IsEnabled = tab.FilePage > 0 };
            var next = new Button { Content = "Weiter", IsEnabled = tab.FilePage + 1 < pageCount };
            pager.Children.Add(previous);
            pager.Children.Add(new TextBlock { Text = $"Dateiseite {tab.FilePage + 1:N0} / {pageCount:N0}", VerticalAlignment = VerticalAlignment.Center });
            pager.Children.Add(next);
            previous.Click += (_, _) => { tab.FilePage--; tab.ScrollOffset = 0; RenderReviewContent(tab); };
            next.Click += (_, _) => { tab.FilePage++; tab.ScrollOffset = 0; RenderReviewContent(tab); };
            ReviewChangesPanel.Children.Add(pager);
        }
        var visiblePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files.Skip(tab.FilePage * ReviewFilesPerPage).Take(ReviewFilesPerPage))
        {
            var path = S(file, "path", "Datei");
            visiblePaths.Add(path);
            if (!tab.Files.TryGetValue(path, out var card))
            {
                card = CreateReviewFileCard(tab, path);
                tab.Files[path] = card;
            }
            card.File = file;
            card.Path.Text = path;
            var binary = S(file, "isBinary") == "True";
            card.Open.IsEnabled = !binary;
            card.Counts.Inlines.Clear();
            if (binary) card.Counts.Text = "Binärdatei";
            else if (TryReviewLineCount(file, "addedLines", out var added) && TryReviewLineCount(file, "removedLines", out var removed))
            {
                card.Counts.Text = "";
                card.Counts.Inlines.Add(new Run { Text = $"+{added:N0}", Foreground = ThemeBrush("MissumSuccessBrush", 160) });
                card.Counts.Inlines.Add(new Run { Text = $"  −{removed:N0}", Foreground = ThemeBrush("MissumDangerBrush", 160) });
            }
            else card.Counts.Text = "Zeilenzahlen nicht verfügbar";
            if (card.IsExpanded) UpdateReviewFileContent(card);
            ReviewChangesPanel.Children.Add(card.Container);
        }
        // Retain only the displayed file page, not hundreds of hidden XAML trees.
        foreach (var path in tab.Files.Keys.Where(path => !visiblePaths.Contains(path)).ToArray())
        {
            tab.Files[path].Diff?.Dispose(); tab.Files.Remove(path);
        }
        // Avoid a synchronous layout of all diff rows on every live snapshot.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!_disposed && _activeReviewRunId == tab.RunId)
                ReviewScroll.ChangeView(null, Math.Min(tab.ScrollOffset, ReviewScroll.ScrollableHeight), null, true);
        });
    }

    private static ReviewFileCard CreateReviewFileCard(ChangesReviewTab tab, string path)
    {
        var panel = new StackPanel { Spacing = 0 };
        var header = new Grid { Padding = new Thickness(14, 12, 14, 12), ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock { Text = path, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var counts = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        var open = new Button { Content = "Diff anzeigen" };
        var body = new StackPanel { Visibility = Visibility.Collapsed };
        header.Children.Add(label); Grid.SetColumn(counts, 1); header.Children.Add(counts);
        Grid.SetColumn(open, 2); header.Children.Add(open);
        panel.Children.Add(header); panel.Children.Add(body);
        var container = new Border { Child = panel, Background = ThemeBrush("MissumLayerBrush", 30), BorderBrush = ThemeBrush("MissumStrokeBrush", 52),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) };
        var card = new ReviewFileCard(container, label, counts, open, body);
        AutomationProperties.SetName(open, "Datei-Diff anzeigen: " + path);
        open.Click += (_, _) => SetReviewFileExpanded(tab, card, !card.IsExpanded);
        return card;
    }

    private static void SetReviewFileExpanded(ChangesReviewTab tab, ReviewFileCard card, bool expanded)
    {
        foreach (var other in tab.Files.Values.Where(other => other.IsExpanded && (!ReferenceEquals(other, card) || !expanded)))
        {
            other.IsExpanded = false; other.Open.Content = "Diff anzeigen";
            other.Diff?.Dispose();
            other.Body.Children.Clear(); other.Body.Visibility = Visibility.Collapsed; other.Diff = null;
        }
        card.IsExpanded = expanded;
        card.Open.Content = expanded ? "Diff ausblenden" : "Diff anzeigen";
        card.Body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (expanded) UpdateReviewFileContent(card);
    }

    private static void UpdateReviewFileContent(ReviewFileCard card)
    {
        var diff = S(card.File, "diff");
        if (card.Diff is null)
        {
            card.Diff = new NativeDiffView(diff, wrapLines: true);
            card.Body.Children.Add(card.Diff);
            if (S(card.File, "diffTruncated") == "True") card.Body.Children.Add(ReviewNotice("Dieser Diff ist gekürzt. Die Quittung enthält nicht alle geänderten Zeilen."));
        }
        else card.Diff.UpdateDiff(diff);
    }

    private void OnReviewSizeChanged(object sender, SizeChangedEventArgs e) =>
        ReviewChangesPanel.Width = Math.Max(0, e.NewSize.Width - ReviewScroll.Padding.Left - ReviewScroll.Padding.Right);

    private static TextBlock ReviewNotice(string text) => new()
    {
        Text = text, FontSize = 13, Foreground = ThemeBrush("MissumMutedTextBrush", 170), TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(14, 6, 14, 12),
    };

    private static bool TryReviewLineCount(JsonElement file, string name, out long count)
    {
        count = 0;
        return file.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out count) && count >= 0;
    }

    private sealed class ChangesReviewTab(Guid sessionId, Guid runId, Border container, TextBlock label, Button select, Button close)
    {
        public Guid SessionId { get; } = sessionId;
        public Guid RunId { get; } = runId;
        public JsonElement Summary { get; set; }
        public long Revision { get; set; }
        public int FilePage { get; set; }
        public Dictionary<string, ReviewFileCard> Files { get; } = new(StringComparer.Ordinal);
        public string Title { get; set; } = "Änderungen";
        public double ScrollOffset { get; set; }
        public Border Container { get; } = container;
        public TextBlock Label { get; } = label;
        public Button Select { get; } = select;
        public Button Close { get; } = close;
    }
    private sealed class ReviewFileCard(Border container, TextBlock path, TextBlock counts, Button open, StackPanel body)
    {
        public Border Container { get; } = container;
        public TextBlock Path { get; } = path;
        public TextBlock Counts { get; } = counts;
        public Button Open { get; } = open;
        public StackPanel Body { get; } = body;
        public JsonElement File { get; set; }
        public bool IsExpanded { get; set; }
        public NativeDiffView? Diff { get; set; }
    }

}
