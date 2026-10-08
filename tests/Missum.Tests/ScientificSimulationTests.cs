using System.Text.Json;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Missum.App.Services;
using Missum.Ai.Contracts;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Core.Research;

namespace Missum.Tests;

public sealed class ScientificSimulationTests
{
    [Fact]
    public void NativeNavigationAllowsOnlyItsExactHostDocumentAndBlankFrame()
    {
        var document = ScientificSimulationHtml.NativeDocument(InteractiveHtml);
        var trusted = ScientificSimulationHtml.HostDocumentUri(document);
        Assert.StartsWith("data:text/html;charset=utf-8;base64,", trusted, StringComparison.Ordinal);
        Assert.True(ScientificSimulationHtml.IsHostNavigationAllowed(trusted, trusted));
        Assert.True(ScientificSimulationHtml.IsHostNavigationAllowed("about:blank", trusted));
        Assert.True(ScientificSimulationHtml.IsHostNavigationAllowed("about:srcdoc", trusted));
        Assert.False(ScientificSimulationHtml.IsHostNavigationAllowed(ScientificSimulationHtml.HostDocumentUri("<script>window.open('http://example.test')</script>"), trusted));
        Assert.False(ScientificSimulationHtml.IsHostNavigationAllowed("data:text/html,other", trusted));
        Assert.False(ScientificSimulationHtml.IsHostNavigationAllowed("https://example.test", trusted));
        Assert.False(ScientificSimulationHtml.IsHostNavigationAllowed("file:///C:/secret", trusted));
        Assert.False(ScientificSimulationHtml.IsHostNavigationAllowed("javascript:alert(1)", trusted));
        Assert.False(ScientificSimulationHtml.IsHostNavigationAllowed(trusted, null));
    }

    [Fact]
    public void ArtifactPathsRejectEscapesAndRootAliases()
    {
        var root = Path.Combine(Path.GetTempPath(), "missum-science-simulation-boundary");
        Assert.Equal(Path.Combine(root, "plots", "result.png"), ScientificSimulationService.SafePath(root, "plots/result.png"));
        Assert.Throws<UnauthorizedAccessException>(() => ScientificSimulationService.SafePath(root, "../outside.png"));
        Assert.Throws<UnauthorizedAccessException>(() => ScientificSimulationService.SafePath(root, "."));
    }

    [Fact]
    public async Task RefreshNeverGeneratesPublicationOrEvidencePlotsAndIgnoresUnattributedImages()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (repository, project) = await CreateProjectAsync(environment, "empty");
        await repository.SaveArchiveSnapshotAsync(project.Id, new(1, "{}", [], [], "report-table", "scientificMarkdown",
            "unresolved", "| Zeit (s) | Wert (m) |\n| --- | --- |\n| 0 | 2 |\n| 1 | 4 |", "{}",
            "test.snapshot", "{}", "run-table", 1, DateTimeOffset.UtcNow));
        var sandbox = new RecordingSandbox(Path.Combine(environment.Directory, "simulation"));
        var layout = await sandbox.EnsureProjectAsync(project.Id);
        var legacy = Path.Combine(layout.ArtifactsPath, "publication", "legacy");
        Directory.CreateDirectory(legacy);
        await File.WriteAllBytesAsync(Path.Combine(legacy, "evidence.png"), Png);
        await File.WriteAllBytesAsync(Path.Combine(layout.ArtifactsPath, "unattributed.png"), Png);
        using var service = new ScientificSimulationService(repository, sandbox);

        var snapshot = await service.RefreshAsync(project.Id);

