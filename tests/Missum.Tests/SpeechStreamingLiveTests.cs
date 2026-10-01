using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Missum.Tests;

/// <summary>Opt-in real gateway synthesis and physical playback, driven through the normal coordinator update path.</summary>
public sealed class SpeechStreamingLiveTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(ChatMode.General)]
    [InlineData(ChatMode.ClaudeScience)]
    [Trait("Category", "Live")]
    public async Task CoordinatorPlaysRealSpeechBeforeCompletionWithMicrophoneDisabledAndExcludesTools(ChatMode mode)
    {
        if (Environment.GetEnvironmentVariable("MISSUM_AI_STREAMING_SPEECH_LIVE") != "1") return;
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(value => value with
        {
            MissumAiServerUrl = Environment.GetEnvironmentVariable("MISSUM_AI_SERVER_URL") ?? "http://127.0.0.1:8080",
            IsAutomaticSpeechEnabled = true,
        });
        var paragraphs = new ConcurrentQueue<string>();
        var requestPaths = new ConcurrentQueue<string>();
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance,
            () => new RecordingHandler(paragraphs, requestPaths));
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
        var coordinator = new AssistantCoordinator(chats, documents,
            environment.Get<IContextAssembler>(), environment.Get<IPromptTriggerRepository>(),
            environment.Get<IAssistantAttachmentRepository>(), environment.Get<IChatArtifactRepository>(),
            environment.Get<IConversationSnapshotRepository>(), service, settings, recent, microphone);
        var session = await chats.CreateSessionAsync("Isolierte Prüfung der laufenden Sprachausgabe", mode);
        await settings.UpdateAsync(value => value with { ActiveSessionId = session.Id });
        var turn = await chats.AddTurnAsync(session.Id, "Erkläre die Aufgabe in kurzen Abschnitten.");
        var current = turn.AssistantMessage;
        var playing = Channel.CreateUnbounded<Guid>();
        var playbackIds = new ConcurrentDictionary<Guid, byte>();
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        const string first = "Die erste Erklärung wird bereits während des Laufs vorgelesen.";
        const string second = "Die zweite Erklärung folgt, während die Antwort noch entsteht.";
        const string remainder = "Zum Abschluss folgt der verbliebene Antworttext";
        const string secret = "DO_NOT_SPEAK_TOOL_DATA";
        var tool = new AssistantToolStep("speech-live-tool", "coding.command", "completed", secret,
            InputJson: "{\"command\":\"DO_NOT_SPEAK_TOOL_DATA\"}",
            OutputJson: "{\"stdout\":\"DO_NOT_SPEAK_TOOL_DATA\",\"stderr\":\"DO_NOT_SPEAK_TOOL_DATA\"}");
        var steps = new[] { tool };
        output.WriteLine($"profile={environment.Directory}; server={settings.Current.MissumAiServerUrl}; microphone=disabled; actual audio playback is enabled");
        try
        {
            // No microphone/voice-control start occurs anywhere in this test.
            Assert.False(microphone.Current.IsRecording);
            await coordinator.EmitMissumAiUpdateAsync(new(MissumAiAssistantUpdateKind.Started, current), Emit, "speech-live");
            await Send(MissumAiAssistantUpdateKind.Delta, first + " ", MessageStatus.Streaming);
            // No tool boundary is needed: a complete sentence already starts audio.
            var firstPlayback = await WaitForPlaybackAsync();
            Assert.Equal(MessageStatus.Streaming, (await chats.GetMessageAsync(current.Id))!.Status);
            Assert.False(microphone.Current.IsRecording);
            Assert.False(done.Task.IsCompleted);
            output.WriteLine("First real Playing event arrived during streaming without a tool boundary while the persisted AI message is still streaming.");

            Assert.True((await microphone.ToggleSpeechPauseAsync(timeout.Token)).IsSpeechPaused);
            await Task.Delay(250, timeout.Token);
            Assert.True(microphone.Current.IsSpeechPaused);
            Assert.False(done.Task.IsCompleted);
            Assert.False((await microphone.ToggleSpeechPauseAsync(timeout.Token)).IsSpeechPaused);
            output.WriteLine("Physical playback paused and resumed without ending the streaming answer.");

            await Send(MissumAiAssistantUpdateKind.Delta, first + " " + second + " ", MessageStatus.Streaming);
            var secondPlayback = await WaitForPlaybackAsync();
            Assert.NotEqual(firstPlayback, secondPlayback);
            Assert.Equal(MessageStatus.Streaming, (await chats.GetMessageAsync(current.Id))!.Status);
            Assert.False(done.Task.IsCompleted);

            await Send(MissumAiAssistantUpdateKind.Completed, first + " " + second + " " + remainder, MessageStatus.Completed);
            await coordinator.EmitMissumAiUpdateAsync(new(MissumAiAssistantUpdateKind.Completed, current), Emit, "speech-live");
            Assert.Equal("Abgeschlossen", await done.Task.WaitAsync(timeout.Token));
            Assert.Equal(3, playbackIds.Count);
            var actual = paragraphs.ToArray();
            Assert.Equal(3, actual.Length);
            Assert.Contains(first, actual[0], StringComparison.Ordinal);
            Assert.Contains(second, actual[1], StringComparison.Ordinal);
            Assert.Contains(remainder, actual[2], StringComparison.Ordinal);
            Assert.DoesNotContain(secret, string.Join("\n", actual), StringComparison.Ordinal);
            Assert.DoesNotContain(requestPaths, path => path.Contains("transcription", StringComparison.OrdinalIgnoreCase)
                || path.Contains("live-caption", StringComparison.OrdinalIgnoreCase));
            Assert.False(microphone.Current.IsRecording);
            output.WriteLine("Passed: three real TTS paragraphs and playback IDs; two audible before completion; final tail once; no microphone or tool text.");
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await service.CancelSpeechAsync(cleanup.Token);
            if (!playbackIds.IsEmpty && !done.Task.IsCompleted)
                await done.Task.WaitAsync(cleanup.Token);
        }

        async Task Send(MissumAiAssistantUpdateKind kind, string content, MessageStatus status)
        {
            var currentSteps = steps;
            if (mode == ChatMode.ClaudeScience)
            {
                currentSteps = [new("live-introduction", "assistant.narration", "completed", first), tool];
                content = content.StartsWith(first, StringComparison.Ordinal) ? content[first.Length..].TrimStart() : content;
            }
            await chats.UpdateMessageWithToolStepsAsync(current.Id, content, status, currentSteps, timeout.Token);
            current = (await chats.GetMessageAsync(current.Id, timeout.Token))!;
            await coordinator.EmitMissumAiUpdateAsync(new(kind, current), Emit, "speech-live");
        }

        async Task<Guid> WaitForPlaybackAsync()
        {
            var next = playing.Reader.ReadAsync(timeout.Token).AsTask();
            if (await Task.WhenAny(next, done.Task) == done.Task)
                throw new InvalidOperationException("Speech ended before the expected real playback: " + await done.Task);
            return await next;
        }

        Task Emit(string kind, object payload, string? requestId)
        {
            if (kind is not ("speech.progress" or "speech.status")) return Task.CompletedTask;
            var data = JsonSerializer.SerializeToElement(payload, Json);
            Assert.False(microphone.Current.IsRecording);
            if (kind == "speech.progress" && data.GetProperty("state").GetString() == "playing")
            {
                var playbackId = data.GetProperty("playbackId").GetGuid();
                if (playbackIds.TryAdd(playbackId, 0)) playing.Writer.TryWrite(playbackId);
            }
            if (kind == "speech.status")
            {
                output.WriteLine($"{DateTimeOffset.UtcNow:O} {JsonSerializer.Serialize(data)}");
                if (!data.GetProperty("active").GetBoolean())
                {
                    if (data.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                        done.TrySetException(new InvalidOperationException(error.GetString()));
                    else done.TrySetResult(data.GetProperty("status").GetString()!);
                }
            }
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHandler(ConcurrentQueue<string> paragraphs, ConcurrentQueue<string> requestPaths) : DelegatingHandler(new HttpClientHandler { UseProxy = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            requestPaths.Enqueue(path);
            if (request.Method == HttpMethod.Post && path.EndsWith("/paragraphs", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                paragraphs.Enqueue(body.RootElement.GetProperty("text").GetString()!);
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
