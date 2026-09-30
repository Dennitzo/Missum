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
            RenderResearchView();
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
            App.Current.GetService<Missum.App.Services.ScientificPresentationCoordinator>().Queue(selectedId!);
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
                    App.Current.GetService<Missum.App.Services.ScientificPresentationCoordinator>().Queue(projectId);
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
        _simulationView = false;
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
            ToolTipService.SetToolTip(_researchTabButton, "Wissenschaftliche Publikation · PDF mit mathematischen Formeln");
        }
        var active = _activeResearchSessionId == _session && _activeReviewRunId is null && !_simulationView;
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

    private void ShowResearchError(Guid sessionId, string error)
    {
        if (_disposed) return;
        ResearchState(sessionId).Error = error;
        if (_activeResearchSessionId == sessionId) RenderResearchView();
    }

    private void UpdateResearchRuntimeChrome() => RenderResearchView();
    private void UpdateResearchChrome(NativeResearchSession state) => RenderResearchView();
    private static JsonElement ResearchObject(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
    private static long ResearchRevision(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty("revision", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var revision) ? revision : 0;
    private static SolidColorBrush ResearchThemeBrush(string key, byte fallback) => ThemeBrush(key, fallback);

    private sealed class NativeResearchSession
    {
        public JsonElement[] Projects { get; set; } = [];
        public JsonElement Detail { get; set; }
        public string? SelectedProjectId { get; set; }
        public string? RequestedProjectId { get; set; }
        public long Revision { get; set; }
        public bool Loaded { get; set; }
        public bool Available { get; set; } = true;
        public string DisabledReason { get; set; } = "";
        public bool Exporting { get; set; }
        public string Error { get; set; } = "";
        public string? ExportDirectory { get; set; }
        public string[] ExportFiles { get; set; } = [];
    }
}