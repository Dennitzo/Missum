using System.Diagnostics;
using System.Text.Json;
using Missum.Ai.Client;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Coding;
using Missum.Core.Models;
using Xunit.Abstractions;

namespace Missum.Tests;

public sealed class SubagentLiveTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions ProtocolJson = MissumAiProtocol.CreateJsonOptions();
    private static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] ExpectedParentServerTools = ["subagent.spawn", "subagent.wait", "coding.updatePlan"];
    private static readonly string[] ExpectedChildServerTools = ["math.evaluate", "coding.updatePlan"];
    private static readonly RunState[] TerminalStates = [RunState.Completed, RunState.Failed, RunState.Cancelled, RunState.Interrupted];
    private static readonly (string Name, int Value)[] ExpectedResultFields = [("count", 3), ("sum", 81), ("weighted_sum", 308)];

    [Fact]
    [Trait("Category", "Live")]
    public async Task RealQwenDelegatesFilesInParallelAndUsesTheChildResultWithoutRepeatingItsWork()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_SUBAGENT_LIVE") != "1") return;
        var model = Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_GENERAL_MODEL");
        Assert.False(string.IsNullOrWhiteSpace(model), "Select the installed Qwen model with MISSUM_AI_LIVE_GENERAL_MODEL.");
        Assert.Contains("qwen", model, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("27b", model, StringComparison.OrdinalIgnoreCase);
        var server = Environment.GetEnvironmentVariable("MISSUM_AI_SERVER_URL") ?? "http://127.0.0.1:8080";
        var evidenceRoot = Environment.GetEnvironmentVariable("MISSUM_SUBAGENT_EVIDENCE_DIRECTORY")
            ?? Path.Combine(Path.GetTempPath(), "Missum-subagent-live-evidence-" + Guid.NewGuid().ToString("N"));
        var attemptDirectory = Path.Combine(Path.GetFullPath(evidenceRoot), "live-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(attemptDirectory, "workspace");
        Directory.CreateDirectory(workspace);
        const string fixture = "item,value,weight\nAlpha,17,2\nBeta,23,3\nGamma,41,5\n";
        await File.WriteAllTextAsync(Path.Combine(workspace, "input.csv"), fixture);
        var parentSession = Guid.NewGuid();
        var marker = "SUBAGENT-LIVE-" + Guid.NewGuid().ToString("N");
        var request = new RunRequest(MissumAiProtocol.Version, RunMode.Coding,
        [
            new("user", [new("text", Text: $"""
                Du führst einen realen lokalen Auftrag in Missum aus. Verwende die angebotenen strukturierten Werkzeuge vollständig. Alle Dateien liegen im autorisierten Workspace. Arbeite mit genau einem Subagenten; übernimm dessen abgeschlossenes Ergebnis unmittelbar. Keine Websuche, keine Shellbefehle, keine erneute Prüfung seiner Datei oder Rechnung.
                Workspace: {workspace}
                Kennung: {marker}
                Führe diese Arbeit vollständig mit genau einem Subagenten aus:
                1. Starte sofort subagent.spawn mit diesem Auftrag: Lies input.csv genau einmal mit coding.read. Ermittle count aus den Datenzeilen. Berechne weighted_sum zwingend mit math.evaluate, operation=dot, left=Vektor der aus input.csv gelesenen value-Werte, right=Vektor der zugehörigen weight-Werte. Berechne auch sum der value-Spalte mit math.evaluate, operation=dot, left=derselbe value-Vektor, right=gleich langer Vektor aus Einsen. Übernimm die tatsächlichen Werkzeugergebnisse; rechne die Summen nicht selbst im Modell aus. Schreibe ausschließlich result.txt mit coding.write und exakt drei Zeilen count=<Zahl>, sum=<Zahl>, weighted_sum=<Zahl>. Verwende ausschließlich coding.read, math.evaluate und coding.write, keine Shellbefehle und lies das geschriebene Ergebnis nicht noch einmal. Antworte anschließend mit diesen drei tatsächlichen Zahlen und dem erzeugten Dateipfad. Dein Schreibpfad ist ausschließlich result.txt; der Hauptagent bearbeitet main.txt.
                2. Arbeite währenddessen selbst: Schreibe mit coding.write main.txt mit den Zeilen marker={marker} und parent_stage=prepared. Deine eigene Aufgabe benötigt keine Datei-Leseoperation.
                3. Hole anschließend das echte Subagent-Ergebnis mit subagent.wait und der erhaltenen runId ab. Verwende count, sum und weighted_sum direkt aus dieser Rückgabe. Lies weder input.csv noch result.txt, wiederhole keine Rechnung des Subagenten und starte keinen zweiten Subagenten.
                4. Aktualisiere main.txt mit coding.write und expectedSha256 aus deinem ersten Schreibbeleg. Inhalt: marker={marker}, parent_stage=completed und die drei vom Subagenten gelieferten Zeilen count, sum, weighted_sum. Verwende nur zwei eigene coding.write-Aufrufe auf main.txt, keine coding.read-/coding.edit-/coding.command-Aufrufe. Beende mit einer kurzen Antwort, welche die übernommenen drei Zahlen enthält.
                """)]),
        ],
            ClientCapabilities: ["coding", "coding.process", "coding.evidence", "workspace", "workspace.open", "documents", "documentIo", "visual-tools", "subagents"],
            Limits: new(MaximumOutputTokens: 8000, MaximumContextTokens: 262144),
            SessionId: parentSession.ToString("D"),
            AllowedServerTools: ["web.search", "web.fetch", "media.inspect", "media.analyze", "image.generate", "speech.synthesize", "math.evaluate", "context.embed", "context.retrieve"],
            PreferredGeneralModelId: model, PreferredCodingModelId: model, ReasoningEffort: "none",
            CodingOptions: new(UseWorkingState: true, ReasoningPolicy: "maximum", WorkspacePath: workspace, ContinueSessionContext: true),
            WorkspacePath: workspace);
        await File.WriteAllTextAsync(Path.Combine(attemptDirectory, "request.json"), JsonSerializer.Serialize(request, EvidenceJson));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        using var handler = new HttpClientHandler { UseProxy = false };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(server.TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };
        using var client = new MissumAiClient(http, "subagent-live-" + Guid.NewGuid().ToString("N"));
        var executor = new LocalCodingToolExecutor(workspace);
        var events = new List<RunEvent>();
        var executions = new List<LiveToolExecution>();
        var results = new Dictionary<string, ClientToolResult>(StringComparer.Ordinal);
        var children = new Dictionary<string, SubagentChatState>(StringComparer.Ordinal);
        var timer = Stopwatch.StartNew();
        string? parentRunId = null;
        var parentAnswer = "";
        var parentTerminal = false;
        var passed = false;
        Exception? failure = null;
        RunSnapshot? parentSnapshot = null;
        RunSnapshot? childSnapshot = null;
        LiveCacheReuse? cacheReuse = null;
        long cursor = 0;
        try
        {
            var accepted = await client.CreateRunAsync(request, "subagent-live-" + Guid.NewGuid().ToString("N"), deadline.Token);
            parentRunId = accepted.RunId;
            output.WriteLine($"Real subagent run: {parentRunId}; model: {model}; workspace: {workspace}");
            while (!parentTerminal)
            {
                await foreach (var item in client.StreamRunEventsAsync(parentRunId, cursor, deadline.Token))
                {
                    if (item.Id <= cursor) continue;
                    cursor = item.Id; events.Add(item);
                    switch (item.Type)
                    {
                        case RunEventTypes.SubagentStarted:
                        case RunEventTypes.SubagentUpdated:
                        case RunEventTypes.SubagentCompleted:
                            var info = item.Data.Deserialize<SubagentRunEvent>(ProtocolJson)!;
                            Assert.Equal(parentRunId, info.ParentRunId);
                            Assert.Equal(model, info.ModelId);
                            if (!children.TryGetValue(info.AgentId, out var state))
                                state = SubagentChatState.Create(info, parentSession, item.CreatedAt);
                            state = state with { Status = SubagentChatState.StateName(info.State), Model = info.ModelId };
                            if (!state.IsRunning)
                                state = state with { AssistantMessage = SubagentChatState.Finish(state.AssistantMessage,
                                    info.State == RunState.Completed ? MessageStatus.Completed : info.State == RunState.Cancelled ? MessageStatus.Cancelled : MessageStatus.Failed, item.CreatedAt) };
                            children[info.AgentId] = state;
                            break;
                        case RunEventTypes.SubagentEvent:
                            var forwarded = item.Data.Deserialize<SubagentForwardedEvent>(ProtocolJson)!;
                            Assert.Equal(parentRunId, forwarded.ParentRunId);
                            Assert.Equal(forwarded.RunId, forwarded.Event.RunId);
                            Assert.True(children.ContainsKey(forwarded.AgentId), "The gateway must announce the child before forwarding its events.");
                            children[forwarded.AgentId] = children[forwarded.AgentId].Apply(forwarded.Event);
                            if (forwarded.Event.Type == RunEventTypes.ServerToolStarted)
                                Assert.Contains(forwarded.Event.Data.GetProperty("tool").GetString(), ExpectedChildServerTools);
                            if (forwarded.Event.Type == RunEventTypes.ClientToolProposed)
                                await ExecuteProposalAsync(forwarded.Event, forwarded.AgentId);
                            break;
                        case RunEventTypes.ClientToolProposed:
                            await ExecuteProposalAsync(item, null);
                            break;
                        case RunEventTypes.TextDelta:
                            parentAnswer = MissumAiAssistantService.ApplyTextDelta(parentAnswer, item.Data.Deserialize<TextDeltaEvent>(ProtocolJson));
                            break;
                        case RunEventTypes.ServerToolStarted:
                            Assert.Contains(item.Data.GetProperty("tool").GetString(), ExpectedParentServerTools);
                            break;
                        case RunEventTypes.RunCompleted:
                        case RunEventTypes.RunFailed:
                        case RunEventTypes.RunCancelled:
                            parentTerminal = true;
                            break;
                    }
                    if (parentTerminal) break;
                }
                if (!parentTerminal)
                {
                    var current = await client.GetRunAsync(parentRunId, deadline.Token);
                    Assert.DoesNotContain(current.State, TerminalStates);
                }
            }
            parentSnapshot = await client.GetRunAsync(parentRunId, deadline.Token);
            Assert.Equal(RunState.Completed, parentSnapshot.State);
            Assert.Equal(model, parentSnapshot.SelectedModel);
            var child = Assert.Single(children.Values);
            childSnapshot = await client.GetRunAsync(child.RunId, deadline.Token);
            Assert.Equal(RunState.Completed, childSnapshot.State);
            Assert.Equal(model, childSnapshot.SelectedModel);
            Assert.Equal(MessageStatus.Completed, child.AssistantMessage.Status);
            Assert.DoesNotContain(events, item => item.Type is RunEventTypes.ModelFallback or RunEventTypes.ProviderFallback);
            Assert.Single(events, item => item.Type == RunEventTypes.SubagentStarted);
            Assert.Single(events, item => item.Type == "subagent.resultConsumed");
            var childRead = Assert.Single(executions, item => item.AgentId is not null && item.Proposal.Name == ClientToolNames.CodingRead);
            Assert.Equal("input.csv", RelativeToolPath(childRead.Proposal));
            Assert.Single(executions, item => item.AgentId is not null && item.Proposal.Name == ClientToolNames.CodingWrite);
            Assert.Equal(2, executions.Count(item => item.AgentId is null && item.Proposal.Name == ClientToolNames.CodingWrite));
            Assert.DoesNotContain(executions, item => item.AgentId is null && item.Proposal.Name == ClientToolNames.CodingRead);
            Assert.Equal(executions.Count, executions.Select(item => item.Proposal.ProposalId).Distinct(StringComparer.Ordinal).Count());
            var parentWrites = executions.Where(item => item.AgentId is null).OrderBy(item => item.StartedAt).ToArray();
            var waitStarted = Assert.Single(events, item => item.Type == RunEventTypes.ServerToolStarted && item.Data.GetProperty("tool").GetString() == "subagent.wait");
            var waitCompleted = Assert.Single(events, item => item.Type == RunEventTypes.ServerToolCompleted && item.Data.GetProperty("tool").GetString() == "subagent.wait");
            Assert.True(parentWrites[0].StartedAt < waitStarted.CreatedAt, "The parent must perform its own write before waiting for the child.");
            Assert.True(parentWrites[1].StartedAt >= waitCompleted.CreatedAt, "The final parent write must consume the delivered child result.");
            Assert.Equal(child.AssistantMessage.Content, waitCompleted.Data.GetProperty("result").GetProperty("result").GetString());
            var mainText = await File.ReadAllTextAsync(Path.Combine(workspace, "main.txt"), deadline.Token);
            var childText = await File.ReadAllTextAsync(Path.Combine(workspace, "result.txt"), deadline.Token);
            Assert.Equal(fixture, await File.ReadAllTextAsync(Path.Combine(workspace, "input.csv"), deadline.Token));
            foreach (var (name, expected) in ExpectedResultFields)
            {
                Assert.Equal(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), Fields(childText)[name]);
                Assert.Equal(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), Fields(mainText)[name]);
                Assert.Contains(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), child.AssistantMessage.Content, StringComparison.Ordinal);
                Assert.Contains(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), parentAnswer, StringComparison.Ordinal);
            }
            Assert.Equal(marker, Fields(mainText)["marker"]);
            Assert.Equal("completed", Fields(mainText)["parent_stage"]);
            Assert.Equal(3, Fields(childText).Count);
            var childEvents = events.Where(item => item.Type == RunEventTypes.SubagentEvent)
                .Select(item => item.Data.Deserialize<SubagentForwardedEvent>(ProtocolJson)!)
                .Where(item => item.AgentId == child.AgentId).Select(item => item.Event).ToArray();
            var mathCompletions = childEvents.Where(item => item.Type == RunEventTypes.ServerToolCompleted
                && item.Data.GetProperty("tool").GetString() == "math.evaluate").ToArray();
            Assert.NotEmpty(mathCompletions);
            Assert.Contains(mathCompletions, item => item.Data.GetProperty("success").GetBoolean()
                && item.Data.GetProperty("result").GetProperty("operation").GetString() == "dot"
                && item.Data.GetProperty("result").GetProperty("result").GetDouble() == 308d);
            var fork = Assert.Single(childEvents, item => item.Type == "subagent.contextForked");
            var forkMetadata = fork.Data;
            Assert.Equal(1, forkMetadata.GetProperty("gpuIndex").GetInt32());
            Assert.Equal("forked", forkMetadata.GetProperty("cacheStatus").GetString());
            var forkedTokens = forkMetadata.GetProperty("cachedTokens").GetInt32();
            Assert.True(forkedTokens > 0, "The child must load the evaluated parent KV prefix.");
            var firstCompletedTurn = childEvents.First(item => item.Type == RunEventTypes.CodingMetrics);
            var firstTurnMetrics = firstCompletedTurn.Data.Deserialize<CodingTurnMetricsEvent>(ProtocolJson)!;
            Assert.Equal(1, firstTurnMetrics.Round);
            // CompleteChatAsync emits this final progress record with native usage counters.
            // Restrict it to the first turn: subsequent child turns could reuse their own KV
            // even when the inherited parent prefix was restored but never used.
            var firstTurnGeneration = childEvents.Where(item => item.Type == RunEventTypes.ModelGeneration
                    && item.Id < firstCompletedTurn.Id)
                .Select(item => new { Event = item, Progress = item.Data.Deserialize<ModelGenerationEvent>(ProtocolJson)! })
                .Last(item => item.Progress.State == "tokenProgress");
            var cachedPromptTokens = firstTurnGeneration.Progress.CachedPromptTokens;
            var reusedForkFraction = cachedPromptTokens is { } cached ? (double)cached / forkedTokens : (double?)null;
            cacheReuse = new LiveCacheReuse(child.AgentId, child.RunId, fork.CreatedAt, firstTurnGeneration.Event.CreatedAt,
                firstTurnMetrics.Round, forkedTokens, cachedPromptTokens, firstTurnGeneration.Progress.PromptTokens,
                firstTurnMetrics.Metrics.PromptEvaluatedTokens, reusedForkFraction);
            Assert.Equal(1d, firstTurnGeneration.Progress.PromptProgress);
            Assert.True(cachedPromptTokens is > 0, "The first completed child inference must actually reuse native prompt KV; a restored slot alone is insufficient.");
            Assert.Equal(cachedPromptTokens, firstTurnMetrics.Metrics.CachedPromptTokens);
            Assert.True(reusedForkFraction is > 0.5,
                $"The first child inference must reuse more than half of the stable parent prefix: cachedPromptTokens={cachedPromptTokens}, forkedTokens={forkedTokens}, reusedForkFraction={reusedForkFraction}.");
            await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "native-subagent-live-input.json"),
                JsonSerializer.Serialize(ToNativeChild(child), EvidenceJson));
            passed = true;
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            if (parentRunId is not null && !parentTerminal)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await client.CancelRunAsync(parentRunId, cleanup.Token);
                }
                catch (Exception cleanupError) when (cleanupError is HttpRequestException or OperationCanceledException or JsonException)
                { output.WriteLine("Cleanup of this test run failed: " + cleanupError.Message); }
            }
            timer.Stop();
            var report = new
            {
                passed, model, server, parentRunId, parentSession, workspace, elapsedMilliseconds = timer.ElapsedMilliseconds,
                error = failure?.ToString(), parentSnapshot, childSnapshot, parentAnswer, cacheReuse,
                children = children.Values.Select(ToNativeChild),
                executions, events,
                contextForks = events.Where(item => item.Type == RunEventTypes.SubagentEvent
                    && item.Data.GetProperty("event").GetProperty("type").GetString() == "subagent.contextForked")
                    .Select(item => new { item.CreatedAt, metadata = item.Data.GetProperty("event").GetProperty("data") }),
                mathEvaluations = events.Where(item => item.Type == RunEventTypes.SubagentEvent
                    && item.Data.GetProperty("event").GetProperty("type").GetString() == RunEventTypes.ServerToolCompleted
                    && item.Data.GetProperty("event").GetProperty("data").GetProperty("tool").GetString() == "math.evaluate")
                    .Select(item => new { item.CreatedAt, receipt = item.Data.GetProperty("event").GetProperty("data") }),
                timings = events.Where(item => item.Type is RunEventTypes.ModelGeneration or RunEventTypes.SubagentStarted or RunEventTypes.SubagentCompleted
                    || item.Type == RunEventTypes.SubagentEvent && item.Data.GetProperty("event").GetProperty("type").GetString() == RunEventTypes.ModelGeneration)
                    .Select(item => new { item.Id, item.Type, item.CreatedAt, elapsedMilliseconds = (item.CreatedAt - (events.FirstOrDefault()?.CreatedAt ?? item.CreatedAt)).TotalMilliseconds, item.Data }),
            };
            var reportPath = Path.Combine(attemptDirectory, "subagent-live-report.json");
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, EvidenceJson));
            await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "subagent-live-latest-report.json"), JsonSerializer.Serialize(report, EvidenceJson));
            output.WriteLine("Live report: " + reportPath);
        }

        async Task ExecuteProposalAsync(RunEvent item, string? agentId)
        {
            var proposal = item.Data.Deserialize<ToolProposal>(ProtocolJson)!;
            Assert.Equal(item.RunId, proposal.RunId);
            if (results.TryGetValue(proposal.ProposalId, out var stored))
            {
                await client.SubmitClientToolResultAsync(item.RunId, stored, deadline.Token);
                return;
            }
            Assert.True(proposal.Name is ClientToolNames.CodingRead or ClientToolNames.CodingWrite,
                "Unexpected real local tool: " + proposal.Name);
            var relative = RelativeToolPath(proposal);
            Assert.True(agentId is null ? proposal.Name == ClientToolNames.CodingWrite && relative == "main.txt"
                : proposal.Name == ClientToolNames.CodingRead && relative == "input.csv"
                    || proposal.Name == ClientToolNames.CodingWrite && relative == "result.txt", "The tool must stay within its assigned test path.");
            var at = DateTimeOffset.UtcNow;
            var raw = await executor.ExecuteAsync(proposal.Name, proposal.Arguments, deadline.Token);
            Assert.False(raw.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False, raw.GetRawText());
            var result = new ClientToolResult(proposal.ProposalId, "completed", raw);
            results.Add(proposal.ProposalId, result);
            executions.Add(new(agentId, proposal, result, at, DateTimeOffset.UtcNow));
            if (agentId is not null)
            {
                var child = children[agentId];
                var previous = child.AssistantMessage.ToolSteps?.FirstOrDefault(step => step.Id == proposal.ProposalId);
                children[agentId] = child with { AssistantMessage = SubagentChatState.AddStep(child.AssistantMessage,
                    new(proposal.ProposalId, proposal.Name, "completed", raw.GetRawText(), InputJson: proposal.Arguments.GetRawText(),
                        OutputJson: raw.GetRawText(), Explanation: proposal.Summary, ContentOffset: previous?.ContentOffset ?? child.AssistantMessage.Content.Length,
                        StartedAt: previous?.StartedAt ?? at, CompletedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow, AgentId: agentId)) };
            }
            await client.SubmitClientToolResultAsync(item.RunId, result, deadline.Token);
        }

        string RelativeToolPath(ToolProposal proposal)
        {
            var path = proposal.Arguments.GetProperty("path").GetString()!;
            var absolute = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workspace, path));
            Assert.StartsWith(Path.TrimEndingDirectorySeparator(workspace) + Path.DirectorySeparatorChar, absolute, StringComparison.OrdinalIgnoreCase);
            return Path.GetRelativePath(workspace, absolute).Replace('\\', '/');
        }
    }

    private static Dictionary<string, string> Fields(string text) => text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Split('=', 2)).ToDictionary(parts => parts[0].Trim(), parts => parts.Length == 2 ? parts[1].Trim() : "", StringComparer.Ordinal);

    private static object ToNativeChild(SubagentChatState child) => new
    {
        child.AgentId, child.RunId, child.ParentRunId, child.SessionId, child.Title, child.Status, child.IsRunning,
        child.Model, child.ContextUsed, child.ContextLimit, child.RunStatus, child.RunDetail, child.GenerationState, child.GeneratedTokens, child.GenerationUpdatedAt,
        runMessageId = child.AssistantMessage.Id,
        messages = new[] { ToNativeMessage(child.UserMessage), ToNativeMessage(child.AssistantMessage) },
    };

    private static object ToNativeMessage(ChatMessage message) => new
    {
        message.Id, message.SessionId, role = message.Role.ToString().ToLowerInvariant(), message.Content,
        status = message.Status.ToString().ToLowerInvariant(), message.CreatedAt, message.UpdatedAt, message.Error,
        message.Revision, toolSteps = message.ToolSteps ?? [], artifacts = Array.Empty<object>(),
    };

    private sealed record LiveToolExecution(string? AgentId, ToolProposal Proposal, ClientToolResult Result, DateTimeOffset StartedAt, DateTimeOffset CompletedAt);
    private sealed record LiveCacheReuse(string AgentId, string RunId, DateTimeOffset ForkedAt, DateTimeOffset FirstInferenceCompletedAt,
        long Round, int ForkedTokens, int? CachedPromptTokens, int? PromptTokens, int? PromptEvaluatedTokens, double? ReusedForkFraction);
}
