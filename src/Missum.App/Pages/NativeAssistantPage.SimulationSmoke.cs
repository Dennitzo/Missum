using System.Security.Cryptography;
using System.Text.Json;
using Missum.App.Controls;
using Missum.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    // Set only by the explicit isolated smoke below. Production always reads
    // the real coordinator, and no model/tool or persistence path is replaced.
    private Func<string, ScientificPresentationSnapshot?>? _sciencePresentationSmokeProvider;

    private async Task<object> VerifySimulationTabLifecycleSmokeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MISSUM_SMOKE_INSTANCE_KEY")))
            throw new InvalidOperationException("Der Simulation-Tab-Smoke ist ausschließlich im isolierten Portable-Smoke erlaubt.");
        var previousOwner = _session;
        var previousMode = _mode;
        var previousResearchOwner = _activeResearchSessionId;
        var previousSimulation = _simulationView;
        var previousProvider = _sciencePresentationSmokeProvider;
        var previousDiagnostics = Environment.GetEnvironmentVariable("MISSUM_SCIENCE_QA_DIAGNOSTICS");
        var previousOpenAdapter = _simulationOpenSmokeAdapter;
        var owner = Guid.NewGuid();
        var projectId = "simulation-smoke-" + owner.ToString("N");
        var directory = Path.Combine(App.Current.DataDirectory, "SimulationTabSmoke", projectId);
        var htmlPath = Path.Combine(directory, "animation.html");
        var paneKey = owner + "|" + projectId;
        var viewKey = SimulationViewKey(owner, projectId, htmlPath);
        var faultCount = _uiCallbackFaultCount;
        NativeSimulationView? viewer = null;
        try
        {
            Directory.CreateDirectory(directory);
            Environment.SetEnvironmentVariable("MISSUM_SCIENCE_QA_DIAGNOSTICS", "1");
            var launchTargets = new List<string>();
            _simulationOpenSmokeAdapter = path => { launchTargets.Add(path); return Task.FromResult(true); };
            async Task<ScientificSimulationArtifact> WriteArtifactAsync(int revision)
            {
                var source = $$"""
                    <!doctype html><html><head><meta charset="utf-8"><title>Simulation {{revision}}</title></head>
                    <body><h1 id="revision">Simulation {{revision}}</h1><canvas width="480" height="240"></canvas>
                    <script>window.__missumSmokeRevision={{revision}};window.__missumSmokeTicks=0;
                    const context=document.querySelector('canvas').getContext('2d');
                    function draw(){window.__missumSmokeTicks++;context.fillStyle='#222';context.fillRect(0,0,480,240);
                    context.fillStyle='#56cacc';context.fillRect((window.__missumSmokeTicks*3)%460,90,20,20);requestAnimationFrame(draw);}draw();</script>
                    </body></html>
                    """;
                await File.WriteAllTextAsync(htmlPath, source, _lifetime.Token);
                var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(htmlPath, _lifetime.Token)));
                return new(projectId + "-html", "Echte Canvas-Animation", htmlPath, htmlPath, null,
                    "Interaktive Simulation · HTML/JavaScript", false, hash, Kind: "interactive", ContentType: "text/html", ProjectRoot: directory);
            }
            var artifact = await WriteArtifactAsync(1);
            var plot = await WriteSimulationControlPlotSmokeAsync(projectId, directory);
            var pdfPath = Environment.GetEnvironmentVariable("MISSUM_SCIENCE_PDF_SMOKE_PATH");
            var publication = !string.IsNullOrWhiteSpace(pdfPath) && File.Exists(pdfPath)
                ? new ScientificPublicationArtifact(projectId, 1, htmlPath, pdfPath, true, DateTimeOffset.UtcNow, "smoke") : null;
            var simulation = new ScientificSimulationSnapshot(projectId, 1, "ready", "", [artifact, plot], DateTimeOffset.UtcNow);
            ScientificPresentationSnapshot presentation = new(1, publication, simulation, null, null);
            _sciencePresentationSmokeProvider = id => id == projectId ? presentation : previousProvider?.Invoke(id)
                ?? App.Current.GetService<ScientificPresentationCoordinator>().GetSnapshot(id);
            _session = owner; _mode = "claudescience";
            var state = ResearchState(owner);
            state.Loaded = state.Available = true;
            state.SelectedProjectId = state.RequestedProjectId = projectId;
            state.Projects = [JsonSerializer.SerializeToElement(new { id = projectId, originalQuestion = "Simulation-Tab-Lebensdauer" })];
            // Tab navigation still uses the real UI route. Suppress only remote
            // research refreshes for this synthetic, unpersisted fixture owner.
            _researchLoading.Add(owner);
            QueueResearchTabSelection(simulation: true);
            await WaitForSimulationSmokeAsync(() => _activeResearchSessionId == owner && _simulationView
                && _interactiveSimulationViews.TryGetValue(viewKey, out viewer), "Simulation-Tab wurde nicht geöffnet.");
            await WaitForSimulationSmokeAsync(() => viewer is { IsInitialized: true, HasCompletedNavigation: true },
                "Die echte WebView2-Simulation wurde nicht geladen.");
            var retainedRoot = ResearchHost.Content;
            var retainedParent = viewer!.Parent;
            if (retainedParent is null) throw new InvalidOperationException("Die Simulation hat keinen festen visuellen Parent.");
            var initial = await WaitForSimulationSmokeFrameAsync(viewer, 1);
            await Task.Delay(120, _lifetime.Token);
            var moving = await ReadSimulationSmokeFrameAsync(viewer);
            if (initial.Canvases != 1 || moving.Ticks <= initial.Ticks)
                throw new InvalidOperationException("Die reale Simulation zeichnet oder animiert ihr Canvas nicht.");
            var controls = await VerifySimulationControlsSmokeAsync(_simulationPanes[paneKey], artifact, plot, viewer, retainedRoot, retainedParent, launchTargets);
            var publicationVisits = 0;
            var chatVisits = 0;
            var suspendResumeCycles = 0;
            for (var iteration = 0; iteration < 40; iteration++)
            {
                state.Error = iteration % 3 == 0 ? "Smoke: temporärer Präsentationshinweis " + iteration : "";
                if (iteration == 20) artifact = await WriteArtifactAsync(2);
                simulation = simulation with { Revision = iteration + 2, Detail = "Snapshot " + iteration,
                    Artifacts = [artifact, plot], UpdatedAt = DateTimeOffset.UtcNow };
                presentation = presentation with { Version = iteration + 2, Simulation = simulation };
                QueueResearchTabSelection(simulation: false);
                await WaitForSimulationSmokeAsync(() => _activeResearchSessionId == owner && !_simulationView,
                    "Publikation wurde beim echten Tabwechsel nicht ausgewählt.");
                publicationVisits++;
                if (iteration % 10 == 0)
                {
                    await Task.Delay(500, _lifetime.Token);
                    await WaitForSimulationSmokeAsync(() => viewer.IsSuspended,
                        "Die tatsächlich ausgeblendete Simulation wurde nicht pausiert.");
                    suspendResumeCycles++;
                }
                if (publication is not null && (!_publicationViews.TryGetValue(owner, out var pdf) || pdf.Parent != _scienceBody))
                    throw new InvalidOperationException("Der Publikation-Tab verwendet keinen behaltenen PDF-Parent.");
                if (iteration % 8 == 0)
                {
                    ShowChatView();
                    UpdateLayout(); await Task.Delay(40, _lifetime.Token); chatVisits++;
                }
                QueueResearchTabSelection(simulation: true);
                await WaitForSimulationSmokeAsync(() => _activeResearchSessionId == owner && _simulationView,
                    "Simulation wurde nach Tabwechsel nicht wieder geöffnet.");
                RenderResearchView(); UpdateLayout();
                await WaitForSimulationSmokeAsync(() => !viewer.IsSuspended,
                    "Die Simulation wurde nach längerer Ausblendung nicht fortgesetzt.");
                await Task.Delay(40, _lifetime.Token);
                if (!ReferenceEquals(ResearchHost.Content, retainedRoot) || !ReferenceEquals(viewer.Parent, retainedParent)
                    || !ReferenceEquals(_interactiveSimulationViews[viewKey], viewer))
                    throw new InvalidOperationException("Ein Tab-/Fehler-/Snapshotwechsel hat die native Browserdarstellung neu geparentet.");
                if (_uiCallbackFaultCount != faultCount || viewer.BrowserFaultCount != 0)
                    throw new InvalidOperationException("Simulation-Tabwechsel hat einen UI- oder Browserfehler erzeugt.");
            }
            await WaitForSimulationSmokeAsync(() => viewer.HasCompletedNavigation && viewer.CommittedNavigationCount >= 3,
                "Die geänderte HTML-Datei wurde nicht übernommen.");
            var updated = await WaitForSimulationSmokeFrameAsync(viewer, 2);
            await Task.Delay(120, _lifetime.Token);
            var resumed = await ReadSimulationSmokeFrameAsync(viewer);
            if (updated.Revision != 2 || resumed.Ticks <= updated.Ticks || viewer.CommittedNavigationCount != 3)
                throw new InvalidOperationException("HTML-Änderung oder Wiederaufnahme nach Tabwechsel ist nicht korrekt.");
            await SaveMathPreviewAsync(ResearchHost, "native-simulation-lifecycle-preview.png");
            return new
            {
                passed = true, processId = Environment.ProcessId, tabCycles = 40, simulationVisits = 41, publicationVisits, chatVisits,
                snapshotUpdates = 40, errorUpdates = 40, htmlRevisions = 2, suspendResumeCycles,
                navigationCount = viewer.CommittedNavigationCount, browserFaults = viewer.BrowserFaultCount,
                explicitReloadNavigations = 1, controls,
                uiFaults = _uiCallbackFaultCount - faultCount, stableRoot = true, stableBrowserParent = true,
                realCanvasAnimation = true, animationResumed = true, publicationFixtureLoaded = publication is not null,
            };
        }
        finally
        {
            HideResearchView();
            // Close the fixture browser while still parented, then remove only
            // its owning pane. No user/session view is moved or disposed here.
            if (_interactiveSimulationViews.Remove(viewKey, out var fixtureViewer))
                await fixtureViewer.DisposeForSmokeAsync().WaitAsync(TimeSpan.FromSeconds(20));
            if (_simulationPanes.Remove(paneKey, out var fixturePane)) _scienceBody?.Children.Remove(fixturePane.Root);
            if (_publicationViews.Remove(owner, out var fixturePublication))
            { await fixturePublication.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20)); _scienceBody?.Children.Remove(fixturePublication); }
            foreach (var key in _scienceEmptyBodies.Keys.Where(key => key.StartsWith(owner + "|", StringComparison.Ordinal)).ToArray())
                if (_scienceEmptyBodies.Remove(key, out var empty)) _scienceBody?.Children.Remove(empty);
            _researchLoading.Remove(owner); _researchSessions.Remove(owner); _simulationScrollOffsets.Remove(paneKey);
            _sciencePresentationSmokeProvider = previousProvider;
            _simulationOpenSmokeAdapter = previousOpenAdapter;
            _session = previousOwner; _mode = previousMode; _simulationView = previousSimulation;
            _scienceViewSignature = null;
            Environment.SetEnvironmentVariable("MISSUM_SCIENCE_QA_DIAGNOSTICS", previousDiagnostics);
            if (previousResearchOwner == previousOwner) OpenResearchView(previousSimulation);
            else ShowChatView();
            RenderSessionTabs();
        }
    }

    private async Task<ScientificSimulationArtifact> WriteSimulationControlPlotSmokeAsync(string projectId, string directory)
    {
        // A deterministic, real PNG exercises WinUI's image loader and zoom
        // viewport. This isolated UI fixture has no research execution receipt.
        var path = Path.Combine(directory, "control-plot.png");
        const int width = 1920, height = 1080;
        var pixels = new byte[width * height * 4];
        for (var position = 0; position < pixels.Length; position += 4)
        { pixels[position] = pixels[position + 1] = pixels[position + 2] = 34; pixels[position + 3] = 255; }
        for (var x = 0; x < width; x++)
        {
            var y = height / 2 + (int)Math.Round(Math.Sin(x / 100d) * 300);
            var position = (y * width + x) * 4;
            pixels[position] = 204; pixels[position + 1] = 202; pixels[position + 2] = 86;
        }
        using (var file = File.Create(path))
        using (var stream = file.AsRandomAccessStream())
        {
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                width, height, 96, 96, pixels);
            await encoder.FlushAsync();
        }
        var scriptPath = Path.Combine(directory, "control-plot.py");
        var dataPath = Path.Combine(directory, "control-plot.json");
        await File.WriteAllTextAsync(scriptPath, "# UI smoke fixture: the PNG is generated by the native smoke, not a scientific experiment.\n", _lifetime.Token);
        await File.WriteAllTextAsync(dataPath, $"{{\"fixture\":true,\"width\":{width},\"height\":{height}}}", _lifetime.Token);
        var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, _lifetime.Token)));
        return new(projectId + "-plot", "PNG-Bedienelement-Fixture", path, scriptPath, dataPath,
            "Forschungsexperiment · isoliertes UI-Fixture", true, hash, ProjectRoot: directory);
    }

    private async Task<object> VerifySimulationControlsSmokeAsync(SimulationPane pane, ScientificSimulationArtifact html,
        ScientificSimulationArtifact plot, NativeSimulationView viewer, object? retainedRoot, DependencyObject retainedParent, List<string> launchTargets)
    {
        var htmlKey = SimulationCardKey(html);
        var plotKey = SimulationCardKey(plot);
        var htmlCard = pane.Cards[htmlKey];
        var plotCard = pane.Cards[plotKey];
        var labels = new[] { "Öffnen", "Neu laden", "−", "100 %", "+", "Quellcode", "Daten" };
        if (!htmlCard.Toolbar.Children.OfType<Button>().Select(button => button.Content?.ToString()).SequenceEqual(labels)
            || !plotCard.Toolbar.Children.OfType<Button>().Select(button => button.Content?.ToString()).SequenceEqual(labels)
            || !htmlCard.SourceButton.IsEnabled || htmlCard.DataButton.IsEnabled
            || !plotCard.SourceButton.IsEnabled || !plotCard.DataButton.IsEnabled)
            throw new InvalidOperationException("HTML und PNG verwenden keine einheitlichen, verfügbaren Simulations-Steuerelemente.");
        void AssertRetained()
        {
            if (!ReferenceEquals(ResearchHost.Content, retainedRoot) || !ReferenceEquals(viewer.Parent, retainedParent)
                || !ReferenceEquals(pane.Cards[htmlKey], htmlCard) || !ReferenceEquals(pane.Cards[plotKey], plotCard))
                throw new InvalidOperationException("Die Simulations-Bedienelemente haben ihre behaltenen visuellen Hosts ersetzt.");
        }
        await InvokeSimulationControlSmokeAsync(htmlCard.OpenButton,
            () => launchTargets.Count == 1,
            "Öffnen hat den HTML-Artefaktpfad nicht an die native Dateizuordnung übergeben.");
        if (launchTargets[0] != Path.GetFullPath(html.ImagePath) || !_simulationView
            || plotCard.Container.Visibility != Visibility.Visible || htmlCard.Container.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Der HTML-Dateistart verwendet nicht die Simulation oder verändert unerwartet die Inline-Ansichten.");
        AssertRetained();
        var htmlLarger = htmlCard.Toolbar.Children.OfType<Button>().Single(button => Equals(button.Content, "+"));
        await InvokeSimulationControlSmokeAsync(htmlLarger, () => Math.Abs(htmlCard.Scale - 1.25) < .001,
            "HTML-Zoom wurde über die sichtbare Toolbar nicht geändert.");
        var zoom = await WaitForSimulationWrapperZoomSmokeAsync(viewer, 1.25);
        await InvokeSimulationControlSmokeAsync(htmlCard.ZoomButton, () => Math.Abs(htmlCard.Scale - 1) < .001,
            "HTML-Zoom wurde nicht zurückgesetzt.");
        await WaitForSimulationWrapperZoomSmokeAsync(viewer, 1);
        var originalNavigationCount = viewer.CommittedNavigationCount;
        var originalDocument = await viewer.ExecuteFrameDiagnosticScriptAsync("performance.timeOrigin");
        var reload = htmlCard.Toolbar.Children.OfType<Button>().Single(button => Equals(button.Content, "Neu laden"));
        await InvokeSimulationControlSmokeAsync(reload,
            () => viewer.HasCompletedNavigation && viewer.CommittedNavigationCount == originalNavigationCount + 1,
            "Neu laden hat kein neues HTML-Dokument im behaltenen Browser geöffnet.");
        await WaitForSimulationSmokeFrameAsync(viewer, 1);
        if (originalDocument == await viewer.ExecuteFrameDiagnosticScriptAsync("performance.timeOrigin"))
            throw new InvalidOperationException("Neu laden hat denselben HTML-Dokumentkontext behalten.");
        AssertRetained();
        await InvokeSimulationControlSmokeAsync(plotCard.OpenButton,
            () => launchTargets.Count == 2,
            "Öffnen hat den PNG-Artefaktpfad nicht an die native Dateizuordnung übergeben.");
        if (launchTargets[1] != Path.GetFullPath(plot.ImagePath) || htmlCard.Container.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Der PNG-Dateistart verwendet nicht die Abbildung oder blendet die Inline-Simulation aus.");
        await WaitForSimulationSmokeAsync(() => plotCard.Image!.Source is Microsoft.UI.Xaml.Media.Imaging.BitmapImage { PixelWidth: 1920, PixelHeight: 1080 }
            && plotCard.ImageViewport!.ViewportWidth > 0 && plotCard.ImageViewport.ViewportHeight > 0,
            "Das PNG-Fixture wurde im echten Bild-Viewport nicht geladen.");
        await WaitForSimulationSmokeAsync(() => plotCard.Image!.Width <= plotCard.ImageViewport!.ViewportWidth + 1
            && plotCard.Image.Height <= plotCard.ImageViewport.ViewportHeight + 1 && plotCard.ImageViewport.ScrollableWidth <= 1
            && plotCard.ImageViewport.ScrollableHeight <= 1,
            "Der große PNG-Plot passt bei 100 Prozent nicht vollständig in den verfügbaren Viewport.");
        var fittedWidth = plotCard.ImageViewport!.ActualWidth;
        var plotLarger = plotCard.Toolbar.Children.OfType<Button>().Single(button => Equals(button.Content, "+"));
        await InvokeSimulationControlSmokeAsync(plotLarger, () => Math.Abs(plotCard.ImageViewport!.ZoomFactor - 1.25) < .01,
            "PNG-Zoom hat den echten nativen ScrollViewer nicht erreicht.");
        var previousViewportWidth = plotCard.ImageViewport.Width;
        try
        {
            plotCard.ImageViewport.Width = Math.Max(1, fittedWidth * .75);
            await WaitForSimulationSmokeAsync(() => Math.Abs(plotCard.Image!.Width - plotCard.ImageViewport.ActualWidth) < 1
                && plotCard.ImageViewport.ActualWidth < fittedWidth * .8 && Math.Abs(plotCard.ImageViewport.ZoomFactor - 1.25) < .01,
                "Nach Verkleinerung des Viewports wurde die Basisfläche nicht angepasst oder der Benutzerzoom ging verloren.");
        }
        finally { plotCard.ImageViewport.Width = previousViewportWidth; UpdateLayout(); }
        await WaitForSimulationSmokeAsync(() => Math.Abs(plotCard.Image!.Width - fittedWidth) < 1
            && Math.Abs(plotCard.ImageViewport.ZoomFactor - 1.25) < .01,
            "Beim Vergrößern des Viewports wurde der Benutzerzoom nicht beibehalten.");
        await InvokeSimulationControlSmokeAsync(plotCard.ZoomButton, () => Math.Abs(plotCard.ImageViewport!.ZoomFactor - 1) < .01,
            "PNG-Zoom konnte nicht auf die Ausgangsgröße zurückgesetzt werden.");
        await WaitForSimulationSmokeAsync(() => plotCard.ImageViewport.ScrollableWidth <= 1 && plotCard.ImageViewport.ScrollableHeight <= 1,
            "100 Prozent setzt die Abbildung nicht auf die vollständige Viewportdarstellung zurück.");
        var source = plotCard.Sources.Children.OfType<Expander>().Single(item => Equals(item.Header, "Python-Code"));
        await InvokeSimulationControlSmokeAsync(plotCard.SourceButton, () => source.IsExpanded && source.Content is not null,
            "Quellcode wurde nicht als eingebettete Darstellung geöffnet.");
        var data = plotCard.Sources.Children.OfType<Expander>().Single(item => Equals(item.Header, "Daten"));
        await InvokeSimulationControlSmokeAsync(plotCard.DataButton, () => data.IsExpanded && data.Content is not null,
            "Daten wurden nicht als eingebettete Darstellung geöffnet.");
        var plotImage = plotCard.Image ?? throw new InvalidOperationException("Der PNG-Viewer fehlt.");
        var originalImage = plotImage.Source;
        var plotReload = plotCard.Toolbar.Children.OfType<Button>().Single(button => Equals(button.Content, "Neu laden"));
        await InvokeSimulationControlSmokeAsync(plotReload,
            () => !ReferenceEquals(plotImage.Source, originalImage) && plotImage.Source is Microsoft.UI.Xaml.Media.Imaging.BitmapImage { PixelWidth: 1920, PixelHeight: 1080 },
            "PNG-Neuladen hat die Bilddatei nicht erneut eingelesen.");
        var restored = await ReadSimulationSmokeFrameAsync(viewer);
        await Task.Delay(120, _lifetime.Token);
        if ((await ReadSimulationSmokeFrameAsync(viewer)).Ticks <= restored.Ticks)
            throw new InvalidOperationException("Die HTML-Animation läuft nach der PNG-Bedienung nicht weiter.");
        AssertRetained();
        return new { passed = true, matchingToolbars = true, htmlShellTarget = launchTargets[0], plotShellTarget = launchTargets[1],
            shellLaunchIntercepted = true, externalProcessesStarted = false, fitAt100Percent = true, zoomPreservedAcrossResize = true,
            htmlZoomApplied = zoom, htmlZoomReset = true, nativePlotZoom = true, htmlReloadDocumentChanged = true,
            plotReloadDecoded = true, inlineSourceAndData = true, inlineViewsPreserved = true, stableBrowserParent = true };
    }

    private async Task InvokeSimulationControlSmokeAsync(Button button, Func<bool> completed, string error)
    {
        if (!button.IsEnabled || button.Visibility != Visibility.Visible
            || new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new InvalidOperationException("Die Simulationsaktion ist kein bedienbares natives Steuerelement: " + button.Content);
        invoke.Invoke();
        await WaitForSimulationSmokeAsync(completed, error);
    }

    private static async Task<double> ReadSimulationWrapperZoomSmokeAsync(NativeSimulationView viewer)
    {
        // Probe and pending scale application share the real serialized browser
        // queue. This reads the visible wrapper rather than trusting card state.
        var raw = await viewer.ExecuteDiagnosticScriptAsync("Number(document.querySelector('iframe').style.zoom || 1)")
            .WaitAsync(TimeSpan.FromSeconds(10));
        using var result = JsonDocument.Parse(raw);
        return result.RootElement.GetDouble();
    }

    private async Task<double> WaitForSimulationWrapperZoomSmokeAsync(NativeSimulationView viewer, double expected)
    {
        var deadline = Environment.TickCount64 + 20_000;
        while (Environment.TickCount64 < deadline)
        {
            var actual = await ReadSimulationWrapperZoomSmokeAsync(viewer);
            if (Math.Abs(actual - expected) < .001) return actual;
            await Task.Delay(40, _lifetime.Token);
        }
        throw new TimeoutException("Die native Zoomaktion wurde nicht im echten Browser-Wrapper angewendet.");
    }

    private async Task WaitForSimulationSmokeAsync(Func<bool> predicate, string error)
    {
        var deadline = Environment.TickCount64 + 20_000;
        while (!predicate())
        {
            if (Environment.TickCount64 >= deadline) throw new TimeoutException(error);
            UpdateLayout(); await Task.Delay(40, _lifetime.Token);
        }
        UpdateLayout();
    }

    private static async Task<(int Canvases, long Ticks, int Revision)> ReadSimulationSmokeFrameAsync(NativeSimulationView viewer)
    {
        var raw = await viewer.ExecuteFrameDiagnosticScriptAsync(
            "JSON.stringify({canvases:document.querySelectorAll('canvas').length,ticks:window.__missumSmokeTicks,revision:window.__missumSmokeRevision})")
            .WaitAsync(TimeSpan.FromSeconds(10));
        using var encoded = JsonDocument.Parse(raw);
        var json = encoded.RootElement.ValueKind == JsonValueKind.String ? encoded.RootElement.GetString()! : raw;
        using var document = JsonDocument.Parse(json);
        var data = document.RootElement;
        return (data.GetProperty("canvases").GetInt32(), data.GetProperty("ticks").GetInt64(), data.GetProperty("revision").GetInt32());
    }

    private async Task<(int Canvases, long Ticks, int Revision)> WaitForSimulationSmokeFrameAsync(NativeSimulationView viewer, int revision)
    {
        var deadline = Environment.TickCount64 + 20_000;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                var frame = await ReadSimulationSmokeFrameAsync(viewer);
                if (frame.Canvases == 1 && frame.Revision == revision && frame.Ticks >= 0) return frame;
            }
            catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or JsonException)
            {
                // Outer navigation may finish before the sandboxed iframe has
                // installed its DOM/script. This readiness wait is bounded.
            }
            await Task.Delay(40, _lifetime.Token);
        }
        throw new TimeoutException("Der Simulationsframe hat seine Canvas-/Revisionsdaten nicht bereitgestellt.");
    }
}
