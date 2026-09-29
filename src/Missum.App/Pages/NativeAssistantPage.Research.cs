using System.Text.Json;
using Missum.App.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.Storage;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private readonly Dictionary<Guid, NativeResearchSession> _researchSessions = [];
    private readonly HashSet<Guid> _researchLoading = [];
    private Guid? _activeResearchSessionId;
    private Button? _researchTabButton;
    private DispatcherTimer? _researchRefreshTimer;
    private string? _researchRenderSignature;
    private Guid? _researchChromeSessionId;
    private ComboBox? _researchProjectPicker;
    private Button? _researchRefreshButton;
    private Button? _researchExportButton;
    private InfoBar? _researchErrorBar;
    private TextBlock? _researchRuntimeStatus;
    private ProgressRing? _researchRuntimeProgress;
    private Border? _researchRuntimePill;
    private TextBlock? _researchEmptyStatus;

    private string ModelRole => _mode == "coding" ? "coding" : "general";

    private NativeResearchSession ResearchState(Guid sessionId)
    {
        if (!_researchSessions.TryGetValue(sessionId, out var state))
            _researchSessions[sessionId] = state = new NativeResearchSession();
        return state;
    }

    private void SyncResearchSession()
    {
        if (_activeResearchSessionId is { } previous && previous != _session) ShowChatView();
        if (_mode != "claudescience")
        {
            _researchRefreshTimer?.Stop();
            if (_activeResearchSessionId is not null) ShowChatView();
            RenderSessionTabs();
            return;
        }
        _researchRefreshTimer ??= CreateResearchRefreshTimer();
        _researchRefreshTimer.Start();
        if (!ResearchState(_session).Loaded) _ = RefreshNativeResearchAsync(_session);
        RenderResearchView();
        RenderSessionTabs();
    }

    private DispatcherTimer CreateResearchRefreshTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) =>
        {
            if (!_disposed && _mode == "claudescience" && (_running || _activeResearchSessionId == _session))
                _ = RefreshNativeResearchAsync(_session);
        };
        return timer;
    }

    private async Task RefreshNativeResearchAsync(Guid sessionId)
    {
        if (_disposed || sessionId == Guid.Empty || !_researchLoading.Add(sessionId)) return;
        if (_activeResearchSessionId == sessionId) UpdateResearchChrome(ResearchState(sessionId));
        string? requestedProject = null;
        try
        {
            if (!await CommandAsync("research.list", new { sessionId }, error => ShowResearchError(sessionId, error))) return;
            var state = ResearchState(sessionId);
            if (!state.Available || state.Projects.Length == 0) return;
            var selectedId = state.SelectedProjectId;
            if (selectedId is null || !state.Projects.Any(project => S(project, "id") == selectedId))
                selectedId = S(state.Projects[0], "id");
            if (state.SelectedProjectId != selectedId)
            {
                state.Detail = default; state.Revision = 0;
                state.ExportDirectory = null; state.ExportFiles = [];
            }
            state.SelectedProjectId = selectedId;
            state.RequestedProjectId = selectedId;
            requestedProject = selectedId;
            await CommandAsync("research.open", new { sessionId, projectId = selectedId }, error => ShowResearchError(sessionId, error));
        }
        finally
        {
            _researchLoading.Remove(sessionId);
            if (!_disposed && _activeResearchSessionId == sessionId) RenderResearchView();
            // A user may select another project while the old read is in flight.
            // Its response is ignored by RequestedProjectId; load the new choice next.
            if (!_disposed && requestedProject is not null && ResearchState(sessionId).RequestedProjectId is { } nextProject
                && nextProject != requestedProject) _ = RefreshNativeResearchAsync(sessionId);
        }
    }

    private bool ApplyResearchEvent(string type, JsonElement data)
    {
        if (type is not ("research.snapshot" or "research.exported")) return false;
        if (!Guid.TryParse(S(data, "sessionId"), out var sessionId) || sessionId == Guid.Empty) return true;
        var state = ResearchState(sessionId);
        state.Error = "";
        if (type == "research.exported")
        {
            if (S(data, "projectId") == state.SelectedProjectId)
            {
                state.ExportDirectory = S(data, "directory");
                state.ExportFiles = Items(data, "files").Select(item => item.ToString()).ToArray();
            }
            state.Exporting = false;
        }
        else
        {
            state.Loaded = true;
            state.Available = S(data, "available") != "False";
            state.DisabledReason = S(data, "disabledReason");
            state.Projects = Items(data, "projects").Select(project => project.Clone()).ToArray();
            var detail = ResearchObject(data, "detail");
            var project = ResearchObject(detail, "project");
            var projectId = S(project, "id");
            if (detail.ValueKind == JsonValueKind.Object && projectId.Length > 0
                && (state.RequestedProjectId is null || projectId == state.RequestedProjectId))
            {
                var revision = ResearchRevision(project);
                if (projectId != S(ResearchObject(state.Detail, "project"), "id") || revision >= state.Revision)
                {
                    state.Detail = detail.Clone();
                    state.Revision = revision;
                    state.SelectedProjectId = projectId;
                }
            }
            if (state.Projects.Length == 0)
            {
                state.SelectedProjectId = null;
                state.Detail = default;
                state.Revision = 0;
            }
        }
        if (_activeResearchSessionId == sessionId) RenderResearchView();
        return true;
    }

    private void RefreshResearchAfterRun(string type)
    {
        if (_mode == "claudescience" && type is ("chat.completed" or "chat.cancelled" or "chat.failed"))
            _ = RefreshNativeResearchAsync(_session);
    }

    private void OpenResearchView()
    {
        if (_disposed || _mode != "claudescience") return;
        SaveReviewScrollOffset();
        _activeReviewRunId = null;
        ReviewScroll.Visibility = Visibility.Collapsed;
        BodyGrid.Visibility = Visibility.Collapsed;
        _activeResearchSessionId = _session;
        ResearchHost.Visibility = Visibility.Visible;
        RenderResearchView();
        RenderSessionTabs();
        _ = RefreshNativeResearchAsync(_session);
    }

    private void HideResearchView()
    {
        _activeResearchSessionId = null;
        ResearchHost.Visibility = Visibility.Collapsed;
    }

    private void RenderResearchTab(int index)
    {
        if (_mode != "claudescience")
        {
            if (_researchTabButton is not null) SessionTabsPanel.Children.Remove(_researchTabButton);
            return;
        }
        if (_researchTabButton is null)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            content.Children.Add(new FontIcon { Glyph = "\uE9CE", FontSize = 13 });
            content.Children.Add(new TextBlock { Text = "Forschung", FontSize = 13 });
            _researchTabButton = TabButton();
            _researchTabButton.Content = content;
            _researchTabButton.Height = 32;
            _researchTabButton.Padding = new Thickness(10, 0, 10, 0);
            _researchTabButton.BorderThickness = new Thickness(1);
            _researchTabButton.Click += (_, _) => OpenResearchView();
            AutomationProperties.SetName(_researchTabButton, "Forschung dieser Sitzung öffnen");
            ToolTipService.SetToolTip(_researchTabButton, "Deep Research · Fortschritt, Quellen, Analysen und Ergebnis");
        }
        var active = _activeResearchSessionId == _session && _activeReviewRunId is null;
        _researchTabButton.Background = active
            ? ResearchThemeBrush("MissumAccentSubtleBrush", 42)
            : ResearchThemeBrush("MissumLayerStrongBrush", 29);
        _researchTabButton.BorderBrush = active ? ResearchThemeBrush("MissumAccentBrush", 72) : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _researchTabButton.IsEnabled = !_sessionTabNavigationBusy;
        AutomationProperties.SetHelpText(_researchTabButton, active ? "Aktive Forschung" : "Forschung der geöffneten Sitzung");
        if (index < SessionTabsPanel.Children.Count && ReferenceEquals(SessionTabsPanel.Children[index], _researchTabButton)) return;
        SessionTabsPanel.Children.Remove(_researchTabButton);
        SessionTabsPanel.Children.Insert(Math.Min(index, SessionTabsPanel.Children.Count), _researchTabButton);
    }

    private void RenderResearchView()
    {
        if (_disposed || _activeResearchSessionId != _session || _mode != "claudescience") return;
        var state = ResearchState(_session);
        var signature = _session + "|" + state.View + "|" + state.SelectedProjectId + "|" + state.Loaded
            + "|" + state.Available + "|" + state.DisabledReason + "|" + state.ExportDirectory + "|" + string.Join("|", state.ExportFiles)
            + "|" + string.Join("", state.Projects.Select(project => project.GetRawText()))
            + "|" + (state.Detail.ValueKind == JsonValueKind.Object ? state.Detail.GetRawText() : "");
        if (signature == _researchRenderSignature) { UpdateResearchChrome(state); return; }
        _researchRenderSignature = signature;
        var renderedSessionId = _session;
        _researchChromeSessionId = renderedSessionId;
        _researchRuntimeStatus = null;
        _researchRuntimeProgress = null;
        _researchRuntimePill = null;
        _researchEmptyStatus = null;

        var root = new Grid
        {
            RowSpacing = 16,
            Padding = new Thickness(24, 18, 24, 0),
            MaxWidth = 1180,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });

        var headerContent = new StackPanel { Spacing = 18 };
        var title = new Grid { ColumnSpacing = 16 };
        title.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        title.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var identity = new Grid { ColumnSpacing = 13 };
        identity.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        identity.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var iconSurface = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(13),
            Background = ResearchThemeBrush("MissumAccentSubtleBrush", 42),
            BorderBrush = ResearchThemeBrush("MissumAccentBrush", 96),
            BorderThickness = new Thickness(1),
            Child = new FontIcon
            {
                Glyph = "\uE9CE",
                FontSize = 21,
                Foreground = ResearchThemeBrush("MissumAccentBrush", 180),
            },
        };
        AutomationProperties.SetName(iconSurface, "Claude Science Forschung");
        identity.Children.Add(iconSurface);
        var titleText = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        var heading = ResearchText("Claude Science", 24);
        heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        titleText.Children.Add(heading);
        titleText.Children.Add(ResearchMutedText(
            "Deep Research sammelt Quellen, dokumentiert Analysen und macht Nachweise nachvollziehbar.", 13));
        Grid.SetColumn(titleText, 1);
        identity.Children.Add(titleText);
        title.Children.Add(identity);
        var back = ResearchButton(
            "Zum Chat", () => { ShowChatView(); RenderSessionTabs(); }, "\uE72B", "Zur Chatansicht dieser Sitzung");
        back.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(back, 1);
        title.Children.Add(back);
        headerContent.Children.Add(title);

        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var runtimeProgress = new ProgressRing
        {
            Width = 14,
            Height = 14,
            IsActive = false,
            Foreground = ResearchThemeBrush("MissumAccentBrush", 180),
        };
        _researchRuntimeProgress = runtimeProgress;
        var runtimeStatus = ResearchText("Bereit", 12);
        runtimeStatus.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _researchRuntimeStatus = runtimeStatus;
        var statusContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        statusContent.Children.Add(runtimeProgress);
        statusContent.Children.Add(runtimeStatus);
        var runtimePill = ResearchPill(statusContent, accent: true);
        _researchRuntimePill = runtimePill;
        AutomationProperties.SetName(runtimePill, "Status der Forschung");
        statusRow.Children.Add(runtimePill);
        statusRow.Children.Add(ResearchPill(ResearchMutedText(
            state.Projects.Length == 1 ? "1 Vorhaben" : $"{state.Projects.Length} Vorhaben", 12)));
        headerContent.Children.Add(statusRow);

        var controls = new Grid { ColumnSpacing = 10 };
        controls.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var projects = _researchProjectPicker = new ComboBox
        {
            Header = "Forschungsvorhaben",
            PlaceholderText = "Noch kein Forschungsvorhaben",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinHeight = 40,
            IsEnabled = state.Projects.Length > 0,
        };
        AutomationProperties.SetName(projects, "Forschungsvorhaben dieser Sitzung");
        foreach (var project in state.Projects)
        {
            var label = S(project, "interpretedQuestion", S(project, "originalQuestion", S(project, "id")));
            projects.Items.Add(new ComboBoxItem { Content = label, Tag = S(project, "id") });
        }
        projects.SelectedItem = projects.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == state.SelectedProjectId);
        projects.SelectionChanged += async (_, _) =>
        {
            if (_session != renderedSessionId || _activeResearchSessionId != renderedSessionId || !ReferenceEquals(projects, _researchProjectPicker)) return;
            if (projects.SelectedItem is not ComboBoxItem selected || selected.Tag?.ToString() is not { } projectId || projectId == state.SelectedProjectId) return;
            state.SelectedProjectId = state.RequestedProjectId = projectId;
            state.Detail = default; state.Revision = 0; state.ExportDirectory = null; state.ExportFiles = []; state.Error = "";
            RenderResearchView();
            await RefreshNativeResearchAsync(renderedSessionId);
        };
        controls.Children.Add(projects);
        var refresh = _researchRefreshButton = ResearchButton(
            "Aktualisieren", () => _ = RefreshNativeResearchAsync(renderedSessionId), "\uE72C", "Gespeicherten Forschungsstand neu laden");
        refresh.Margin = new Thickness(0, 24, 0, 0);
        refresh.IsEnabled = !_researchLoading.Contains(_session);
        Grid.SetColumn(refresh, 1);
        controls.Children.Add(refresh);
        var export = _researchExportButton = ResearchButton(
            "Exportieren", () => _ = ExportResearchAsync(renderedSessionId), "\uE74E", "Forschungspaket im Projektordner speichern", accent: true);
        export.Margin = new Thickness(0, 24, 0, 0);
        var selectedProject = ResearchObject(state.Detail, "project");
        export.IsEnabled = !state.Exporting && S(selectedProject, "workspacePath").Length > 0
            && S(ResearchObject(_snapshot, "scienceCapabilities"), "export") == "True";
        ToolTipService.SetToolTip(export, export.IsEnabled
            ? "Forschungspaket im Projektordner speichern"
            : "Ein gespeichertes Vorhaben mit Forschungsordner ist erforderlich");
        Grid.SetColumn(export, 2);
        controls.Children.Add(export);
        headerContent.Children.Add(controls);
        _researchErrorBar = new InfoBar
        {
            Severity = InfoBarSeverity.Error,
            IsClosable = true,
            Title = "Forschungsstand konnte nicht aktualisiert werden",
        };
        _researchErrorBar.Closed += (_, _) => state.Error = "";
        headerContent.Children.Add(_researchErrorBar);
        root.Children.Add(new Border
        {
            Padding = new Thickness(22, 20, 22, 20),
            CornerRadius = new CornerRadius(18),
            Background = ResearchThemeBrush("MissumLayerBrush", 31),
            BorderBrush = ResearchThemeBrush("MissumStrokeBrush", 54),
            BorderThickness = new Thickness(1),
            Child = headerContent,
        });

        var tabRows = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (key, label, glyph) in new[]
        {
            ("overview", "Überblick", "\uE80F"),
            ("progress", "Fortschritt", "\uE9D9"),
            ("sources", "Quellen", "\uE8A5"),
            ("analysis", "Analysen", "\uE9F9"),
            ("result", "Ergebnis", "\uE8A1"),
            ("verification", "Nachweise", "\uE73E"),
        })
        {
            var active = state.View == key;
            var button = ResearchButton(label, () => { state.View = key; RenderResearchView(); }, glyph);
            button.MinHeight = 36;
            button.Padding = new Thickness(12, 6, 12, 6);
            button.Background = active
                ? ResearchThemeBrush("MissumAccentSubtleBrush", 47)
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.BorderBrush = active
                ? ResearchThemeBrush("MissumAccentBrush", 96)
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            AutomationProperties.SetHelpText(button, active ? "Ausgewählter Forschungsbereich" : $"{label} öffnen");
            tabRows.Children.Add(button);
        }
        var tabsScroll = new ScrollViewer
        {
            Content = tabRows,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var tabs = new Border
        {
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(12),
            Background = ResearchThemeBrush("MissumLayerStrongBrush", 35),
            BorderBrush = ResearchThemeBrush("MissumStrokeBrush", 49),
            BorderThickness = new Thickness(1),
            Child = tabsScroll,
        };
        AutomationProperties.SetName(tabs, "Bereiche des Forschungsvorhabens");
        Grid.SetRow(tabs, 1);
        root.Children.Add(tabs);

        var content = new StackPanel { Spacing = 20, Padding = new Thickness(0, 4, 0, 28) };
        if (!state.Available)
        {
            content.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Warning,
                Title = "Forschung ist derzeit nicht verfügbar",
                Message = state.DisabledReason,
            });
        }
        else if (state.Detail.ValueKind != JsonValueKind.Object)
        {
            var emptyStatus = ResearchText("", 20);
            emptyStatus.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            _researchEmptyStatus = emptyStatus;
            AutomationProperties.SetHeadingLevel(emptyStatus, AutomationHeadingLevel.Level2);
            content.Children.Add(ResearchEmptyCard(
                "\uE9CE",
                emptyStatus,
                "Stelle im Chat eine Forschungsfrage. Quellen, Fortschritt, Analysen und Ergebnisse werden anschließend automatisch in dieser Sitzung geordnet.",
                "Forschungsfrage im Chat stellen",
                () => { ShowChatView(); RenderSessionTabs(); Composer.Focus(FocusState.Programmatic); }));
        }
        else RenderResearchContent(content, state);
        if (!string.IsNullOrWhiteSpace(state.ExportDirectory))
        {
            var directory = state.ExportDirectory;
            var exportContent = new StackPanel { Spacing = 10 };
            var exportTitle = ResearchText("Forschungspaket gespeichert", 16);
            exportTitle.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            exportContent.Children.Add(exportTitle);
            exportContent.Children.Add(ResearchMutedText(
                $"{state.ExportFiles.Length} Dateien · {state.ExportDirectory}", 12));
            exportContent.Children.Add(ResearchButton("Exportordner öffnen", async () =>
            {
                try { if (Directory.Exists(directory)) await Launcher.LaunchFolderAsync(await StorageFolder.GetFolderFromPathAsync(directory)); }
                catch (Exception exception) { ShowResearchError(renderedSessionId, exception.Message); }
            }, "\uE8B7", "Gespeicherten Export im Datei-Explorer öffnen"));
            content.Children.Add(new Border
            {
                Padding = new Thickness(16),
                CornerRadius = new CornerRadius(14),
                Background = ResearchThemeBrush("MissumAccentSubtleBrush", 41),
                BorderBrush = ResearchThemeBrush("MissumAccentBrush", 82),
                BorderThickness = new Thickness(1),
                Child = exportContent,
            });
        }
        var scroll = new ScrollViewer
        {
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var scrollKey = state.SelectedProjectId + "|" + state.View;
        var savedOffset = state.ScrollOffsets.GetValueOrDefault(scrollKey);
        scroll.Loaded += (_, _) => scroll.ChangeView(null, savedOffset, null, disableAnimation: true);
        scroll.ViewChanged += (_, args) => { if (!args.IsIntermediate) state.ScrollOffsets[scrollKey] = scroll.VerticalOffset; };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
        ResearchHost.Content = root;
        UpdateResearchChrome(state);
    }

    private static void RenderResearchContent(StackPanel content, NativeResearchSession state)
    {
        var detail = state.Detail;
        var project = ResearchObject(detail, "project");
        var checkpoint = ResearchObject(detail, "checkpoint");
        var intermediate = S(ResearchObject(detail, "report"), "reportKind") == "researchProgress";
        var question = S(project, "interpretedQuestion", S(project, "originalQuestion"));

        var questionContent = new StackPanel { Spacing = 9 };
        var questionLabel = ResearchMutedText("FORSCHUNGSFRAGE", 11);
        questionLabel.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        questionContent.Children.Add(questionLabel);
        var questionText = ResearchText(question, 21);
        questionText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetHeadingLevel(questionText, AutomationHeadingLevel.Level2);
        questionContent.Children.Add(questionText);
        var metadata = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        metadata.Children.Add(ResearchPill(ResearchMutedText(ResearchStatus(S(project, "status")), 12), accent: true));
        if (S(project, "profile").Length > 0)
            metadata.Children.Add(ResearchPill(ResearchMutedText(S(project, "profile"), 12)));
        metadata.Children.Add(ResearchPill(ResearchMutedText("Stand " + S(project, "revision"), 12)));
        if (S(checkpoint, "runId").Length > 0)
            metadata.Children.Add(ResearchPill(ResearchMutedText(
                "Lauf " + ResearchCompactIdentifier(S(checkpoint, "runId")) + " · " + S(checkpoint, "stage"), 12)));
        questionContent.Children.Add(metadata);
        if (S(project, "originalQuestion").Length > 0 && S(project, "originalQuestion") != question)
            questionContent.Children.Add(ResearchMutedText("Ursprüngliche Frage: " + S(project, "originalQuestion"), 12));
        content.Children.Add(new Border
        {
            Padding = new Thickness(18),
            CornerRadius = new CornerRadius(15),
            Background = ResearchThemeBrush("MissumLayerBrush", 31),
            BorderBrush = ResearchThemeBrush("MissumStrokeBrush", 50),
            BorderThickness = new Thickness(1),
            Child = questionContent,
        });

        if (intermediate)
        {
            content.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Informational,
                Title = S(project, "status") == "active" ? "Forschung läuft" : "Zwischenstand gespeichert",
                Message = Items(detail, "works").Length == 0
                    ? "Noch keine gelesenen Originalbelege gespeichert."
                    : "Originalquellen wurden gelesen. Auszüge gelten erst nach der Prüfung als belastbare Nachweise.",
            });
        }

        switch (state.View)
        {
            case "sources":
                ResearchHeading(content, "Literatur und Quellen", Items(detail, "works").Length);
                foreach (var work in Items(detail, "works"))
                {
                    var card = ResearchCard(content);
                    var url = S(work, "canonicalUrl");
                    var workTitle = S(work, "title", url.Length > 0 ? url : S(work, "workId"));
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                    {
                        var linkText = ResearchText(workTitle, 15);
                        linkText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                        var link = new HyperlinkButton
                        {
                            Content = linkText,
                            NavigateUri = uri,
                            HorizontalAlignment = HorizontalAlignment.Left,
                            HorizontalContentAlignment = HorizontalAlignment.Left,
                            Padding = new Thickness(0),
                        };
                        AutomationProperties.SetName(link, $"Quelle öffnen: {workTitle}");
                        card.Children.Add(link);
                    }
                    else
                    {
                        var sourceTitle = ResearchText(workTitle, 15);
                        sourceTitle.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                        card.Children.Add(sourceTitle);
                    }
                    card.Children.Add(ResearchMutedText(S(work, "evidenceLevel") == "retrievedExcerpt"
                        ? "Gelesen · Prüfung steht aus"
                        : $"{ResearchStatus(S(work, "screeningStatus"))} · {S(work, "evidenceLevel")}", 12));
                    if (url.Length > 0) card.Children.Add(ResearchMutedText(url, 12));
                }
                RenderResearchEvidence(content, detail, intermediate);
                break;

            case "analysis":
                ResearchHeading(content, "Analysen und Experimente", Items(detail, "experiments").Length);
                foreach (var experiment in Items(detail, "experiments"))
                {
                    var card = ResearchCard(content);
                    card.Children.Add(ResearchMutedText("AUSGEFÜHRTER SCHRITT", 11));
                    card.Children.Add(ResearchCodeBlock(S(experiment, "commandText")));
                    card.Children.Add(ResearchMutedText(
                        "Prüfstatus: " + ResearchStatus(S(experiment, "verificationStatus"))
                        + " · Umgebung: " + S(experiment, "environmentLock"), 12));
                    foreach (var (name, key) in new[]
                    {
                        ("Ausgabe", "stdoutEvidence"),
                        ("Fehlerausgabe", "stderrEvidence"),
                        ("Ergebnisartefakte", "resultArtifactsJson"),
                        ("Eingabedateien", "inputHashesJson"),
                    })
                    {
                        if (S(experiment, key).Length > 0)
                            card.Children.Add(ResearchExpander(state, "experiment|" + S(experiment, "id") + "|" + key,
                                name, ResearchText(S(experiment, key), 12)));
                    }
                }
                break;

            case "result":
                var report = ResearchObject(detail, "report");
                var reportContent = S(report, "contentMarkdown");
                ResearchHeading(content, "Forschungsbericht", reportContent.Length > 0 ? 1 : 0);
                if (reportContent.Length > 0)
                {
                    content.Children.Add(ResearchPill(ResearchMutedText(
                        $"{S(report, "reportKind")} · {ResearchStatus(S(report, "conclusionStatus"))}", 12), accent: true));
                    content.Children.Add(new Border
                    {
                        Padding = new Thickness(20),
                        CornerRadius = new CornerRadius(15),
                        Background = ResearchThemeBrush("MissumLayerBrush", 31),
                        BorderBrush = ResearchThemeBrush("MissumStrokeBrush", 50),
                        BorderThickness = new Thickness(1),
                        Child = new NativeStreamingMarkdown(reportContent),
                    });
                }
                break;

            case "verification":
                if (intermediate)
                {
                    RenderResearchEvidence(content, detail, intermediate: true);
                    break;
                }
                ResearchHeading(content, "Prüfmatrix", Items(detail, "verifications").Length);
                foreach (var verification in Items(detail, "verifications"))
                {
                    var card = ResearchCard(content);
                    var verificationTitle = ResearchText(
                        S(verification, "dimension") + " · " + ResearchStatus(S(verification, "status")), 15);
                    verificationTitle.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                    card.Children.Add(verificationTitle);
                    card.Children.Add(ResearchMutedText(
                        S(verification, "method") + "\n" + S(verification, "targetType") + ": " + S(verification, "targetId"), 12));
                    if (S(verification, "evidenceJson").Length > 0)
                        card.Children.Add(ResearchExpander(state, "verification|" + S(verification, "id"),
                            "Prüfbeleg", ResearchText(S(verification, "evidenceJson"), 12)));
                }
                ResearchHeading(content, "Aussagen und Evidenzstatus", Items(detail, "claims").Length);
                foreach (var claim in Items(detail, "claims"))
                {
                    var card = ResearchCard(content);
                    var statement = ResearchText(S(claim, "statement"), 15);
                    statement.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                    card.Children.Add(statement);
                    card.Children.Add(ResearchMutedText(
                        S(claim, "claimClass") + " · " + ResearchStatus(S(claim, "conclusionStatus"))
                        + " · Konfidenz " + S(claim, "confidence"), 12));
                }
                break;

            default:
                if (state.View == "overview")
                {
                    RenderResearchMetrics(content, detail, intermediate);
                    ResearchHeading(content, "Arbeitsrahmen", 1, showCount: false);
                    var scope = ResearchCard(content);
                    scope.Children.Add(ResearchText(
                        "Autonomie: " + S(project, "autonomyLevel") + " · Prüfung: " + S(project, "verificationLevel"), 14));
                    scope.Children.Add(ResearchMutedText(
                        "Forschungsordner: " + S(project, "workspacePath", "Noch nicht angelegt"), 12));
                }
                if (intermediate)
                {
                    content.Children.Add(ResearchSectionEmpty(
                        "\uE9D9", "Forschungsplan wird vorbereitet",
                        "Sobald der Plan gespeichert ist, erscheinen hier seine Schritte und der aktuelle Bearbeitungsstand."));
                    break;
                }
                ResearchHeading(content, "Forschungsplan", Items(detail, "nodes").Length);
                foreach (var node in Items(detail, "nodes"))
                {
                    var card = ResearchCard(content);
                    var nodeTitle = ResearchText(S(node, "title"), 15);
                    nodeTitle.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                    card.Children.Add(nodeTitle);
                    card.Children.Add(ResearchMutedText(
                        $"{ResearchStatus(S(node, "status"))} · {S(node, "nodeType")} · Versuche {S(node, "attemptCount")} · Konfidenz {S(node, "confidence")}", 12));
                    if (state.View == "progress")
                    {
                        card.Children.Add(ResearchExpander(state, "node|" + S(node, "id") + "|requirements",
                            "Beleg- und Prüfanforderungen",
                            ResearchText("Erforderliche Belege: " + S(node, "evidenceRequirementsJson")
                                + "\nPrüfanforderungen: " + S(node, "verificationRequirementsJson"), 12)));
                    }
                }
                if (state.View == "progress" && checkpoint.ValueKind == JsonValueKind.Object)
                    content.Children.Add(ResearchExpander(state, "checkpoint", "Gespeicherter Checkpoint",
                        ResearchText($"{S(checkpoint, "id")} · Revision {S(checkpoint, "revision")}\n{S(checkpoint, "stateJson")}", 12)));
                break;
        }
    }

    private static void RenderResearchEvidence(StackPanel content, JsonElement detail, bool intermediate)
    {
        ResearchHeading(content, intermediate ? "Gelesene Originalauszüge · noch nicht geprüft" : "Verortete Belege", Items(detail, "evidence").Length);
        foreach (var evidence in Items(detail, "evidence"))
        {
            var card = ResearchCard(content);
            var workId = S(evidence, "workId");
            var work = Items(detail, "works").FirstOrDefault(item => S(item, "workId") == workId);
            var evidenceTitle = ResearchText(S(work, "title", workId), 15);
            evidenceTitle.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            card.Children.Add(evidenceTitle);
            if (!intermediate && S(evidence, "normalizedStatement").Length > 0)
                card.Children.Add(ResearchText(S(evidence, "normalizedStatement")));
            if (S(evidence, "exactExcerpt").Length > 0)
            {
                card.Children.Add(new Border
                {
                    Padding = new Thickness(12),
                    CornerRadius = new CornerRadius(10),
                    Background = ResearchThemeBrush("MissumLayerStrongBrush", 37),
                    Child = ResearchText(S(evidence, "exactExcerpt"), 13),
                });
            }
            card.Children.Add(ResearchMutedText((S(evidence, "evidenceLevel") == "retrievedExcerpt" ? "Gelesen · noch nicht geprüft" : S(evidence, "evidenceLevel"))
                + "\n" + S(work, "canonicalUrl") + "\nSHA-256 " + S(evidence, "contentHash"), 12));
            if (S(evidence, "page").Length > 0 || S(evidence, "section").Length > 0 || S(evidence, "tableOrFigure").Length > 0)
                card.Children.Add(ResearchMutedText($"Seite {S(evidence, "page", "–")} · {S(evidence, "section")} · {S(evidence, "tableOrFigure")}", 12));
        }
    }

    private async Task ExportResearchAsync(Guid sessionId)
    {
        var state = ResearchState(sessionId);
        if (state.Exporting || state.SelectedProjectId is not { } projectId) return;
        state.Exporting = true; RenderResearchView();
        try { await CommandAsync("research.export", new { sessionId, projectId }, error => ShowResearchError(sessionId, error)); }
        finally { state.Exporting = false; RenderResearchView(); }
    }

    private void ShowResearchError(Guid sessionId, string error)
    {
        if (_disposed) return;
        var state = ResearchState(sessionId);
        state.Error = error;
        state.Loaded = true;
        if (_activeResearchSessionId == sessionId) RenderResearchView();
    }

    private void UpdateResearchRuntimeChrome()
    {
        if (_activeResearchSessionId == _session && _researchSessions.TryGetValue(_session, out var state))
            UpdateResearchChrome(state);
    }

    private void UpdateResearchChrome(NativeResearchSession state)
    {
        if (_disposed || _researchChromeSessionId != _session || _activeResearchSessionId != _session) return;
        var loading = _researchLoading.Contains(_session);
        if (_researchProjectPicker is not null) _researchProjectPicker.IsEnabled = state.Projects.Length > 0;
        if (_researchRefreshButton is not null)
        {
            _researchRefreshButton.IsEnabled = !loading;
            var refreshLabel = loading ? "Lädt …" : "Aktualisieren";
            if (AutomationProperties.GetName(_researchRefreshButton) != refreshLabel)
                SetResearchButtonContent(
                    _researchRefreshButton,
                    refreshLabel,
                    loading ? null : "\uE72C",
                    loading);
        }
        if (_researchExportButton is not null)
        {
            var exportLabel = state.Exporting ? "Export läuft …" : "Exportieren";
            if (AutomationProperties.GetName(_researchExportButton) != exportLabel)
                SetResearchButtonContent(
                    _researchExportButton,
                    exportLabel,
                    state.Exporting ? null : "\uE74E",
                    state.Exporting);
            _researchExportButton.IsEnabled = !state.Exporting && S(ResearchObject(state.Detail, "project"), "workspacePath").Length > 0
                && S(ResearchObject(_snapshot, "scienceCapabilities"), "export") == "True";
        }
        if (_researchErrorBar is not null)
        {
            _researchErrorBar.Message = state.Error;
            _researchErrorBar.IsOpen = state.Error.Length > 0;
        }
        if (_researchRuntimeStatus is not null)
        {
            var projectStatus = S(ResearchObject(state.Detail, "project"), "status");
            var projectRunning = projectStatus is "active" or "running";
            var busy = loading || state.Exporting || _running || projectRunning;
            _researchRuntimeStatus.Text = loading
                ? "Forschungsstand wird geladen"
                : state.Exporting
                    ? "Forschungspaket wird exportiert"
                    : _running && StatusText.Text.Length > 0
                        ? StatusText.Text
                        : projectRunning
                            ? "Forschung läuft"
                            : projectStatus == "completed"
                                ? "Forschung abgeschlossen"
                                : state.Projects.Length > 0
                                    ? "Gespeicherter Forschungsstand"
                                    : "Bereit für eine Forschungsfrage";
            _researchRuntimeStatus.Visibility = Visibility.Visible;
            if (_researchRuntimeProgress is not null)
            {
                _researchRuntimeProgress.IsActive = busy;
                _researchRuntimeProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_researchRuntimePill is not null)
                ToolTipService.SetToolTip(_researchRuntimePill, _researchRuntimeStatus.Text);
        }
        if (_researchEmptyStatus is not null)
            _researchEmptyStatus.Text = loading ? "Gespeicherten Forschungsstand laden …"
                : state.Error.Length > 0 ? "Der Forschungsstand konnte nicht geladen werden."
                : !state.Loaded ? "Gespeicherten Forschungsstand laden …" : "Noch kein Forschungsvorhaben gespeichert.";
    }

    private static Expander ResearchExpander(NativeResearchSession state, string key, string title, TextBlock content)
    {
        var identity = state.SelectedProjectId + "|" + key;
        var expander = new Expander { Header = title, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = content, IsExpanded = state.ExpandedItems.Contains(identity) };
        expander.RegisterPropertyChangedCallback(Expander.IsExpandedProperty, (_, _) =>
        {
            if (expander.IsExpanded) state.ExpandedItems.Add(identity);
            else state.ExpandedItems.Remove(identity);
        });
        return expander;
    }

    private static JsonElement ResearchObject(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : default;

    private static long ResearchRevision(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty("revision", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var revision) ? revision : 0;

    private static TextBlock ResearchText(string text, double size = 14) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = size <= 12
            ? ResearchThemeBrush("MissumMutedTextBrush", 167)
            : ResearchThemeBrush("TextFillColorPrimaryBrush", 234),
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
    };

    private static TextBlock ResearchMutedText(string text, double size = 13) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = ResearchThemeBrush("MissumMutedTextBrush", 167),
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
    };

    private static SolidColorBrush ResearchThemeBrush(string key, byte fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is SolidColorBrush brush)
            return brush;
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, fallback, fallback, fallback));
    }

    private static Button ResearchButton(
        string label,
        Action action,
        string? glyph = null,
        string? toolTip = null,
        bool accent = false)
    {
        var button = new Button
        {
            MinHeight = 38,
            Padding = new Thickness(13, 7, 13, 7),
            Background = ResearchThemeBrush(accent ? "MissumAccentBrush" : "MissumLayerStrongBrush", accent ? (byte)176 : (byte)38),
            Foreground = ResearchThemeBrush(accent ? "MissumAccentTextBrush" : "TextFillColorPrimaryBrush", accent ? (byte)20 : (byte)234),
            BorderThickness = new Thickness(1),
            BorderBrush = ResearchThemeBrush(accent ? "MissumAccentBrush" : "MissumStrokeBrush", accent ? (byte)176 : (byte)59),
            CornerRadius = new CornerRadius(10),
        };
        SetResearchButtonContent(button, label, glyph);
        button.Click += (_, _) => action();
        AutomationProperties.SetName(button, label);
        if (!string.IsNullOrWhiteSpace(toolTip)) ToolTipService.SetToolTip(button, toolTip);
        return button;
    }

    private static void SetResearchButtonContent(Button button, string label, string? glyph, bool busy = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        if (busy)
        {
            content.Children.Add(new ProgressRing
            {
                Width = 14,
                Height = 14,
                IsActive = true,
                Foreground = button.Foreground,
            });
        }
        else if (!string.IsNullOrWhiteSpace(glyph))
        {
            content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        }
        content.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        button.Content = content;
        AutomationProperties.SetName(button, label);
    }

    private static Border ResearchPill(UIElement child, bool accent = false) => new()
    {
        MinHeight = 28,
        Padding = new Thickness(10, 4, 10, 4),
        CornerRadius = new CornerRadius(9),
        Background = ResearchThemeBrush(accent ? "MissumAccentSubtleBrush" : "MissumLayerStrongBrush", accent ? (byte)42 : (byte)37),
        BorderBrush = ResearchThemeBrush(accent ? "MissumAccentBrush" : "MissumStrokeBrush", accent ? (byte)90 : (byte)54),
        BorderThickness = new Thickness(1),
        Child = child,
    };

    private static Border ResearchEmptyCard(
        string glyph,
        TextBlock title,
        string description,
        string actionLabel,
        Action action)
    {
        var body = new StackPanel
        {
            Spacing = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        body.Children.Add(new Border
        {
            Width = 54,
            Height = 54,
            CornerRadius = new CornerRadius(17),
            Background = ResearchThemeBrush("MissumAccentSubtleBrush", 42),
            Child = new FontIcon
            {
                Glyph = glyph,
                FontSize = 25,
                Foreground = ResearchThemeBrush("MissumAccentBrush", 180),
            },
        });
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.TextAlignment = TextAlignment.Center;
        body.Children.Add(title);
        var message = ResearchMutedText(description, 14);
        message.MaxWidth = 560;
        message.TextAlignment = TextAlignment.Center;
        message.HorizontalAlignment = HorizontalAlignment.Center;
        body.Children.Add(message);
        var actionButton = ResearchButton(actionLabel, action, "\uE70F", accent: true);
        actionButton.HorizontalAlignment = HorizontalAlignment.Center;
        body.Children.Add(actionButton);
        return new Border
        {
            MinHeight = 270,
            MaxWidth = 700,
            Padding = new Thickness(36),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = ResearchThemeBrush("MissumLayerBrush", 31),
            BorderBrush = ResearchThemeBrush("MissumStrokeBrush", 52),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Child = body,
        };
    }

    private static Border ResearchSectionEmpty(string glyph, string title, string description)
    {
        var text = new StackPanel { Spacing = 3 };
        var heading = ResearchText(title, 14);
        heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        text.Children.Add(heading);
        text.Children.Add(ResearchMutedText(description, 12));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 19,
            Foreground = ResearchThemeBrush("MissumMutedTextBrush", 167),
        });
        row.Children.Add(text);
        return new Border
        {
            Padding = new Thickness(16),
            Background = ResearchThemeBrush("MissumLayerBrush", 31),
            BorderBrush = ResearchThemeBrush("MissumStrokeBrush", 49),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(13),
            Child = row,
        };
    }

    private static Border ResearchCodeBlock(string code) => new()
    {
        Padding = new Thickness(12),
        Background = ResearchThemeBrush("MissumLayerStrongBrush", 36),
        BorderBrush = ResearchThemeBrush("MissumStrokeBrush", 49),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Child = new TextBlock
        {
            Text = code,
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = 13,
            Foreground = ResearchThemeBrush("TextFillColorPrimaryBrush", 234),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        },
    };

    private static void RenderResearchMetrics(StackPanel content, JsonElement detail, bool intermediate)
    {
        var heading = ResearchText("Stand des Vorhabens", 18);
        heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        content.Children.Add(heading);
        var metrics = intermediate
            ? new[]
            {
                ("\uE8A5", "Quellen", Items(detail, "works").Length),
                ("\uE8D2", "Auszüge", Items(detail, "evidence").Length),
                ("\uE9F9", "Analysen", Items(detail, "experiments").Length),
                ("\uE73E", "Prüfungen", Items(detail, "verifications").Length),
            }
            : new[]
            {
                ("\uE9D9", "Planschritte", Items(detail, "nodes").Length),
                ("\uE8A5", "Quellen", Items(detail, "works").Length),
                ("\uE9F9", "Analysen", Items(detail, "experiments").Length),
                ("\uE73E", "Prüfungen", Items(detail, "verifications").Length),
            };
        var grid = new Grid { ColumnSpacing = 10 };
        for (var index = 0; index < metrics.Length; index++)
        {
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var metric = metrics[index];
            var metricBody = new StackPanel { Spacing = 8 };
            metricBody.Children.Add(new FontIcon
            {
                Glyph = metric.Item1,
                FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Left,
                Foreground = ResearchThemeBrush("MissumAccentBrush", 180),
            });
            var value = ResearchText(metric.Item3.ToString(System.Globalization.CultureInfo.CurrentCulture), 24);
            value.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            metricBody.Children.Add(value);
            metricBody.Children.Add(ResearchMutedText(metric.Item2, 12));
            var card = new Border
            {
                MinHeight = 112,
                Padding = new Thickness(15),
                Background = ResearchThemeBrush("MissumLayerBrush", 31),
                BorderBrush = ResearchThemeBrush("MissumStrokeBrush", 49),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Child = metricBody,
            };
            AutomationProperties.SetName(card, $"{metric.Item2}: {metric.Item3}");
            Grid.SetColumn(card, index);
            grid.Children.Add(card);
        }
        content.Children.Add(grid);
    }

    private static void ResearchHeading(StackPanel content, string heading, int count, bool showCount = true)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var title = ResearchText(heading, 18);
        title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level2);
        row.Children.Add(title);
        if (showCount)
        {
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var countPill = ResearchPill(ResearchMutedText(count.ToString(System.Globalization.CultureInfo.CurrentCulture), 12));
            Grid.SetColumn(countPill, 1);
            row.Children.Add(countPill);
        }
        content.Children.Add(row);
        if (count == 0)
            content.Children.Add(ResearchSectionEmpty("\uE946", "Noch keine Einträge",
                "Dieser Bereich füllt sich automatisch, sobald die Forschung entsprechende Daten gespeichert hat."));
    }

    private static StackPanel ResearchCard(StackPanel content)
    {
        var card = new StackPanel { Spacing = 9 };
        content.Children.Add(new Border
        {
            Child = card,
            Padding = new Thickness(16),
            Background = ResearchThemeBrush("MissumLayerBrush", 32),
            BorderBrush = ResearchThemeBrush("MissumStrokeBrush", 49),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
        });
        return card;
    }

    private static string ResearchCompactIdentifier(string value) =>
        value.Length <= 12 ? value : value[..8] + "…";

    private static string ResearchStatus(string value) => value switch
    {
        "pending" => "Ausstehend", "active" => "Läuft", "running" => "Läuft", "completed" => "Abgeschlossen", "failed" => "Fehlgeschlagen",
        "cancelled" => "Abgebrochen", "verified" => "Geprüft", "supported" => "Gestützt", "refuted" => "Widerlegt",
        "unresolved" => "Offen", "conflictingEvidence" => "Widersprüchliche Evidenz", "blocked" => "Blockiert", _ => value,
    };

    private sealed class NativeResearchSession
    {
        public JsonElement[] Projects { get; set; } = [];
        public JsonElement Detail { get; set; }
        public string? SelectedProjectId { get; set; }
        public string? RequestedProjectId { get; set; }
        public long Revision { get; set; }
        public string View { get; set; } = "overview";
        public bool Loaded { get; set; }
        public bool Available { get; set; } = true;
        public string DisabledReason { get; set; } = "";
        public bool Exporting { get; set; }
        public string Error { get; set; } = "";
        public string? ExportDirectory { get; set; }
        public string[] ExportFiles { get; set; } = [];
        public Dictionary<string, double> ScrollOffsets { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ExpandedItems { get; } = new(StringComparer.Ordinal);
    }
}
