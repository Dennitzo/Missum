using System.Security.Cryptography;
using System.Text.Json;
using Missum.App.Controls;
using Missum.App.Services;
using Microsoft.UI.Xaml;
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
            var pdfPath = Environment.GetEnvironmentVariable("MISSUM_SCIENCE_PDF_SMOKE_PATH");
            var publication = !string.IsNullOrWhiteSpace(pdfPath) && File.Exists(pdfPath)
                ? new ScientificPublicationArtifact(projectId, 1, htmlPath, pdfPath, true, DateTimeOffset.UtcNow, "smoke") : null;
            var simulation = new ScientificSimulationSnapshot(projectId, 1, "ready", "", [artifact], DateTimeOffset.UtcNow);
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
            var publicationVisits = 0;
            var chatVisits = 0;
            var suspendResumeCycles = 0;
            for (var iteration = 0; iteration < 40; iteration++)
            {
                state.Error = iteration % 3 == 0 ? "Smoke: temporärer Präsentationshinweis " + iteration : "";
                if (iteration == 20) artifact = await WriteArtifactAsync(2);
                simulation = simulation with { Revision = iteration + 2, Detail = "Snapshot " + iteration,
                    Artifacts = [artifact], UpdatedAt = DateTimeOffset.UtcNow };
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
            await WaitForSimulationSmokeAsync(() => viewer.HasCompletedNavigation && viewer.CommittedNavigationCount >= 2,
                "Die geänderte HTML-Datei wurde nicht übernommen.");
            var updated = await WaitForSimulationSmokeFrameAsync(viewer, 2);
            await Task.Delay(120, _lifetime.Token);
            var resumed = await ReadSimulationSmokeFrameAsync(viewer);
            if (updated.Revision != 2 || resumed.Ticks <= updated.Ticks || viewer.CommittedNavigationCount != 2)
                throw new InvalidOperationException("HTML-Änderung oder Wiederaufnahme nach Tabwechsel ist nicht korrekt.");
            await SaveMathPreviewAsync(ResearchHost, "native-simulation-lifecycle-preview.png");
            return new
            {
                passed = true, processId = Environment.ProcessId, tabCycles = 40, simulationVisits = 41, publicationVisits, chatVisits,
                snapshotUpdates = 40, errorUpdates = 40, htmlRevisions = 2, suspendResumeCycles,
                navigationCount = viewer.CommittedNavigationCount, browserFaults = viewer.BrowserFaultCount,
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
            _session = previousOwner; _mode = previousMode; _simulationView = previousSimulation;
            _scienceViewSignature = null;
            Environment.SetEnvironmentVariable("MISSUM_SCIENCE_QA_DIAGNOSTICS", previousDiagnostics);
            if (previousResearchOwner == previousOwner) OpenResearchView(previousSimulation);
            else ShowChatView();
            RenderSessionTabs();
        }
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