        Assert.Equal("empty", snapshot.Status);
        Assert.Empty(snapshot.Artifacts);
        Assert.Equal(0, sandbox.Executions);
        Assert.Empty(Directory.EnumerateFileSystemEntries(layout.WorkPath));
        Assert.False(File.Exists(Path.Combine(layout.RootPath, "last-visualization.json")));
    }

    [Fact]
    public async Task StandaloneRealtimeSimulationRestoresInItsProjectWithoutPythonExecution()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (repository, project) = await CreateProjectAsync(environment, "interactive");
        var sandbox = new RecordingSandbox(Path.Combine(environment.Directory, "simulation"));
        var layout = await sandbox.EnsureProjectAsync(project.Id);
        var path = Path.Combine(layout.WorkPath, "animation.html");
        await File.WriteAllTextAsync(path, InteractiveHtml);
        using var service = new ScientificSimulationService(repository, sandbox);

        var first = await service.RefreshAsync(project.Id);
        using var restarted = new ScientificSimulationService(repository, sandbox);
        var restored = await restarted.RefreshAsync(project.Id);

        Assert.Equal("ready", restored.Status);
        var artifact = Assert.Single(restored.Artifacts);
        Assert.Equal(Assert.Single(first.Artifacts), artifact);
        Assert.Equal(path, artifact.ImagePath);
        Assert.Equal(path, artifact.ScriptPath);
        Assert.Equal("Erdmagnetfeld – Echtzeit", artifact.Title);
        Assert.Equal("interactive", artifact.Kind);
        Assert.Equal("text/html", artifact.ContentType);
        Assert.False(artifact.IsResearchData);
        Assert.Null(artifact.Execution);
        Assert.StartsWith("Interaktive Simulation · Projektdatei", artifact.Provenance, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessSucceeded", artifact.Provenance, StringComparison.Ordinal);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))), artifact.Sha256);
        Assert.Equal(0, sandbox.Executions);
        Assert.Empty((await repository.LoadResultSnapshotAsync(project.Id)).Experiments);
    }

    [Fact]
    public async Task HtmlDiscoveryKeepsProjectScopeAndRejectsDocumentsAndPublicationPreviews()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (repository, project) = await CreateProjectAsync(environment, "interactive-scope");
        var sandbox = new RecordingSandbox(Path.Combine(environment.Directory, "simulation"));
        var layout = await sandbox.EnsureProjectAsync(project.Id);
        var other = await sandbox.EnsureProjectAsync("other-project");
        await File.WriteAllTextAsync(Path.Combine(other.WorkPath, "animation.html"), InteractiveHtml);
        await File.WriteAllTextAsync(Path.Combine(layout.WorkPath, "report.html"), "<!doctype html><html><script>let a=1;</script><p>Publikation</p></html>");
        Directory.CreateDirectory(Path.Combine(layout.ArtifactsPath, "publication"));
        await File.WriteAllTextAsync(Path.Combine(layout.ArtifactsPath, "publication", "preview.html"), InteractiveHtml);
        using var service = new ScientificSimulationService(repository, sandbox);

        Assert.Empty((await service.RefreshAsync(project.Id)).Artifacts);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ResolveInteractiveSimulationAsync(project.Id,
            Path.Combine(other.WorkPath, "animation.html")));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ResolveInteractiveSimulationAsync(project.Id,
            "work/report.html"));
        Assert.Equal(0, sandbox.Executions);
    }

    [Fact]
    public async Task ChangedSimulationGetsANewHashAndInvalidOrOversizedHtmlIsNotRestored()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (repository, project) = await CreateProjectAsync(environment, "interactive-update");
        var sandbox = new RecordingSandbox(Path.Combine(environment.Directory, "simulation"));
        var layout = await sandbox.EnsureProjectAsync(project.Id);
        var path = Path.Combine(layout.WorkPath, "animation.html");
        await File.WriteAllTextAsync(path, InteractiveHtml);
        using var service = new ScientificSimulationService(repository, sandbox);
        var old = Assert.Single((await service.RefreshAsync(project.Id)).Artifacts);
        await File.WriteAllTextAsync(path, InteractiveHtml.Replace("let t=0", "let t=1", StringComparison.Ordinal));
        var current = Assert.Single((await service.RefreshAsync(project.Id)).Artifacts);
        Assert.NotEqual(old.Sha256, current.Sha256);
        Assert.NotEqual(old.Id, current.Id);
        await File.WriteAllTextAsync(path, "<!doctype html><canvas></canvas>");
        Assert.Empty((await service.RefreshAsync(project.Id)).Artifacts);
        await File.WriteAllTextAsync(path, InteractiveHtml + new string(' ', ScientificSimulationHtml.MaximumBytes));
        Assert.Empty((await service.RefreshAsync(project.Id)).Artifacts);
        Assert.Equal(0, sandbox.Executions);
    }

    [Fact]
    public void NativeSimulationUsesOnlyOpaqueSandboxedInlineDocument()
    {
        var document = ScientificSimulationHtml.NativeDocument(InteractiveHtml);
        Assert.Contains("sandbox=\"allow-scripts\"", document, StringComparison.Ordinal);
        Assert.DoesNotContain("allow-same-origin", document, StringComparison.Ordinal);
        Assert.DoesNotContain("allow-popups", document, StringComparison.Ordinal);
        Assert.DoesNotContain("file://", document, StringComparison.Ordinal);
        Assert.DoesNotContain("chrome.webview", document, StringComparison.Ordinal);
        Assert.Contains("connect-src 'none'", ScientificSimulationHtml.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains("frame-src 'none'", ScientificSimulationHtml.ContentSecurityPolicy, StringComparison.Ordinal);
        var start = document.IndexOf("atob('", StringComparison.Ordinal) + "atob('".Length;
        var end = document.IndexOf("')", start, StringComparison.Ordinal);
        var frameSource = Encoding.UTF8.GetString(Convert.FromBase64String(document[start..end]));
        Assert.Contains(InteractiveHtml, frameSource, StringComparison.Ordinal);
        Assert.Contains("Content-Security-Policy", frameSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScienceWorkspaceOpenRegistersRealtimeSimulationWithoutAnExternalProcess()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Erdmagnetfeld", ChatMode.ClaudeScience);
        var repository = environment.Get<IScientificResearchRepository>();
        var now = DateTimeOffset.UtcNow;
        var project = new ScientificResearchProject("research-" + session.Id.ToString("N"), session.Id, "scientificEvidence",
            "Erdmagnetfeld", "Erdmagnetfeld", "codingWorkspaceResearch", "multiPath", "active", 2, 1, now, now);
        await repository.UpsertProjectAsync(project);
        var states = Assert.IsAssignableFrom<IScientificResearchStateRepository>(repository);
        var working = await states.ApplyWorkingUpdateAsync(project.Id, "simulation-fixture-section", null, "Erdmagnetfeld",
            [new("grundlagen", "section", 0, JsonSerializer.SerializeToElement(new
            {
                title = "Modellannahmen", contentMarkdown = "Die Echtzeit-Simulation untersucht ein ausdrücklich hypothetisches Modell.",
                status = "unresolved", order = 10,
            }))]);
        Assert.True(working.Success);
        var sandbox = new RecordingSandbox(Path.Combine(environment.Directory, "Science"));
        var layout = await sandbox.EnsureProjectAsync(project.Id);
        var path = Path.Combine(layout.WorkPath, "animation.html");
        await File.WriteAllTextAsync(path, InteractiveHtml);
        using var simulations = new ScientificSimulationService(repository, sandbox);
        var renders = 0;
        using var publications = new ScientificPublicationService(repository, async (source, token) =>
        {
            Interlocked.Increment(ref renders);
            var pdf = Path.ChangeExtension(source, ".pdf");
            await File.WriteAllBytesAsync(pdf, Encoding.ASCII.GetBytes("%PDF-1.7\n" + new string(' ', 2048) + "\n%%EOF\n"), token);
            return pdf;
        }, Path.Combine(environment.Directory, "publications"), sandbox: sandbox);
        using var presentation = new ScientificPresentationCoordinator(publications, simulations, static (_, _) => Task.CompletedTask);
        var broker = new LocalToolBroker(null!, null!, null!, chats,
            sciencePresentation: presentation, scientificResearch: repository, scientificSimulations: simulations);
        var requested = Path.GetRelativePath(environment.Directory, path);
        var proposal = new ToolProposal("open-simulation", "science-run", WorkspaceTools.Open,
            JsonSerializer.SerializeToElement(new { path = requested }), ToolRiskClass.Process,
            "Echtzeit-Simulation anzeigen", DateTimeOffset.UtcNow.AddMinutes(1));

        var opened = await broker.ExecuteAsync(proposal, session.Id, null, environment.Directory);

        Assert.Equal("completed", opened.Status);
        Assert.False(opened.Result.GetProperty("opened").GetBoolean());
        Assert.True(opened.Result.GetProperty("available").GetBoolean());
        Assert.False(opened.Result.GetProperty("displayed").GetBoolean());
        Assert.True(opened.Result.GetProperty("registered").GetBoolean());
        Assert.True(opened.Result.GetProperty("integrated").GetBoolean());
        Assert.Equal("simulation", opened.Result.GetProperty("view").GetString());
        Assert.Equal(project.Id, opened.Result.GetProperty("projectId").GetString());
        Assert.Equal(requested, opened.Result.GetProperty("path").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))),
            opened.Result.GetProperty("sha256").GetString());
        var instruction = opened.Result.GetProperty("instruction").GetString()!;
        Assert.Contains("registriert, wurde aber nicht angezeigt", instruction, StringComparison.Ordinal);
        Assert.Contains("aktuelle Nutzeransicht bleibt erhalten", instruction, StringComparison.Ordinal);
        Assert.Contains("Chat statt der Simulation", instruction, StringComparison.Ordinal);
        Assert.Contains("image.input operation=file", instruction, StringComparison.Ordinal);
        Assert.Contains("keine ungeprüfte HTML-Animation", instruction, StringComparison.Ordinal);
        Assert.False(opened.Result.TryGetProperty("processId", out _));
        await presentation.WaitForIdleAsync(project.Id);
        var snapshot = Assert.IsType<ScientificPresentationSnapshot>(presentation.GetSnapshot(project.Id));
        Assert.NotNull(snapshot.Publication);
        Assert.Null(snapshot.PublicationError);
        Assert.Equal("interactive", Assert.Single(snapshot.Simulation!.Artifacts).Kind);
        Assert.Equal(1, renders);
        var other = await sandbox.EnsureProjectAsync("other-project");
        await File.WriteAllTextAsync(Path.Combine(other.WorkPath, "animation.html"), InteractiveHtml);
        var outside = await broker.ExecuteAsync(proposal with
        {
            ProposalId = "open-other-simulation", Arguments = JsonSerializer.SerializeToElement(new
            { path = Path.GetRelativePath(environment.Directory, Path.Combine(other.WorkPath, "animation.html")) }),
        }, session.Id, null, environment.Directory);
        Assert.Equal("failed", outside.Status);
        Assert.Equal("client.tool_failed", outside.ErrorCode);
        Assert.False(outside.Result.TryGetProperty("processId", out _));
        Assert.Equal(0, sandbox.Executions);
    }

    [Fact]
    public async Task SuccessfulPythonFigureRestoresAfterRestartAndLaterReportWithoutReexecution()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (repository, project) = await CreateProjectAsync(environment, "restore");
        var sandbox = new RecordingSandbox(Path.Combine(environment.Directory, "simulation"));
        using var service = new ScientificSimulationService(repository, sandbox);
        const string code = "import matplotlib.pyplot as plt\nplt.plot([0, 1], [0, 1])";
        var original = await service.RunAsync(project.Id, code, "Prüfplot");
        Assert.Equal("ready", original.Status);
        var image = Assert.Single(original.Artifacts);
        Assert.Equal(code, await File.ReadAllTextAsync(image.ScriptPath!));
        Assert.StartsWith("Forschungsexperiment · ", image.Provenance, StringComparison.Ordinal);
        _ = await service.RefreshAsync(project.Id);
        await SaveBoundReportAsync(repository, project, "later-research-run", DateTimeOffset.UtcNow.AddMinutes(1));
        using var restoredService = new ScientificSimulationService(repository, sandbox);

        var restored = await restoredService.RefreshAsync(project.Id);

        Assert.Equal("ready", restored.Status);
        var restoredImage = Assert.Single(restored.Artifacts);
        Assert.Equal(image.ImagePath, restoredImage.ImagePath);
        Assert.Equal(image.Sha256, restoredImage.Sha256);
        Assert.Equal(image.ScriptPath, restoredImage.ScriptPath);
        Assert.Equal(1, sandbox.Executions);
        var layout = await sandbox.EnsureProjectAsync(project.Id);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(layout.RootPath, "last-visualization.json")));
        Assert.Equal(project.Id, manifest.RootElement.GetProperty("projectId").GetString());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restoredService.RefreshAsync(project.Id, cancelled.Token));
        Assert.Equal(1, sandbox.Executions);
    }

    [Fact]
    public async Task FailedPythonDoesNotCreateAValidSimulation()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (repository, project) = await CreateProjectAsync(environment, "failed");
        var sandbox = new RecordingSandbox(Path.Combine(environment.Directory, "simulation")) { FailExecution = true };
        using var service = new ScientificSimulationService(repository, sandbox);

        var failed = await service.RunAsync(project.Id, "print('failed')", "Fehlgeschlagene Simulation");
        var refreshed = await service.RefreshAsync(project.Id);

        Assert.Equal("failed", failed.Status);
        Assert.Empty(failed.Artifacts);
        Assert.Equal("empty", refreshed.Status);
        Assert.Empty(refreshed.Artifacts);
        Assert.Equal("ProcessFailed", Assert.Single((await repository.LoadResultSnapshotAsync(project.Id)).Experiments).VerificationStatus);
        Assert.Equal(1, sandbox.Executions);
    }

    [Fact]
    public async Task PublicationRevisionAndNumericContentNeverTriggerASimulation()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (repository, project) = await CreateProjectAsync(environment, "publication-owner");
        var publication = await WritePublicationFixtureAsync(environment.Directory, project, "publication",
            "# Publikation\n\n| Zeit (s) | Wert (m) |\n| --- | --- |\n| 0 | 4 |\n| 2 | 8 |");
        var sandbox = new RecordingSandbox(Path.Combine(environment.Directory, "simulation"));
        using var service = new ScientificSimulationService(repository, sandbox);

        var wrongProject = await service.RefreshAsync(project.Id, publication with { ProjectId = "another-project" });
        var oldRevision = await service.RefreshAsync(project.Id, publication with { Revision = project.Revision - 1 });

        Assert.Equal("failed", wrongProject.Status);
        Assert.Contains("anderen Forschungsprojekt", wrongProject.Detail);
        Assert.Equal("empty", oldRevision.Status);
        Assert.Empty(oldRevision.Artifacts);
        Assert.Equal(0, sandbox.Executions);
    }

    [Fact]
    public async Task CustomSimulationFinishingForReplacedResearchDoesNotLeakItsImages()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (repository, project) = await CreateProjectAsync(environment, "replaced");
        await SaveBoundReportAsync(repository, project, "before-run", DateTimeOffset.UtcNow.AddMinutes(-1));
        var sandbox = new RecordingSandbox(Path.Combine(environment.Directory, "simulation"))
        {
            OnExecution = () => SaveBoundReportAsync(repository, project, "after-run", DateTimeOffset.UtcNow),
        };
        using var service = new ScientificSimulationService(repository, sandbox);
        var snapshot = await service.RunAsync(project.Id, "print('old research')", "Alter Lauf");

        Assert.Equal("updating", snapshot.Status);
        Assert.Empty(snapshot.Artifacts);
        Assert.Single((await repository.LoadResultSnapshotAsync(project.Id)).Experiments);
    }

    private static Task SaveBoundReportAsync(IScientificResearchRepository repository, ScientificResearchProject project,
        string runId, DateTimeOffset startedAt) => repository.SaveArchiveSnapshotAsync(project.Id, new(1, "{}", [], [],
        "report-" + runId, "scientificMarkdown", "unresolved", "# Aktuelle Forschungsfrage", JsonSerializer.Serialize(new
        { projectId = project.Id, runId, runStartedAt = startedAt }), "test.snapshot", "{}", runId, 1, DateTimeOffset.UtcNow));

    private static async Task<ScientificPublicationArtifact> WritePublicationFixtureAsync(string directory,
        ScientificResearchProject project, string name, string markdown)
    {
        var path = Path.Combine(directory, name + ".md");
        await File.WriteAllTextAsync(path, markdown);
        return new(project.Id, project.Revision, path, Path.ChangeExtension(path, ".pdf"), true,
            DateTimeOffset.UtcNow, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(markdown))));
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task DockerRendersModelSelectedSimulationAndRestoresItsActualFigure()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_AI_RESEARCH_SANDBOX_LIVE") != "1") return;
        await using var environment = await TestEnvironment.CreateAsync();
        var (repository, project) = await CreateProjectAsync(environment, "live");
        var now = DateTimeOffset.UtcNow;
        static double Displacement(double t)
        {
            var gamma = .2;
            var omega = Math.Sqrt(4 - gamma * gamma);
            return Math.Exp(-gamma * t) * (Math.Cos(omega * t) + gamma / omega * Math.Sin(omega * t));
        }
        var report = "# Gedämpfter harmonischer Oszillator\n\nModellrechnung, keine experimentellen Messwerte. "
            + "Annahmen: $m=1\\,\\mathrm{kg}$, $c=0{,}4\\,\\mathrm{Ns/m}$, $k=4\\,\\mathrm{N/m}$; "
            + "$m\\ddot{x}+c\\dot{x}+kx=0$, $x(0)=1\\,\\mathrm{m}$ und $\\dot{x}(0)=0$.\n\n"
            + "| Zeit (s) | Auslenkung (m) |\n| --- | --- |\n"
            + string.Join('\n', Enumerable.Range(0, 9).Select(index => "| " + (index * .5).ToString(CultureInfo.InvariantCulture)
                + " | " + Displacement(index * .5).ToString("G10", CultureInfo.InvariantCulture) + " |"));
        await repository.SaveArchiveSnapshotAsync(project.Id, new(1, "{}", [], [], "report-live", "scientificMarkdown",
            "unresolved", report, "{}",
            "test.snapshot", "{}", "run-live", 1, now));
        var profile = new AssistantRuntimeProfile("simulation-test", "Missum", environment.Directory,
            new Uri("http://127.0.0.1:8080"), 8081, 8082, Path.Combine(environment.Directory, "native"),
            Path.Combine(environment.Directory, "llama.exe"), Path.Combine(environment.Directory, "stack"), "test");
        using var sandbox = new ResearchSandboxService(profile);
        using var service = new ScientificSimulationService(repository, sandbox);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var automatic = await service.RefreshAsync(project.Id, timeout.Token);
        Assert.Empty(automatic.Artifacts);
        var simulation = await service.RunAsync(project.Id, """
            import numpy as np
            import matplotlib.pyplot as plt
            import json, os, pathlib
            from scipy.integrate import solve_ivp
            # Explicit hypothetical mechanical model, not measured data.
            m, c, k = 1.0, 0.4, 4.0
            t = np.linspace(0, 12, 601)
            numerical = solve_ivp(lambda time, y: [y[1], -(c*y[1]+k*y[0])/m],
                (t[0], t[-1]), [1., 0.], t_eval=t, rtol=1e-10, atol=1e-12)
            assert numerical.success
            gamma = c/(2*m)
            omega = np.sqrt(k/m-gamma**2)
            analytic = np.exp(-gamma*t)*(np.cos(omega*t)+gamma/omega*np.sin(omega*t))
            maximum_error = float(np.max(np.abs(numerical.y[0]-analytic)))
            assert maximum_error < 1e-8, maximum_error
            output = pathlib.Path(os.environ['MISSUM_ARTIFACTS'])
            (output/'data.json').write_text(json.dumps({'model':'damped harmonic oscillator',
                'm_kg':m, 'c_Ns_per_m':c, 'k_N_per_m':k, 'x0_m':1, 'v0_m_per_s':0,
                'time_s':t.tolist(), 'displacement_m':numerical.y[0].tolist(),
                'maximum_absolute_error_m':maximum_error}), encoding='utf-8')
            fig, ax = plt.subplots(figsize=(9,4.8))
            ax.plot(t, numerical.y[0], label='Numerisch (solve_ivp)', color='#8057c7')
            ax.plot(t[::20], analytic[::20], '.', label='Analytische Kontrolle', color='#27888b')
            ax.set_xlabel('Zeit (s)'); ax.set_ylabel('Auslenkung (m)')
            ax.set_title('Gedämpfter Oszillator · explizites Modell, keine Messdaten')
            ax.grid(alpha=.2); ax.legend(); fig.tight_layout()
            plt.show()
            print(json.dumps({'maximumAbsoluteError':maximum_error, 'verified':True}))
            """, "Gedämpfter harmonischer Oszillator", timeout.Token);
        Assert.Equal("ready", simulation.Status);
        Assert.True(new FileInfo(Assert.Single(simulation.Artifacts).ImagePath).Length > 10_000);
        var restored = await service.RefreshAsync(project.Id, timeout.Token);
        Assert.Contains(restored.Artifacts, item => item.ImagePath == simulation.Artifacts[0].ImagePath);
        var evidenceDirectory = Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_EVIDENCE_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(evidenceDirectory))
        {
            var destination = Path.Combine(evidenceDirectory, "scientific-simulation");
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Combine(destination, "publication.md"), report, timeout.Token);
            foreach (var (snapshot, prefix) in new[] { (simulation, "oscillator") })
            {
                var artifact = Assert.Single(snapshot.Artifacts);
                File.Copy(artifact.ImagePath, Path.Combine(destination, prefix + ".png"), overwrite: true);
                if (artifact.ScriptPath is not null) File.Copy(artifact.ScriptPath, Path.Combine(destination, prefix + ".py"), overwrite: true);
                var dataPath = artifact.DataPath ?? Path.Combine(Path.GetDirectoryName(artifact.ImagePath)!, "data.json");
                if (File.Exists(dataPath)) File.Copy(dataPath, Path.Combine(destination, prefix + "-data.json"), overwrite: true);
            }
            await File.WriteAllTextAsync(Path.Combine(destination, "execution.json"), JsonSerializer.Serialize(
                (await repository.LoadResultSnapshotAsync(project.Id, timeout.Token)).Experiments), timeout.Token);
        }
    }

    private static async Task<(IScientificResearchRepository, ScientificResearchProject)> CreateProjectAsync(TestEnvironment environment, string suffix)
    {
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("Simulation " + suffix);
        var repository = environment.Get<IScientificResearchRepository>();
        var now = DateTimeOffset.UtcNow;
        var project = new ScientificResearchProject("research-simulation-" + suffix, session.Id, "scientificEvidence",
            "Daten untersuchen", "Daten untersuchen", "codingWorkspaceResearch", "multiPath", "active", 1, 1, now, now);
        await repository.UpsertProjectAsync(project);
        return (repository, project);
    }

    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private const string InteractiveHtml = "<!doctype html><html><head><meta charset=\"utf-8\"><title>Erdmagnetfeld – Echtzeit</title></head><body><button>Pause</button><canvas id=\"plot\"></canvas><script>let t=0;function tick(){t++;requestAnimationFrame(tick)};tick();</script></body></html>";

    private sealed class RecordingSandbox(string root) : IResearchSandboxService
    {
        public int Executions { get; private set; }
        public int LastTimeout { get; private set; }
        public bool FailExecution { get; init; }
        public Func<Task>? OnExecution { get; set; }
        public Task<ResearchSandboxLayout> EnsureProjectAsync(string projectId, CancellationToken cancellationToken = default)
        {
            var directory = Path.Combine(root, projectId);
            foreach (var child in new[] { "inputs", "work", "artifacts", "notebooks", "manuscripts", "env", "runs", "snapshots" })
                Directory.CreateDirectory(Path.Combine(directory, child));
            return Task.FromResult(new ResearchSandboxLayout(projectId, directory, Path.Combine(directory, "inputs"),
                Path.Combine(directory, "work"), Path.Combine(directory, "artifacts"), Path.Combine(directory, "notebooks"),
                Path.Combine(directory, "manuscripts"), Path.Combine(directory, "env"), Path.Combine(directory, "runs"),
                Path.Combine(directory, "snapshots"), DateTimeOffset.UtcNow, "test"));
        }
        public async Task<ResearchSandboxFileChange> WriteTextAsync(string projectId, string relativePath, string content,
            string? expectedSha256 = null, CancellationToken cancellationToken = default)
        {
            var layout = await EnsureProjectAsync(projectId, cancellationToken);
            var destination = ScientificSimulationService.SafePath(layout.WorkPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllTextAsync(destination, content, cancellationToken);
            return new("test", relativePath, null, "test-hash", true);
        }
        public async Task<ResearchSandboxRunResult> RunPythonAsync(string projectId, string relativeScriptPath,
            IReadOnlyList<string>? arguments = null, int timeoutSeconds = 7200, string? relativeWorkingDirectory = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Executions++; LastTimeout = timeoutSeconds;
            var layout = await EnsureProjectAsync(projectId, cancellationToken);
            if (!FailExecution)
            {
                var target = Path.Combine(layout.ArtifactsPath, arguments![1]["/sandbox/artifacts/".Length..]);
                Directory.CreateDirectory(target);
                await File.WriteAllBytesAsync(Path.Combine(target, "figure.png"), Png, cancellationToken);
                if (relativeScriptPath.EndsWith("render.py", StringComparison.Ordinal))
                {
                    var dataPath = Path.Combine(layout.WorkPath, arguments[0]["/sandbox/work/".Length..]);
                    using var data = JsonDocument.Parse(await File.ReadAllTextAsync(dataPath, cancellationToken));
                    await File.WriteAllTextAsync(Path.Combine(target, "manifest.json"), JsonSerializer.Serialize(new[]
                    { new { file = "figure.png", title = "Daten", isResearchData = data.RootElement.GetProperty("tables").GetArrayLength() > 0 } }), cancellationToken);
                }
            }
            var callback = OnExecution; OnExecution = null;
            if (callback is not null) await callback();
            return new(Guid.NewGuid().ToString("N"), FailExecution ? 1 : 0, false, "", FailExecution ? "matplotlib unavailable" : "",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "sandbox-python");
        }
        public Task<ResearchSandboxRuntimeStatus> PrepareRuntimeAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ResearchSandboxRuntimeStatus(true, "test"));
        public Task RestoreChangeSetAsync(string projectId, string changeSetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ArchiveProjectAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RestoreProjectAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
