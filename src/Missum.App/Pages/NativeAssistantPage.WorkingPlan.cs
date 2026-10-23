using System.Text.Json;
using Missum.App.Controls;
using Missum.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NoteIcon = Microsoft.UI.Xaml.Shapes.Path;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private DateTimeOffset _workingPlanUpdatedAt;
    private NativeWorkingPlanProjection.Snapshot _workingPlan = NativeWorkingPlanProjection.Snapshot.Empty;
    private NativeWorkingPlanProjection.Snapshot? _renderedWorkingPlan;
    private readonly NativeWorkingPlanView _workingPlanView = new();
    private readonly ContentControl _workingPlanHost = new() { Visibility = Visibility.Collapsed,
        HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly ScrollViewer _workingPlanScroll = new() { Padding = new(28, 20, 28, 28),
        HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _workingPlanScope = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap,
        Foreground = ThemeBrush("MissumMutedTextBrush", 160), Visibility = Visibility.Collapsed };
    private Button? _workingPlanTabButton;
    private Guid? _activeWorkingPlanSession;
    private string? _workingPlanAgentId;
    private JsonElement _workingPlanChildStep;
    private NativeWorkingPlanProjection.Snapshot _workingPlanChildSnapshot = NativeWorkingPlanProjection.Snapshot.Empty;
    private string? _workingPlanDetailRequest;
    private bool _workingPlanDetailsLoading;
    private bool _workingPlanUsed;
    private long _workingPlanNavigation;
    private string? _workingPlanScrollOwner;

    private bool HasWorkingPlanTab => _session != Guid.Empty
        && (_mode is "coding" or "claudescience" || _workingPlanUsed);

    private void ObserveWorkingPlan(JsonElement data, bool sessionChanged = false)
    {
        if (sessionChanged)
        {
            _workingPlanUpdatedAt = default;
            _workingPlan = NativeWorkingPlanProjection.Snapshot.Empty;
            _renderedWorkingPlan = null;
            _workingPlanUsed = false;
            HideWorkingPlanView();
        }
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("workingPlan", out var plan))
        {
            var updated = DateTimeOffset.TryParse(S(plan, "updatedAt"), out var time) ? time : default;
            // An asynchronous durable read can finish after a newer live receipt.
            if (sessionChanged || updated >= _workingPlanUpdatedAt)
            {
                _workingPlanUpdatedAt = updated;
                _workingPlan = NativeWorkingPlanProjection.Read(plan);
                _workingPlanUsed |= _workingPlan.Steps.Count + _workingPlan.AcceptanceCriteria.Count > 0 || S(plan, "stepId").Length > 0;
            }
        }
        RefreshWorkingPlanTab();
        if (!sessionChanged) RenderSessionTabs();
    }

    private int RenderWorkingPlanTab(int position)
    {
        if (!HasWorkingPlanTab)
        {
            if (_workingPlanTabButton is not null) SessionTabsPanel.Children.Remove(_workingPlanTabButton);
            if (_activeWorkingPlanSession is not null) ShowChatView();
            return position;
        }
        if (_workingPlanTabButton is null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            row.Children.Add(CreateWorkingPlanTabIcon());
            row.Children.Add(new TextBlock { Text = "Arbeitsplan", FontSize = 13 });
            _workingPlanTabButton = TabButton();
            _workingPlanTabButton.Content = row;
            _workingPlanTabButton.Padding = new(10, 0, 10, 0);
            _workingPlanTabButton.Click += async (_, _) => await OpenWorkingPlanTabAsync(ActiveSubagent?.AgentId);
            SetTabName(_workingPlanTabButton, "Arbeitsplan öffnen");
            SetTabToolTip(_workingPlanTabButton, "Aktueller vollständiger Arbeitsplan");
        }
        if (_activeWorkingPlanSession is { } owner && owner != _session) HideWorkingPlanView();
        var active = _activeWorkingPlanSession == _session;
        ApplyTabAppearance(_workingPlanTabButton, active);
        SetTabEnabled(_workingPlanTabButton, !_sessionTabNavigationBusy);
        SetTabHelp(_workingPlanTabButton, active ? "Aktiver Arbeitsplan" : "Arbeitsplan der geöffneten Sitzung");
        PositionTab(_workingPlanTabButton, position);
        return position + 1;
    }

    private async Task OpenWorkingPlanTabAsync(string? agentId = null)
    {
        if (_disposed || _session == Guid.Empty || _sessionTabNavigationBusy) return;
        SaveConversationOffset();
        ShowChatView();
        var navigation = ++_workingPlanNavigation;
        var session = _session;
        _workingPlanUsed = true;
        _workingPlanAgentId = agentId;
        _workingPlanChildStep = default;
        _workingPlanDetailRequest = null;
        _workingPlanChildSnapshot = NativeWorkingPlanProjection.Snapshot.Empty;
        _renderedWorkingPlan = null;
        _activeWorkingPlanSession = session;
        if (_workingPlanHost.Parent is null)
        {
            var parent = (Panel)ResearchHost.Parent;
            parent.Children.Add(_workingPlanHost);
            Grid.SetRow(_workingPlanHost, Grid.GetRow(ResearchHost));
            Grid.SetColumn(_workingPlanHost, Grid.GetColumn(ResearchHost));
            var content = new StackPanel { Spacing = 14 };
            content.Children.Add(_workingPlanScope);
            content.Children.Add(_workingPlanView);
            _workingPlanScroll.Content = content;
            _workingPlanHost.Content = _workingPlanScroll;
        }
        BodyGrid.Visibility = Visibility.Collapsed;
        _workingPlanHost.Visibility = Visibility.Visible;
        RefreshWorkingPlanTab();
        RenderSessionTabs();
        var scrollOwner = session + ":" + agentId;
        if (_workingPlanScrollOwner != scrollOwner)
        {
            _workingPlanScrollOwner = scrollOwner;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_disposed && navigation == _workingPlanNavigation)
                    _workingPlanScroll.ChangeView(null, 0, null, disableAnimation: true);
            });
        }
        if (agentId is not null && _subagents.TryGetValue(agentId, out var child) && child.SessionId == session)
        {
            if (!await EnsureSubagentTranscriptAsync(child) || navigation != _workingPlanNavigation || session != _session) return;
            RefreshWorkingPlanTab();
        }
    }

    private void HideWorkingPlanView()
    {
        _workingPlanNavigation++;
        _activeWorkingPlanSession = null;
        _workingPlanHost.Visibility = Visibility.Collapsed;
        _workingPlanView.SetViewActive(false);
    }

    private void RefreshWorkingPlanTab()
    {
        var active = _activeWorkingPlanSession == _session && _workingPlanHost.Visibility == Visibility.Visible;
        _workingPlanView.SetViewActive(active);
        var child = _workingPlanAgentId is { } id && _subagents.TryGetValue(id, out var found) && found.SessionId == _session ? found : null;
        _workingPlanView.SetRunActive(child is not null ? child.IsRunning : _running);
        if (!active) return;
        var plan = _workingPlan;
        if (child is not null)
        {
            // The compact plan is independent of the child's paged tool history.
            // Legacy snapshots still hydrate their most recent available receipt.
            if (child.Snapshot.TryGetProperty("workingPlan", out var current) && current.ValueKind == JsonValueKind.Object)
            {
                if (!_workingPlanChildStep.Equals(current))
                {
                    _workingPlanChildStep = current;
                    _workingPlanChildSnapshot = NativeWorkingPlanProjection.Read(current);
                }
            }
            else
            {
                var source = LatestSubagentPlan(child);
                var latest = source.Step;
                if (!_workingPlanChildStep.Equals(latest))
                {
                    _workingPlanChildStep = latest;
                    _workingPlanChildSnapshot = ToolTabDetailsDeferred(latest) ? NativeWorkingPlanProjection.Snapshot.Empty
                        : NativeWorkingPlanProjection.Read(ToolStepView.ReadMetadata(S(latest, "outputJson")), ToolStepView.ReadMetadata(S(latest, "inputJson")));
                }
                var request = source.MessageId + ":" + S(latest, "id") + ":" + S(latest, "updatedAt");
                if (ToolTabDetailsDeferred(latest) && !_workingPlanDetailsLoading && _workingPlanDetailRequest != request)
                {
                    _workingPlanDetailRequest = request;
                    _ = LoadWorkingPlanDetailsAsync(source.MessageId, S(latest, "id"));
                }
            }
            plan = _workingPlanChildSnapshot;
        }
        _workingPlanScope.Text = child is not null ? "Subagent · " + child.Title : "";
        _workingPlanScope.Visibility = child is not null ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(plan, _renderedWorkingPlan))
        {
            _workingPlanView.Update(plan);
            _renderedWorkingPlan = plan;
        }
    }

    private async Task LoadWorkingPlanDetailsAsync(string messageId, string stepId)
    {
        _workingPlanDetailsLoading = true;
        try { await LoadToolStepDetailsAsync(messageId, stepId); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { if (!_disposed) ShowError(exception.Message); }
        finally
        {
            _workingPlanDetailsLoading = false;
            if (!_disposed) RefreshWorkingPlanTab();
        }
    }

    private (string MessageId, JsonElement Step) LatestSubagentPlan(NativeSubagentState child) =>
        child.Messages.Values.OrderBy(MessageCreatedAt)
            .SelectMany(message => MessageSteps(message).Where(step => S(step, "tool") == "coding.updatePlan" && S(step, "status") == "completed")
                .Select(step => (MessageId: S(message, "id"), Step: step))).LastOrDefault();

    private static Viewbox CreateWorkingPlanTabIcon()
    {
        // A folded note, using the same mutable icon brush as all native tabs.
        var geometry = new PathGeometry();
        geometry.Figures.Add(new PathFigure { StartPoint = new(2, 1), IsClosed = true, IsFilled = false,
            Segments = { new PolyLineSegment { Points = new PointCollection { new(10, 1), new(14, 5), new(14, 15), new(2, 15) } } } });
        geometry.Figures.Add(new PathFigure { StartPoint = new(10, 1), IsFilled = false,
            Segments = { new PolyLineSegment { Points = new PointCollection { new(10, 5), new(14, 5) } } } });
        geometry.Figures.Add(new PathFigure { StartPoint = new(5, 8), IsFilled = false, Segments = { new LineSegment { Point = new(11, 8) } } });
        geometry.Figures.Add(new PathFigure { StartPoint = new(5, 11), IsFilled = false, Segments = { new LineSegment { Point = new(10, 11) } } });
        var canvas = new Canvas { Width = 16, Height = 16,
            Clip = new RectangleGeometry { Rect = new(0, 0, 16, 16) } };
        canvas.Children.Add(new NoteIcon { Data = geometry, Stretch = Stretch.None,
            Stroke = NativeIconPalette.BrushFor("navigation"), StrokeThickness = 1.2 });
        return new Viewbox { Child = canvas, Width = 13, Height = 13, Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center };
    }
}
