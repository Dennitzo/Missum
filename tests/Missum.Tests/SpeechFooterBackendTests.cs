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
    public void SteeringFooterIdentityMatchesHistoryAndSeparatesSiblingPrompts()
    {
        var parentId = Guid.Parse("12345678-1234-5678-9abc-def012345678");
        const string stepId = "steering-user-directed-1";
        var identity = MissumAiAssistantService.SteeringPromptMessageId(parentId, stepId);
        // Existing expanded history identities must survive the footer change.
        Assert.Equal(Guid.Parse("b0e8c20e-f78d-39de-9e08-6106e960deaf"), identity);
        Assert.Equal(identity, MissumAiAssistantService.SteeringPromptMessageId(parentId, stepId));
        Assert.NotEqual(identity, MissumAiAssistantService.SteeringPromptMessageId(parentId, "steering-user-directed-2"));
        Assert.NotEqual(identity, MissumAiAssistantService.SteeringPromptMessageId(Guid.NewGuid(), stepId));
        var parent = Message(ChatRole.Assistant, MessageStatus.Streaming, "Übergeordnete AI-Antwort.") with
        {
            Id = parentId,
            ToolSteps = [new(stepId, "assistant.steering", "completed", "Neue Richtung.", ContentOffset: 0)],
        };
        var prompt = Assert.Single(MissumAiAssistantService.ExpandSteeringHistory([parent]), message => message.Role == ChatRole.User);
        Assert.Equal(identity, prompt.Id);
    }

    [Fact]
    public async Task SteeringSpeechUsesOnlyVisiblePromptAndStartsUnpausedBeforeDatabaseCommit()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(value => value with { MissumAiServerUrl = "http://isolated.invalid:8080" });
        var requests = new ConcurrentQueue<string>();
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance,
            () => new ForbiddenHttpHandler(requests));
        using var microphone = new MicrophoneTranscriptionService(connection, settings, NullLogger<MicrophoneTranscriptionService>.Instance);
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        // Deliberately unavailable repositories: the accepted live prompt may not
        // yet be committed, and a document reference must remain literal text.
        using var service = new MissumAiAssistantService(connection, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, microphone, settings, recent, NullLogger<MissumAiAssistantService>.Instance);
        var sessionId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        const string stepId = "steering-accepted-before-persistence";
        const string text = "Bitte lies das Dokument aus dem Anhang vor und beachte meine neue Richtung.";
        var identity = MissumAiAssistantService.SteeringPromptMessageId(parentId, stepId);
        var updates = new List<MissumAiSpeechUpdate>();
        var progress = new List<SpeechPlaybackProgress>();
        using var previousSpeech = microphone.BeginSpeechSession(resetPause: true);
        Assert.True((await microphone.ToggleSpeechPauseAsync()).IsSpeechPaused);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await service.SpeakSteeringPromptAsync(sessionId, parentId, stepId, text,
            update => { updates.Add(update); return Task.CompletedTask; },
            playback =>
            {
                progress.Add(playback);
                if (playback.State == SpeechPlaybackState.Buffering)
                {
                    Assert.False(microphone.Current.IsSpeechPaused);
                    Assert.Equal(text, Assert.Single(playback.SourceUnits!).Text);
                    // Stop after source resolution, before synthesis or playback.
                    cancellation.Cancel();
                }
                return Task.CompletedTask;
            }, cancellation.Token);

        Assert.Contains(progress, playback => playback.State == SpeechPlaybackState.Buffering);
        Assert.Contains(progress, playback => playback.State == SpeechPlaybackState.Cancelled);
        Assert.All(progress, playback =>
        {
            Assert.Equal(sessionId, playback.SessionId);
            Assert.Equal(identity, playback.SourceMessageId);
            Assert.NotEqual(parentId, playback.SourceMessageId);
        });
        Assert.All(updates, update =>
        {
            Assert.Equal(sessionId, update.SessionId);
            Assert.Equal(identity, update.SourceMessageId);
            Assert.False(update.IsAutomatic);
        });
        Assert.Contains(updates, update => !update.IsActive && update.Status == "Abgebrochen");
        Assert.False(service.IsSpeaking);
        Assert.Empty(requests);
    }

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
