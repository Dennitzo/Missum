using System.Collections.Concurrent;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Extensions;
using Missum.Core.Models;
using Missum.Core.Research;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;
using Xunit.Abstractions;

namespace Missum.Tests;

/// <summary>Real Science coordinator, automatic delegation, native replicas, Docker Python and PDF; no inference fixtures.</summary>
public sealed class ClaudeScienceSubagentLiveTests(ITestOutputHelper output)
{
    private const string PhysicsUrl = "https://openstax.org/books/university-physics-volume-1/pages/15-5-damped-oscillations";
    private const string SolverUrl = "https://docs.scipy.org/doc/scipy/reference/generated/scipy.integrate.solve_ivp.html";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions ProtocolJson = MissumAiProtocol.CreateJsonOptions();
    private static readonly string[] PrimaryUrls = [PhysicsUrl, SolverUrl];
    private static readonly byte[] PngHeader = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly string[] ScientificProcessTools = [ClientToolNames.ResearchCodeExecute, ClientToolNames.ResearchCodeTest, ClientToolNames.ResearchCodeBenchmark];

    [Fact]
    [Trait("Category", "Live")]
    public async Task ScienceAutomaticallyDelegatesNumericalValidationAndUsesItsRealPythonFigureWithoutRepeatingIt()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_SCIENCE_SUBAGENT_LIVE") != "1") return;
        var modelId = Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_GENERAL_MODEL")?.Trim();
        Assert.False(string.IsNullOrWhiteSpace(modelId), "MISSUM_AI_LIVE_GENERAL_MODEL must select the installed Qwen model.");
        Assert.Contains("qwen", modelId, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("27b", modelId, StringComparison.OrdinalIgnoreCase);
        var serverUrl = Environment.GetEnvironmentVariable("MISSUM_AI_SERVER_URL") ?? "http://127.0.0.1:8080";
        var minutes = int.TryParse(Environment.GetEnvironmentVariable("MISSUM_SCIENCE_SUBAGENT_TIMEOUT_MINUTES"), out var configured)
            ? Math.Clamp(configured, 2, 90) : 30;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(minutes));
        using var observerLifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var startedAt = DateTimeOffset.UtcNow;
        var evidenceRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("MISSUM_SUBAGENT_EVIDENCE_DIRECTORY")
            ?? Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_EVIDENCE_DIRECTORY")
            ?? Path.Combine(Path.GetTempPath(), "Missum-science-subagent-evidence"));
        var attemptDirectory = Path.Combine(evidenceRoot, "science-" + Guid.NewGuid().ToString("N"));
        var workspace = Directory.CreateDirectory(Path.Combine(attemptDirectory, "workspace")).FullName;
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var runs = environment.Get<IMissumAiRunRepository>();
        var documents = environment.Get<IDocumentIngestor>();
        var artifacts = environment.Get<IChatArtifactRepository>();
        var blobs = environment.Get<IBinaryObjectStore>();
        var research = environment.Get<IScientificResearchRepository>();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync(deadline.Token);
        await settings.UpdateAsync(value => value with
        {
            MissumAiServerUrl = serverUrl, IsAutomaticSpeechEnabled = false,
            SelectedModel = modelId, ReasoningEffort = "none",
            ReasoningEffortsByModel = new(StringComparer.OrdinalIgnoreCase)
                { [MissumAiAssistantService.ReasoningKey(modelId!, "general")] = "none" },
        }, deadline.Token);
        var requests = new ConcurrentQueue<AcceptedRequest>();
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance,
            () => new ObservingHandler(requests));
        using var client = await connection.CreateClientAsync(deadline.Token);
        var catalog = await client.GetModelStatusAsync(deadline.Token);
        Assert.True(catalog.ProviderReachable, catalog.ErrorMessage);
        var model = Assert.Single(catalog.Models, item => item.Id == modelId && item.Role == "general" && item.Downloaded && item.SupportsTools);
        Assert.Contains(model.ReasoningEfforts ?? [], effort => effort == "none");
        var profile = AssistantRuntimeProfile.Resolve() with
        {
            DataDirectory = attemptDirectory, NativeStateDirectory = Path.Combine(attemptDirectory, "native-state"),
        };
        // The isolated DB is disposable; the uniquely owned workspace, Python and PDFs remain evidence.
        using var sandbox = new ResearchSandboxService(profile, chats);
        using var exporter = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
        using var publications = new ScientificPublicationService(research,
            (path, token) => exporter.EnsureCurrentAsync(path, sourceChanged: true, scientificPublication: true, cancellationToken: token),
            Path.Combine(attemptDirectory, "publications"), chats, runs, sandbox);
        using var simulations = new ScientificSimulationService(research, sandbox);
        using var presentation = new ScientificPresentationCoordinator(publications, simulations);
        var documentTools = new LocalDocumentToolService(environment.Get<IGeneratedDocumentRepository>(), documents,
            artifacts, blobs, chats, environment.Get<IDocumentFileCodec>(), exporter);
        var actions = ExtensionActionCatalog.CreateWithBuiltIns();
        var broker = new LocalToolBroker(connection, documents, documentTools, chats, actions,
            researchSandbox: sandbox, sciencePresentation: presentation);
        using var microphone = new MicrophoneTranscriptionService(connection, settings, NullLogger<MicrophoneTranscriptionService>.Instance);
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        using var service = new MissumAiAssistantService(connection, chats, environment.Get<IAssistantAttachmentRepository>(),
            artifacts, runs, environment.Get<IClientToolExecutionRepository>(), blobs, documents,
            new DocumentContextPreparationService(documents), new SessionContextPreparationService(chats), broker,
            null!, microphone, settings, recent, NullLogger<MissumAiAssistantService>.Instance,
            extensionActions: actions, scientificResearch: research, researchSandbox: sandbox, sciencePresentation: presentation);
        var coordinator = new AssistantCoordinator(chats, documents, environment.Get<IContextAssembler>(),
            environment.Get<IPromptTriggerRepository>(), environment.Get<IAssistantAttachmentRepository>(), artifacts,
            environment.Get<IConversationSnapshotRepository>(), service, settings, recent, microphone, profile, actions,
            scientificResearch: research, scientificResearchExports: environment.Get<IScientificResearchExportService>());
        var session = await chats.CreateSessionAsync("Gedämpfter Oszillator – parallele numerische Validierung", ChatMode.ClaudeScience, deadline.Token);
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace, deadline.Token);
        await settings.UpdateAsync(value => value with
        {
            ActiveSessionId = session.Id, ActiveClaudeScienceSessionId = session.Id, SelectedChatMode = ChatMode.ClaudeScience,
        }, deadline.Token);
        var projectId = "research-" + session.Id.ToString("N");
        var uiEvents = new ConcurrentQueue<(string Type, JsonElement Payload)>();
        var serverEvents = new ConcurrentQueue<RunEvent>();
        var runtimeSamples = new List<RuntimeSample>();
        var runtimeProbeErrors = new List<string>();
        var nativeUrl = Environment.GetEnvironmentVariable("MISSUM_AI_NATIVE_RUNTIME_URL") ?? $"http://127.0.0.1:{profile.NativePort}";
        using var nativeHandler = new HttpClientHandler { UseProxy = false };
        using var native = new HttpClient(nativeHandler) { BaseAddress = new Uri(nativeUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(10) };
        Task? send = null;
        Task? observer = null;
        AcceptedRequest? accepted = null;
        RunSnapshot? parentRun = null;
        RunSnapshot? childRun = null;
        ChatMessage? parent = null;
        SubagentChatState? child = null;
        object? cacheReuse = null;
        object? prePromptPair = null;
        object? earlyDelegation = null;
        object? delegatedSources = null;
        double? actualError = null;
        Exception? failure = null;
        var passed = false;
        try
        {
            await CommandAsync("session.open", new { sessionId = session.Id });
            // Acceptance starts after the ordinary model-load operation. Both
            // admitted instances must already be resident before chat.send,
            // rather than loading GPU1 lazily after source work has begun.
            using (var resident = JsonDocument.Parse(await native.GetStringAsync("v1/models", deadline.Token)))
            {
                var pair = resident.RootElement.GetProperty("data").EnumerateArray()
                    .Where(item => Text(item, "id") == modelId || Text(item, "id") == modelId + "@subagent")
                    .Select(item => new { id = Text(item, "id"),
                        state = Text(item.GetProperty("status"), "value"),
                        arguments = item.GetProperty("status").GetProperty("args").EnumerateArray()
                            .Select(argument => argument.GetString() ?? "").ToArray() }).ToArray();
                Assert.Equal(2, pair.Length);
                Assert.All(pair, item => Assert.True(item.state is "loaded" or "sleeping", item.id + " must be resident before the prompt."));
                var primary = Assert.Single(pair, item => item.id == modelId);
                var replica = Assert.Single(pair, item => item.id == modelId + "@subagent");
                Assert.Equal("CUDA0", Argument(primary.arguments, "--device"));
                Assert.Equal("CUDA1", Argument(replica.arguments, "--device"));
                Assert.Equal("262144", Argument(primary.arguments, "--ctx-size"));
                Assert.Equal(Argument(primary.arguments, "--ctx-size"), Argument(replica.arguments, "--ctx-size"));
                prePromptPair = new { observedAt = DateTimeOffset.UtcNow, instances = pair };
            }
            var prompt = $$"""
                Untersuche einen frei gedämpften harmonischen Oszillator und organisiere eine parallele Arbeitsteilung für zwei unabhängige Forschungsstränge.
                Du übernimmst die physikalische Quellenrecherche, die nachvollziehbare analytische Herleitung und den Aufbau der wissenschaftlichen Publikation.
                Die separate numerische Validierung soll währenddessen eigenständig bearbeitet werden. Sobald ihr Ergebnis vorliegt, übernimm die abgeschlossene Berechnung und die erzeugten Dateien unmittelbar; führe dieselbe Validierung nicht nochmals aus und prüfe ihre Ergebnisdatei nicht erneut.
                Lies selbst die physikalische Originalquelle {{PhysicsUrl}} und ergänze eine kurze lokale SearXNG-Suche.
                Der separate numerische Forschungsstrang liest parallel die SciPy-Originaldokumentation {{SolverUrl}} und verwendet sie für seine Implementierung. Übernimm diesen Quellenbeleg ebenso direkt aus dem gelieferten Ergebnis, ohne die Seite nochmals selbst zu lesen. Zwei gelesene Quellen genügen.
                Untersuche m*x''+c*x'+k*x=0 mit ausdrücklich gewählten Modellparametern m=1 kg, c=0.4 kg/s, k=4 N/m, x(0)=1 m und x'(0)=0 m/s für 0<=t<=10 s; dies sind keine Messdaten.
                Der numerische Forschungsstrang implementiert scipy.integrate.solve_ivp in einem eigenen neuen Python-Skript, nutzt mindestens 101 Zeitpunkte, rtol=1e-9 und atol=1e-11 und vergleicht die berechnete Lösung mit der analytischen Lösung.
                Schreibe und starte das Skript tatsächlich mit den Forschungswerkzeugen in der isolierten Python-Sandbox. Erzeuge /sandbox/artifacts/damped-oscillator.png mit Zeitverlauf und Fehler sowie /sandbox/artifacts/oscillator-result.json mit Feldern t (Sekunden als Zahlenliste), x (Auslenkung in Metern als Zahlenliste) und max_abs_error (Zahl). Bewahre das ausgeführte Skript als /sandbox/artifacts/damped-oscillator.py auf.
                Die Publikation soll während der numerischen Arbeit bereits wachsen. Erkläre die vollständige analytische Herleitung, Einheiten und Symbolbedeutungen mit LaTeX-Formeln. Binde abschließend die reale Abbildung und den tatsächlich berechneten maximalen Fehler aus dem gelieferten Resultat ein; diskutiere die Modellgrenzen und zitiere beide Quellen.
                Titel: "Gedämpfter Oszillator". Etwa 450 bis 600 Wörter mit den Abschnitten Kurzfassung, Forschungsfrage, Voraussetzungen, Herleitungen, Ergebnisse, Diskussion und Literatur sowie mindestens einer abgesetzten Gleichung.
                Streame das Publikationsmanuskript direkt als AI-Antwort zwischen <!-- MISSUM_PUBLICATION_BEGIN --> und <!-- MISSUM_PUBLICATION_END -->; die Anwendung erzeugt daraus die PDF. research.code.write verwendest du ausschließlich für das numerische Python-Skript, nicht für ein zusätzliches Markdown-Manuskript. Beende erst, wenn Publikation und ausgeführte numerische Darstellung vollständig vorliegen.
                """;
            Assert.DoesNotContain("subagent.spawn", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("subagent.wait", prompt, StringComparison.Ordinal);
            send = CommandAsync("chat.send", new { sessionId = session.Id, prompt, deepResearch = true, deepResearchProfile = "mathematicalInvestigation" });
            while (!send.IsCompleted)
            {
                deadline.Token.ThrowIfCancellationRequested();
                accepted ??= requests.LastOrDefault(item => item.Request.SessionId == session.Id.ToString("D") && item.Request.DeepResearch);
                if (accepted is not null && observer is null) observer = ObserveServerAsync(accepted.RunId);
                var fork = Forwarded(serverEvents).FirstOrDefault(item => item.Event.Type == "subagent.contextForked");
                if (fork is not null && !serverEvents.Any(item => item.Type == RunEventTypes.SubagentCompleted))
                    await ProbeNativeAsync(fork);
                await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(1), deadline.Token));
            }
            await send;
            accepted ??= requests.LastOrDefault(item => item.Request.SessionId == session.Id.ToString("D") && item.Request.DeepResearch);
            Assert.NotNull(accepted);
            observer ??= ObserveServerAsync(accepted.RunId);
            await observer.WaitAsync(deadline.Token);
            await presentation.WaitForIdleAsync(projectId, deadline.Token);
            parentRun = await client.GetRunAsync(accepted.RunId, deadline.Token);
            parent = (await chats.ListMessagesAsync(session.Id, deadline.Token)).Last(item => item.Role == ChatRole.Assistant);
            child = Assert.Single(SubagentChatState.Read([parent], environment.Directory));
            Assert.True(child.ResultDelivered == true, "The successful child result must be durably delivered before showing its completion in the main chat.");
            childRun = await client.GetRunAsync(child.RunId, deadline.Token);
            var shown = presentation.GetSnapshot(projectId);
            Assert.NotNull(shown);
            earlyDelegation = ValidateEarlyDelegation(serverEvents.ToArray(), child.Title);
            Assert.Contains(child.AssistantMessage.ToolSteps ?? [], step => step.Tool == "web.fetch" && step.Status == "completed"
                && Text(Parse(step.OutputJson), "url").TrimEnd('/') == SolverUrl.TrimEnd('/')
                && Parse(step.OutputJson).TryGetProperty("found", out var found) && found.ValueKind == JsonValueKind.True);
            Assert.DoesNotContain(parent.ToolSteps ?? [], step => step.Tool == "web.fetch"
                && Text(Parse(step.InputJson), "url").TrimEnd('/') == SolverUrl.TrimEnd('/'));
            var sourceArchive = await research.LoadArchiveSnapshotAsync(projectId, deadline.Token);
            Assert.NotNull(sourceArchive.Report);
            Assert.Equal(accepted.RunId, Text(Parse(sourceArchive.Report.ManifestJson), "runId"));
            foreach (var url in PrimaryUrls)
                Assert.Contains(sourceArchive.Works, work => work.CanonicalUrl.TrimEnd('/') == url.TrimEnd('/'));
            delegatedSources = new { parentRunId = accepted.RunId, childRunId = child.RunId,
                parentUrl = PhysicsUrl, childUrl = SolverUrl, durableWorks = sourceArchive.Works,
                durableEvidence = sourceArchive.Evidence, reportManifest = Parse(sourceArchive.Report.ManifestJson) };
            var proof = await ValidateAcceptanceAsync(new(startedAt, modelId!, session.Id, projectId, workspace,
                accepted, parentRun, childRun, parent, child, shown, runtimeSamples.ToArray(), serverEvents.ToArray()), deadline.Token);
            cacheReuse = proof.CacheReuse;
            actualError = proof.ActualError;
            Assert.NotNull(shown.Publication);
            File.Copy(shown.Publication.PdfPath, Path.Combine(attemptDirectory, "Publikation.pdf"), overwrite: true);
            File.Copy(shown.Publication.MarkdownPath, Path.Combine(attemptDirectory, "Publikation.md"), overwrite: true);
            passed = true;
        }
        catch (Exception exception) { failure = exception; throw; }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            accepted ??= requests.LastOrDefault(item => item.Request.SessionId == session.Id.ToString("D") && item.Request.DeepResearch);
            if (accepted is not null)
            {
                try
                {
                    var current = await client.GetRunAsync(accepted.RunId, cleanup.Token);
                    if (!IsTerminal(current.State)) await client.CancelRunAsync(accepted.RunId, cleanup.Token);
                    if (service.IsRunning && service.ActiveSessionId == session.Id) await service.CancelCurrentAsync(cleanup.Token);
                    if (send is not null) await send.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception exception) { output.WriteLine("Owned Science run cleanup: " + exception.Message); }
            }
            foreach (var childId in serverEvents.Where(item => item.Type == RunEventTypes.SubagentStarted)
                .Select(item => Text(item.Data, "runId")).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal))
            {
                try
                {
                    var current = await client.GetRunAsync(childId, cleanup.Token);
                    if (!IsTerminal(current.State)) await client.CancelRunAsync(childId, cleanup.Token);
                }
                catch (Exception exception) { output.WriteLine("Owned Science child cleanup: " + exception.Message); }
            }
            await observerLifetime.CancelAsync();
            if (observer is not null)
            {
                try { await observer.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception exception) { output.WriteLine("Science event observer ended: " + exception.Message); }
            }
            try
            {
                using var evidenceDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                var messages = await chats.ListMessagesAsync(session.Id, evidenceDeadline.Token);
                parent ??= messages.LastOrDefault(item => item.Role == ChatRole.Assistant);
                var recordedChildren = SubagentChatState.Read(messages, environment.Directory);
                if (child is null && recordedChildren.Count > 0) child = recordedChildren[0];
                var shown = presentation.GetSnapshot(projectId);
                var report = new
                {
                    passed, startedAt, completedAt = DateTimeOffset.UtcNow, modelId, serverUrl, nativeUrl,
                    sessionId = session.Id, projectId, workspace, error = failure?.ToString(),
                    accepted, parentRun, childRun, parent, child, cacheReuse, actualError, shown,
                    prePromptPair, earlyDelegation, delegatedSources, runtimeSamples, runtimeProbeErrors, serverEvents,
                    uiEventCounts = uiEvents.GroupBy(item => item.Type).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                };
                var reportText = JsonSerializer.Serialize(report, Json);
                await File.WriteAllTextAsync(Path.Combine(attemptDirectory, "claude-science-subagent-report.json"), reportText, evidenceDeadline.Token);
                await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "claude-science-subagent-latest-report.json"), reportText, evidenceDeadline.Token);
                if (child is not null)
                {
                    var childText = JsonSerializer.Serialize(ToNativeChild(child), Json);
                    await File.WriteAllTextAsync(Path.Combine(attemptDirectory, "native-subagent-live-input.json"), childText, evidenceDeadline.Token);
                    await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "native-subagent-live-input.json"), childText, evidenceDeadline.Token);
                }
                if (parent is not null)
                    await File.WriteAllTextAsync(Path.Combine(attemptDirectory, "native-main-input.json"), JsonSerializer.Serialize(new
                    {
                        activeSessionId = session.Id, chatMode = "claudescience", isRunning = false, model = modelId,
                        reasoningModelId = modelId, runMessageId = parent.Id, messages = messages.Select(ToNativeMessage),
                        subagents = child is null ? Array.Empty<object>() : new[] { ToNativeChild(child) },
                    }, Json), evidenceDeadline.Token);
                output.WriteLine("Science subagent evidence: " + attemptDirectory);
            }
            catch (Exception exception) when (failure is not null) { output.WriteLine("Science failure evidence: " + exception.Message); }
        }

        Task CommandAsync(string type, object payload) => coordinator.HandleAsync(new(AssistantWebBridge.ProtocolVersion,
            type, Guid.NewGuid().ToString("N"), JsonSerializer.SerializeToElement(payload, Json)), (eventType, data, _) =>
        {
            var value = JsonSerializer.SerializeToElement(data, Json);
            uiEvents.Enqueue((eventType, value));
            if (eventType == "host.error") throw new InvalidOperationException(Text(value, "message"));
            return Task.CompletedTask;
        }, deadline.Token);

        async Task ObserveServerAsync(string runId)
        {
            await foreach (var item in client.StreamRunEventsAsync(runId, 0, observerLifetime.Token))
            {
                serverEvents.Enqueue(item);
                if (item.Type is RunEventTypes.RunCompleted or RunEventTypes.RunFailed or RunEventTypes.RunCancelled) break;
            }
        }

        async Task ProbeNativeAsync(SubagentForwardedEvent fork)
        {
            try
            {
                var instanceId = Text(fork.Event.Data, "instanceId");
                using var modelsResponse = await native.GetAsync("v1/models", deadline.Token);
                modelsResponse.EnsureSuccessStatusCode();
                using var models = JsonDocument.Parse(await modelsResponse.Content.ReadAsStringAsync(deadline.Token));
                var childModel = Items(models.RootElement, "data").FirstOrDefault(item => Text(item, "id") == instanceId);
                var parentModel = Items(models.RootElement, "data").FirstOrDefault(item => Text(item, "id") == modelId);
                if (childModel.ValueKind != JsonValueKind.Object || parentModel.ValueKind != JsonValueKind.Object) return;
                using var childSlotsResponse = await native.GetAsync("slots?model=" + Uri.EscapeDataString(instanceId), deadline.Token);
                childSlotsResponse.EnsureSuccessStatusCode();
                using var childSlots = JsonDocument.Parse(await childSlotsResponse.Content.ReadAsStringAsync(deadline.Token));
                using var parentSlotsResponse = await native.GetAsync("slots?model=" + Uri.EscapeDataString(modelId!), deadline.Token);
                parentSlotsResponse.EnsureSuccessStatusCode();
                using var parentSlots = JsonDocument.Parse(await parentSlotsResponse.Content.ReadAsStringAsync(deadline.Token));
                runtimeSamples.Add(new(DateTimeOffset.UtcNow, fork.RunId, instanceId, Processing(childSlots.RootElement), Processing(parentSlots.RootElement),
                    Arguments(childModel), Arguments(parentModel)));
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
            { runtimeProbeErrors.Add(DateTimeOffset.UtcNow.ToString("O") + " " + exception.Message); }
        }
    }

    [Fact]
    [Trait("Category", "RecordedAcceptance")]
    public async Task RecordedScienceRunPassesTheSameCompleteAcceptanceWithoutNewInference()
    {
        var configured = Environment.GetEnvironmentVariable("MISSUM_SCIENCE_SUBAGENT_RECORDED_REPORT");
        if (string.IsNullOrWhiteSpace(configured)) return;
        var reportPath = Path.GetFullPath(configured);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var original = await File.ReadAllBytesAsync(reportPath, deadline.Token);
        var originalHash = Hash(original);
        using var document = JsonDocument.Parse(original);
        var evidence = JsonSerializer.Deserialize<AcceptanceInput>(original, Json);
        Assert.NotNull(evidence);
        AcceptanceProof? proof = null;
        EarlyDelegationProof? earlyProof = null;
        Exception? failure = null;
        try
        {
            if (document.RootElement.TryGetProperty("prePromptPair", out var pair) && pair.ValueKind == JsonValueKind.Object)
            {
                Assert.True(pair.GetProperty("observedAt").GetDateTimeOffset() <= evidence.ParentRun.CreatedAt);
                var instances = Items(pair, "instances");
                Assert.Equal(2, instances.Length);
                Assert.All(instances, instance => Assert.True(Text(instance, "state") is "loaded" or "sleeping"));
                Assert.Contains(instances, instance => Text(instance, "id") == evidence.ModelId);
                Assert.Contains(instances, instance => Text(instance, "id") == evidence.ModelId + "@subagent");
                earlyProof = ValidateEarlyDelegation(evidence.ServerEvents, evidence.Child.Title);
            }
            proof = await ValidateAcceptanceAsync(evidence, deadline.Token);
            output.WriteLine($"Recorded real Science run accepted: {evidence.ParentRun.RunId}, child {evidence.Child.RunId}, error={proof.ActualError:R}, {proof.CacheReuse.OverlapSamples} parallel GPU samples.");
        }
        catch (Exception exception) { failure = exception; throw; }
        finally
        {
            var currentHash = Hash(await File.ReadAllBytesAsync(reportPath, CancellationToken.None));
            Assert.Equal(originalHash, currentHash);
            var resultPath = Path.Combine(Path.GetDirectoryName(reportPath)!, "acceptance-result.json");
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(new
            {
                passed = proof is not null && failure is null, validatedAt = DateTimeOffset.UtcNow,
                sourceReport = reportPath, sourceReportSha256 = originalHash, originalReportPreserved = true,
                originalPassed = document.RootElement.GetProperty("passed").GetBoolean(),
                originalFailure = Text(document.RootElement, "error"), error = failure?.ToString(), earlyProof, proof,
            }, Json), CancellationToken.None);
            output.WriteLine("Recorded acceptance evidence: " + resultPath);
        }
    }

    private static EarlyDelegationProof ValidateEarlyDelegation(RunEvent[] events, string title)
    {
        var ordered = events.OrderBy(item => item.Id).ToArray();
        var firstTool = ordered.First(item => item.Type == RunEventTypes.ServerToolStarted);
        Assert.Equal("subagent.spawn", Text(firstTool.Data, "tool"));
        var assigned = Assert.Single(ordered, item => item.Type == RunEventTypes.SubagentStarted);
        var firstSource = ordered.First(item => item.Type == RunEventTypes.ServerToolStarted
            && Text(item.Data, "tool") is "web.search" or "web.fetch" or "web.deepResearch");
        Assert.True(assigned.Id < firstSource.Id, "The primary must assign the child before its first source action.");
        var enrolled = ordered.First(item => item.Type == RunEventTypes.ResearchProblemInterpreted);
        Assert.True(enrolled.Id < firstSource.Id, "Early delegation must still initialize durable research progress before source receipts arrive.");
        Assert.DoesNotContain(ordered, item => item.Type == RunEventTypes.ResearchPlanUpdated);
        return new(firstTool.Id, assigned.Id, firstSource.Id, Text(firstSource.Data, "tool"), title, true, enrolled.Id);
    }

    private sealed record EarlyDelegationProof(long FirstToolEventId, long AssignmentEventId,
        long FirstSourceEventId, string FirstSourceTool, string Title, bool AssignedBeforeSources, long ResearchEnrollmentEventId);

    private static async Task<AcceptanceProof> ValidateAcceptanceAsync(AcceptanceInput evidence, CancellationToken token)
    {
        Assert.NotNull(evidence.Accepted);
        Assert.NotNull(evidence.ParentRun);
        Assert.NotNull(evidence.ChildRun);
        Assert.NotNull(evidence.Parent);
        Assert.NotNull(evidence.Child);
        Assert.NotNull(evidence.Shown);
        Assert.NotNull(evidence.RuntimeSamples);
        Assert.NotNull(evidence.ServerEvents);
        Assert.Contains("qwen", evidence.ModelId, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("27b", evidence.ModelId, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("research-" + evidence.SessionId.ToString("N"), evidence.ProjectId);
        var accepted = evidence.Accepted;
        var parent = evidence.Parent;
        var child = evidence.Child;
        var shown = evidence.Shown;
        var events = evidence.ServerEvents;
        Assert.True(parent.Status == MessageStatus.Completed, $"Science ended {parent.Status}: {parent.Error}");
        Assert.Equal(evidence.SessionId, parent.SessionId);
        Assert.Equal(RunState.Completed, evidence.ParentRun.State);
        Assert.Equal(accepted.RunId, evidence.ParentRun.RunId);
        Assert.Equal(evidence.ModelId, evidence.ParentRun.SelectedModel);
        Assert.Equal(RunMode.General, accepted.Request.Mode);
        Assert.Equal(evidence.ModelId, accepted.Request.PreferredGeneralModelId);
        Assert.Null(accepted.Request.PreferredCodingModelId);
        Assert.Null(accepted.Request.CodingOptions);
        Assert.Equal("none", accepted.Request.ReasoningEffort);
        Assert.Equal(evidence.SessionId.ToString("D"), accepted.Request.SessionId);
        Assert.True(accepted.Request.DeepResearch);
        Assert.Equal(evidence.Workspace, accepted.Request.WorkspacePath);
        Assert.Equal(evidence.ProjectId, accepted.Request.ResearchOptions?.ProjectId);
        Assert.Equal(DeepResearchProfile.MathematicalInvestigation, accepted.Request.ResearchOptions?.Profile);
        Assert.Equal(ResearchAutonomyLevel.SandboxResearch, accepted.Request.ResearchOptions?.AutonomyLevel);
        Assert.Equal(ResearchVerificationLevel.MultiPath, accepted.Request.ResearchOptions?.VerificationLevel);
        Assert.Contains("subagents", accepted.Request.ClientCapabilities ?? []);
        Assert.Contains("research.sandbox", accepted.Request.ClientCapabilities ?? []);
        Assert.Contains("research.deliverables", accepted.Request.ClientCapabilities ?? []);
        Assert.Contains("coding", accepted.Request.ClientCapabilities ?? []);
        foreach (var part in accepted.Request.Messages.Where(message => message.Role == "user").SelectMany(message => message.Content))
        {
            Assert.DoesNotContain("subagent.spawn", part.Text ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain("subagent.wait", part.Text ?? "", StringComparison.Ordinal);
        }
        Assert.Equal(accepted.RunId, child.ParentRunId);
        Assert.Equal(evidence.SessionId, child.SessionId);
        Assert.Equal("completed", child.Status);
        Assert.True(child.ResultDelivered is not false);
        Assert.False(child.IsRunning);
        Assert.Equal(MessageStatus.Completed, child.AssistantMessage.Status);
        Assert.Equal(evidence.ModelId, child.Model);
        Assert.Equal(RunState.Completed, evidence.ChildRun.State);
        Assert.Equal(child.RunId, evidence.ChildRun.RunId);
        Assert.Equal(evidence.ModelId, evidence.ChildRun.SelectedModel);
        var persisted = Assert.Single(SubagentChatState.Read([parent]));
        Assert.Equal(child.AgentId, persisted.AgentId);
        Assert.Equal(child.RunId, persisted.RunId);
        Assert.Equal(child.AssistantMessage.Content, persisted.AssistantMessage.Content);
        Assert.Equal(child.AssistantMessage.ToolSteps, persisted.AssistantMessage.ToolSteps);
        var parentSteps = parent.ToolSteps ?? [];
        var childSteps = child.AssistantMessage.ToolSteps ?? [];
        Assert.Contains(childSteps, step => step.Tool == ClientToolNames.ResearchCodeWrite && step.Status == "completed");
        var started = Assert.Single(events, item => item.Type == RunEventTypes.SubagentStarted);
        Assert.Equal(child.RunId, Text(started.Data, "runId"));
        Assert.Equal(child.AgentId, Text(started.Data, "agentId"));
        Assert.Equal(accepted.RunId, Text(started.Data, "parentRunId"));
        var completed = Assert.Single(events, item => item.Type == RunEventTypes.SubagentCompleted);
        Assert.Equal(child.RunId, Text(completed.Data, "runId"));
        var consumed = Assert.Single(events, item => item.Type == "subagent.resultConsumed");
        Assert.Equal(child.RunId, Text(consumed.Data, "runId"));
        Assert.Equal(child.AgentId, Text(consumed.Data, "agentId"));
        Assert.Equal("completed", Text(consumed.Data, "state"));
        Assert.Contains(events, item => item.Type == RunEventTypes.ServerToolStarted && Text(item.Data, "tool") == "subagent.spawn");
        Assert.Contains(events, item => item.Type == RunEventTypes.ServerToolStarted && Text(item.Data, "tool") == "subagent.wait");
        var delivered = Assert.Single(events, item => item.Type == RunEventTypes.ServerToolCompleted && Text(item.Data, "tool") == "subagent.wait");
        Assert.Equal(child.AssistantMessage.Content, delivered.Data.GetProperty("result").GetProperty("result").GetString());
        Assert.True(consumed.CreatedAt >= completed.CreatedAt);
        Assert.True(evidence.ParentRun.UpdatedAt >= consumed.CreatedAt);
        Assert.DoesNotContain(events, item => item.Type is RunEventTypes.ModelFallback or RunEventTypes.ProviderFallback);
        var childEvents = Forwarded(events).Where(item => item.AgentId == child.AgentId).Select(item => item.Event).ToArray();
        Assert.DoesNotContain(childEvents, item => item.Type is RunEventTypes.ModelFallback or RunEventTypes.ProviderFallback);
        Assert.Contains(childEvents, item => item.Type == RunEventTypes.RunCompleted && item.RunId == child.RunId);
        var forkEvent = Assert.Single(childEvents, item => item.Type == "subagent.contextForked");
        Assert.Equal(1, forkEvent.Data.GetProperty("gpuIndex").GetInt32());
        Assert.Equal("forked", Text(forkEvent.Data, "cacheStatus"));
        var forkedTokens = forkEvent.Data.GetProperty("cachedTokens").GetInt32();
        var sourceCachedTokens = forkEvent.Data.GetProperty("sourceCachedTokens").GetInt32();
        Assert.True(forkedTokens > 0);
        Assert.True(sourceCachedTokens > 0);
        Assert.Equal(1, forkEvent.Data.GetProperty("preparationSampledTokens").GetInt32());
        Assert.Equal(0, forkEvent.Data.GetProperty("evaluatedGeneratedTokens").GetInt32());
        Assert.NotEqual(Text(forkEvent.Data, "parentCacheKey"), Text(forkEvent.Data, "childCacheKey"));
        var firstAction = childEvents.First(item => item.Type is RunEventTypes.ClientToolProposed or RunEventTypes.ServerToolStarted);
        var firstGeneration = childEvents.Where(item => item.Type == RunEventTypes.ModelGeneration && item.Id < firstAction.Id)
            .Select(item => item.Data.Deserialize<ModelGenerationEvent>(ProtocolJson)!).Last(item => item.State == "tokenProgress");
        Assert.True(firstGeneration.GeneratedTokens is > 0);
        Assert.True(firstGeneration.CachedPromptTokens is > 0, "Restoring a slot does not prove reuse by the child's first inference.");
        Assert.True(firstGeneration.CachedPromptTokens > forkedTokens / 2, "The first inference must reuse a substantial part of its forked prefix.");
        var instanceId = Text(forkEvent.Data, "instanceId");
        Assert.Contains(evidence.RuntimeSamples, item => item.InstanceId == instanceId && item.RunId == child.RunId && item.ChildProcessing
            && Argument(item.ChildArguments, "--split-mode") == "none"
            && Argument(item.ChildArguments, "--device") is { Length: > 0 } device
            && device != Argument(item.ParentArguments, "--device"));
        var parallel = evidence.RuntimeSamples.Where(item => item.InstanceId == instanceId && item.RunId == child.RunId
            && item.ChildProcessing && item.ParentProcessing).ToArray();
        Assert.NotEmpty(parallel);
        Assert.All(parallel, item =>
        {
            Assert.Equal("CUDA0", Argument(item.ParentArguments, "--device"));
            Assert.Equal("CUDA1", Argument(item.ChildArguments, "--device"));
            Assert.Equal("none", Argument(item.ParentArguments, "--split-mode"));
            Assert.Equal("none", Argument(item.ChildArguments, "--split-mode"));
            Assert.Equal("262144", Argument(item.ParentArguments, "--ctx-size"));
            Assert.Equal("262144", Argument(item.ChildArguments, "--ctx-size"));
            Assert.Equal(Argument(item.ParentArguments, "--model"), Argument(item.ChildArguments, "--model"));
            Assert.InRange(item.ObservedAt, started.CreatedAt, completed.CreatedAt);
        });
        foreach (var url in PrimaryUrls)
            Assert.Contains(parentSteps.Concat(childSteps), step => step.Tool == "web.fetch" && step.Status == "completed"
                && Text(Parse(step.OutputJson), "url").TrimEnd('/') == url.TrimEnd('/')
                && Parse(step.OutputJson).TryGetProperty("found", out var found) && found.ValueKind == JsonValueKind.True);
        var searches = parentSteps.Where(step => step.Tool == "web.search" && step.Status == "completed").ToArray();
        Assert.NotEmpty(searches);
        Assert.All(searches, step =>
        {
            Assert.Equal("searxng", Text(Parse(step.OutputJson), "provider"));
            Assert.False(Parse(step.OutputJson).GetProperty("isFallback").GetBoolean());
        });
        Assert.Null(shown.PublicationError);
        Assert.Null(shown.SimulationError);
        Assert.NotNull(shown.Publication);
        Assert.NotNull(shown.Simulation);
        Assert.Equal(evidence.ProjectId, shown.Publication.ProjectId);
        Assert.Equal(evidence.ProjectId, shown.Simulation.ProjectId);
        var figure = Assert.Single(shown.Simulation.Artifacts, item => Path.GetFileName(item.ImagePath) == "damped-oscillator.png" && item.IsResearchData);
        Assert.NotNull(figure.Execution);
        var trace = figure.Execution;
        var projectRoot = Path.GetFullPath(Path.Combine(evidence.Workspace, "Science", evidence.ProjectId));
        Assert.Equal(projectRoot, Path.GetFullPath(trace.ProjectRoot));
        var scriptPath = ScientificSimulationService.SafePath(projectRoot, trace.ExecutedScriptPath);
        var frozenScriptPath = ScientificSimulationService.SafePath(projectRoot, "snapshots/" + trace.SnapshotId + "/frozen/" + trace.ExecutedScriptPath);
        var scriptBytes = await File.ReadAllBytesAsync(scriptPath, token);
        var scriptHash = Hash(scriptBytes);
        Assert.Equal(trace.ScriptSha256, scriptHash);
        Assert.Equal(scriptHash, Hash(await File.ReadAllBytesAsync(frozenScriptPath, token)));
        Assert.Contains("solve_ivp", System.Text.Encoding.UTF8.GetString(scriptBytes), StringComparison.Ordinal);
        var executed = childSteps.Last(step => step.Tool == ClientToolNames.ResearchCodeExecute && step.Status == "completed"
            && Text(Parse(step.OutputJson), "verificationStatus") == "ProcessSucceeded"
            && Items(Parse(step.OutputJson), "runs").Any(run => Text(run, "runId") == trace.RunId));
        var receipt = Parse(executed.OutputJson);
        Assert.True(receipt.GetProperty("success").GetBoolean());
        Assert.Equal(evidence.ProjectId, Text(receipt, "projectId"));
        Assert.Equal(trace.ExperimentRecordId, Text(receipt, "experimentRecordId"));
        Assert.Equal("Docker network none", Text(receipt, "networkIsolation"));
        Assert.All(Items(receipt, "runs"), run =>
        {
            Assert.Equal(0, run.GetProperty("exitCode").GetInt32());
            Assert.False(run.GetProperty("timedOut").GetBoolean());
            Assert.True(run.GetProperty("startedAt").GetDateTimeOffset() >= evidence.StartedAt);
        });
        var numericRun = Assert.Single(Items(receipt, "runs"), run => Text(run, "runId") == trace.RunId);
        Assert.Equal(trace.ExecutedScriptPath, Text(numericRun, "executedScriptPath"));
        Assert.Equal(scriptHash, Text(numericRun, "scriptSha256"));
        Assert.Equal(trace.SnapshotId, Text(numericRun, "snapshotId"));
        Assert.Equal(trace.StartedAt, numericRun.GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal(trace.CompletedAt, numericRun.GetProperty("completedAt").GetDateTimeOffset());
        AssertHashes(trace.InputHashes, numericRun.GetProperty("inputHashes"));
        AssertHashes(trace.OutputHashes, numericRun.GetProperty("outputHashes"));
        // Analytical/symbolic derivation is the main agent's independent job.
        // Forbid repeating the child's numerical solver or touching its outputs,
        // rather than forbidding every legitimate Python calculation on GPU0.
        foreach (var step in parentSteps.Where(step => step.Status == "completed"
            && ScientificProcessTools.Contains(step.Tool, StringComparer.Ordinal)))
        {
            foreach (var execution in Items(Parse(step.OutputJson), "runs"))
            {
                var relative = Text(execution, "executedScriptPath");
                Assert.False(string.IsNullOrWhiteSpace(relative), "Main-agent Python work must retain its measured script provenance.");
                Assert.NotEqual(trace.ExecutedScriptPath, relative);
                var independentSource = await File.ReadAllTextAsync(ScientificSimulationService.SafePath(projectRoot, relative), token);
                Assert.DoesNotContain("solve_ivp", independentSource, StringComparison.Ordinal);
                Assert.DoesNotContain("oscillator-result.json", independentSource, StringComparison.Ordinal);
                var changedOutputs = execution.GetProperty("outputHashes");
                Assert.False(changedOutputs.TryGetProperty("artifacts/damped-oscillator.png", out _));
                Assert.False(changedOutputs.TryGetProperty("artifacts/oscillator-result.json", out _));
            }
        }
        Assert.DoesNotContain(parentSteps, step => step.Tool == ClientToolNames.CodingRead
            && Path.GetFileName(Text(Parse(step.InputJson), "path")) == "oscillator-result.json");
        var numericPath = ScientificSimulationService.SafePath(projectRoot, "artifacts/oscillator-result.json");
        Assert.Equal(trace.OutputHashes["artifacts/oscillator-result.json"], Hash(await File.ReadAllBytesAsync(numericPath, token)));
        var numeric = Parse(await File.ReadAllTextAsync(numericPath, token));
        var times = numeric.GetProperty("t").EnumerateArray().Select(item => item.GetDouble()).ToArray();
        var values = numeric.GetProperty("x").EnumerateArray().Select(item => item.GetDouble()).ToArray();
        Assert.InRange(times.Length, 101, 100_000);
        Assert.Equal(times.Length, values.Length);
        Assert.Equal(0d, times[0], 9);
        Assert.Equal(10d, times[^1], 9);
        Assert.All(values, value => Assert.True(double.IsFinite(value)));
        var frequency = Math.Sqrt(3.96);
        var actualError = times.Select((time, index) => Math.Abs(values[index] - Math.Exp(-0.2 * time)
            * (Math.Cos(frequency * time) + 0.2 / frequency * Math.Sin(frequency * time)))).Max();
        Assert.InRange(actualError, 0d, 1e-6);
        Assert.InRange(Math.Abs(numeric.GetProperty("max_abs_error").GetDouble() - actualError), 0d, 1e-8);
        var imageBytes = await File.ReadAllBytesAsync(figure.ImagePath, token);
        Assert.True(imageBytes.Length > 1024);
        Assert.True(imageBytes.AsSpan(0, PngHeader.Length).SequenceEqual(PngHeader));
        Assert.Equal(figure.Sha256, Hash(imageBytes));
        Assert.Equal(trace.OutputHashes["artifacts/damped-oscillator.png"], figure.Sha256);
        var manuscript = await File.ReadAllTextAsync(shown.Publication.MarkdownPath, token);
        Assert.Contains("Oszillator", manuscript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("$$", manuscript, StringComparison.Ordinal);
        var pdfBytes = await File.ReadAllBytesAsync(shown.Publication.PdfPath, token);
        using var pdf = PdfDocument.Open(pdfBytes);
        Assert.True(pdf.NumberOfPages > 0);
        Assert.Contains("Oszillator", string.Join('\n', pdf.GetPages().Select(page => page.Text)), StringComparison.OrdinalIgnoreCase);
        var verified = await ScientificDeliverablesVerifier.VerifyAsync(evidence.ProjectId, shown, token);
        Assert.True(verified.GetProperty("success").GetBoolean(), verified.GetRawText());
        var verifiedFigure = Assert.Single(Items(verified.GetProperty("simulation"), "artifacts"), item => Text(item, "runId") == trace.RunId);
        Assert.Equal(scriptHash, Text(verifiedFigure, "scriptSha256"));
        AssertHashes(trace.InputHashes, verifiedFigure.GetProperty("inputHashes"));
        AssertHashes(trace.OutputHashes, verifiedFigure.GetProperty("outputHashes"));
        return new(actualError, new(forkedTokens, sourceCachedTokens, firstGeneration.CachedPromptTokens!.Value,
            firstGeneration.PromptTokens, firstGeneration.GeneratedTokens!.Value, parallel.Length), trace,
            scriptPath, frozenScriptPath, scriptHash, figure.ImagePath, imageBytes.Length, Hash(imageBytes),
            numericPath, shown.Publication.PdfPath, pdfBytes.Length, pdf.NumberOfPages, Hash(pdfBytes), verified);
    }

    private static void AssertHashes(IReadOnlyDictionary<string, string> expected, JsonElement actual)
    {
        Assert.Equal(expected.Count, actual.EnumerateObject().Count());
        foreach (var pair in expected) Assert.Equal(pair.Value, actual.GetProperty(pair.Key).GetString());
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
    private sealed record AcceptanceInput(DateTimeOffset StartedAt, string ModelId, Guid SessionId, string ProjectId, string Workspace,
        AcceptedRequest Accepted, RunSnapshot ParentRun, RunSnapshot ChildRun, ChatMessage Parent, SubagentChatState Child,
        ScientificPresentationSnapshot Shown, RuntimeSample[] RuntimeSamples, RunEvent[] ServerEvents);
    private sealed record AcceptanceProof(double ActualError, CacheReuseProof CacheReuse, ScientificExecutionEvidence Execution,
        string ScriptPath, string FrozenScriptPath, string ScriptSha256, string ImagePath, int ImageBytes, string ImageSha256,
        string NumericResultPath, string PdfPath, int PdfBytes, int PdfPages, string PdfSha256, JsonElement CurrentDeliverables);
    private sealed record CacheReuseProof(int ForkedTokens, int SourceCachedTokens, int CachedPromptTokens,
        int? PromptTokens, int GeneratedTokens, int OverlapSamples);

    private static IEnumerable<SubagentForwardedEvent> Forwarded(IEnumerable<RunEvent> events) => events
        .Where(item => item.Type == RunEventTypes.SubagentEvent).Select(item => item.Data.Deserialize<SubagentForwardedEvent>(ProtocolJson)!);
    private static bool IsTerminal(RunState state) => state is RunState.Completed or RunState.Failed or RunState.Cancelled or RunState.Interrupted;
    private static bool Processing(JsonElement slots) => slots.ValueKind == JsonValueKind.Array && slots.EnumerateArray()
        .Any(item => item.TryGetProperty("is_processing", out var active) && active.ValueKind == JsonValueKind.True);
    private static string[] Arguments(JsonElement model) => Items(model.GetProperty("status"), "args").Select(item => item.GetString() ?? "").ToArray();
    private static string? Argument(string[] arguments, string name)
    {
        var index = Array.IndexOf(arguments, name);
        return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
    }
    private static JsonElement Parse(string? json) => string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<JsonElement>(json);
    private static string Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? property.ToString() : "";
    private static JsonElement[] Items(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array ? property.EnumerateArray().ToArray() : [];
    private static object ToNativeChild(SubagentChatState child) => new
    {
        chatMode = "claudescience",
        child.AgentId, child.RunId, child.ParentRunId, child.SessionId, child.Title, child.Status, child.IsRunning, child.ResultDelivered,
        child.Model, child.ContextUsed, child.ContextLimit, child.RunStatus, child.RunDetail, child.GenerationState,
        child.GeneratedTokens, child.GenerationUpdatedAt, runMessageId = child.AssistantMessage.Id,
        messages = new[] { ToNativeMessage(child.UserMessage), ToNativeMessage(child.AssistantMessage) },
    };
    private static object ToNativeMessage(ChatMessage message) => new
    {
        message.Id, message.SessionId, role = message.Role.ToString().ToLowerInvariant(), message.Content,
        status = message.Status.ToString().ToLowerInvariant(), message.CreatedAt, message.UpdatedAt, message.Error,
        message.Revision, toolSteps = message.ToolSteps ?? [], artifacts = Array.Empty<object>(),
    };
    private sealed record RuntimeSample(DateTimeOffset ObservedAt, string RunId, string InstanceId, bool ChildProcessing,
        bool ParentProcessing, string[] ChildArguments, string[] ParentArguments);
    private sealed record AcceptedRequest(string RunId, RunRequest Request);
    private sealed class ObservingHandler(ConcurrentQueue<AcceptedRequest> requests) : DelegatingHandler(new HttpClientHandler { UseProxy = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var run = request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/runs"
                ? JsonSerializer.Deserialize<RunRequest>(await request.Content!.ReadAsStringAsync(cancellationToken), ProtocolJson) : null;
            var response = await base.SendAsync(request, cancellationToken);
            if (run is not null && response.IsSuccessStatusCode)
            {
                var accepted = JsonSerializer.Deserialize<RunAccepted>(await response.Content.ReadAsStringAsync(cancellationToken), ProtocolJson);
                if (accepted is not null) requests.Enqueue(new(accepted.RunId, run));
            }
            return response;
        }
    }
}
