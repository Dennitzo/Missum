using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Extensions;
using Missum.Core.Models;
using Missum.Core.Research;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Missum.Tests;

/// <summary>
/// Opt-in acceptance through the native page's real coordinator protocol. No UI,
/// inference fixture, synthetic sources, or changes to the user's chat database.
/// </summary>
public sealed class ClaudeScienceLiveTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions ProtocolJson = MissumAiProtocol.CreateJsonOptions();

    [Fact]
    [Trait("Category", "Live")]
    public async Task ClaudeScienceResearchesSqliteWithThreeFetchedPrimarySourcesAndRestoresItsPersistedResult()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_AI_SCIENCE_LIVE") != "1") return;
        var modelId = Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_GENERAL_MODEL")?.Trim();
        Assert.False(string.IsNullOrWhiteSpace(modelId), "MISSUM_AI_LIVE_GENERAL_MODEL must name an installed general model.");
        PortableToolAcceptance.AssertDeepSeekReasoningOff(modelId!);
        var serverUrl = Environment.GetEnvironmentVariable("MISSUM_AI_SERVER_URL") ?? "http://127.0.0.1:8080";
        var timeoutMinutes = int.TryParse(Environment.GetEnvironmentVariable("MISSUM_AI_SCIENCE_LIVE_TIMEOUT_MINUTES"), out var requestedMinutes)
            ? Math.Clamp(requestedMinutes, 2, 60) : 30;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
        var startedAt = DateTimeOffset.UtcNow;
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var documents = environment.Get<IDocumentIngestor>();
        var artifacts = environment.Get<IChatArtifactRepository>();
        var blobs = environment.Get<IBinaryObjectStore>();
        var research = environment.Get<IScientificResearchRepository>();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(value => value with
        {
            MissumAiServerUrl = serverUrl,
            IsAutomaticSpeechEnabled = false,
            SelectedModel = modelId,
            SelectedCodingModel = null,
            ReasoningEffort = PortableToolAcceptance.ReasoningEffort(),
            ReasoningEffortsByModel = PortableToolAcceptance.ReasoningSelections((modelId!, "general")),
        });
        var requests = new ConcurrentQueue<AcceptedRequest>();
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance,
            () => new ObservingHandler(requests));
        using var client = await connection.CreateClientAsync(timeout.Token);
        var modelStatus = await client.GetModelStatusAsync(timeout.Token);
        Assert.Contains(modelStatus.Models, model => model.Id == modelId && model.Role == "general" && model.Downloaded);
        var profile = AssistantRuntimeProfile.Resolve() with
        {
            DataDirectory = environment.Directory,
            NativeStateDirectory = Path.Combine(environment.Directory, "native-state"),
        };
        using var sandbox = new ResearchSandboxService(profile);
        using var exporter = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
        var documentTools = new LocalDocumentToolService(environment.Get<IGeneratedDocumentRepository>(), documents,
            artifacts, blobs, chats, environment.Get<IDocumentFileCodec>(), exporter);
        var actionCatalog = ExtensionActionCatalog.CreateWithBuiltIns();
        var broker = new LocalToolBroker(connection, documents, documentTools, chats, actionCatalog, researchSandbox: sandbox);
        using var microphone = new MicrophoneTranscriptionService(connection, settings, NullLogger<MicrophoneTranscriptionService>.Instance);
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        using var service = new MissumAiAssistantService(connection, chats, environment.Get<IAssistantAttachmentRepository>(),
            artifacts, environment.Get<IMissumAiRunRepository>(), environment.Get<IClientToolExecutionRepository>(), blobs,
            documents, new DocumentContextPreparationService(documents), new SessionContextPreparationService(chats), broker,
            null!, microphone, settings, recent, NullLogger<MissumAiAssistantService>.Instance,
            extensionActions: actionCatalog, scientificResearch: research, researchSandbox: sandbox);
        var coordinator = CreateCoordinator();
        var events = new ConcurrentQueue<(string Type, JsonElement Payload)>();
        var serverEvents = new List<RunEvent>();
        Guid sessionId = Guid.Empty;
        string? finalRunId = null;
        var acceptedTerminal = false;
        Task? sendTask = null;
        JsonElement intermediateDetail = default;
        DateTimeOffset? intermediateObservedAt = null;
        try
        {
            await CommandAsync(coordinator, "mode.switch", new { chatMode = "claudescience" });
            var initial = await SnapshotAsync(coordinator);
            Assert.Equal("claudescience", initial.GetProperty("chatMode").GetString());
            sessionId = initial.GetProperty("activeSessionId").GetGuid();
            Assert.Equal(ChatMode.ClaudeScience, (await chats.GetSessionAsync(sessionId, timeout.Token))!.ChatMode);
            Assert.True(initial.GetProperty("scienceCapabilities").GetProperty("researchProjects").GetBoolean());
            Assert.False(AssistantCoordinator.IsCodingAgentSession(ChatMode.ClaudeScience));
            output.WriteLine($"profile={environment.Directory}; session={sessionId}; model={modelId}; server={serverUrl}; audio=disabled");

            const string prompt = """
                Recherchiere für eine lokale native WinUI-3-Chat-Anwendung mit SQLite, ob WAL oder Rollback-Journal
                sinnvoller ist. Die Anwendung speichert Chats, parallele Leseansichten und einzelne Schreibvorgänge.
                Prüfe Leser-/Schreiberkonkurrenz, SQLITE_BUSY, Checkpoints, Backups sowie die Grenzen bei Netzlaufwerken.
                Verwende ausschließlich Primärquellen der SQLite-Dokumentation und lies tatsächlich mindestens diese
                drei unterschiedlichen Originalseiten vollständig genug für verortete Belege:
                https://www.sqlite.org/wal.html
                https://www.sqlite.org/lockingv3.html
                https://www.sqlite.org/isolation.html
                Suche ergänzend über lokale SearXNG-Recherche; Such-Snippets reichen als Beleg nicht.
                Belege jede Empfehlung mit einer gelesenen Quelle und kennzeichne offene Fragen.
                Erzeuge anschließend mit document.create ein echtes Sitzungsartefakt wal-rollback-review.md
                (format=markdown, sectionId=review) mit Vergleichstabelle, Empfehlung, Grenzen und allen drei Quellen.
                Prüfe das gespeicherte Dokument mit document.read. Führe keine lokalen Benchmarks aus und erfinde keine
                Messdaten. Beende mit einer knappen deutschen Zusammenfassung und dem erzeugten Artefakt.
                """;
            sendTask = CommandAsync(coordinator, "chat.send", new { sessionId, prompt, deepResearch = true, deepResearchProfile = "web" });
            while (!sendTask.IsCompleted)
            {
                await Task.WhenAny(sendTask, Task.Delay(TimeSpan.FromSeconds(1), timeout.Token));
                timeout.Token.ThrowIfCancellationRequested();
                if (sendTask.IsCompleted) break;
                var projectIdInFlight = $"research-{sessionId:N}";
                if (await research.GetProjectAsync(projectIdInFlight, timeout.Token) is null) continue;
                await CommandAsync(coordinator, "research.open", new { sessionId, projectId = projectIdInFlight });
                var live = LastEvent("research.snapshot").GetProperty("detail");
                if (Text(live.GetProperty("report"), "reportKind") != "researchProgress") continue;
                var liveWorks = Array(live, "works").Where(work => IsPrimarySource(Text(work, "canonicalUrl"))).ToArray();
                var liveWorkIds = liveWorks.Select(work => Text(work, "workId")).ToHashSet(StringComparer.Ordinal);
                var liveEvidence = Array(live, "evidence").Where(item => liveWorkIds.Contains(Text(item, "workId"))).ToArray();
                if (liveWorks.Length == 0 || liveEvidence.Length == 0) continue;
                var liveMessages = await chats.ListMessagesAsync(sessionId, timeout.Token);
                var liveAssistant = liveMessages.Last(message => message.Role == ChatRole.Assistant);
                var realFetches = (liveAssistant.ToolSteps ?? []).Where(step => step.Tool == "web.fetch" && step.Status == "completed")
                    .Select(step => ReadToolResult(step.OutputJson)).Where(result => IsTrue(result, "found") && IsTrue(result, "isUntrusted")).ToArray();
                if (realFetches.Length == 0) continue; // Source snapshot can commit just before its tool-step receipt.
                if (liveEvidence.Any(item => !realFetches.Any(fetched =>
                    CanonicalSource(Text(fetched, "url")) == CanonicalSource(Text(liveWorks.Single(work => Text(work, "workId") == Text(item, "workId")), "canonicalUrl"))
                    && Array(fetched, "matches").Any(match => Text(match, "text") == Text(item, "exactExcerpt"))))) continue;
                Assert.All(liveEvidence, item =>
                {
                    Assert.Equal("retrievedExcerpt", Text(item, "evidenceLevel"));
                    Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text(item, "exactExcerpt")))), Text(item, "contentHash"));
                    var work = liveWorks.Single(work => Text(work, "workId") == Text(item, "workId"));
                    Assert.Contains(realFetches, fetched => CanonicalSource(Text(fetched, "url")) == CanonicalSource(Text(work, "canonicalUrl"))
                        && Array(fetched, "matches").Any(match => Text(match, "text") == Text(item, "exactExcerpt")));
                });
                Assert.Empty(Array(live, "claims"));
                Assert.Empty(Array(live, "verifications"));
                Assert.Equal("unresolved", Text(live.GetProperty("report"), "conclusionStatus"));
                Assert.Equal("sources.active", Text(live.GetProperty("checkpoint"), "stage"));
                Assert.False(sendTask.IsCompleted, "Intermediate sources must be observed while the research run is still generating.");
                intermediateDetail = live.Clone();
                intermediateObservedAt = DateTimeOffset.UtcNow;
                output.WriteLine($"{intermediateObservedAt:O} intermediate research: {liveWorks.Length} sources, {liveEvidence.Length} exact excerpts; generation still running");
                break;
            }
            await sendTask;
            Assert.True(intermediateObservedAt.HasValue, "No real source/excerpt snapshot was visible before the final research checkpoint.");

            var messages = await chats.ListMessagesAsync(sessionId, timeout.Token);
            var assistant = messages.Last(message => message.Role == ChatRole.Assistant);
            output.WriteLine($"Persisted answer: status={assistant.Status}; error={assistant.Error}; characters={assistant.Content.Length}");
            Assert.True(assistant.Status == MessageStatus.Completed, $"Research ended {assistant.Status}: {assistant.Error}");
            Assert.False(string.IsNullOrWhiteSpace(assistant.Content));
            Assert.DoesNotContain(messages, message => message.Status is MessageStatus.Pending or MessageStatus.Streaming);

            var accepted = requests.Last(request => request.Request.SessionId == sessionId.ToString("D") && request.Request.DeepResearch);
            finalRunId = accepted.RunId;
            Assert.Equal(modelId, accepted.Request.PreferredGeneralModelId);
            Assert.Null(accepted.Request.PreferredCodingModelId);
            Assert.Null(accepted.Request.CodingOptions);
            Assert.Equal($"research-{sessionId:N}", accepted.Request.ResearchOptions?.ProjectId);
            var serverRun = await client.GetRunAsync(finalRunId, timeout.Token);
            acceptedTerminal = serverRun.State is RunState.Completed or RunState.Failed or RunState.Cancelled or RunState.Interrupted;
            Assert.Equal(RunState.Completed, serverRun.State);
            var durableRun = await environment.Get<IMissumAiRunRepository>().GetByServerRunIdAsync(finalRunId, timeout.Token);
            Assert.NotNull(durableRun);
            Assert.Equal("completed", durableRun.State);
            Assert.Equal(assistant.Id, durableRun.AssistantMessageId);
            Assert.Equal(sessionId, durableRun.SessionId);

            var steps = assistant.ToolSteps ?? [];
            var fetches = steps.Where(step => step.Tool == "web.fetch" && step.Status == "completed")
                .Select(step => ReadToolResult(step.OutputJson))
                .Where(result => IsTrue(result, "found") && IsPrimarySource(Text(result, "url"))
                    && Array(result, "matches").Any(match => Text(match, "text").Length >= 32))
                .GroupBy(result => CanonicalSource(Text(result, "url")), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last()).ToArray();
            Assert.True(fetches.Length >= 3, $"Expected >=3 really fetched SQLite primary sources; found {fetches.Length}: "
                + string.Join(", ", fetches.Select(result => Text(result, "url"))));
            Assert.All(fetches, result =>
            {
                Assert.True(IsTrue(result, "isUntrusted"));
                Assert.True(result.GetProperty("sourceCharacters").GetInt32() > 0);
                Assert.True(result.GetProperty("retrievedAt").GetDateTimeOffset() >= startedAt.AddMinutes(-1));
            });
            // General Deep Research publishes its dossier as a persisted checkpoint,
            // not as a synthetic completed web.deepResearch tool step.
            await foreach (var item in client.StreamRunEventsAsync(finalRunId, 0, timeout.Token))
            {
                Assert.Equal(finalRunId, item.RunId);
                serverEvents.Add(item);
                if (item.Id >= serverRun.LastEventId || item.Type is RunEventTypes.RunCompleted or RunEventTypes.RunFailed or RunEventTypes.RunCancelled) break;
            }
            var researchReceipt = Assert.Single(serverEvents, item => item.Type == RunEventTypes.ResearchCheckpointCreated
                && item.Data.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object);
            Assert.Equal(finalRunId, Text(researchReceipt.Data, "runId"));
            Assert.Equal(finalRunId, Text(intermediateDetail.GetProperty("checkpoint"), "runId"));
            Assert.True(intermediateObservedAt < researchReceipt.CreatedAt, "The observed intermediate source snapshot must precede the server's final checkpoint.");
            Assert.Equal($"research-{sessionId:N}", Text(researchReceipt.Data, "projectId"));
            var deepResearch = researchReceipt.Data.GetProperty("result");
            Assert.True(IsTrue(deepResearch, "success"), deepResearch.GetRawText());
            Assert.Equal("searxng", Text(deepResearch, "provider"));
            Assert.False(IsTrue(deepResearch, "isFallback"));
            Assert.True(Array(deepResearch, "sources").Count(source => IsPrimarySource(Text(source, "url"))) >= 3,
                "All three primary sources must survive into the stored synthesis, not just be fetched.");

            await CommandAsync(coordinator, "research.list", new { sessionId });
            var researchList = LastEvent("research.snapshot");
            var project = Array(researchList, "projects").Single(item => Text(item, "id") == $"research-{sessionId:N}");
            var projectId = Text(project, "id");
            await CommandAsync(coordinator, "research.open", new { sessionId, projectId });
            var researchSnapshot = LastEvent("research.snapshot");
            var detail = researchSnapshot.GetProperty("detail");
            Assert.Equal(sessionId, researchSnapshot.GetProperty("sessionId").GetGuid());
            Assert.Equal(projectId, Text(detail.GetProperty("project"), "id"));
            Assert.Equal(finalRunId, Text(detail.GetProperty("checkpoint"), "runId"));
            Assert.NotEmpty(Array(detail, "nodes"));
            var works = Array(detail, "works").Where(work => IsPrimarySource(Text(work, "canonicalUrl"))).ToArray();
            Assert.True(works.Length >= 3, "The research source tab must restore at least three persisted SQLite sources.");
            var workIds = works.Select(work => Text(work, "workId")).ToHashSet(StringComparer.Ordinal);
            var evidence = Array(detail, "evidence").Where(item => workIds.Contains(Text(item, "workId"))).ToArray();
            Assert.True(evidence.Select(item => Text(item, "workId")).Distinct().Count() >= 3,
                "At least one verified excerpt is required for each of the three primary sources.");
            Assert.All(evidence, item => Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text(item, "exactExcerpt")))), Text(item, "contentHash")));
            var report = detail.GetProperty("report");
            Assert.Contains("SQLite", Text(report, "contentMarkdown"), StringComparison.OrdinalIgnoreCase);
            Assert.False(Text(report, "conclusionStatus") is "active" or "planned" or "unresolved" or "");

            var artifact = Assert.Single(await artifacts.ListForMessageAsync(assistant.Id, timeout.Token),
                item => item.FileName == "wal-rollback-review.md");
            await using var blob = await blobs.OpenReadAsync(artifact.BlobId, timeout.Token);
            using var artifactBytes = new MemoryStream();
            await blob.CopyToAsync(artifactBytes, timeout.Token);
            Assert.Equal(artifact.Length, artifactBytes.Length);
            Assert.Equal(artifact.Sha256, Convert.ToHexStringLower(SHA256.HashData(artifactBytes.ToArray())));
            var artifactText = Encoding.UTF8.GetString(artifactBytes.ToArray());
            Assert.Contains("WAL", artifactText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Rollback", artifactText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("sqlite.org/wal.html", artifactText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("sqlite.org/lockingv3.html", artifactText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("sqlite.org/isolation.html", artifactText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(steps, step => step.Tool == ClientToolNames.DocumentRead && step.Status == "completed");
            Assert.Contains(steps, step => step.Tool == ClientToolNames.DocumentCreate && step.Status == "completed");

            // The same session.open / snapshots power native chat tabs. Reconstruct
            // the coordinator, navigate away, and prove the stored UI data survives.
            await chats.SaveDraftAsync(sessionId, "Folgefrage: Wann soll ein Checkpoint laufen?", timeout.Token);
            var reopened = CreateCoordinator();
            await CommandAsync(reopened, "mode.switch", new { chatMode = "general" });
            var other = await SnapshotAsync(reopened);
            var otherSessionId = other.GetProperty("activeSessionId").GetGuid();
            Assert.NotEqual(sessionId, otherSessionId);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CommandAsync(reopened, "research.open", new { sessionId = otherSessionId, projectId }));
            await CommandAsync(reopened, "session.open", new { sessionId });
            var restored = await SnapshotAsync(reopened);
            Assert.Equal(sessionId, restored.GetProperty("activeSessionId").GetGuid());
            Assert.Equal("claudescience", restored.GetProperty("chatMode").GetString());
            Assert.Equal("Folgefrage: Wann soll ein Checkpoint laufen?", restored.GetProperty("draft").GetString());
            Assert.Contains(Array(restored, "sessions"), item => item.GetProperty("id").GetGuid() == sessionId);
            var restoredMessage = Array(restored, "messages").Single(item => item.GetProperty("id").GetGuid() == assistant.Id);
            Assert.Equal("completed", Text(restoredMessage, "status"));
            Assert.Contains(Array(restoredMessage, "artifacts"), item => item.GetProperty("id").GetGuid() == artifact.Id);
            await CommandAsync(reopened, "research.open", new { sessionId, projectId });
            var restoredResearch = LastEvent("research.snapshot").GetProperty("detail");
            Assert.Equal(Text(report, "contentMarkdown"), Text(restoredResearch.GetProperty("report"), "contentMarkdown"));
            Assert.Equal(works.Select(work => Text(work, "workId")).Order(), Array(restoredResearch, "works")
                .Where(work => IsPrimarySource(Text(work, "canonicalUrl"))).Select(work => Text(work, "workId")).Order());
            Assert.False(microphone.Current.IsRecording);

            var receipt = new
            {
                startedAt, completedAt = DateTimeOffset.UtcNow, sessionId, projectId, runId = finalRunId, modelId,
                runState = durableRun.State, messageStatus = assistant.Status.ToString(),
                fetchedSources = fetches.Select(result => new { url = Text(result, "url"), retrievedAt = Text(result, "retrievedAt"), matches = Array(result, "matches").Length }),
                storedSources = works.Length, storedEvidence = evidence.Length,
                artifact = new { artifact.Id, artifact.FileName, artifact.Sha256, artifact.Length },
                provider = Text(deepResearch, "provider"), isFallback = IsTrue(deepResearch, "isFallback"),
                sessionRestore = true, researchRestore = true,
                intermediateObservedAt, intermediateResearch = intermediateDetail,
            };
            output.WriteLine(JsonSerializer.Serialize(receipt, Json));
            var evidenceDirectory = Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_EVIDENCE_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(evidenceDirectory))
            {
                var destination = Path.GetFullPath(evidenceDirectory);
                Directory.CreateDirectory(destination);
                await File.WriteAllTextAsync(Path.Combine(destination, "claude-science-live-" + sessionId.ToString("N") + ".json"), JsonSerializer.Serialize(receipt, Json), timeout.Token);
                await File.WriteAllBytesAsync(Path.Combine(destination, "wal-rollback-review-" + sessionId.ToString("N") + ".md"), artifactBytes.ToArray(), timeout.Token);
            }
        }
        catch (Exception exception)
        {
            output.WriteLine("Live acceptance failed: " + exception.Message);
            if (sessionId != Guid.Empty)
            {
                // Persist complete local evidence before the temporary profile is
                // disposed. Diagnostics must never replace the original test failure.
                try
                {
                    using var evidenceTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var evidenceToken = evidenceTimeout.Token;
                    var recorded = await chats.ListMessagesAsync(sessionId, evidenceToken);
                    var evidenceDirectory = Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_EVIDENCE_DIRECTORY");
                    if (!string.IsNullOrWhiteSpace(evidenceDirectory))
                    {
                        var destination = Path.GetFullPath(evidenceDirectory);
                        Directory.CreateDirectory(destination);
                        finalRunId ??= requests.LastOrDefault(request => request.Request.SessionId == sessionId.ToString("D") && request.Request.DeepResearch)?.RunId;
                        var durableRun = finalRunId is null ? null
                            : await environment.Get<IMissumAiRunRepository>().GetByServerRunIdAsync(finalRunId, evidenceToken);
                        var savedArtifacts = await artifacts.ListForSessionAsync(sessionId, evidenceToken);
                        await File.WriteAllTextAsync(Path.Combine(destination, "claude-science-failure-" + sessionId.ToString("N") + ".json"),
                            JsonSerializer.Serialize(new { startedAt, failedAt = DateTimeOffset.UtcNow, sessionId, runId = finalRunId,
                                error = exception.ToString(), messages = recorded, durableRun, artifacts = savedArtifacts, requests,
                                clientEvents = events.Select(item => new { item.Type, item.Payload }), serverEvents }, Json), evidenceToken);
                        foreach (var project in await research.ListSessionProjectsAsync(sessionId, evidenceToken))
                        {
                            var archive = await research.LoadArchiveSnapshotAsync(project.Id, evidenceToken);
                            var graph = await research.LoadGraphAsync(project.Id, evidenceToken);
                            var checkpoint = await research.GetLatestCheckpointAsync(project.Id, evidenceToken);
                            var result = await research.LoadResultSnapshotAsync(project.Id, evidenceToken);
                            await File.WriteAllTextAsync(Path.Combine(destination, "claude-science-failure-project-" + sessionId.ToString("N") + ".json"),
                                JsonSerializer.Serialize(new { project, checkpoint, result, graph.Nodes, graph.Edges, archive.Works, archive.Evidence, archive.Report }, Json), evidenceToken);
                        }
                        foreach (var artifact in savedArtifacts.Values.SelectMany(items => items))
                        {
                            await using var input = await blobs.OpenReadAsync(artifact.BlobId, evidenceToken);
                            await using var file = File.Create(Path.Combine(destination, "claude-science-failure-artifact-" + artifact.Id.ToString("N") + ".bin"));
                            await input.CopyToAsync(file, evidenceToken);
                        }
                    }
                    foreach (var message in recorded.Where(message => message.Role == ChatRole.Assistant))
                    {
                        output.WriteLine($"Message {message.Id}: {message.Status}; {message.Error}");
                        foreach (var step in message.ToolSteps ?? [])
                        {
                            var detail = step.OutputJson ?? step.Detail ?? "";
                            output.WriteLine($"{step.Tool} [{step.Status}] {detail[..Math.Min(detail.Length, 1400)]}");
                        }
                    }
                }
                catch (Exception evidenceException) { output.WriteLine("Failure evidence export: " + evidenceException.Message); }
            }
            throw;
        }
        finally
        {
            // A cancelled client stream clears service.IsRunning before the server
            // necessarily stops. Resolve only the run accepted for this test session.
            finalRunId ??= requests.LastOrDefault(request => request.Request.SessionId == sessionId.ToString("D")
                && request.Request.DeepResearch)?.RunId;
            if (!acceptedTerminal && service.IsRunning)
            {
                try { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await service.CancelCurrentAsync(cleanup.Token); }
                catch (Exception exception) { output.WriteLine("Live-run cleanup: " + exception.Message); }
            }
            if (!acceptedTerminal && finalRunId is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    RunSnapshot? current = null;
                    try
                    {
                        using var probe = CancellationTokenSource.CreateLinkedTokenSource(cleanup.Token);
                        probe.CancelAfter(TimeSpan.FromSeconds(10));
                        current = await client.GetRunAsync(finalRunId, probe.Token);
                    }
                    catch (Exception exception) { output.WriteLine("Owned-run state probe: " + exception.Message); }
                    acceptedTerminal = current?.State is RunState.Completed or RunState.Failed or RunState.Cancelled or RunState.Interrupted;
                    if (!acceptedTerminal)
                    {
                        await client.CancelRunAsync(finalRunId, cleanup.Token);
                        do
                        {
                            current = await client.GetRunAsync(finalRunId, cleanup.Token);
                            acceptedTerminal = current.State is RunState.Completed or RunState.Failed or RunState.Cancelled or RunState.Interrupted;
                            if (!acceptedTerminal) await Task.Delay(200, cleanup.Token);
                        } while (!acceptedTerminal);
                    }
                }
                catch (Exception exception) { output.WriteLine($"Owned-run cleanup ({finalRunId}): {exception.Message}"); }
            }
            if (sendTask is not null)
            {
                try { await sendTask.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (Exception exception) { output.WriteLine("Owned send task ended: " + exception.Message); }
            }
            output.WriteLine($"Recorded requests={requests.Count}; events={events.Count}; terminal={acceptedTerminal}; session={sessionId}; run={finalRunId}");
        }

        AssistantCoordinator CreateCoordinator() => new(chats, documents,
            environment.Get<IContextAssembler>(), environment.Get<IPromptTriggerRepository>(), environment.Get<IAssistantAttachmentRepository>(),
            artifacts, environment.Get<IConversationSnapshotRepository>(), service, settings, recent, microphone, profile,
            actionCatalog, scientificResearch: research, scientificResearchExports: environment.Get<IScientificResearchExportService>());

        async Task CommandAsync(AssistantCoordinator target, string type, object payload)
        {
            await target.HandleAsync(new(AssistantWebBridge.ProtocolVersion, type, Guid.NewGuid().ToString("N"), JsonSerializer.SerializeToElement(payload, Json)), Emit, timeout.Token);
        }

        Task Emit(string type, object payload, string? requestId)
        {
            var value = JsonSerializer.SerializeToElement(payload, Json);
            events.Enqueue((type, value));
            if (type == "host.error") throw new InvalidOperationException(Text(value, "message"));
            if (type is "chat.started" or "chat.completed" or "chat.failed" or "chat.cancelled")
                output.WriteLine($"{DateTimeOffset.UtcNow:O} {type}");
            return Task.CompletedTask;
        }

        JsonElement LastEvent(string type) => events.Last(item => item.Type == type).Payload;
        async Task<JsonElement> SnapshotAsync(AssistantCoordinator target) => JsonSerializer.SerializeToElement(await target.BuildSnapshotAsync(timeout.Token), Json);
    }

    private static JsonElement ReadToolResult(string? json) => string.IsNullOrWhiteSpace(json)
        ? default : JsonSerializer.Deserialize<JsonElement>(json);

    private static string Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? property.ToString() : "";

    private static bool IsTrue(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;

    private static JsonElement[] Array(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array ? property.EnumerateArray().ToArray() : [];

    private static bool IsPrimarySource(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && (uri.Host.Equals("sqlite.org", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("www.sqlite.org", StringComparison.OrdinalIgnoreCase));

    private static string CanonicalSource(string url) => new Uri(url).GetLeftPart(UriPartial.Path).Replace("https://www.sqlite.org/", "https://sqlite.org/", StringComparison.OrdinalIgnoreCase).TrimEnd('/');

    private sealed record AcceptedRequest(string RunId, RunRequest Request);

    /// <summary>Observes serialized requests and real accepted run IDs without replacing any network response.</summary>
    private sealed class ObservingHandler(ConcurrentQueue<AcceptedRequest> requests) : DelegatingHandler(new HttpClientHandler { UseProxy = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RunRequest? run = null;
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/runs")
                run = JsonSerializer.Deserialize<RunRequest>(await request.Content!.ReadAsStringAsync(cancellationToken), ProtocolJson);
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
