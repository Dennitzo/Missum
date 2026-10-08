using Missum.App.Controls;
using Missum.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.System;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private Button? _simulationTabButton;
    private bool _simulationView;
    private string? _scienceViewSignature;
    private readonly Dictionary<Guid, NativePublicationView> _publicationViews = [];
    private readonly Dictionary<string, NativeSimulationView> _interactiveSimulationViews = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _simulationScrollOffsets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SimulationPane> _simulationPanes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FrameworkElement> _scienceEmptyBodies = new(StringComparer.Ordinal);
    private Grid? _scienceRoot, _scienceHeader, _scienceBody;
    private StackPanel? _scienceDescription;
    private TextBlock? _scienceHeading, _scienceSubtitle;
    private StackPanel? _scienceControls;
    private ComboBox? _scienceProjectPicker;
    private InfoBar? _scienceNotice;
    private string? _scienceControlsKey, _scienceProjectKey;
    private bool _scienceRendering, _scienceRenderAgain, _sciencePickerUpdating;

    private void RenderSimulationTab(int index)
    {
        if (_simulationTabButton is null)
        {
            _simulationTabButton = TabButton();
            _simulationTabButton.Height = 32; _simulationTabButton.Padding = new Thickness(10, 0, 10, 0);
            _simulationTabButton.BorderThickness = new Thickness(1);
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            content.Children.Add(new FontIcon { Glyph = "\uE9D9", FontSize = 13, Foreground = NativeIconPalette.BrushFor("research") });
            content.Children.Add(new TextBlock { Text = "Simulation", FontSize = 13 });
            _simulationTabButton.Content = content;
            _simulationTabButton.Click += (_, _) =>
            {
                QueueResearchTabSelection(simulation: true);
            };
            AutomationProperties.SetName(_simulationTabButton, "Simulation dieser Sitzung öffnen");
            ToolTipService.SetToolTip(_simulationTabButton, "Python · Simulationen, Plots und Daten");
        }
        var active = _activeResearchSessionId == _session && _simulationView && _activeReviewRunId is null;
        ApplyTabAppearance(_simulationTabButton, active);
        SetTabEnabled(_simulationTabButton, !_sessionTabNavigationBusy);
        PositionTab(_simulationTabButton, index);
    }

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
        var contentKey = _simulationView ? string.Join("|", presentation?.Simulation?.Artifacts.Select(item => item.ToString()) ?? [])
            : presentation?.Publication?.PdfPath;
        var projectKey = System.Text.Json.JsonSerializer.Serialize(state.Projects.Select(project => new
        {
            Id = S(project, "id"), Title = S(project, "interpretedQuestion", S(project, "originalQuestion")),
        }));
        // Background polling must not rebuild existing cards or collapse their
        // source disclosures. Only the initial restore needs a loading state.
        var loading = string.IsNullOrWhiteSpace(state.Error) && string.IsNullOrWhiteSpace(presentation?.SimulationError)
            && (!state.Loaded || state.SelectedProjectId is not null && presentation?.Simulation is null);
        var signature = _simulationView ? $"{owner}|simulation|{state.SelectedProjectId}|{contentKey}|{state.Error}|{presentation?.SimulationError}|{presentation?.Simulation?.Status}|{presentation?.Simulation?.Detail}|{loading}|{projectKey}"
            : $"{owner}|publication|{state.SelectedProjectId}|{contentKey}|{state.Error}|{presentation?.PublicationError}|{state.Loaded}|{projectKey}";
        if (signature == _scienceViewSignature && ReferenceEquals(ResearchHost.Content, _scienceRoot))
        {
            UpdateInteractiveSimulationActivity();
            return;
        }
        EnsureScienceVisualTree();
        // Publication uses the whole chat viewport. Its PDF supplies its own
        // title/content; ordinary headings, descriptions and buttons add no row.
        _scienceRoot!.Padding = _simulationView ? new Thickness(24, 16, 24, 0) : new Thickness(0);
        _scienceRoot.RowSpacing = _simulationView ? 12 : 0;
        _scienceHeader!.Visibility = _simulationView ? Visibility.Visible : Visibility.Collapsed;
        _scienceHeading!.Text = _simulationView ? "Simulation" : "";
        _scienceSubtitle!.Text = _simulationView ? "Simulationen und Python-Analysen mit Daten und Quellcode" : "";
        var controlsKey = $"{owner}|{_simulationView}|{presentation?.Publication?.PdfPath}|{presentation?.Publication?.MarkdownPath}";
        if (_scienceControlsKey != controlsKey)
        {
            _scienceControls!.Children.Clear();
            if (_simulationView)
                _scienceControls.Children.Add(ScienceButton("Aktualisieren", async () => await RefreshNativeResearchAsync(owner)));
            _scienceControlsKey = controlsKey;
        }
        _sciencePickerUpdating = true;
        try
        {
            if (_scienceProjectKey != projectKey)
            {
                _scienceProjectPicker!.Items.Clear();
                foreach (var project in state.Projects) _scienceProjectPicker.Items.Add(new ComboBoxItem
                    { Content = S(project, "interpretedQuestion", S(project, "originalQuestion")), Tag = S(project, "id") });
                _scienceProjectKey = projectKey;
            }
            _scienceProjectPicker!.Visibility = _simulationView && state.Projects.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
            _scienceProjectPicker.SelectedItem = _scienceProjectPicker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, state.SelectedProjectId));
        }
        finally { _sciencePickerUpdating = false; }
        var error = state.Error.Length > 0 ? state.Error : _simulationView
            ? presentation?.SimulationError ?? (presentation?.Simulation?.Status == "failed" ? presentation.Simulation.Detail : null)
            : presentation?.PublicationError;
        _scienceNotice!.Message = error ?? "";
        _scienceNotice.IsOpen = !string.IsNullOrWhiteSpace(error);
        // A genuine PDF failure remains readable/selectable, including when an
        // older PDF is still displayed while the model repairs the publication.
        _scienceDescription!.Visibility = _simulationView || _scienceNotice.IsOpen ? Visibility.Visible : Visibility.Collapsed;

        FrameworkElement body;
        if (_simulationView)
        {
            var key = owner + "|" + state.SelectedProjectId;
            var pane = GetSimulationPane(key);
            UpdateSimulationPane(pane, presentation?.Simulation, state.SelectedProjectId is not null, loading, owner);
            body = pane.Root;
        }
        else if (presentation?.Publication is { } pdf)
        {
            if (!_publicationViews.TryGetValue(owner, out var viewer)) _publicationViews[owner] = viewer = new NativePublicationView();
            if (viewer.Parent is null) _scienceBody!.Children.Add(viewer);
            body = viewer; _ = LoadPublicationAsync(viewer, pdf.PdfPath, owner, state.SelectedProjectId!);
        }
        else
        {
            var key = owner + "|" + (state.SelectedProjectId is null ? "no-project" : "publication-pending");
            if (!_scienceEmptyBodies.TryGetValue(key, out body!))
            {
                var emptyState = ScienceEmpty(state.SelectedProjectId is null ? "Stelle im Chat eine Forschungsfrage." : "Die wissenschaftliche Publikation wird erstellt.",
                    "Hier erscheint das PDF bereits während der Recherche. Der Arbeitsablauf bleibt im Chat sichtbar.");
                body = new ScrollViewer { Content = emptyState, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 0, 0, 16) };
                _scienceEmptyBodies.Add(key, body); _scienceBody!.Children.Add(body);
            }
        }
        // A WebView2 remains attached to the same card and visual tree for its
        // whole lifetime. Tabs and snapshot changes only select retained panes.
        foreach (var child in _scienceBody!.Children.OfType<FrameworkElement>())
            child.Visibility = ReferenceEquals(child, body) ? Visibility.Visible : Visibility.Collapsed;
        _scienceViewSignature = signature;
        UpdateInteractiveSimulationActivity();
    }

    private ScientificPresentationSnapshot? SciencePresentationSnapshot(string projectId) =>
        _sciencePresentationSmokeProvider is { } smoke ? smoke(projectId)
            : App.Current.GetService<ScientificPresentationCoordinator>().GetSnapshot(projectId);

    private void EnsureScienceVisualTree()
    {
        if (_scienceRoot is null)
        {
            _scienceRoot = new Grid { Padding = new Thickness(24, 16, 24, 0), RowSpacing = 12 };
            _scienceRoot.RowDefinitions.Add(new() { Height = GridLength.Auto });
            _scienceRoot.RowDefinitions.Add(new() { Height = GridLength.Auto });
            _scienceRoot.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
            var header = _scienceHeader = new Grid { ColumnSpacing = 12 };
            header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var identity = new StackPanel { Spacing = 3 };
            _scienceHeading = ScienceText("", 22, true); _scienceSubtitle = ScienceText("", 13);
            identity.Children.Add(_scienceHeading); identity.Children.Add(_scienceSubtitle); header.Children.Add(identity);
            _scienceControls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(_scienceControls, 1); header.Children.Add(_scienceControls); _scienceRoot.Children.Add(header);
            _scienceDescription = new StackPanel { Spacing = 6 };
            _scienceProjectPicker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MaxWidth = 680 };
            AutomationProperties.SetName(_scienceProjectPicker, "Forschungsvorhaben wählen");
            _scienceProjectPicker.SelectionChanged += async (_, _) =>
            {
                if (_sciencePickerUpdating || _disposed || _activeResearchSessionId != _session
                    || _scienceProjectPicker.SelectedItem is not ComboBoxItem { Tag: string selected }) return;
                var owner = _session;
                var state = ResearchState(owner);
                if (selected == state.SelectedProjectId) return;
                if (!RunUiCallback("Research.ProjectSelection", () =>
                { state.SelectedProjectId = state.RequestedProjectId = selected; state.Detail = default; state.Revision = 0; })) return;
                await RefreshNativeResearchAsync(owner);
            };
            _scienceDescription.Children.Add(_scienceProjectPicker);
            _scienceNotice = new InfoBar { IsClosable = false, Severity = InfoBarSeverity.Warning };
            NativeNotice.Attach(_scienceNotice); _scienceDescription.Children.Add(_scienceNotice);
            Grid.SetRow(_scienceDescription, 1); _scienceRoot.Children.Add(_scienceDescription);
            _scienceBody = new Grid(); Grid.SetRow(_scienceBody, 2); _scienceRoot.Children.Add(_scienceBody);
        }
        if (!ReferenceEquals(ResearchHost.Content, _scienceRoot)) ResearchHost.Content = _scienceRoot;
    }

    private void UpdateInteractiveSimulationActivity()
    {
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!_disposed && _simulationView && _activeResearchSessionId == _session
            && ResearchHost.Visibility == Visibility.Visible
            && ResearchState(_session).SelectedProjectId is { } projectId
            && SciencePresentationSnapshot(projectId)?.Simulation is { } simulation)
            foreach (var artifact in VisibleSimulationArtifacts(simulation).Where(item => item.Kind == "interactive"))
                active.Add(SimulationViewKey(_session, projectId, artifact.ImagePath));
        foreach (var (key, viewer) in _interactiveSimulationViews) viewer.SetActive(active.Contains(key));
        UpdatePublicationActivity();
    }

    private void UpdatePublicationActivity()
    {
        var publicationVisible = !_disposed && _mode == "claudescience" && !_simulationView
            && _activeResearchSessionId == _session && ResearchHost.Visibility == Visibility.Visible
            && ReferenceEquals(ResearchHost.Content, _scienceRoot);
        foreach (var (owner, viewer) in _publicationViews)
            viewer.SetActive(publicationVisible && owner == _session && viewer.Parent == _scienceBody
                && viewer.Visibility == Visibility.Visible);
    }

    private FrameworkElement BuildSimulationView(ScientificSimulationSnapshot? snapshot, bool projectSelected, bool loading)
    {
        var artifacts = VisibleSimulationArtifacts(snapshot);
        if (artifacts.Length == 0)
        {
            if (loading) return ScienceEmpty("Simulationen werden geladen.",
                "Gespeicherte Python-Ergebnisse dieser Sitzung werden wiederhergestellt. Hier erscheinen Abbildungen zusammen mit ihrem Quellcode und den zugehörigen Daten.");
            if (!projectSelected) return ScienceEmpty("Stelle im Chat eine Forschungsfrage.",
                "Hier erscheinen passende Simulationen, Plots und Graphen aus Python. Jede Darstellung bleibt mit ihrem Quellcode und den zugehörigen Daten nachvollziehbar; der Arbeitsablauf ist im Chat sichtbar.");
            return ScienceEmpty("Noch keine Simulation vorhanden.",
                "Sobald eine Python-Auswertung eine Abbildung erzeugt, erscheint sie hier mit Quellcode und verfügbaren Daten. Gespeicherte Ergebnisse werden beim erneuten Öffnen automatisch geladen; der Arbeitsablauf bleibt im Chat sichtbar.");
        }
        var key = _session + "|" + snapshot!.ProjectId;
        EnsureScienceVisualTree();
        var pane = GetSimulationPane(key);
        UpdateSimulationPane(pane, snapshot, projectSelected, loading, _session);
        return pane.Root;
    }

    private SimulationPane GetSimulationPane(string key)
    {
        if (!_simulationPanes.TryGetValue(key, out var pane))
        {
            pane = new SimulationPane();
            _simulationPanes.Add(key, pane);
            pane.Scroll.ViewChanged += (_, _) => _simulationScrollOffsets[key] = pane.Scroll.VerticalOffset;
            pane.Scroll.Loaded += (_, _) => RunUiCallback("Simulation.RestoreScroll", () =>
            { if (_simulationScrollOffsets.TryGetValue(key, out var offset)) pane.Scroll.ChangeView(null, offset, null, true); });
        }
        if (pane.Root.Parent is null) _scienceBody!.Children.Add(pane.Root);
        return pane;
    }

    private static ScientificSimulationArtifact[] VisibleSimulationArtifacts(ScientificSimulationSnapshot? snapshot) =>
        snapshot?.Artifacts.Where(item => File.Exists(item.ImagePath)
            && (item.Kind == "interactive" || item.IsResearchData
                && item.Provenance.StartsWith("Forschungsexperiment · ", StringComparison.Ordinal))).ToArray() ?? [];

    private static string SimulationViewKey(Guid owner, string projectId, string path) => owner + "|" + projectId + "|" + path;

    private void UpdateSimulationPane(SimulationPane pane, ScientificSimulationSnapshot? snapshot, bool projectSelected, bool loading, Guid owner)
    {
        var artifacts = VisibleSimulationArtifacts(snapshot);
        pane.Scroll.Visibility = artifacts.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        pane.EmptyScroll.Visibility = artifacts.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (artifacts.Length == 0)
        {
            var empty = (StackPanel)BuildSimulationView(null, projectSelected, loading);
            pane.EmptyHeading.Text = ((TextBlock)empty.Children[0]).Text;
            pane.EmptyExplanation.Text = ((TextBlock)empty.Children[1]).Text;
        }
        var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var row = 0;
        foreach (var artifact in artifacts)
        {
            var artifactKey = artifact.Kind + "|" + artifact.ImagePath;
            visible.Add(artifactKey);
            if (!pane.Cards.TryGetValue(artifactKey, out var card))
            {
                card = new SimulationCard(artifact.Kind == "interactive");
                // Native browser controls have one permanent parent. An error,
                // refresh, project switch or new artifact may never move it.
                if (card.Interactive is { } interactive)
                    _interactiveSimulationViews.Add(SimulationViewKey(owner, snapshot!.ProjectId, artifact.ImagePath), interactive);
                else
                {
                    var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    actions.Children.Add(ScienceButton("Abbildung öffnen", async () => await OpenScienceFileAsync(artifact.ImagePath, owner)));
                    card.Content.Children.Add(actions);
                }
                pane.Cards.Add(artifactKey, card); pane.Artifacts.Children.Add(card.Container);
            }
            card.Title.Text = artifact.Title; card.Provenance.Text = artifact.Provenance;
            if (card.Interactive is { } view) _ = view.LoadAsync(artifact, _lifetime.Token);
            else if (card.LastHash != artifact.Sha256)
            {
                card.Image!.Source = new BitmapImage { CreateOptions = BitmapCreateOptions.IgnoreImageCache, UriSource = new Uri(artifact.ImagePath) };
                AutomationProperties.SetName(card.Image, artifact.Title);
            }
            // Preserve source disclosure state when only status text changes.
            var sourcesKey = artifact.ScriptPath + "|" + artifact.DataPath + "|" + artifact.Sha256;
            if (card.SourcesKey != sourcesKey)
            {
                card.Sources.Children.Clear();
                if (!string.IsNullOrEmpty(artifact.ScriptPath)) card.Sources.Children.Add(ScienceSource(
                    artifact.Kind == "interactive" ? "HTML-/JavaScript-Code" : "Python-Code", artifact.ScriptPath,
                    artifact.Kind == "interactive" ? "html" : "python", owner));
                if (!string.IsNullOrEmpty(artifact.DataPath)) card.Sources.Children.Add(ScienceSource("Daten", artifact.DataPath, "json", owner));
                card.SourcesKey = sourcesKey;
            }
            card.LastHash = artifact.Sha256;
            while (pane.Artifacts.RowDefinitions.Count <= row) pane.Artifacts.RowDefinitions.Add(new() { Height = GridLength.Auto });
            if (Grid.GetRow(card.Container) != row) Grid.SetRow(card.Container, row);
            row++;
            if (card.Container.Visibility != Visibility.Visible) card.Container.Visibility = Visibility.Visible;
        }
        foreach (var (key, existing) in pane.Cards)
            if (!visible.Contains(key) && existing.Container.Visibility != Visibility.Collapsed)
                existing.Container.Visibility = Visibility.Collapsed;
    }

    private sealed class SimulationPane
    {
        internal Grid Root { get; } = new();
        internal Grid Artifacts { get; } = new() { RowSpacing = 20, MaxWidth = 1150, HorizontalAlignment = HorizontalAlignment.Stretch };
        internal ScrollViewer Scroll { get; }
        internal ScrollViewer EmptyScroll { get; }
        internal TextBlock EmptyHeading { get; } = ScienceText("", 20, true);
        internal TextBlock EmptyExplanation { get; } = ScienceText("", 14);
        internal Dictionary<string, SimulationCard> Cards { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal SimulationPane()
        {
            Scroll = new ScrollViewer { Content = Artifacts, Padding = new Thickness(0, 0, 12, 24),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var empty = new StackPanel { Spacing = 10, MaxWidth = 540, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center };
            empty.Children.Add(EmptyHeading); empty.Children.Add(EmptyExplanation);
            EmptyScroll = new ScrollViewer { Content = empty, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 0, 0, 16) };
            Root.Children.Add(Scroll); Root.Children.Add(EmptyScroll);
        }
    }

    private sealed class SimulationCard
    {
        internal Border Container { get; }
        internal StackPanel Content { get; } = new() { Spacing = 10 };
        internal TextBlock Title { get; } = ScienceText("", 18, true);
        internal TextBlock Provenance { get; } = ScienceText("", 13);
        internal StackPanel Sources { get; } = new() { Spacing = 10 };
        internal NativeSimulationView? Interactive { get; }
        internal Image? Image { get; }
        internal string? LastHash { get; set; }
        internal string? SourcesKey { get; set; }

        internal SimulationCard(bool interactive)
        {
            Content.Children.Add(Title); Content.Children.Add(Provenance);
            if (interactive) { Interactive = new NativeSimulationView(); Content.Children.Add(Interactive); }
            else { Image = new Image { Stretch = Stretch.Uniform, MaxHeight = 740 }; Content.Children.Add(Image); }
            Content.Children.Add(Sources);
            Container = new Border { Child = Content, Padding = new Thickness(20), CornerRadius = new CornerRadius(12),
                Background = ThemeBrush("MissumLayerBrush", 31) };
        }
    }

    private Expander ScienceSource(string label, string path, string language, Guid owner)
    {
        var expander = new Expander { Header = label, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        expander.Expanding += async (_, _) =>
        {
            if (expander.Content is not null) return;
            try
            {
                using var reader = File.OpenText(path);
                var buffer = new char[100_001];
                var count = await reader.ReadBlockAsync(buffer.AsMemory(), _lifetime.Token);
                var text = new string(buffer, 0, Math.Min(100_000, count));
                var fence = "````";
                while (text.Contains(fence, StringComparison.Ordinal)) fence += "`";
                expander.Content = new NativeStreamingMarkdown(fence + language + "\n" + text + "\n" + fence
                    + (count > 100_000 ? "\n\nVorschau auf 100.000 Zeichen begrenzt." : ""));
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception exception) when (exception is not (OutOfMemoryException or AccessViolationException or StackOverflowException)
                && exception.HResult != unchecked((int)0x8007000E)) { ShowResearchError(owner, exception.Message); }
        };
        return expander;
    }

    private async Task LoadPublicationAsync(NativePublicationView view, string path, Guid owner, string projectId)
    {
        try { await view.LoadAsync(path, projectId, _lifetime.Token); }
        catch (OperationCanceledException) { /* A newer PDF revision supersedes an in-flight load. */ }
        catch (Exception exception) when (exception is not (OutOfMemoryException or AccessViolationException or StackOverflowException)
            && exception.HResult != unchecked((int)0x8007000E))
        {
            if (ResearchState(owner).SelectedProjectId == projectId
                && SciencePresentationSnapshot(projectId)?.Publication?.PdfPath == path)
                ShowResearchError(owner, "Das PDF konnte nicht angezeigt werden: " + exception.Message);
        }
    }
    private void DisposeScienceViews()
    {
        foreach (var viewer in _publicationViews.Values) viewer.Dispose();
        _publicationViews.Clear();
        foreach (var viewer in _interactiveSimulationViews.Values) viewer.Dispose();
        _interactiveSimulationViews.Clear();
        _simulationPanes.Clear();
        _scienceEmptyBodies.Clear();
    }
    private async Task OpenScienceFileAsync(string path, Guid owner)
    {
        try { await Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(path)); }
        catch (Exception exception) when (exception is not (OutOfMemoryException or AccessViolationException or StackOverflowException)
            && exception.HResult != unchecked((int)0x8007000E)) { ShowResearchError(owner, exception.Message); }
    }
    private static TextBlock ScienceText(string text, double size, bool heading = false) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
        FontWeight = heading ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
    };
    private Button ScienceButton(string label, Func<Task> action)
    {
        var owner = _session;
        var button = new Button { Content = label, Padding = new Thickness(12, 7, 12, 7) };
        AutomationProperties.SetName(button, label);
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception exception) when (IsRecoverableUiProjectionException(exception))
            {
                ReportUiProjectionFailure("ScienceButton." + label, exception);
                ShowResearchError(owner, exception.Message);
            }
        };
        return button;
    }
    private static StackPanel ScienceEmpty(string heading, string explanation)
    {
        var content = new StackPanel { Spacing = 10, MaxWidth = 540, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(ScienceText(heading, 20, true)); content.Children.Add(ScienceText(explanation, 14)); return content;
    }
}
