using System.Text.Json;
using Missum.App.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private readonly Dictionary<Guid, NativeResearchSession> _researchSessions = [];
    private readonly HashSet<Guid> _researchLoading = [];
    private Guid? _activeResearchSessionId;
    private Button? _researchTabButton;
    private DispatcherTimer? _researchRefreshTimer;
    private long _researchTabSelectionVersion;

    private string ModelRole => _mode is "coding" or "claudescience" ? "coding" : "general";

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
            RunUiCallback("researchRefreshTimer.Tick", RenderResearchView);
            if (!_disposed && _mode == "claudescience" && (_activeResearchSessionId == _session && ResearchHost.Visibility == Visibility.Visible))
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
            if (!await CommandAsync("publication.list", new { sessionId }, error => ShowResearchError(sessionId, error))) return;
            var state = ResearchState(sessionId);
            if (!state.Available || state.Projects.Length == 0) return;
            var selectedId = state.SelectedProjectId;
            if (selectedId is null || !state.Projects.Any(project => S(project, "id") == selectedId))
                selectedId = S(state.Projects[0], "id");
            state.SelectedProjectId = selectedId;
            state.RequestedProjectId = selectedId;
            requestedProject = selectedId;
            await CommandAsync("publication.open", new { sessionId, projectId = selectedId }, error => ShowResearchError(sessionId, error));
            App.Current.GetService<Missum.App.Services.ScientificPresentationCoordinator>().Queue(selectedId!);
        }
        finally
        {
            _researchLoading.Remove(sessionId);
            if (!_disposed && _activeResearchSessionId == sessionId)
                RunUiCallback("Research.RefreshCompleted", RenderResearchView);
            // A user may select another project while the old read is in flight.
            // Its response is ignored by RequestedProjectId; load the new choice next.
            if (!_disposed && requestedProject is not null && ResearchState(sessionId).RequestedProjectId is { } nextProject
                && nextProject != requestedProject) _ = RefreshNativeResearchAsync(sessionId);
        }
    }

    private bool ApplyResearchEvent(string type, JsonElement data)
    {
        if (type != "publication.snapshot") return false;
        if (!Guid.TryParse(S(data, "sessionId"), out var sessionId) || sessionId == Guid.Empty) return true;
        var state = ResearchState(sessionId);
        state.Error = "";
        state.Loaded = true;
        state.Available = S(data, "available") != "False";
        state.Projects = Items(data, "projects").Select(project => project.Clone()).ToArray();
        var project = ResearchObject(ResearchObject(data, "detail"), "project");
        var projectId = S(project, "id");
        if (projectId.Length > 0 && (state.RequestedProjectId is null || projectId == state.RequestedProjectId))
        {
            state.SelectedProjectId = projectId;
            App.Current.GetService<Missum.App.Services.ScientificPresentationCoordinator>().Queue(projectId);
        }
        if (state.Projects.Length == 0) state.SelectedProjectId = null;
        if (_activeResearchSessionId == sessionId) RunUiCallback("Research.Snapshot", RenderResearchView);
        if (_session == sessionId) RenderSources(_snapshot);
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
        HideWorkingPlanView();
        HideToolView();
        HideSubagentOverview();
        _activeSourcesSession = null; _sourcesHost.Visibility = Visibility.Collapsed;
        SaveReviewScrollOffset();
        _activeReviewRunId = null;
        ReviewScroll.Visibility = Visibility.Collapsed;
        BodyGrid.Visibility = Visibility.Collapsed;
        _activeResearchSessionId = _session;
        // A retained PDF may have been replaced at its stable path while the
        // tab was hidden. Revalidate it on every explicit tab selection.
        _scienceViewSignature = null;
        ResearchHost.Visibility = Visibility.Visible;
        RunUiCallback("Research.Open", RenderResearchView);
        UpdatePublicationActivity();
        RenderSessionTabs();
        _ = RefreshNativeResearchAsync(_session);
    }

    private void HideResearchView()
    {
        _researchTabSelectionVersion++;
        _activeResearchSessionId = null;
        ResearchHost.Visibility = Visibility.Collapsed;
        foreach (var viewer in _publicationViews.Values) viewer.SetActive(false);
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
            content.Children.Add(CreateTabIcon("\uE9CE"));
            content.Children.Add(new TextBlock { Text = "Publikation", FontSize = 13 });
            _researchTabButton = TabButton();
            _researchTabButton.Content = content;
            _researchTabButton.Height = 32;
            _researchTabButton.Padding = new Thickness(10, 0, 10, 0);
            _researchTabButton.Click += (_, _) => QueueResearchTabSelection();
            AutomationProperties.SetName(_researchTabButton, "Publikation dieser Sitzung öffnen");
            ToolTipService.SetToolTip(_researchTabButton, "Wissenschaftliche Publikation · PDF mit mathematischen Formeln");
        }
        var active = _activeResearchSessionId == _session && _activeReviewRunId is null;
        ApplyTabAppearance(_researchTabButton, active);
        SetTabEnabled(_researchTabButton, !_sessionTabNavigationBusy);
        SetTabHelp(_researchTabButton, active ? "Aktive Publikation" : "Publikation der geöffneten Sitzung");
        PositionTab(_researchTabButton, index);
    }

    private void ShowResearchError(Guid sessionId, string error)
    {
        if (_disposed) return;
        ResearchState(sessionId).Error = error;
        if (_activeResearchSessionId == sessionId) RunUiCallback("Research.Error", RenderResearchView);
    }

    private void QueueResearchTabSelection()
    {
        var owner = _session;
        var version = ++_researchTabSelectionVersion;
        // WebView2/XAML changes must occur after the pointer's routed event has
        // unwound. A later navigation invalidates a queued, outdated selection.
        DispatcherQueue.TryEnqueue(() => RunUiCallback("Research.TabSelection", () =>
        {
            if (version != _researchTabSelectionVersion || owner != _session || _mode != "claudescience") return;
            OpenResearchView();
        }));
    }

    private void UpdateResearchRuntimeChrome() => RunUiCallback("Research.RuntimeChrome", RenderResearchView);
    private void UpdateResearchChrome(NativeResearchSession state) => RunUiCallback("Research.Chrome", RenderResearchView);
    private static JsonElement ResearchObject(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
    private sealed class NativeResearchSession
    {
        public JsonElement[] Projects { get; set; } = [];
        public string? SelectedProjectId { get; set; }
        public string? RequestedProjectId { get; set; }
        public bool Loaded { get; set; }
        public bool Available { get; set; } = true;
        public string Error { get; set; } = "";
    }
}
