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
    private readonly Dictionary<string, double> _simulationScrollOffsets = new(StringComparer.Ordinal);

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
                OpenResearchView(simulation: true);
            };
            AutomationProperties.SetName(_simulationTabButton, "Simulation dieser Sitzung öffnen");
            ToolTipService.SetToolTip(_simulationTabButton, "Python · Simulationen, Plots und Daten");
        }
        var active = _activeResearchSessionId == _session && _simulationView && _activeReviewRunId is null;
        ApplyTabAppearance(_simulationTabButton, active);
        _simulationTabButton.IsEnabled = !_sessionTabNavigationBusy;
        if (index < SessionTabsPanel.Children.Count && ReferenceEquals(SessionTabsPanel.Children[index], _simulationTabButton)) return;
        SessionTabsPanel.Children.Remove(_simulationTabButton);
        SessionTabsPanel.Children.Insert(Math.Min(index, SessionTabsPanel.Children.Count), _simulationTabButton);
    }

    private void RenderResearchView()
    {
        if (_disposed || _activeResearchSessionId != _session || _mode != "claudescience") return;
        var owner = _session;
        var state = ResearchState(owner);
        var presentation = state.SelectedProjectId is { } id ? App.Current.GetService<ScientificPresentationCoordinator>().GetSnapshot(id) : null;
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
        if (signature == _scienceViewSignature) return;
        _scienceViewSignature = signature;
        // Detach the previous visual tree before moving the cached PDF view.
        // Switching straight to Simulation must never briefly rebuild Publication.
        ResearchHost.Content = null;
        var root = new Grid { Padding = new Thickness(24, 16, 24, 0), RowSpacing = 12 };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var identity = new StackPanel { Spacing = 3 };
        identity.Children.Add(ScienceText(_simulationView ? "Simulation" : "Wissenschaftliche Publikation", 22, true));
        identity.Children.Add(ScienceText(_simulationView ? "Reproduzierbare Python-Analysen mit Daten und Quellcode" : "Fortlaufend aktualisiertes PDF · Formeln, Ergebnisse und Quellen", 13));
        header.Children.Add(identity);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (!_simulationView && presentation?.Publication is { } publication)
        {
            controls.Children.Add(ScienceButton("PDF öffnen", async () => await OpenScienceFileAsync(publication.PdfPath, owner)));
            controls.Children.Add(ScienceButton("Quelle", async () => await OpenScienceFileAsync(publication.MarkdownPath, owner)));
        }
        controls.Children.Add(ScienceButton("Aktualisieren", async () => await RefreshNativeResearchAsync(owner)));
        Grid.SetColumn(controls, 1); header.Children.Add(controls); root.Children.Add(header);

        var description = new StackPanel { Spacing = 6 };
        if (state.Projects.Length > 1)
        {
            var picker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MaxWidth = 680 };
            foreach (var project in state.Projects) picker.Items.Add(new ComboBoxItem { Content = S(project, "interpretedQuestion", S(project, "originalQuestion")), Tag = S(project, "id") });
            picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, state.SelectedProjectId));
            picker.SelectionChanged += async (_, _) =>
            {
                if (_session != owner || picker.SelectedItem is not ComboBoxItem item || item.Tag is not string selected || selected == state.SelectedProjectId) return;
                state.SelectedProjectId = state.RequestedProjectId = selected; state.Detail = default; state.Revision = 0;
                await RefreshNativeResearchAsync(owner);
            };
            AutomationProperties.SetName(picker, "Forschungsvorhaben wählen"); description.Children.Add(picker);
        }
        var error = state.Error.Length > 0 ? state.Error : _simulationView
            ? presentation?.SimulationError ?? (presentation?.Simulation?.Status == "failed" ? presentation.Simulation.Detail : null)
            : presentation?.PublicationError;
        if (!string.IsNullOrWhiteSpace(error)) description.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = error });
        Grid.SetRow(description, 1); root.Children.Add(description);

        FrameworkElement body;
        if (_simulationView) body = BuildSimulationView(presentation?.Simulation, state.SelectedProjectId is not null, loading);
        else if (presentation?.Publication is { } pdf)
        {
            if (!_publicationViews.TryGetValue(owner, out var viewer)) _publicationViews[owner] = viewer = new NativePublicationView();
            if (viewer.Parent is Panel parent) parent.Children.Remove(viewer);
            body = viewer; _ = LoadPublicationAsync(viewer, pdf.PdfPath, owner, state.SelectedProjectId!);
        }
        else body = ScienceEmpty(state.SelectedProjectId is null ? "Stelle im Chat eine Forschungsfrage." : "Die wissenschaftliche Publikation wird erstellt.",
            "Hier erscheint das PDF bereits während der Recherche. Der Arbeitsablauf bleibt im Chat sichtbar.");
        if (body is StackPanel emptyState)
            body = new ScrollViewer { Content = emptyState, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 0, 0, 16) };
        Grid.SetRow(body, 2); root.Children.Add(body); ResearchHost.Content = root;
    }

    private FrameworkElement BuildSimulationView(ScientificSimulationSnapshot? snapshot, bool projectSelected, bool loading)
    {
        var artifacts = snapshot?.Artifacts.Where(item => item.IsResearchData && File.Exists(item.ImagePath)
            && item.Provenance.StartsWith("Forschungsexperiment · ", StringComparison.Ordinal)).ToArray() ?? [];
        if (artifacts.Length == 0)
        {
            if (loading) return ScienceEmpty("Simulationen werden geladen.",
                "Gespeicherte Python-Ergebnisse dieser Sitzung werden wiederhergestellt. Hier erscheinen Abbildungen zusammen mit ihrem Quellcode und den zugehörigen Daten.");
            if (!projectSelected) return ScienceEmpty("Stelle im Chat eine Forschungsfrage.",
                "Hier erscheinen passende Simulationen, Plots und Graphen aus Python. Jede Darstellung bleibt mit ihrem Quellcode und den zugehörigen Daten nachvollziehbar; der Arbeitsablauf ist im Chat sichtbar.");
            return ScienceEmpty("Noch keine Simulation vorhanden.",
                "Sobald eine Python-Auswertung eine Abbildung erzeugt, erscheint sie hier mit Quellcode und verfügbaren Daten. Gespeicherte Ergebnisse werden beim erneuten Öffnen automatisch geladen; der Arbeitsablauf bleibt im Chat sichtbar.");
        }
        var body = new StackPanel { Spacing = 20, MaxWidth = 1150, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var artifact in artifacts)
        {
            var card = new StackPanel { Spacing = 10 };
            card.Children.Add(ScienceText(artifact.Title, 18, true)); card.Children.Add(ScienceText(artifact.Provenance, 13));
            var bitmap = new BitmapImage { CreateOptions = BitmapCreateOptions.IgnoreImageCache, UriSource = new Uri(artifact.ImagePath) };
            var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxHeight = 740 };
            AutomationProperties.SetName(image, artifact.Title); card.Children.Add(image);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var owner = _session;
            actions.Children.Add(ScienceButton("Abbildung öffnen", async () => await OpenScienceFileAsync(artifact.ImagePath, owner)));
            card.Children.Add(actions);
            if (!string.IsNullOrEmpty(artifact.ScriptPath)) card.Children.Add(ScienceSource("Python-Code", artifact.ScriptPath, "python", owner));
            if (!string.IsNullOrEmpty(artifact.DataPath)) card.Children.Add(ScienceSource("Daten", artifact.DataPath, "json", owner));
            body.Children.Add(new Border { Child = card, Padding = new Thickness(20), CornerRadius = new CornerRadius(12), Background = ThemeBrush("MissumLayerBrush", 31) });
        }
        var scrollKey = _session + "|" + snapshot!.ProjectId;
        var scroll = new ScrollViewer { Content = body, Padding = new Thickness(0, 0, 12, 24), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroll.ViewChanged += (_, _) => _simulationScrollOffsets[scrollKey] = scroll.VerticalOffset;
        scroll.Loaded += (_, _) => { if (_simulationScrollOffsets.TryGetValue(scrollKey, out var offset)) scroll.ChangeView(null, offset, null, true); };
        return scroll;
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
            catch (Exception exception) when (exception is not OutOfMemoryException) { ShowResearchError(owner, exception.Message); }
        };
        return expander;
    }

    private async Task LoadPublicationAsync(NativePublicationView view, string path, Guid owner, string projectId)
    {
        try { await view.LoadAsync(path, _lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || _disposed) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (ResearchState(owner).SelectedProjectId == projectId
                && App.Current.GetService<ScientificPresentationCoordinator>().GetSnapshot(projectId)?.Publication?.PdfPath == path)
                ShowResearchError(owner, "Das PDF konnte nicht angezeigt werden: " + exception.Message);
        }
    }
    private void DisposeScienceViews()
    {
        foreach (var viewer in _publicationViews.Values) viewer.Dispose();
        _publicationViews.Clear();
    }
    private async Task OpenScienceFileAsync(string path, Guid owner)
    {
        try { await Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(path)); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowResearchError(owner, exception.Message); }
    }
    private static TextBlock ScienceText(string text, double size, bool heading = false) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
        FontWeight = heading ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
    };
    private static Button ScienceButton(string label, Func<Task> action)
    {
        var button = new Button { Content = label, Padding = new Thickness(12, 7, 12, 7) };
        AutomationProperties.SetName(button, label); button.Click += async (_, _) => await action(); return button;
    }
    private static StackPanel ScienceEmpty(string heading, string explanation)
    {
        var content = new StackPanel { Spacing = 10, MaxWidth = 540, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(ScienceText(heading, 20, true)); content.Children.Add(ScienceText(explanation, 14)); return content;
    }
}
