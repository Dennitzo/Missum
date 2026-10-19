using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class AssistantStreamingProjectionTests
{
    [Theory]
    [InlineData(MissumAiAssistantUpdateKind.Delta)]
    [InlineData(MissumAiAssistantUpdateKind.Status)]
    public async Task LiveSourceMetadataArrivesBeforeCommitWithoutLoadingToolBodies(MissumAiAssistantUpdateKind kind)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var chats = environment.Get<IChatRepository>();
        var snapshots = environment.Get<IConversationSnapshotRepository>();
        var session = await chats.CreateSessionAsync("First live source", ChatMode.ClaudeScience);
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "", MessageStatus.Streaming);
        var step = new AssistantToolStep("first-source", "web.search", "completed", new string('x', 100_000),
            InputJson: "{\"query\":\"Originalquelle\"}", OutputJson: JsonSerializer.Serialize(new
            {
                nested = JsonSerializer.Serialize(new { results = new[] { new { title = "Originalbeleg", url = "https://example.org/paper",
                    thumbnailUrl = "https://example.org/thumb", iconUrl = "https://example.org/icon" } } }), body = new string('x', 100_000),
            }), UpdatedAt: DateTimeOffset.UtcNow);
        var coordinator = CreateCoordinator(environment, settings, snapshots, environment.Get<IChatArtifactRepository>());
        JsonElement displayed = default;
        await coordinator.EmitMissumAiUpdateAsync(new(kind, message with { ToolSteps = [step] }, ToolStep: step), async (type, payload, _) =>
        {
            Assert.Equal(kind == MissumAiAssistantUpdateKind.Delta ? "chat.delta" : "status.changed", type);
            displayed = JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web);
            Assert.Null(await snapshots.GetToolStepAsync(session.Id, message.Id, step.Id));
            Assert.Empty(await snapshots.GetSourcesAsync(session.Id));
        }, "source-before-commit");
        var source = Assert.Single(displayed.GetProperty("sourceUpdates").EnumerateArray());
        Assert.Equal(step.Id, source.GetProperty("id").GetString());
        Assert.Equal("Originalbeleg", source.GetProperty("title").GetString());
        Assert.Equal("https://example.org/paper", source.GetProperty("url").GetString());
        Assert.Equal(JsonValueKind.Null, displayed.GetProperty("toolStep").GetProperty("inputJson").ValueKind);
        Assert.Equal(JsonValueKind.Null, displayed.GetProperty("toolStep").GetProperty("outputJson").ValueKind);
        Assert.True(displayed.GetRawText().Length < 4000);
        Assert.Same(AssistantCoordinator.LiveSourcesFor(step), AssistantCoordinator.LiveSourcesFor(step));
        Assert.Null(AssistantCoordinator.LiveSourcesFor(new("not-source", "coding.read", "completed", OutputJson: step.OutputJson)));
    }

    [Fact]
    public async Task ChildSourcesSurviveThinDisplayWindowWithoutOpeningItsTranscript()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Child sources", ChatMode.ClaudeScience);
        var parent = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "", MessageStatus.Streaming);
        var at = DateTimeOffset.UtcNow;
        var child = SubagentChatState.Create(new("parent", "child", "source-agent", "Originalbeleg lesen", "model",
            Missum.Ai.Contracts.RunState.Running), session.Id, at);
        var sourceStep = new AssistantToolStep("early-child-source", "web.fetch", "completed",
            OutputJson: "{\"title\":\"Früher Beleg\",\"url\":\"https://example.org/child\"}", UpdatedAt: at);
        child = child with { AssistantMessage = child.AssistantMessage with { ToolSteps = new[] { sourceStep }
            .Concat(Enumerable.Range(0, 80).Select(index => new AssistantToolStep("later-" + index, "coding.read", "completed"))).ToArray() } };
        var coordinator = CreateCoordinator(environment, settings, environment.Get<IConversationSnapshotRepository>(), environment.Get<IChatArtifactRepository>());
        JsonElement snapshot = default;
        await coordinator.EmitMissumAiUpdateAsync(new(MissumAiAssistantUpdateKind.SubagentChanged, parent, Subagent: child),
            (type, payload, _) =>
            {
                if (type == "subagent.snapshot") snapshot = JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web);
                return Task.CompletedTask;
            }, "child-source");
        Assert.Equal("https://example.org/child", Assert.Single(snapshot.GetProperty("sourceUpdates").EnumerateArray()).GetProperty("url").GetString());
        Assert.DoesNotContain(snapshot.GetProperty("messages")[1].GetProperty("toolSteps").EnumerateArray(),
            step => step.GetProperty("id").GetString() == sourceStep.Id);
        Assert.Empty(await environment.Get<IConversationSnapshotRepository>().GetSourcesAsync(session.Id));
    }

    [Fact]
    public async Task LiveSteeringPromptIsInlineConversationTextBeforeItsDurableCommit()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var chats = environment.Get<IChatRepository>();
        var snapshots = environment.Get<IConversationSnapshotRepository>();
        var session = await chats.CreateSessionAsync("Visible steering", ChatMode.ClaudeScience);
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Bisherige Antwort", MessageStatus.Streaming);
        var step = new AssistantToolStep("steering-input", "assistant.steering", "running",
            "Bitte zuerst den quantitativen Vergleich prüfen.", ContentOffset: message.Content.Length,
            InputJson: "{\"inputId\":\"input\",\"sequence\":1}");
        var coordinator = CreateCoordinator(environment, settings, snapshots, environment.Get<IChatArtifactRepository>());
        JsonElement displayed = default;
        await coordinator.EmitMissumAiUpdateAsync(new(MissumAiAssistantUpdateKind.Delta, message with { ToolSteps = [step] }, ToolStep: step),
            (type, payload, _) =>
            {
                if (type == "chat.delta") displayed = JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web).GetProperty("toolStep");
                return Task.CompletedTask;
            }, "steer-live");
        Assert.Equal(step.Detail, displayed.GetProperty("detail").GetString());
        Assert.False(displayed.TryGetProperty("detailsDeferred", out var deferred) && deferred.GetBoolean());
        Assert.Equal(JsonValueKind.Null, displayed.GetProperty("inputJson").ValueKind);
        Assert.Null(await snapshots.GetToolStepAsync(session.Id, message.Id, step.Id));
    }

    [Fact]
    public async Task ExpandedLiveStepCanBeReadReentrantlyBeforeItsWriteBehindCommit()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var chats = environment.Get<IChatRepository>();
        var snapshots = environment.Get<IConversationSnapshotRepository>();
        var session = await chats.CreateSessionAsync("Live details", ChatMode.ClaudeScience);
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "", MessageStatus.Streaming);
        var now = DateTimeOffset.UtcNow;
        var runs = environment.Get<IMissumAiRunRepository>();
        var run = await runs.CreateAsync(new(Guid.NewGuid(), session.Id, message.Id, null, "live-details", "run-live-details", 0, "running", null, null, now, now));
        await using var stream = new ConversationStreamBuffer(chats, runs, message, run);
        var step = new AssistantToolStep("new-read", "coding.read", "running", "Vollständiger Live-Inhalt", InputJson: "{\"path\":\"sample.py\"}");
        await stream.UpsertAsync(step);
        message = message with { ToolSteps = stream.ToolSteps };
        var coordinator = CreateCoordinator(environment, settings, snapshots, environment.Get<IChatArtifactRepository>());
        JsonElement loaded = default;
        await coordinator.EmitMissumAiUpdateAsync(new(MissumAiAssistantUpdateKind.Status, message, ToolStep: step), async (type, statusPayload, _) =>
        {
            Assert.Equal("status.changed", type);
            var displayed = JsonSerializer.SerializeToElement(statusPayload, JsonSerializerOptions.Web).GetProperty("toolStep");
            Assert.True(displayed.GetProperty("detailsDeferred").GetBoolean());
            Assert.Equal(JsonValueKind.Null, displayed.GetProperty("inputJson").ValueKind);
            Assert.Equal("sample.py", displayed.GetProperty("displayHeader").GetProperty("summary").GetString());
            Assert.Null(await snapshots.GetToolStepAsync(session.Id, message.Id, step.Id));
            await coordinator.HandleAsync(new(AssistantProtocol.ProtocolVersion, "tool.details", "live-open",
                JsonSerializer.SerializeToElement(new { sessionId = session.Id, messageId = message.Id, stepId = step.Id })),
                (detailType, payload, _) =>
                {
                    Assert.Equal("tool.details", detailType);
                    loaded = JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web);
                    return Task.CompletedTask;
                });
        }, "live-status");
        Assert.Equal(step.Detail, loaded.GetProperty("toolStep").GetProperty("detail").GetString());
        Assert.Equal(step.InputJson, loaded.GetProperty("toolStep").GetProperty("inputJson").GetString());
        Assert.Equal("sample.py", loaded.GetProperty("toolStep").GetProperty("displayHeader").GetProperty("summary").GetString());
        Assert.Equal(0, (await runs.GetAsync(run.Id))!.LastEventId);
        Assert.Null(await snapshots.GetToolStepAsync(session.Id, message.Id, step.Id));
        await stream.AcknowledgeAsync(run.ServerRunId!, 1, "running", null, force: true);
        Assert.Equal(step, await snapshots.GetToolStepAsync(session.Id, message.Id, step.Id));
    }

    [Fact]
    public async Task LiveStepLookupIsSessionScopedAndTerminalUpdatesReleaseItsDetails()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Scoped details");
        var other = await chats.CreateSessionAsync("Other session");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "", MessageStatus.Streaming);
        var step = new AssistantToolStep("read", "coding.read", "running", "Live source");
        var coordinator = CreateCoordinator(environment, settings, environment.Get<IConversationSnapshotRepository>(), environment.Get<IChatArtifactRepository>());
        await coordinator.EmitMissumAiUpdateAsync(new(MissumAiAssistantUpdateKind.Status, message,
            GenerationState: "tokenProgress", GeneratedTokens: 1), Ignore, "initial-progress");
        await coordinator.EmitMissumAiUpdateAsync(new(MissumAiAssistantUpdateKind.Status, message, ToolStep: step), Ignore, "status");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(other.Id));
        var persisted = step with { Status = "completed", Detail = "Durable final source" };
        await chats.SaveToolStepAsync(message.Id, persisted);
        await chats.UpdateMessageAsync(message.Id, "Finished", MessageStatus.Completed);
        message = (await chats.GetMessageAsync(message.Id))!;
        await coordinator.EmitMissumAiUpdateAsync(new(MissumAiAssistantUpdateKind.Completed, message), Ignore, "complete");
        Assert.Equal(persisted.Detail, (await Read(session.Id)).GetProperty("toolStep").GetProperty("detail").GetString());

        async Task<JsonElement> Read(Guid sessionId)
        {
            JsonElement result = default;
            await coordinator.HandleAsync(new(AssistantProtocol.ProtocolVersion, "tool.details", "scoped-read",
                JsonSerializer.SerializeToElement(new { sessionId, messageId = message.Id, stepId = step.Id })), (_, payload, _) =>
                { result = JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web); return Task.CompletedTask; });
            return result;
        }
    }

    [Fact]
    public async Task FreshLiveSubagentBindsItsParentAndLoadsDetailsBeforeTheParentReceiptIsCommitted()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Live child", ChatMode.ClaudeScience);
        var parent = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "", MessageStatus.Streaming);
        var child = SubagentChatState.Create(new("run-parent", "run-child", "child-1", "Datei prüfen", "model", Missum.Ai.Contracts.RunState.Running), session.Id, DateTimeOffset.UtcNow);
        var step = new AssistantToolStep("child-read", "coding.read", "completed", "print('child')", AgentId: child.AgentId);
        child = child with { AssistantMessage = child.AssistantMessage with { ToolSteps = [step] } };
        Assert.Null(child.ParentMessageId);
        var receipt = new AssistantToolStep(child.ReceiptId, "subagent", "running", child.UserMessage.Content,
            OutputJson: await child.PersistAsync(settings.DataDirectory));
        parent = parent with { ToolSteps = [receipt] };
        var snapshots = environment.Get<IConversationSnapshotRepository>();
        var coordinator = CreateCoordinator(environment, settings, snapshots, environment.Get<IChatArtifactRepository>());
        JsonElement loaded = default;
        await coordinator.EmitMissumAiUpdateAsync(new(MissumAiAssistantUpdateKind.SubagentChanged, parent, ToolStep: receipt, Subagent: child), async (type, payload, _) =>
        {
            if (type != "subagent.snapshot") return;
            var projected = JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web);
            Assert.Equal(parent.Id, projected.GetProperty("parentMessageId").GetGuid());
            Assert.Null(await snapshots.GetSubagentReceiptAsync(session.Id, parent.Id, child.AgentId));
            await coordinator.HandleAsync(new(AssistantProtocol.ProtocolVersion, "tool.details", "child-open",
                JsonSerializer.SerializeToElement(new { sessionId = session.Id, messageId = child.AssistantMessage.Id,
                    parentMessageId = projected.GetProperty("parentMessageId").GetGuid(), agentId = child.AgentId, stepId = step.Id })), (_, detail, _) =>
                { loaded = JsonSerializer.SerializeToElement(detail, JsonSerializerOptions.Web); return Task.CompletedTask; });
        }, "child-status");
        Assert.Equal(step.Detail, loaded.GetProperty("toolStep").GetProperty("detail").GetString());
        Assert.Equal(child.AssistantMessage.Id, loaded.GetProperty("messageId").GetGuid());
        Assert.Null(await snapshots.GetSubagentReceiptAsync(session.Id, parent.Id, child.AgentId));
    }

    private static Task Ignore(string type, object payload, string? requestId) => Task.CompletedTask;

    [Theory]
    [InlineData("subagent", false)]
    [InlineData("deepResearch", false)]
    [InlineData("deepResearch", true)]
    public async Task LiveSubagentSnapshotsCarryReasoningBeforeCompletionAndKeepTokenTicksStable(string kind, bool externalTranscript)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Live reasoning", ChatMode.ClaudeScience);
        var parent = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "", MessageStatus.Streaming);
        var child = SubagentChatState.Create(new("parent", "child", "agent", "Quellen prüfen", "model",
            Missum.Ai.Contracts.RunState.Running, Specialization: kind), session.Id, DateTimeOffset.UtcNow);
        var coordinator = CreateCoordinator(environment, settings, environment.Get<IConversationSnapshotRepository>(), environment.Get<IChatArtifactRepository>());
        var firstText = (externalTranscript ? new string('r', 40_000) + " " : "") + "Die Originalquelle wird geprüft.";
        child = child.Apply(Event(1, Missum.Ai.Contracts.RunEventTypes.ReasoningDelta,
            new Missum.Ai.Contracts.ReasoningDeltaEvent(firstText, 1)));
        var first = await Publish();
        Assert.Equal(firstText, Reasoning(first).GetProperty("detail").GetString());
        Assert.False(Reasoning(first).TryGetProperty("detailsDeferred", out var firstDeferred) && firstDeferred.GetBoolean());
        Assert.Equal("streaming", first.GetProperty("messages")[1].GetProperty("status").GetString());

        child = child.Apply(Event(2, Missum.Ai.Contracts.RunEventTypes.ModelGeneration,
            new Missum.Ai.Contracts.ModelGenerationEvent("tokenProgress", GeneratedTokens: 12)));
        var progress = await Publish();
        Assert.Equal(first.GetProperty("transcriptRevision").GetInt64(), progress.GetProperty("transcriptRevision").GetInt64());
        Assert.Equal(firstText, Reasoning(progress).GetProperty("detail").GetString());

        child = child.Apply(Event(3, Missum.Ai.Contracts.RunEventTypes.ReasoningDelta,
            new Missum.Ai.Contracts.ReasoningDeltaEvent(" Eine Einschränkung wird verglichen.", 1)));
        var appended = await Publish();
        Assert.Equal(firstText + " Eine Einschränkung wird verglichen.", Reasoning(appended).GetProperty("detail").GetString());
        Assert.True(appended.GetProperty("transcriptRevision").GetInt64() > first.GetProperty("transcriptRevision").GetInt64());

        child = child.Apply(Event(4, Missum.Ai.Contracts.RunEventTypes.RunCompleted, new { }));
        var complete = await Publish();
        Assert.Equal(JsonValueKind.Null, Reasoning(complete).GetProperty("detail").ValueKind);
        Assert.True(Reasoning(complete).GetProperty("detailsDeferred").GetBoolean());

        Missum.Ai.Contracts.RunEvent Event(long id, string type, object payload) => new(id, child.RunId, type,
            DateTimeOffset.UtcNow.AddMilliseconds(id), JsonSerializer.SerializeToElement(payload, Missum.Ai.Contracts.MissumAiProtocol.CreateJsonOptions()));
        static JsonElement Reasoning(JsonElement snapshot) => snapshot.GetProperty("messages")[1].GetProperty("toolSteps")[0];
        async Task<JsonElement> Publish()
        {
            child = child with { ProjectionRevision = child.ProjectionRevision + 1 };
            var receipt = new AssistantToolStep(child.ReceiptId, "subagent", child.Status,
                OutputJson: await child.PersistAsync(settings.DataDirectory));
            parent = parent with { ToolSteps = [receipt] };
            JsonElement result = default;
            await coordinator.EmitMissumAiUpdateAsync(new(MissumAiAssistantUpdateKind.SubagentChanged, parent,
                ToolStep: receipt, Subagent: child), (type, payload, _) =>
            {
                if (type == "subagent.snapshot") result = JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web);
                return Task.CompletedTask;
            }, "child-reasoning");
            Assert.False(result.GetProperty("transcriptDeferred").GetBoolean());
            Assert.Equal(parent.Id, result.GetProperty("parentMessageId").GetGuid());
            return result;
        }
    }

    [Theory]
    [InlineData("chat.send", "agentId", "deep-research:activity")]
    [InlineData("chat.resume", "runId", "deep-research-run:activity")]
    [InlineData("chat.steer", "expectedRunId", "deep-research-run:activity")]
    [InlineData("chat.cancel", "agentId", "deep-research:activity")]
    public async Task ResearchActivityCannotExecuteCommandsAsAnIndependentServerRun(string command, string field, string identity)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var coordinator = CreateCoordinator(environment, settings, environment.Get<IConversationSnapshotRepository>(), environment.Get<IChatArtifactRepository>());
        var payload = JsonSerializer.SerializeToElement(new Dictionary<string, string> { [field] = identity });
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.HandleAsync(
            new(AssistantProtocol.ProtocolVersion, command, "readonly-research", payload), Ignore));
        Assert.Contains("übergeordneten Chat", failure.Message);
    }

    [Fact]
    public async Task StreamingFragmentsPublishOneLiveProjectionWithoutReloadingHistoryOrArtifacts()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Streaming", ChatMode.ClaudeScience);
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "", MessageStatus.Streaming);
        var snapshots = new CountingSnapshots(environment.Get<IConversationSnapshotRepository>());
        var artifacts = new CountingArtifacts(environment.Get<IChatArtifactRepository>());
        var coordinator = CreateCoordinator(environment, settings, snapshots, artifacts);
        var events = new List<Projection>();
        var step = new AssistantToolStep("reasoning", "assistant.reasoning", "completed", "Geprüft.");

        for (var fragment = 1; fragment <= 64; fragment++)
        {
            message = message with { Content = new string('x', fragment), UpdatedAt = message.CreatedAt.AddMilliseconds(fragment), ToolSteps = [step] };
            await coordinator.EmitMissumAiUpdateAsync(new(MissumAiAssistantUpdateKind.Delta, message), Capture, "live-request");
        }

        Assert.Equal(64, events.Count);
        Assert.All(events, item => { Assert.Equal("chat.delta", item.Type); Assert.Equal("live-request", item.RequestId); });
        Assert.Equal(0, snapshots.Reads);
        Assert.Equal(0, artifacts.MessageReads);
        var latest = events[^1].Payload;
        Assert.Equal(message.Id, latest.GetProperty("messageId").GetGuid());
        Assert.Equal(session.Id, latest.GetProperty("sessionId").GetGuid());
        Assert.Equal(message.Content, string.Concat(events.Select(item => item.Payload.GetProperty("contentAppend").GetString())));
        Assert.All(events, item => Assert.Equal("x", item.Payload.GetProperty("contentAppend").GetString()));
        Assert.False(latest.TryGetProperty("content", out _));
        Assert.Equal(message.UpdatedAt, latest.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.False(latest.TryGetProperty("toolSteps", out _));
        Assert.Equal(JsonValueKind.Null, latest.GetProperty("toolStep").ValueKind);
        Assert.Single(events.Select(item => item.Payload.GetProperty("streamGeneration").GetString()).Distinct());
        Assert.Equal(Enumerable.Range(1, 64).Select(value => (long)value), events.Select(item => item.Payload.GetProperty("revision").GetInt64()));

        Task Capture(string type, object payload, string? requestId)
        {
            events.Add(new(type, JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web), requestId));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task GenerationStatusDoesNotLoadConversationOrUnusedArtifacts()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Fortschritt", ChatMode.ClaudeScience);
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "", MessageStatus.Streaming);
        var snapshots = new CountingSnapshots(environment.Get<IConversationSnapshotRepository>());
        var artifacts = new CountingArtifacts(environment.Get<IChatArtifactRepository>());
        var coordinator = CreateCoordinator(environment, settings, snapshots, artifacts);
        var emissions = 0;

        await coordinator.EmitMissumAiUpdateAsync(new(MissumAiAssistantUpdateKind.Status, message,
            Status: "Modell generiert", GenerationState: "tokenProgress", GeneratedTokens: 42), (type, payload, _) =>
            {
                emissions++;
                Assert.Equal("status.changed", type);
                Assert.Equal(42, JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web).GetProperty("generatedTokens").GetInt32());
                return Task.CompletedTask;
            }, "status-request");

        Assert.Equal(1, emissions);
        Assert.Equal(0, snapshots.Reads);
        Assert.Equal(0, artifacts.MessageReads);
    }

    [Theory]
    [InlineData(MissumAiAssistantUpdateKind.Completed, MessageStatus.Completed, "chat.completed")]
    [InlineData(MissumAiAssistantUpdateKind.Cancelled, MessageStatus.Cancelled, "chat.cancelled")]
    [InlineData(MissumAiAssistantUpdateKind.Failed, MessageStatus.Failed, "chat.failed")]
    public async Task TerminalUpdatesStillPublishDurableRevisionAndArtifacts(
        MissumAiAssistantUpdateKind kind, MessageStatus status, string terminalEvent)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Ergebnis", ChatMode.ClaudeScience);
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Ergebnis", MessageStatus.Streaming);
        await chats.UpdateMessageAsync(message.Id, "Ergebnis", status);
        message = (await chats.GetMessageAsync(message.Id))!;
        var snapshots = new CountingSnapshots(environment.Get<IConversationSnapshotRepository>());
        var artifacts = new CountingArtifacts(environment.Get<IChatArtifactRepository>());
        var bytes = "Ausgabe"u8.ToArray();
        await using var content = new MemoryStream(bytes);
        var artifact = await artifacts.ImportAsync(message.Id, "result", "result.txt", "text/plain",
            Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, "test", null, null, content);
        var coordinator = CreateCoordinator(environment, settings, snapshots, artifacts);
        var events = new List<Projection>();

        await coordinator.EmitMissumAiUpdateAsync(new(kind, message), (type, payload, requestId) =>
        {
            events.Add(new(type, JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web), requestId));
            return Task.CompletedTask;
        }, "terminal-request");

        Assert.Equal(2, events.Count);
        Assert.Equal("conversation.messageCommitted", events[0].Type);
        Assert.Equal(terminalEvent, events[1].Type);
        Assert.Equal(0, snapshots.Reads);
        Assert.Equal(1, artifacts.MessageReads);
        Assert.True(events[0].Payload.GetProperty("conversationRevision").GetInt64() > 0);
        Assert.All(events, item =>
        {
            var projected = item.Payload.GetProperty("message");
            Assert.Equal(message.Id, projected.GetProperty("id").GetGuid());
            Assert.Equal(status.ToString().ToLowerInvariant(), projected.GetProperty("status").GetString());
            Assert.Equal(artifact.Id, Assert.Single(projected.GetProperty("artifacts").EnumerateArray()).GetProperty("id").GetGuid());
        });
    }

    private static AssistantCoordinator CreateCoordinator(TestEnvironment environment, SettingsCoordinator settings,
        IConversationSnapshotRepository snapshots, IChatArtifactRepository artifacts) => new(
        environment.Get<IChatRepository>(), environment.Get<IDocumentIngestor>(), environment.Get<IContextAssembler>(),
        environment.Get<IPromptTriggerRepository>(), environment.Get<IAssistantAttachmentRepository>(), artifacts, snapshots,
        null, settings, new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance));

    private sealed record Projection(string Type, JsonElement Payload, string? RequestId);

    private sealed class CountingSnapshots(IConversationSnapshotRepository inner) : IConversationSnapshotRepository
    {
        public Task<IReadOnlyList<ConversationSource>> GetSourcesAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            inner.GetSourcesAsync(sessionId, cancellationToken);
        public int Reads { get; private set; }
        public Task<ConversationPage?> GetPageAsync(Guid sessionId, int pageSize = 24, ConversationCursor? before = null,
            CancellationToken cancellationToken = default)
        {
            Reads++;
            return inner.GetPageAsync(sessionId, pageSize, before, cancellationToken);
        }
        public Task<AssistantToolStep?> GetToolStepAsync(Guid sessionId, Guid messageId, string stepId,
            CancellationToken cancellationToken = default) => inner.GetToolStepAsync(sessionId, messageId, stepId, cancellationToken);
        public Task<ConversationSnapshot?> GetAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            Reads++;
            return inner.GetAsync(sessionId, cancellationToken);
        }
    }

    private sealed class CountingArtifacts(IChatArtifactRepository inner) : IChatArtifactRepository
    {
        public int MessageReads { get; private set; }
        public Task<IReadOnlyList<ChatArtifact>> ListForMessageAsync(Guid messageId, CancellationToken cancellationToken = default)
        {
            MessageReads++;
            return inner.ListForMessageAsync(messageId, cancellationToken);
        }
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<ChatArtifact>>> ListForSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            inner.ListForSessionAsync(sessionId, cancellationToken);
        public Task<ChatArtifact?> GetAsync(Guid id, CancellationToken cancellationToken = default) => inner.GetAsync(id, cancellationToken);
        public Task<ChatArtifact> ImportAsync(Guid messageId, string serverArtifactId, string fileName, string contentType,
            string sha256, long length, string provider, string? stepId, IReadOnlyDictionary<string, string>? metadata,
            Stream content, CancellationToken cancellationToken = default) =>
            inner.ImportAsync(messageId, serverArtifactId, fileName, contentType, sha256, length, provider, stepId, metadata, content, cancellationToken);
    }
}
