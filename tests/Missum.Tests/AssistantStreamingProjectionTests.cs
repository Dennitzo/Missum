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
        Assert.Equal(message.Content, latest.GetProperty("content").GetString());
        Assert.Equal(message.UpdatedAt, latest.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.Equal(step.Id, Assert.Single(latest.GetProperty("toolSteps").EnumerateArray()).GetProperty("id").GetString());

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
