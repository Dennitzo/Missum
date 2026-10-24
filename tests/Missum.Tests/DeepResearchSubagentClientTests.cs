using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class DeepResearchSubagentClientTests
{
    private static readonly JsonSerializerOptions Json = MissumAiProtocol.CreateJsonOptions();
    private static readonly DateTimeOffset At = new(2026, 10, 17, 14, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task HistoricalResearchChildRemainsReloadableWithoutSubagentControlsOrPlanet(int gpuIndex)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var documents = environment.Get<IDocumentIngestor>();
        var session = await chats.CreateSessionAsync("Echte Recherchedelegation");
        await environment.Get<ISettingsStore>().SaveAsync(new AppSettings { ActiveSessionId = session.Id,
            SelectedModel = "fixture/research", MissumAiServerUrl = "http://127.0.0.1:65000" });
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance,
            () => new GatewayHandler(session.Id, gpuIndex));
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
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await coordinator.HandleAsync(new(AssistantProtocol.ProtocolVersion, "chat.send", "real-research",
            JsonSerializer.SerializeToElement(new { sessionId = session.Id, prompt = "Recherchiere die Originalquelle.", deepResearch = true })),
            (type, data, _) => { if (type == "subagent.snapshot") observations.Add(JsonSerializer.SerializeToElement(data, JsonSerializerOptions.Web)); return Task.CompletedTask; }, deadline.Token);
        var parent = (await chats.ListMessagesAsync(session.Id))[^1];
        Assert.Equal(MessageStatus.Completed, parent.Status);
        var child = Assert.Single(SubagentChatState.Read([parent], environment.Directory));
        Assert.Equal("agent-research-real", child.AgentId);
        Assert.Equal("run-research-real", child.RunId);
        Assert.False(child.IsLegacyDeepResearch);
        Assert.Equal("deepResearch", child.Kind);
        Assert.Equal(gpuIndex, child.GpuIndex);
        Assert.Equal("research-operation", child.OriginToolStepId);
        Assert.Equal(parent.Id, child.OriginMessageId);
        Assert.Equal("Originalquelle und Einschränkungen", child.Title);
        Assert.Equal("completed", child.Status);
        Assert.True(child.ResultDelivered);
        Assert.Equal("Beleg aus der Originalquelle mit offener Einschränkung.", child.AssistantMessage.Content);
        Assert.Null(child.CompletionReceipt(0, At));
        Assert.NotEmpty(observations);
        Assert.All(observations, snapshot =>
        {
            Assert.Equal(child.AgentId, snapshot.GetProperty("agentId").GetString());
            Assert.False(snapshot.GetProperty("canStop").GetBoolean());
            Assert.False(snapshot.GetProperty("canResume").GetBoolean());
            Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("planetIndex").ValueKind);
            Assert.Equal(gpuIndex, snapshot.GetProperty("gpuIndex").GetInt32());
        });
        var liveThinking = observations.Where(snapshot => snapshot.GetProperty("status").GetString() == "running")
            .SelectMany(snapshot => snapshot.GetProperty("messages").EnumerateArray())
            .SelectMany(message => message.GetProperty("toolSteps").EnumerateArray())
            .Where(step => step.GetProperty("tool").GetString() == "assistant.reasoning").ToArray();
        Assert.NotEmpty(liveThinking);
        Assert.All(liveThinking, step =>
        {
            Assert.Equal(JsonValueKind.Null, step.GetProperty("detail").ValueKind);
            Assert.True(step.GetProperty("detailsDeferred").GetBoolean());
        });
        Assert.Contains(child.AssistantMessage.ToolSteps!, step => step.Tool == "assistant.reasoning"
            && step.Detail == "Ich vergleiche die Originalquelle. Eine Einschränkung bleibt offen.");
        JsonElement refreshed = default;
        await coordinator.HandleAsync(new(AssistantProtocol.ProtocolVersion, "conversation.refresh", "reload-research",
            JsonSerializer.SerializeToElement(new { sessionId = session.Id })),
            (type, data, _) => { if (type == "conversation.snapshot") refreshed = JsonSerializer.SerializeToElement(data, JsonSerializerOptions.Web); return Task.CompletedTask; }, deadline.Token);
        var reloaded = Assert.Single(refreshed.GetProperty("subagents").EnumerateArray());
        Assert.Equal(child.AgentId, reloaded.GetProperty("agentId").GetString());
        Assert.Equal("research-operation", reloaded.GetProperty("originToolStepId").GetString());
        Assert.False(reloaded.GetProperty("canResume").GetBoolean());
        Assert.Equal(JsonValueKind.Null, reloaded.GetProperty("planetIndex").ValueKind);
    }

    [Fact]
    public void TokenTicksDoNotRebuildRealResearchTranscriptButToolAndTextEventsDo()
    {
        var state = SubagentChatState.Create(new("parent", "child", "agent", "Quellen vergleichen.", "model", RunState.Running,
            Specialization: "deepResearch", OriginToolStepId: "operation", GpuIndex: 0), Guid.NewGuid(), At);
        var tick = Event(1, RunEventTypes.ModelGeneration, new ModelGenerationEvent("tokenProgress", GeneratedTokens: 10));
        var progress = state.Apply(tick);
        Assert.Same(state.AssistantMessage, progress.AssistantMessage);
        Assert.Equal(state.TranscriptRevision, progress.TranscriptRevision);
        var text = progress.Apply(Event(2, RunEventTypes.TextDelta, new TextDeltaEvent("Originalquelle.")));
        Assert.True(text.TranscriptRevision > progress.TranscriptRevision);
        Assert.Same(text, text.Apply(Event(2, RunEventTypes.TextDelta, new TextDeltaEvent("Duplikat"))));
        var tool = text.Apply(Event(3, RunEventTypes.ServerToolStarted, new { tool = "web.fetch", callId = "fetch", target = "https://example.org" }));
        Assert.True(tool.TranscriptRevision > text.TranscriptRevision);
        Assert.Single(tool.AssistantMessage.ToolSteps!);
    }

    private static RunEvent Event(long id, string type, object data) =>
        new(id, "child", type, At.AddSeconds(id), JsonSerializer.SerializeToElement(data, Json));

    private sealed class GatewayHandler(Guid session, int gpuIndex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/models/status") return Task.FromResult(Response(new ModelStatusSnapshot(true, "fixture",
                [new("fixture/research", "general", true, true, "loaded", 32768)], At)));
            if (path == "/v1/capabilities") return Task.FromResult(Response(new CapabilitySnapshot(MissumAiProtocol.Version, "fixture", [],
                ["web.search", "web.fetch", "web.deepResearch"], [], new Dictionary<string, long>(), [], true, MissumAiProtocol.UploadChunkSize)));
            if (path == "/v1/runs" && request.Method == HttpMethod.Post)
                return Task.FromResult(Response(new RunAccepted("parent", RunState.Running, At, "/v1/runs/parent/events")));
            if (path == "/v1/runs/parent/events")
            {
                var events = new List<RunEvent>();
                void Add(string type, object data) => events.Add(new(events.Count + 1, "parent", type, At.AddSeconds(events.Count + 1), JsonSerializer.SerializeToElement(data, Json)));
                var info = new SubagentRunEvent("parent", "run-research-real", "agent-research-real", "Originalquelle und Einschränkungen.", "fixture/research", RunState.Running,
                    session.ToString("D"), Specialization: "deepResearch", OriginToolStepId: "research-operation", GpuIndex: gpuIndex);
                Add(RunEventTypes.ServerToolStarted, new { tool = "web.deepResearch", callId = "research-operation", arguments = new { task = info.Task } });
                Add(RunEventTypes.SubagentStarted, info);
                Add(RunEventTypes.SubagentEvent, new SubagentForwardedEvent(info.AgentId, "parent", info.RunId,
                    new(100, info.RunId, RunEventTypes.ReasoningDelta, At.AddSeconds(3), JsonSerializer.SerializeToElement(
                        new ReasoningDeltaEvent("Ich vergleiche die Originalquelle.", 1), Json))));
                Add(RunEventTypes.SubagentEvent, new SubagentForwardedEvent(info.AgentId, "parent", info.RunId,
                    new(101, info.RunId, RunEventTypes.ModelGeneration, At.AddSeconds(4), JsonSerializer.SerializeToElement(
                        new ModelGenerationEvent("tokenProgress", GeneratedTokens: 12), Json))));
                Add(RunEventTypes.SubagentEvent, new SubagentForwardedEvent(info.AgentId, "parent", info.RunId,
                    new(102, info.RunId, RunEventTypes.ReasoningDelta, At.AddSeconds(5), JsonSerializer.SerializeToElement(
                        new ReasoningDeltaEvent(" Eine Einschränkung bleibt offen.", 1, State: "completed"), Json))));
                Add(RunEventTypes.SubagentEvent, new SubagentForwardedEvent(info.AgentId, "parent", info.RunId,
                    new(103, info.RunId, RunEventTypes.TextDelta, At.AddSeconds(6), JsonSerializer.SerializeToElement(new TextDeltaEvent("Beleg aus der Originalquelle mit offener Einschränkung."), Json))));
                Add(RunEventTypes.SubagentEvent, new SubagentForwardedEvent(info.AgentId, "parent", info.RunId,
                    new(104, info.RunId, RunEventTypes.RunCompleted, At.AddSeconds(7), JsonSerializer.SerializeToElement(new RunCompletedEvent(null, info.ModelId, 100, 20), Json))));
                Add(RunEventTypes.SubagentCompleted, info with { State = RunState.Completed });
                Add("subagent.resultConsumed", new { parentRunId = "parent", runId = info.RunId, agentId = info.AgentId, state = "completed" });
                Add(RunEventTypes.ServerToolCompleted, new { tool = "web.deepResearch", callId = "research-operation", success = true, result = new { message = "Beleg übernommen." } });
                Add(RunEventTypes.RunCompleted, new RunCompletedEvent(null, info.ModelId, 100, 20));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    string.Join("", events.Select(item => "data: " + JsonSerializer.Serialize(item, Json) + "\n\n")), Encoding.UTF8, "text/event-stream") });
            }
            throw new InvalidOperationException("Unexpected research child fixture request: " + request.Method + " " + path);
        }
        private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json") };
    }
}
