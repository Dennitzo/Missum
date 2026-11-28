using Missum.App.Controls;
using Missum.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private string? _scienceViewSignature;
    private readonly Dictionary<Guid, NativePublicationView> _publicationViews = [];
    private readonly Dictionary<string, NativePublicationView> _publicationPool = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _publicationRecency = new();
    private readonly Dictionary<string, FrameworkElement> _scienceEmptyBodies = new(StringComparer.Ordinal);
    private Grid? _scienceRoot, _scienceBody, _scienceBusy;
    private InfoBar? _scienceNotice;
    private ProgressRing? _scienceBusyRing;
    private TextBlock? _scienceBusyTitle, _scienceBusyDetail;
    private bool _scienceBuildEventsAttached;
    private bool _scienceRendering, _scienceRenderAgain;
    private Func<string, ScientificPresentationSnapshot?>? _sciencePresentationSmokeProvider;

    private void RenderResearchView()
    {
        if (_disposed || _activeResearchSessionId != _session || _mode != "claudescience") return;
        if (_scienceRendering) { _scienceRenderAgain = true; return; }
        _scienceRendering = true;
        try { RenderResearchViewCore(); }
        finally
        {
            _scienceRendering = false;
            if (_scienceRenderAgain) { _scienceRenderAgain = false; _scienceViewSignature = null; }
        }
    }

    private void RenderResearchViewCore()
    {
        var owner = _session;
        var state = ResearchState(owner);
        var presentation = state.SelectedProjectId is { } id ? SciencePresentationSnapshot(id) : null;
        var contentKey = presentation?.Publication is { } publication ? PublicationContentKey(publication) : null;
        var busy = SciencePublicationBusyState(owner, state, presentation);
        var signature = $"{owner}|{state.SelectedProjectId}|{contentKey}|{state.Error}|{presentation?.PublicationError}|{state.Loaded}|{busy}";
        if (signature == _scienceViewSignature && ReferenceEquals(ResearchHost.Content, _scienceRoot))
        { UpdatePublicationActivity(); return; }
        EnsureScienceVisualTree();
        var error = state.Error.Length > 0 ? state.Error : presentation?.PublicationError;
        _scienceNotice!.Message = error ?? "";
        _scienceNotice.IsOpen = !string.IsNullOrWhiteSpace(error);
        _scienceNotice.Visibility = _scienceNotice.IsOpen ? Visibility.Visible : Visibility.Collapsed;

        FrameworkElement body;
        if (presentation?.Publication is { } pdf)
        {
            var viewer = GetPublicationView(owner, pdf.PdfPath);
            if (viewer.Parent is null) _scienceBody!.Children.Add(viewer);
            body = viewer;
            _ = LoadPublicationAsync(viewer, pdf, owner, state.SelectedProjectId!);
        }
        else
        {
            var key = owner + "|" + (state.SelectedProjectId is null ? "no-project" : "publication-pending");
            if (!_scienceEmptyBodies.TryGetValue(key, out body!))
            {
                var content = new StackPanel { Spacing = 10, MaxWidth = 540, HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center };
                content.Children.Add(new TextBlock { Text = state.SelectedProjectId is null
                    ? "Öffne ein Projekt und beginne im Chat." : "Noch keine Publikation vorhanden.", FontSize = 20,
                    TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                content.Children.Add(new TextBlock { Text = "Die Publikation wird direkt im Projekt bearbeitet. Hier erscheint ihre aktuelle PDF-Ansicht.",
                    FontSize = 14, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                body = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 0, 0, 16) };
                _scienceEmptyBodies.Add(key, body); _scienceBody!.Children.Add(body);
            }
        }
        // The PDF browser keeps one visual parent across project and tab changes.
        // While projects load or the publication is built, the spinner replaces the empty hint.
        var hideEmptyBody = busy is SciencePublicationBusy.Projects or SciencePublicationBusy.Building;
        foreach (var child in _scienceBody!.Children.OfType<FrameworkElement>())
            child.Visibility = ReferenceEquals(child, body) && !hideEmptyBody ? Visibility.Visible : Visibility.Collapsed;
        UpdateSciencePublicationBusy(busy);
        _scienceViewSignature = signature;
        UpdatePublicationActivity();
    }

    private ScientificPresentationSnapshot? SciencePresentationSnapshot(string projectId) =>
        _sciencePresentationSmokeProvider is { } smoke ? smoke(projectId)
            : App.Current.GetService<ScientificPresentationCoordinator>().GetSnapshot(projectId);

    private void EnsureScienceVisualTree()
    {
        if (_scienceRoot is null)
        {
            _scienceRoot = new Grid();
            _scienceRoot.RowDefinitions.Add(new() { Height = GridLength.Auto });
            _scienceRoot.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
            _scienceNotice = new InfoBar { IsClosable = false, Severity = InfoBarSeverity.Warning, Visibility = Visibility.Collapsed };
            NativeNotice.Attach(_scienceNotice);
            _scienceRoot.Children.Add(_scienceNotice);
            _scienceBody = new Grid(); Grid.SetRow(_scienceBody, 1); _scienceRoot.Children.Add(_scienceBody);
            _scienceRoot.Children.Add(CreateSciencePublicationBusy());
        }
        AttachSciencePublicationBuildEvents();
        if (!ReferenceEquals(ResearchHost.Content, _scienceRoot)) ResearchHost.Content = _scienceRoot;
    }

    private NativePublicationView GetPublicationView(Guid owner, string path)
    {
        var key = Path.GetFullPath(path);
        if (!_publicationPool.TryGetValue(key, out var view))
        {
            view = new NativePublicationView();
            _publicationPool[key] = view;
        }
        _publicationViews[owner] = view;
        _publicationRecency.Remove(key); _publicationRecency.AddFirst(key);
        // Native PDF engines are shared by the stable publication path, not by
        // chat count. At most the active publication and one recent viewer live.
        while (_publicationPool.Count > 2 && _publicationRecency.Last is { } last)
        {
            var stale = _publicationPool[last.Value];
            _publicationPool.Remove(last.Value); _publicationRecency.RemoveLast();
            _scienceBody?.Children.Remove(stale);
            foreach (var session in _publicationViews.Where(pair => ReferenceEquals(pair.Value, stale)).Select(pair => pair.Key).ToArray())
                _publicationViews.Remove(session);
            stale.Dispose();
        }
        foreach (var previous in _publicationViews.Where(pair => pair.Key != owner && ReferenceEquals(pair.Value, view)).Select(pair => pair.Key).ToArray())
            _publicationViews.Remove(previous);
        return view;
    }

    private void UpdatePublicationActivity()
    {
        var visible = !_disposed && _mode == "claudescience" && _activeResearchSessionId == _session
            && ResearchHost.Visibility == Visibility.Visible && ReferenceEquals(ResearchHost.Content, _scienceRoot);
        var current = _publicationViews.GetValueOrDefault(_session);
        foreach (var viewer in _publicationPool.Values.Concat(_publicationViews.Values).Distinct())
            viewer.SetActive(visible && ReferenceEquals(viewer, current) && viewer.Parent == _scienceBody && viewer.Visibility == Visibility.Visible);
    }

    private static string PublicationContentKey(ScientificPublicationArtifact pdf) =>
        $"{pdf.PdfPath}|{pdf.Revision}|{pdf.ContentHash}|{pdf.UpdatedAt.UtcTicks}";

    private async Task LoadPublicationAsync(NativePublicationView view, ScientificPublicationArtifact pdf, Guid owner, string projectId)
    {
        try
        {
            await view.LoadAsync(pdf.PdfPath, projectId, PublicationContentKey(pdf), _lifetime.Token);
            // The PDF is shown: replace the loading spinner with the document.
            if (!_disposed && _activeResearchSessionId == owner) RunUiCallback("Science.PublicationLoaded", RenderResearchView);
        }
        catch (OperationCanceledException) { /* A newer PDF supersedes an in-flight load. */ }
        catch (Exception exception) when (exception is not (OutOfMemoryException or AccessViolationException or StackOverflowException)
            && exception.HResult != unchecked((int)0x8007000E))
        {
            if (ResearchState(owner).SelectedProjectId == projectId
                && SciencePresentationSnapshot(projectId)?.Publication is { } current
                && PublicationContentKey(current) == PublicationContentKey(pdf))
                ShowResearchError(owner, "Das PDF konnte nicht angezeigt werden: " + exception.Message);
        }
    }

    private void DisposeScienceViews()
    {
        DetachSciencePublicationBuildEvents();
        foreach (var viewer in _publicationPool.Values.Concat(_publicationViews.Values).Distinct()) viewer.Dispose();
        _publicationViews.Clear();
        _publicationPool.Clear();
        _publicationRecency.Clear();
        _scienceEmptyBodies.Clear();
    }

    /// <summary>What the publication tab is waiting for, shown as a spinner with a short explanation.</summary>
    private enum SciencePublicationBusy { None, Projects, Building, Viewer }

    private SciencePublicationBusy SciencePublicationBusyState(Guid owner, NativeResearchSession state, ScientificPresentationSnapshot? presentation)
    {
        if (state.SelectedProjectId is not { } projectId)
            return state.Available && (!state.Loaded || _researchLoading.Contains(owner)) ? SciencePublicationBusy.Projects : SciencePublicationBusy.None;
        if (presentation?.Publication is { } pdf)
        {
            // A rendered PDF exists; spin until the PDF browser has shown it.
            var ready = _publicationPool.TryGetValue(Path.GetFullPath(pdf.PdfPath), out var viewer) && viewer.HasCompletedNavigation;
            return ready ? SciencePublicationBusy.None : SciencePublicationBusy.Viewer;
        }
        // No PDF yet: it is being built while the coordinator renders it or the AI run of this session works on it.
        var building = presentation is null || IsSciencePublicationBuilding(projectId) || (_running && owner == _session);
        return building && string.IsNullOrWhiteSpace(presentation?.PublicationError) ? SciencePublicationBusy.Building : SciencePublicationBusy.None;
    }

    private bool IsSciencePublicationBuilding(string projectId) =>
        _sciencePresentationSmokeProvider is null && App.Current.GetService<ScientificPresentationCoordinator>().IsBuilding(projectId);

    private Grid CreateSciencePublicationBusy()
    {
        _scienceBusyRing = new ProgressRing { Width = 40, Height = 40, IsActive = false, HorizontalAlignment = HorizontalAlignment.Center };
        _scienceBusyTitle = new TextBlock { FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(_scienceBusyTitle, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _scienceBusyDetail = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, Foreground = ThemeBrush("MissumMutedTextBrush", 160) };
        var content = new StackPanel { Spacing = 12, MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(_scienceBusyRing);
        content.Children.Add(_scienceBusyTitle);
        content.Children.Add(_scienceBusyDetail);
        _scienceBusy = new Grid { Visibility = Visibility.Collapsed };
        _scienceBusy.Children.Add(content);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_scienceBusy, "Publikation wird vorbereitet");
        Grid.SetRow(_scienceBusy, 1);
        return _scienceBusy;
    }

    private void UpdateSciencePublicationBusy(SciencePublicationBusy busy)
    {
        if (_scienceBusy is null || _scienceBusyRing is null || _scienceBusyTitle is null || _scienceBusyDetail is null) return;
        var (title, detail) = busy switch
        {
            SciencePublicationBusy.Projects => ("Publikation wird geladen …", "Missum liest die Projekte dieser Sitzung."),
            SciencePublicationBusy.Building => ("Publikation wird erstellt …",
                "Die PDF wird gerade aus Publikation.md gebaut. Sie erscheint hier automatisch, sobald sie fertig ist."),
            SciencePublicationBusy.Viewer => ("Publikation wird geladen …", "Die PDF-Ansicht wird vorbereitet."),
            _ => ((string?)null, (string?)null),
        };
        var visible = title is not null;
        _scienceBusyTitle.Text = title ?? "";
        _scienceBusyDetail.Text = detail ?? "";
        _scienceBusyRing.IsActive = visible;
        // Over a loading PDF browser the spinner needs an opaque surface; otherwise it sits on the page.
        _scienceBusy.Background = busy == SciencePublicationBusy.Viewer ? ThemeBrush("MissumMenuBrush", 43) : null;
        _scienceBusy.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AttachSciencePublicationBuildEvents()
    {
        if (_scienceBuildEventsAttached || _sciencePresentationSmokeProvider is not null) return;
        var coordinator = App.Current.GetService<ScientificPresentationCoordinator>();
        coordinator.BuildStateChanged += OnSciencePublicationBuildStateChanged;
        coordinator.SnapshotPublished += OnSciencePublicationSnapshotPublished;
        _scienceBuildEventsAttached = true;
    }

    private void DetachSciencePublicationBuildEvents()
    {
        if (!_scienceBuildEventsAttached) return;
        var coordinator = App.Current.GetService<ScientificPresentationCoordinator>();
        coordinator.BuildStateChanged -= OnSciencePublicationBuildStateChanged;
        coordinator.SnapshotPublished -= OnSciencePublicationSnapshotPublished;
        _scienceBuildEventsAttached = false;
    }

    // Coordinator events arrive on worker threads; the tab re-renders on the UI thread.
    private void OnSciencePublicationBuildStateChanged(object? sender, string projectId) => QueueSciencePublicationRender();

    private void OnSciencePublicationSnapshotPublished(object? sender, ScientificPresentationPublishedEventArgs args) => QueueSciencePublicationRender();

    private void QueueSciencePublicationRender() => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_disposed && _mode == "claudescience" && _activeResearchSessionId == _session)
            RunUiCallback("Science.BuildState", RenderResearchView);
    });
}
