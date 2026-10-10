using System.Collections.Concurrent;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Missum.Tests;

public sealed class SpeechFooterBackendTests
{
    [Fact]
    public async Task AutomaticQueuePublishesStableFooterIdentityBeforeAudioAndRetainsItWhenStopped()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(value => value with
        {
            MissumAiServerUrl = "http://isolated.invalid:8080", IsAutomaticSpeechEnabled = true,
        });
        var requests = new ConcurrentQueue<string>();
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance,
            () => new ForbiddenHttpHandler(requests));
        using var microphone = new MicrophoneTranscriptionService(connection, settings, NullLogger<MicrophoneTranscriptionService>.Instance);
        var chats = environment.Get<IChatRepository>();
        var documents = environment.Get<IDocumentIngestor>();
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        var broker = new LocalToolBroker(connection, documents, null!, chats);
        using var service = new MissumAiAssistantService(connection, chats, environment.Get<IAssistantAttachmentRepository>(),
            environment.Get<IChatArtifactRepository>(), environment.Get<IMissumAiRunRepository>(), environment.Get<IClientToolExecutionRepository>(),
            environment.Get<IBinaryObjectStore>(), documents, new DocumentContextPreparationService(documents),
            new SessionContextPreparationService(chats), broker, null!, microphone, settings, recent,
            NullLogger<MissumAiAssistantService>.Instance);
        var session = await chats.CreateSessionAsync("Footer ohne Audioanforderung");
        var turn = await chats.AddTurnAsync(session.Id, "Noch keinen Antworttext liefern.");
        Assert.Empty(turn.AssistantMessage.Content);
        var statuses = new ConcurrentQueue<MissumAiSpeechUpdate>();
        var active = new TaskCompletionSource<MissumAiSpeechUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inactive = new TaskCompletionSource<MissumAiSpeechUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var playbackCalls = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            service.ObserveAutomaticSpeech(new(MissumAiAssistantUpdateKind.Started, turn.AssistantMessage), Status,
                _ => { Interlocked.Increment(ref playbackCalls); return Task.CompletedTask; });
            var started = await active.Task.WaitAsync(timeout.Token);
            Assert.True(started.IsActive);
            Assert.True(started.IsAutomatic);
            Assert.Equal(session.Id, started.SessionId);
            Assert.Equal(turn.AssistantMessage.Id, started.SourceMessageId);
            Assert.NotNull(started.PlaybackId);
            Assert.NotEqual(Guid.Empty, started.PlaybackId);
            Assert.True(microphone.Current.CanPauseSpeech);
            Assert.False(microphone.Current.IsRecording);
            Assert.Empty(requests);

            Assert.True((await microphone.ToggleSpeechPauseAsync(timeout.Token)).IsSpeechPaused);
            Assert.True(microphone.Current.CanPauseSpeech);
            // Replayed Started for the same answer cannot create a different queue identity.
            service.ObserveAutomaticSpeech(new(MissumAiAssistantUpdateKind.Started, turn.AssistantMessage), Status,
                _ => { Interlocked.Increment(ref playbackCalls); return Task.CompletedTask; });
            Assert.Single(statuses, status => status.IsActive);
            Assert.Empty(requests);

            await service.CancelSpeechAsync(timeout.Token);
            var stopped = await inactive.Task.WaitAsync(timeout.Token);
            Assert.False(stopped.IsActive);
            Assert.True(stopped.IsAutomatic);
            Assert.Equal("Abgebrochen", stopped.Status);
            Assert.Equal(started.SessionId, stopped.SessionId);
            Assert.Equal(started.SourceMessageId, stopped.SourceMessageId);
            Assert.Equal(started.PlaybackId, stopped.PlaybackId);
            Assert.False(microphone.Current.CanPauseSpeech);
            Assert.False(microphone.Current.IsRecording);
            Assert.Equal(0, playbackCalls);
            Assert.Empty(requests);
            var beforeReplay = statuses.Count;
            service.ObserveAutomaticSpeech(new(MissumAiAssistantUpdateKind.Started, turn.AssistantMessage), Status,
                _ => { Interlocked.Increment(ref playbackCalls); return Task.CompletedTask; });
            service.ObserveAutomaticSpeech(new(MissumAiAssistantUpdateKind.Delta,
                turn.AssistantMessage with { Content = "Dieser spätere Text bleibt nach Stop stumm. " }), Status,
                _ => { Interlocked.Increment(ref playbackCalls); return Task.CompletedTask; });
            Assert.Equal(beforeReplay, statuses.Count);
            Assert.False(microphone.Current.CanPauseSpeech);
            Assert.Empty(requests);
            service.ObserveAutomaticSpeech(new(MissumAiAssistantUpdateKind.Started, turn.AssistantMessage,
                LocalRunId: Guid.NewGuid()), Status,
                _ => { Interlocked.Increment(ref playbackCalls); return Task.CompletedTask; });
            var continued = statuses.Last(status => status.IsActive);
            Assert.NotEqual(started.PlaybackId, continued.PlaybackId);
            Assert.True(microphone.Current.CanPauseSpeech);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await service.CancelSpeechAsync(cleanup.Token);
        }

        Task Status(MissumAiSpeechUpdate update)
        {
            statuses.Enqueue(update);
            if (update.IsActive) active.TrySetResult(update);
            else inactive.TrySetResult(update);
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(MessageStatus.Streaming)]
    [InlineData(MessageStatus.Pending)]
    public void VisibleStreamingPrefixMayBeAheadOfDatabaseWithoutChangingMessageIdentity(MessageStatus status)
    {
        var stored = Message(ChatRole.Assistant, status, "Die erste Erklärung.");

        var snapshot = MissumAiAssistantService.ResolveStreamingSpeechSnapshot(stored,
            "Die erste Erklärung. Der nächste Satz ist bereits sichtbar.");

        Assert.Equal("Die erste Erklärung. Der nächste Satz ist bereits sichtbar.", snapshot.Content);
        Assert.Equal(stored.Id, snapshot.Id);
        Assert.Equal(stored.SessionId, snapshot.SessionId);
        Assert.Equal(stored.Status, snapshot.Status);
        Assert.Equal(stored.UpdatedAt, snapshot.UpdatedAt);
        Assert.Equal("Die erste Erklärung.", stored.Content);
    }

    [Fact]
    public void DatabaseAheadOfVisiblePrefixKeepsTheCompleteStoredText()
    {
        var stored = Message(ChatRole.Assistant, MessageStatus.Streaming,
            "Die erste Erklärung. Der zweite Satz ist bereits gespeichert.");

        var snapshot = MissumAiAssistantService.ResolveStreamingSpeechSnapshot(stored, "Die erste Erklärung.");

        Assert.Same(stored, snapshot);
    }

    [Theory]
    [InlineData(MessageStatus.Completed)]
    [InlineData(MessageStatus.Cancelled)]
    [InlineData(MessageStatus.Interrupted)]
    [InlineData(MessageStatus.Failed)]
    public void NonstreamingAnswersIgnoreInjectedVisibleText(MessageStatus status)
    {
        var stored = Message(ChatRole.Assistant, status, "Der dauerhaft gespeicherte Antworttext.");

        Assert.Same(stored, MissumAiAssistantService.ResolveStreamingSpeechSnapshot(stored, "Unzugehöriger injizierter Text."));
    }

    [Theory]
    [InlineData(ChatRole.User)]
    [InlineData(ChatRole.System)]
    public void OtherRolesNeverAcceptAStreamingAssistantTextOverride(ChatRole role)
    {
        var stored = Message(role, MessageStatus.Streaming, "Originaler Fremdtext.");

        Assert.Same(stored, MissumAiAssistantService.ResolveStreamingSpeechSnapshot(stored, "Ein anderer sichtbarer Text."));
    }

    [Theory]
    [InlineData(MessageStatus.Streaming)]
    [InlineData(MessageStatus.Pending)]
    public void DivergentStreamingTextCannotBeSpokenAsTheSavedAnswer(MessageStatus status)
    {
        var stored = Message(ChatRole.Assistant, status, "Die gespeicherte Antwort.");

        Assert.Throws<InvalidOperationException>(() =>
            MissumAiAssistantService.ResolveStreamingSpeechSnapshot(stored, "Eine davon unabhängige Antwort."));
    }

    [Theory]
    [InlineData(ChatRole.Assistant, MessageStatus.Streaming, "Antworttext", true)]
    [InlineData(ChatRole.Assistant, MessageStatus.Pending, "Antworttext", true)]
    [InlineData(ChatRole.Assistant, MessageStatus.Streaming, "  ", false)]
    [InlineData(ChatRole.Assistant, MessageStatus.Pending, "", false)]
    [InlineData(ChatRole.User, MessageStatus.Streaming, "Nutzertext", false)]
    [InlineData(ChatRole.User, MessageStatus.Pending, "Nutzertext", false)]
    [InlineData(ChatRole.User, MessageStatus.Completed, "Nutzertext", true)]
    [InlineData(ChatRole.System, MessageStatus.Streaming, "Systemtext", false)]
    public void FooterReadsOnlyEligibleNonemptyMessageStates(ChatRole role, MessageStatus status, string text, bool expected) =>
        Assert.Equal(expected, MissumAiAssistantService.IsReadableSpeechMessage(Message(role, status, text)));

    private static ChatMessage Message(ChatRole role, MessageStatus status, string content)
    {
        var at = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), Guid.NewGuid(), role, content, status, at, at);
    }

    private sealed class ForbiddenHttpHandler(ConcurrentQueue<string> requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Enqueue(request.Method + " " + request.RequestUri);
            throw new InvalidOperationException("Empty streaming speech must not send a gateway, synthesis, or model request.");
        }
    }
}
