using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class SpecializedAgentCancellationTests
{
    [Theory]
    [InlineData(ChatMode.Coding, false)]
    [InlineData(ChatMode.Coding, true)]
    [InlineData(ChatMode.ClaudeScience, false)]
    [InlineData(ChatMode.ClaudeScience, true)]
    public async Task RemovingProfileStopsDetachedPersistedJobAndRetriesOfflineWithoutStoppingOtherSessions(ChatMode mode, bool offline)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var runs = environment.Get<IMissumAiRunRepository>();
        var target = await chats.CreateSessionAsync("Loop-Agent stoppen", mode);
        var foreign = await chats.CreateSessionAsync("Andere aktive Sitzung", ChatMode.Coding);
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "workspace")).FullName;
        await chats.SetCodingWorkspacePathAsync(target.Id, workspace);
        await chats.SetCodingWorkspacePathAsync(foreign.Id, workspace);
        var targetTurn = await chats.AddTurnAsync(target.Id, "Ziel vollständig untersuchen");
        var foreignTurn = await chats.AddTurnAsync(foreign.Id, "Unabhängigen Auftrag fortsetzen");
        await chats.UpdateMessageAsync(targetTurn.AssistantMessage.Id, "Erhaltener Forschungsstand.", MessageStatus.Streaming);
        await chats.UpdateMessageAsync(foreignTurn.AssistantMessage.Id, "Unabhängiger Fortschritt.", MessageStatus.Streaming);
        var now = DateTimeOffset.UtcNow;
        var targetRun = await runs.CreateAsync(new(Guid.NewGuid(), target.Id, targetTurn.AssistantMessage.Id,
            PromptTriggerAction.Coding, "target-key", "run-loop-target", 42, "running", "fixture", null, now, now, workspace));
        var foreignRun = await runs.CreateAsync(new(Guid.NewGuid(), foreign.Id, foreignTurn.AssistantMessage.Id,
            PromptTriggerAction.Coding, "foreign-key", "run-unrelated", 9, "running", "fixture", null, now, now, workspace));
        await environment.Get<ISettingsStore>().SaveAsync(new AppSettings
        {
            MissumAiServerUrl = "http://127.0.0.1:65000", SelectedChatMode = mode, ActiveSessionId = target.Id,
            ActiveCodingSessionId = mode == ChatMode.Coding ? target.Id : foreign.Id,
            ActiveClaudeScienceSessionId = mode == ChatMode.ClaudeScience ? target.Id : null,
        });
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var profiles = new SessionAgentProfileStore(settings.DataDirectory);
        await profiles.WriteAsync(target.Id, SpecializedAgentProfiles.Loop);
        var child = SubagentChatState.Create(new("run-loop-target", "review-cycle", "loop-agent", "Loop-Agent: Ziel prüfen",
            "fixture", RunState.Running, Specialization: "loopAgent"), target.Id, now);
        child = child with { AssistantMessage = child.AssistantMessage with
            { Content = "Bereits empfangene Prüfung.", ToolSteps = [new("review-read", "coding.read", "running", new string('x', 40_000))] } };
        await chats.SaveToolStepAsync(targetTurn.AssistantMessage.Id, new(child.ReceiptId, "subagent", "running",
            child.UserMessage.Content, OutputJson: await child.PersistAsync(settings.DataDirectory)));
        await chats.SaveToolStepAsync(targetTurn.AssistantMessage.Id, new("read", "coding.read", "completed", "Erhaltener Beleg"));
        await chats.SaveToolStepAsync(targetTurn.AssistantMessage.Id, new("active", "coding.command", "running", "Laufende Arbeit"));
        var requests = new ConcurrentQueue<string>();
        var attempts = 0;
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance,
            () => new CancelHandler(requests, () => offline && Interlocked.Increment(ref attempts) == 1));
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        using var service = CreateService();
        var coordinator = new AssistantCoordinator(chats, environment.Get<IDocumentIngestor>(),
            environment.Get<IContextAssembler>(), environment.Get<IPromptTriggerRepository>(),
            environment.Get<IAssistantAttachmentRepository>(), environment.Get<IChatArtifactRepository>(),
            environment.Get<IConversationSnapshotRepository>(), service, settings, recent);
        Assert.Null(service.ActiveSessionId); // No page reader and no scheduler ticket.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await coordinator.HandleAsync(new(AssistantProtocol.ProtocolVersion, "session.agentProfile", "remove-detached-agent",
            JsonSerializer.SerializeToElement(new { sessionId = target.Id, profile = SpecializedAgentProfiles.Loop, enabled = false })),
            static (_, _, _) => Task.CompletedTask, deadline.Token);

        Assert.Null(await profiles.ReadAsync(target.Id));
        var stopped = (await runs.GetAsync(targetRun.Id))!;
        Assert.Equal("cancelled", stopped.State);
        Assert.Equal(42, stopped.LastEventId);
        var message = (await chats.GetMessageAsync(targetTurn.AssistantMessage.Id))!;
        Assert.Equal(MessageStatus.Cancelled, message.Status);
        Assert.Equal("Erhaltener Forschungsstand.", message.Content);
        Assert.Contains(message.ToolSteps!, step => step.Id == "read" && step.Detail == "Erhaltener Beleg" && step.Status == "completed");
        Assert.Contains(message.ToolSteps!, step => step.Id == "active" && step.Status == "cancelled");
        var stoppedChild = Assert.Single(SubagentChatState.Read([message], settings.DataDirectory));
        Assert.Equal("cancelled", stoppedChild.Status);
        Assert.Equal(MessageStatus.Cancelled, stoppedChild.AssistantMessage.Status);
        Assert.Equal(child.AssistantMessage.Content, stoppedChild.AssistantMessage.Content);
        var childStep = Assert.Single(stoppedChild.AssistantMessage.ToolSteps!);
        Assert.Equal("cancelled", childStep.Status);
        Assert.Equal(new string('x', 40_000), childStep.Detail);
        Assert.Equal("running", (await runs.GetAsync(foreignRun.Id))!.State);
        Assert.Equal(MessageStatus.Streaming, (await chats.GetMessageAsync(foreignTurn.AssistantMessage.Id))!.Status);
        Assert.Equal(["POST /v1/runs/run-loop-target/cancel"], requests.ToArray());
        var outbox = Path.Combine(settings.DataDirectory, "Diagnostics", "pending-run-cancellations.json");
        Assert.Equal(offline, File.Exists(outbox));
        if (offline) Assert.Equal(["run-loop-target"], JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(outbox, deadline.Token))!);

        // A fresh process retries a durable offline stop, without reattaching
        // the cancelled stream or treating the other Coding run as stale.
        using var reopened = CreateService();
        await reopened.StopPersistedRunsAtStartupAsync(deadline.Token);
        Assert.False(File.Exists(outbox));
        Assert.Equal(offline ? 2 : 1, requests.Count);
        Assert.All(requests, request => Assert.Equal("POST /v1/runs/run-loop-target/cancel", request));
        Assert.Equal(foreignRun.Id, Assert.Single(await runs.ListResumableAsync()).Id);
        Assert.Equal(JsonSerializer.Serialize(message), JsonSerializer.Serialize(await chats.GetMessageAsync(message.Id)));

        MissumAiAssistantService CreateService() => new(connection, chats, environment.Get<IAssistantAttachmentRepository>(),
            environment.Get<IChatArtifactRepository>(), runs, environment.Get<IClientToolExecutionRepository>(),
            environment.Get<IBinaryObjectStore>(), environment.Get<IDocumentIngestor>(), null!, null!, null!, null!, null!,
            settings, recent, NullLogger<MissumAiAssistantService>.Instance);
    }

    private sealed class CancelHandler(ConcurrentQueue<string> requests, Func<bool> fail) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Enqueue(request.Method + " " + path);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/runs/run-loop-target/cancel", path);
            if (fail()) throw new HttpRequestException("Fixture gateway offline");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RemovingProfileCancelsUnacknowledgedCreateByKeyWithoutSubmittingNewInference(bool offline, bool failedCreate)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var runs = environment.Get<IMissumAiRunRepository>();
        var target = await chats.CreateSessionAsync("Unbestätigter Loop-Auftrag", ChatMode.ClaudeScience);
        var foreign = await chats.CreateSessionAsync("Anderer unbestätigter Auftrag", ChatMode.Coding);
        var targetTurn = await chats.AddTurnAsync(target.Id, "Langfristiges Ziel");
        var foreignTurn = await chats.AddTurnAsync(foreign.Id, "Anderes Ziel");
        await chats.UpdateMessageAsync(targetTurn.AssistantMessage.Id, "Erhaltener Inhalt", MessageStatus.Interrupted);
        var now = DateTimeOffset.UtcNow;
        var run = await runs.CreateAsync(new(Guid.NewGuid(), target.Id, targetTurn.AssistantMessage.Id,
            PromptTriggerAction.Coding, "lost-acceptance-key", null, 0, failedCreate ? "failed" : "queued", "fixture",
            failedCreate ? "client.run_create_failed" : null, now, now));
        var other = await runs.CreateAsync(new(Guid.NewGuid(), foreign.Id, foreignTurn.AssistantMessage.Id,
            PromptTriggerAction.Coding, "foreign-key", null, 0, "queued", "fixture", null, now, now));
        await environment.Get<ISettingsStore>().SaveAsync(new AppSettings
        {
            MissumAiServerUrl = "http://127.0.0.1:65000", SelectedChatMode = ChatMode.ClaudeScience,
            ActiveSessionId = target.Id, ActiveClaudeScienceSessionId = target.Id,
        });
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var profiles = new SessionAgentProfileStore(settings.DataDirectory);
        await profiles.WriteAsync(target.Id, SpecializedAgentProfiles.Loop);
        var requests = new ConcurrentQueue<string>();
        var attempts = 0;
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance,
            () => new KeyCancelHandler(requests, target.Id, () => offline && Interlocked.Increment(ref attempts) == 1));
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        using var service = CreateService();
        var coordinator = new AssistantCoordinator(chats, environment.Get<IDocumentIngestor>(),
            environment.Get<IContextAssembler>(), environment.Get<IPromptTriggerRepository>(),
            environment.Get<IAssistantAttachmentRepository>(), environment.Get<IChatArtifactRepository>(),
            environment.Get<IConversationSnapshotRepository>(), service, settings, recent);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await coordinator.HandleAsync(new(AssistantProtocol.ProtocolVersion, "session.agentProfile", "remove-unknown-create",
            JsonSerializer.SerializeToElement(new { sessionId = target.Id, profile = SpecializedAgentProfiles.Loop, enabled = false })),
            static (_, _, _) => Task.CompletedTask, deadline.Token);
        Assert.Null(await profiles.ReadAsync(target.Id));
        Assert.Equal("cancelled", (await runs.GetAsync(run.Id))!.State);
        Assert.Null((await runs.GetAsync(run.Id))!.ServerRunId);
        Assert.Empty(await runs.ListPendingForSessionAsync(target.Id));
        Assert.Equal(other.Id, Assert.Single(await runs.ListPendingForSessionAsync(foreign.Id)).Id);
        var message = (await chats.GetMessageAsync(targetTurn.AssistantMessage.Id))!;
        Assert.Equal(MessageStatus.Cancelled, message.Status);
        Assert.Equal("Erhaltener Inhalt", message.Content);
        Assert.Single(requests);
        var outbox = Path.Combine(settings.DataDirectory, "Diagnostics", "pending-run-key-cancellations.json");
        Assert.Equal(offline, File.Exists(outbox));
        if (offline)
        {
            using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(outbox, deadline.Token));
            var entry = Assert.Single(persisted.RootElement.EnumerateArray());
            Assert.Equal(target.Id, entry.GetProperty("sessionId").GetGuid());
            Assert.Equal("lost-acceptance-key", entry.GetProperty("idempotencyKey").GetString());
        }
        using var reopened = CreateService();
        await reopened.StopPersistedRunsAtStartupAsync(deadline.Token);
        Assert.False(File.Exists(outbox));
        Assert.Equal(offline ? 2 : 1, requests.Count);
        Assert.All(requests, request => Assert.Equal("POST /v1/runs/cancel-by-key", request));
        Assert.Equal("queued", (await runs.GetAsync(other.Id))!.State);
        Assert.Equal(JsonSerializer.Serialize(message), JsonSerializer.Serialize(await chats.GetMessageAsync(message.Id)));

        MissumAiAssistantService CreateService() => new(connection, chats, environment.Get<IAssistantAttachmentRepository>(),
            environment.Get<IChatArtifactRepository>(), runs, environment.Get<IClientToolExecutionRepository>(),
            environment.Get<IBinaryObjectStore>(), environment.Get<IDocumentIngestor>(), null!, null!, null!, null!, null!,
            settings, recent, NullLogger<MissumAiAssistantService>.Instance);
    }

    private sealed class KeyCancelHandler(ConcurrentQueue<string> requests, Guid sessionId, Func<bool> fail) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Enqueue(request.Method + " " + path);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/runs/cancel-by-key", path);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(sessionId.ToString("D"), body.RootElement.GetProperty("sessionId").GetString());
            Assert.Equal("lost-acceptance-key", body.RootElement.GetProperty("idempotencyKey").GetString());
            if (fail()) throw new HttpRequestException("Fixture gateway offline");
            return new(HttpStatusCode.NoContent);
        }
    }
}
