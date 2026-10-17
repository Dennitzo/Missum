using System.Text.Json;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class DeepResearchActivityTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = MissumAiProtocol.CreateJsonOptions();
    private static readonly string[] ReportUncertainties = ["Weitere Messung erforderlich."];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GatewayProgressCreatesARealReloadableActivityForDirectGeneralAndNestedResearch(bool nested)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var documents = environment.Get<IDocumentIngestor>();
        var session = await chats.CreateSessionAsync("Observed research");
        await environment.Get<ISettingsStore>().SaveAsync(new AppSettings { ActiveSessionId = session.Id,
            SelectedModel = "fixture/research", MissumAiServerUrl = "http://127.0.0.1:65000" });
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var requested = new List<string>();
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance,
            () => new ActivityGatewayHandler(nested, session.Id, requested));
        var broker = new LocalToolBroker(connection, documents, null!, chats);
        using var microphone = new MicrophoneTranscriptionService(connection, settings, NullLogger<MicrophoneTranscriptionService>.Instance);
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        using var service = new MissumAiAssistantService(connection, chats, environment.Get<IAssistantAttachmentRepository>(),
            environment.Get<IChatArtifactRepository>(), environment.Get<IMissumAiRunRepository>(), environment.Get<IClientToolExecutionRepository>(),
            environment.Get<IBinaryObjectStore>(), documents, new DocumentContextPreparationService(documents),
            new SessionContextPreparationService(chats), broker, null!, microphone, settings, recent, NullLogger<MissumAiAssistantService>.Instance);
        var coordinator = new AssistantCoordinator(chats, documents, environment.Get<IContextAssembler>(), environment.Get<IPromptTriggerRepository>(),
            environment.Get<IAssistantAttachmentRepository>(), environment.Get<IChatArtifactRepository>(), environment.Get<IConversationSnapshotRepository>(),
            service, settings, recent, microphone);
        var observations = new List<JsonElement>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await coordinator.HandleAsync(new(AssistantProtocol.ProtocolVersion, "chat.send", "observed-research",
            JsonSerializer.SerializeToElement(new { sessionId = session.Id, prompt = "Prüfe die Quellenlage.", deepResearch = true })),
            (type, data, _) => { if (type == "subagent.snapshot") observations.Add(JsonSerializer.SerializeToElement(data, JsonSerializerOptions.Web)); return Task.CompletedTask; }, timeout.Token);
        var parent = (await chats.ListMessagesAsync(session.Id))[^1];
        Assert.Equal(MessageStatus.Completed, parent.Status);
        Assert.Equal("Eigenständige Hauptantwort.", parent.Content);
        var states = SubagentChatState.Read([parent], environment.Directory);
        var activity = Assert.Single(states, state => state.Kind == "deepResearch");
        Assert.Equal("completed", activity.Status);
        Assert.Equal(1032, activity.ContextUsed);
        Assert.Equal(32768, activity.ContextLimit);
        Assert.Equal(nested ? "nested-research" : "server-general-research-view-fixture", activity.OriginToolStepId);
        Assert.Equal(nested ? Assert.Single(states, state => state.Kind == "subagent").AssistantMessage.Id : parent.Id, activity.OriginMessageId);
        Assert.Contains("Der Befund ist eingeschränkt bestätigt.", activity.AssistantMessage.Content);
        Assert.DoesNotContain(requested, path => path.Contains("deep-research-run:", StringComparison.Ordinal));
        var emitted = observations.Where(state => state.GetProperty("kind").GetString() == "deepResearch").ToArray();
        Assert.True(emitted.Length >= 3);
        Assert.All(emitted, state => Assert.Equal(parent.Id, state.GetProperty("parentMessageId").GetGuid()));
        Assert.Equal("running", emitted[0].GetProperty("status").GetString());
        Assert.Equal("completed", emitted[^1].GetProperty("status").GetString());
        if (!nested)
        {
            var header = Assert.Single(parent.ToolSteps!, step => step.Tool == "web.deepResearch");
            Assert.Contains("activityProjection", header.InputJson!);
            Assert.Equal("completed", header.Status);
            var history = MissumAiAssistantService.BuildCodingHistoryMessages([parent], 100_000);
            var historyText = string.Join("\n", history.SelectMany(message => message.Content).Select(part => part.Text));
            Assert.DoesNotContain("activityProjection", historyText);
            Assert.DoesNotContain("deep-research:", historyText);
            Assert.DoesNotContain("Der Befund ist eingeschränkt bestätigt.", historyText);
        }
    }

    [Fact]
    public void RealProgressIsOperationScopedReplaySafeAndKeepsOneChronologicalStepPerAction()
    {
        var parent = Parent();
        var activity = SubagentChatState.CreateDeepResearch(parent, "run-parent", "server-operation", "Prüfe die Quelle.", At);
        Assert.Equal("deepResearch", activity.Kind);
        Assert.Equal("Prüfe die Quelle", activity.Title);
        Assert.Equal(parent.Id, activity.ParentMessageId);
        Assert.Equal(parent.Id, activity.OriginMessageId);
        Assert.Equal(activity.AgentId, SubagentChatState.CreateDeepResearch(parent, "run-parent", "server-operation", "Other task", At).AgentId);
        Assert.NotEqual(activity.AgentId, SubagentChatState.CreateDeepResearch(parent, "another-run", "server-other-run-operation", "Other task", At).AgentId);
        var searching = new DeepResearchProgressEvent("server-operation", "run-parent", 1, "search", "running",
            "Quellen suchen", StepId: "search-1", Tool: "web.search", Query: "Bohm quantitative evidence");
        activity = activity.ApplyDeepResearchProgress(Event(2, "run-parent", searching), searching);
        var found = searching with { Sequence = 2, Status = "completed", Detail = "Eine Quelle gefunden.",
            Sources = [new("source-1", "Originalquelle", "https://example.org/paper", "Gelesener Auszug")] };
        activity = activity.ApplyDeepResearchProgress(Event(3, "run-parent", found), found);
        var step = Assert.Single(activity.AssistantMessage.ToolSteps!);
        Assert.Equal("completed", step.Status);
        Assert.Contains("Bohm quantitative evidence", step.InputJson!);
        Assert.Contains("https://example.org/paper", step.OutputJson!);
        Assert.Same(activity, activity.ApplyDeepResearchProgress(Event(4, "run-parent", found), found));
        Assert.Same(activity, activity.ApplyDeepResearchProgress(Event(5, "run-wrong", found), found));
        var wrong = found with { OperationId = "other-operation", Sequence = 3 };
        Assert.Same(activity, activity.ApplyDeepResearchProgress(Event(5, "run-parent", wrong), wrong));
        var result = Result();
        var completed = activity.CompleteDeepResearch(new(6, "run-parent", RunEventTypes.ServerToolCompleted, At.AddSeconds(6), result), result, "completed");
        Assert.False(completed.IsRunning);
        Assert.Contains("Der Befund ist eingeschränkt bestätigt.", completed.AssistantMessage.Content);
        Assert.Contains("Zusammenfassung der Originalquellen.", completed.AssistantMessage.Content);
        Assert.Contains("Die Quelle deckt nur den untersuchten Fall ab.", completed.AssistantMessage.Content);
        Assert.Contains("Weitere Messung erforderlich.", completed.AssistantMessage.Content);
        Assert.DoesNotContain("\"findings\"", completed.AssistantMessage.Content);
        Assert.Null(completed.CompletionReceipt(0, At));
        Assert.Equal(2, completed.AssistantMessage.ToolSteps!.Count);
        Assert.Contains("https://example.org/paper", completed.AssistantMessage.ToolSteps[^1].OutputJson!);
    }

    [Theory]
    [InlineData(MessageStatus.Completed, "interrupted")]
    [InlineData(MessageStatus.Failed, "failed")]
    [InlineData(MessageStatus.Cancelled, "cancelled")]
    public void HistoricalRunningStepsCannotRemainBusyAfterTheirParentEnded(MessageStatus parentStatus, string expected)
    {
        var parent = Parent() with { Status = parentStatus };
        var activity = SubagentChatState.FromDeepResearchTool(parent, new("historical", "web.deepResearch", "running"));
        Assert.Equal(expected, activity.Status);
        Assert.False(activity.IsRunning);
        Assert.Equal(parent.Id, activity.ParentMessageId);
    }

    [Fact]
    public void ModelCountersKeepHydratedTranscriptStableUntilSemanticProgressArrives()
    {
        var activity = SubagentChatState.CreateDeepResearch(Parent(), "run-parent", "operation", "Frage", At);
        var start = new DeepResearchProgressEvent("operation", "run-parent", 1, "model", "running", "Rechercheplan erstellen",
            StepId: "model-1", Tool: "research.plan");
        activity = activity.ApplyDeepResearchProgress(Event(1, "run-parent", start), start);
        var revision = activity.TranscriptRevision;
        var tick = start with { Sequence = 2, Title = "Modellfortschritt", Tool = null,
            ModelProgress = new("tokenProgress", PromptTokens: 100, GeneratedTokens: 20) };
        var counters = activity.ApplyDeepResearchProgress(Event(2, "run-parent", tick), tick);
        Assert.Equal(revision, counters.TranscriptRevision);
        Assert.Same(activity.AssistantMessage, counters.AssistantMessage);
        Assert.Equal("Rechercheplan erstellen", Assert.Single(counters.AssistantMessage.ToolSteps!).Explanation);
        Assert.Equal(20, counters.GeneratedTokens);
        Assert.Equal(120, counters.ContextUsed);
        var finish = start with { Sequence = 3, Status = "completed", Text = "Prüfe die Originalquelle." };
        var completedStep = counters.ApplyDeepResearchProgress(Event(3, "run-parent", finish), finish);
        Assert.True(completedStep.TranscriptRevision > revision);
        Assert.Contains("Prüfe die Originalquelle.", Assert.Single(completedStep.AssistantMessage.ToolSteps!).Detail);
        var terminal = completedStep.CompleteDeepResearch(new(4, "run-parent", RunEventTypes.DeepResearchProgress, At.AddSeconds(4), default), Result(), "completed");
        Assert.True(terminal.TranscriptRevision > completedStep.TranscriptRevision);
    }

    [Fact]
    public async Task ResearchActivityPersistsLosslesslyWithPagedToolsWithoutCreatingAnyModelHistoryMessage()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var snapshots = environment.Get<IConversationSnapshotRepository>();
        var session = await chats.CreateSessionAsync("Research activity");
        var parent = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Unveränderte Hauptantwort", MessageStatus.Completed);
        var original = new AssistantToolStep("server-research", "web.deepResearch", "completed",
            InputJson: "{\"task\":\"Prüfe die Quellenlage.\"}", OutputJson: Result().GetRawText());
        await chats.SaveToolStepDeltaAsync(parent.Id, original);
        var child = SubagentChatState.CreateDeepResearch(parent, "run-parent", original.Id, "Prüfe die Quellenlage.", At);
        for (var index = 1; index <= 75; index++)
        {
            var progress = new DeepResearchProgressEvent(original.Id, "run-parent", index, "fetch", "completed", "Quelle lesen",
                Detail: new string('x', 1000), StepId: "fetch-" + index, Tool: "web.fetch", Url: "https://example.org/" + index);
            child = child.ApplyDeepResearchProgress(Event(index, "run-parent", progress), progress);
        }
        child = child.CompleteDeepResearch(new(76, "run-parent", RunEventTypes.ServerToolCompleted, At.AddSeconds(76), default), Result(), "completed");
        var stored = await child.PersistAsync(environment.Directory);
        Assert.Contains("subagentStorageKey", stored);
        var receipt = new AssistantToolStep(child.ReceiptId, "subagent", "completed", OutputJson: stored);
        await chats.SaveToolStepDeltaAsync(parent.Id, receipt);
        var auxiliary = await snapshots.GetAuxiliaryMessagesAsync(session.Id);
        var summary = Assert.Single(SubagentChatState.ReadSummaries(auxiliary));
        Assert.Equal("deepResearch", summary.Kind);
        Assert.Equal(original.Id, summary.OriginToolStepId);
        Assert.Equal(parent.Id, summary.OriginMessageId);
        var restored = await SubagentChatState.ReadAsync(parent with { ToolSteps = [receipt] }, child.AgentId, environment.Directory);
        Assert.NotNull(restored);
        Assert.Equal(child.TranscriptRevision, restored.TranscriptRevision);
        Assert.Equal(child.AssistantMessage.Content, restored.AssistantMessage.Content);
        Assert.Equal(76, restored.AssistantMessage.ToolSteps!.Count);
        var display = restored.ForDisplay();
        Assert.Equal(64, display.AssistantMessage.ToolSteps!.Count);
        Assert.Equal(76, display.ToolStepWindows![restored.AssistantMessage.Id].TotalSteps);
        Assert.True(display.ToolStepWindows[restored.AssistantMessage.Id].HasMore);
        Assert.Equal(original.OutputJson, (await snapshots.GetToolStepAsync(session.Id, parent.Id, original.Id))!.OutputJson);
        var modelHistory = await chats.ListContextMessagesAsync(session.Id);
        Assert.Equal(parent.Content, Assert.Single(modelHistory).Content);
        Assert.Empty(modelHistory[0].ToolSteps!);
    }

    [Fact]
    public async Task HistoricalResultOpensThroughTheSameActivityAndDetailRoutesWithoutImportingAnotherTranscript()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Historical research");
        var parent = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Bestehender Verlauf", MessageStatus.Completed);
        var original = new AssistantToolStep("old-research", "web.deepResearch", "completed",
            InputJson: "{\"task\":\"Alte Quellenfrage\"}", OutputJson: JsonSerializer.Serialize(new { status = "completed", result = Result() }, Json));
        await chats.SaveToolStepDeltaAsync(parent.Id, original);
        await environment.Get<ISettingsStore>().SaveAsync(new AppSettings { ActiveSessionId = session.Id, SelectedChatMode = ChatMode.General });
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        var coordinator = new AssistantCoordinator(chats, environment.Get<IDocumentIngestor>(), environment.Get<IContextAssembler>(),
            environment.Get<IPromptTriggerRepository>(), environment.Get<IAssistantAttachmentRepository>(), environment.Get<IChatArtifactRepository>(),
            environment.Get<IConversationSnapshotRepository>(), null, settings, recent);
        var agent = SubagentChatState.DeepResearchAgentId(parent.Id, original.Id);
        JsonElement loaded = default;
        await coordinator.HandleAsync(new(AssistantProtocol.ProtocolVersion, "subagent.load", "old-research",
            JsonSerializer.SerializeToElement(new { sessionId = session.Id, parentMessageId = parent.Id, agentId = agent })), (type, data, _) =>
            { Assert.Equal("subagent.snapshot", type); loaded = JsonSerializer.SerializeToElement(data, JsonSerializerOptions.Web); return Task.CompletedTask; });
        Assert.Equal("deepResearch", loaded.GetProperty("kind").GetString());
        Assert.False(loaded.GetProperty("canStop").GetBoolean());
        Assert.False(loaded.GetProperty("canResume").GetBoolean());
        var answer = loaded.GetProperty("messages").EnumerateArray().Last();
        Assert.Contains("Der Befund ist eingeschränkt bestätigt.", answer.GetProperty("content").GetString());
        var resultStep = Assert.Single(answer.GetProperty("toolSteps").EnumerateArray());
        JsonElement expanded = default;
        await coordinator.HandleAsync(new(AssistantProtocol.ProtocolVersion, "tool.details", "old-result",
            JsonSerializer.SerializeToElement(new { sessionId = session.Id, parentMessageId = parent.Id, agentId = agent,
                messageId = answer.GetProperty("id").GetString(), stepId = resultStep.GetProperty("id").GetString() })), (type, data, _) =>
            { Assert.Equal("tool.details", type); expanded = JsonSerializer.SerializeToElement(data, JsonSerializerOptions.Web); return Task.CompletedTask; });
        Assert.Contains("https://example.org/paper", expanded.GetProperty("toolStep").GetProperty("outputJson").GetString());
        Assert.Single((await chats.GetMessageAsync(parent.Id))!.ToolSteps!);
    }

    [Fact]
    public async Task TwoGeneralContinuationsInTheSameParentMessageKeepIndependentResearchHistories()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Continued general research");
        var parent = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Gemeinsame fortgesetzte Nachricht", MessageStatus.Completed);
        var activities = new List<SubagentChatState>();
        foreach (var branch in new[] { "first", "continued" })
        {
            // The gateway names its General operation with the real server run,
            // like Coding's server-tool IDs; an operation retry keeps this same ID.
            var operation = "server-general-" + branch;
            var run = "run-general-" + branch;
            var child = SubagentChatState.CreateDeepResearch(parent, run, operation, branch, At);
            var started = new DeepResearchProgressEvent(operation, run, 1, "operation", "running", "Deep Research");
            child = child.ApplyDeepResearchProgress(Event(1, run, started), started);
            child = child.CompleteDeepResearch(new(2, run, RunEventTypes.DeepResearchProgress, At.AddSeconds(2), default),
                JsonSerializer.SerializeToElement(new { message = "Ergebnis " + branch }, Json), "completed");
            await chats.SaveToolStepDeltaAsync(parent.Id, new(operation, "web.deepResearch", "completed",
                InputJson: "{\"activityProjection\":true}", OutputJson: JsonSerializer.Serialize(new { message = "Ergebnis " + branch }, Json)));
            await chats.SaveToolStepDeltaAsync(parent.Id, new(child.ReceiptId, "subagent", "completed", OutputJson: await child.PersistAsync(environment.Directory)));
            activities.Add(child);
        }
        Assert.NotEqual(activities[0].AgentId, activities[1].AgentId);
        var durable = (await chats.GetMessageAsync(parent.Id))!;
        var restored = SubagentChatState.Read([durable], environment.Directory);
        Assert.Equal(2, restored.Count);
        Assert.Equal("Ergebnis first", restored.Single(child => child.ParentRunId == "run-general-first").AssistantMessage.Content);
        Assert.Equal("Ergebnis continued", restored.Single(child => child.ParentRunId == "run-general-continued").AssistantMessage.Content);
        Assert.Same(activities[0], activities[0].ApplyDeepResearchProgress(Event(10, "run-general-continued",
            new("server-general-continued", "run-general-continued", 10, "search", "running", "Andere Recherche")),
            new("server-general-continued", "run-general-continued", 10, "search", "running", "Andere Recherche")));
        Assert.Equal(2, durable.ToolSteps!.Count(step => step.Tool == "web.deepResearch"));
    }

    private static ChatMessage Parent() => new(Guid.NewGuid(), Guid.NewGuid(), ChatRole.Assistant, "Hauptantwort", MessageStatus.Streaming, At, At);
    private static RunEvent Event(long id, string parentRun, DeepResearchProgressEvent progress) =>
        new(id, parentRun, RunEventTypes.DeepResearchProgress, At.AddSeconds(id), JsonSerializer.SerializeToElement(progress, Json));
    private static JsonElement Result() => JsonSerializer.SerializeToElement(new
    {
        summary = "Zusammenfassung der Originalquellen.",
        assessmentReason = "Die Quelle deckt nur den untersuchten Fall ab.",
        message = "Recherche mit offenen Fragen abgeschlossen.",
        findings = new[] { new { claim = "Der Befund ist eingeschränkt bestätigt.", sourceId = "source-1" } },
        uncertainties = ReportUncertainties,
        sources = new[] { new { id = "source-1", title = "Originalquelle", url = "https://example.org/paper" } },
    }, Json);

    private sealed class ActivityGatewayHandler(bool nested, Guid session, List<string> requests) : HttpMessageHandler
    {
        private const string ParentRun = "run-research-view-fixture";
        private const string ChildRun = "run-real-child-fixture";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add(path);
            if (path == "/v1/models/status") return Task.FromResult(Response(new ModelStatusSnapshot(true, "fixture",
                [new("fixture/research", "general", true, true, "loaded", 32768)], At)));
            if (path == "/v1/capabilities") return Task.FromResult(Response(new CapabilitySnapshot(MissumAiProtocol.Version, "fixture", [],
                ["web.search", "web.fetch", "web.deepResearch"], [], new Dictionary<string, long>(), [], true, MissumAiProtocol.UploadChunkSize)));
            if (path == "/v1/runs" && request.Method == HttpMethod.Post)
                return Task.FromResult(Response(new RunAccepted(ParentRun, RunState.Running, At, "/v1/runs/" + ParentRun + "/events")));
            if (path == "/v1/runs/" + ParentRun + "/events")
            {
                var events = new List<RunEvent>();
                void Add(string type, object data) => events.Add(new(events.Count + 1, ParentRun, type, At.AddSeconds(events.Count + 1), JsonSerializer.SerializeToElement(data, Json)));
                long innerId = 0;
                void Inner(string type, object data)
                {
                    if (nested)
                    {
                        var inner = new RunEvent(++innerId, ChildRun, type, At.AddSeconds(innerId), JsonSerializer.SerializeToElement(data, Json));
                        Add(RunEventTypes.SubagentEvent, new SubagentForwardedEvent("agent-real", ParentRun, ChildRun, inner));
                    }
                    else Add(type, data);
                }
                var ownerRun = nested ? ChildRun : ParentRun;
                var operation = nested ? "nested-research" : "server-general-research-view-fixture";
                if (nested) Add(RunEventTypes.SubagentStarted, new SubagentRunEvent(ParentRun, ChildRun, "agent-real", "Prüfe Quellen.", "fixture/research", RunState.Running, session.ToString("D")));
                Inner(RunEventTypes.ContextChanged, new ContextChangedEvent(1000, 32768, 0, false));
                if (nested) Inner(RunEventTypes.ServerToolStarted, new { tool = "web.deepResearch", callId = operation, arguments = new { task = "Prüfe Quellen." } });
                Inner(RunEventTypes.DeepResearchProgress, new DeepResearchProgressEvent(operation, ownerRun, 10, "operation", "running", "Deep Research", Detail: "Prüfe Quellen."));
                Inner(RunEventTypes.DeepResearchProgress, new DeepResearchProgressEvent(operation, ownerRun, 11, "model", "running", "Recherche planen",
                    StepId: "model-1", ModelProgress: new ModelGenerationEvent("tokenProgress", PromptTokens: 1000, GeneratedTokens: 32, CurrentTokens: 1032)));
                Inner(RunEventTypes.DeepResearchProgress, new DeepResearchProgressEvent(operation, ownerRun, 12, "fetch", "completed", "Originalquelle gelesen",
                    StepId: "fetch-1", Tool: "web.fetch", Url: "https://example.org/paper"));
                Inner(RunEventTypes.DeepResearchProgress, new DeepResearchProgressEvent(operation, ownerRun, 13, "operation", "completed", "Deep Research", Result: Result()));
                if (nested)
                {
                    Inner(RunEventTypes.ServerToolCompleted, new { tool = "web.deepResearch", callId = operation, success = true, result = Result() });
                    Add(RunEventTypes.SubagentCompleted, new SubagentRunEvent(ParentRun, ChildRun, "agent-real", "Prüfe Quellen.", "fixture/research", RunState.Completed, session.ToString("D")));
                }
                Add(RunEventTypes.TextDelta, new TextDeltaEvent("Eigenständige Hauptantwort."));
                Add(RunEventTypes.RunCompleted, new RunCompletedEvent(null, "fixture/research", 1000, 20));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    string.Join("", events.Select(item => "data: " + JsonSerializer.Serialize(item, Json) + "\n\n")), Encoding.UTF8, "text/event-stream") });
            }
            throw new InvalidOperationException("Unexpected activity fixture request: " + request.Method + " " + path);
        }

        private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json") };
    }
}
