using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Missum.Ai.Client;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class BrowserSpeechTests
{
    [Fact]
    public async Task AutomaticFooterOwnerIsAvailableBeforeAnySynthesisOrVisibleText()
    {
        using var gateway = new FakeGateway();
        await using var service = CreateService(gateway, BrowserSpeechPlan.Create(Guid.NewGuid(), null, "Text", "Test."));
        var events = new EventSink();
        var now = DateTimeOffset.UtcNow;
        var message = new ChatMessage(Guid.NewGuid(), Guid.NewGuid(), ChatRole.Assistant, "", MessageStatus.Streaming, now, now);

        service.ObserveAutomaticSpeech("mac-owner", new(MissumAiAssistantUpdateKind.Started, message), events.Emit);
        var buffered = await events.Next("speech.progress");
        var active = await events.Next("speech.status");

        Assert.Equal("buffering", buffered.Payload.GetProperty("state").GetString());
        Assert.Equal(message.SessionId, buffered.Payload.GetProperty("sessionId").GetGuid());
        Assert.Equal(message.Id, buffered.Payload.GetProperty("sourceMessageId").GetGuid());
        Assert.Equal("mac-owner", buffered.Payload.GetProperty("ownerClientId").GetString());
        Assert.Equal(message.Id, active.Payload.GetProperty("sourceMessageId").GetGuid());
        Assert.Equal(buffered.Payload.GetProperty("playbackId").GetGuid(), active.Payload.GetProperty("playbackId").GetGuid());
        Assert.Empty(gateway.Paragraphs);
        await service.HandleAsync("mac-owner", Envelope("microphone.stopSpeech", new { playbackId = buffered.Payload.GetProperty("playbackId").GetGuid(),
            sessionId = message.SessionId, messageId = message.Id }), events.Emit);
    }

    [Fact]
    public async Task OldFooterControlsCannotStopOrPauseAReplacementPlaybackOrAnotherDevice()
    {
        using var gateway = new FakeGateway();
        var plan = BrowserSpeechPlan.Create(Guid.NewGuid(), Guid.NewGuid(), "AI-Nachricht", "Ein Satz.");
        await using var service = CreateService(gateway, plan);
        var events = new EventSink();
        await service.HandleAsync("mac", Envelope("microphone.speak", new { sessionId = plan.SessionId, messageId = plan.MessageId }), events.Emit);
        var first = await events.Next("speech.audio");
        await events.Next("speech.complete");
        await service.HandleAsync("mac", Envelope("microphone.speak", new { sessionId = plan.SessionId, messageId = plan.MessageId }), events.Emit);
        var second = await events.Next("speech.audio");
        await events.Next("speech.complete");
        var oldId = first.Payload.GetProperty("playbackId").GetGuid();
        var currentId = second.Payload.GetProperty("playbackId").GetGuid();
        var count = events.All.Count;

        await service.HandleAsync("mac", Envelope("microphone.toggleSpeechPause", new { playbackId = oldId, sessionId = plan.SessionId, messageId = plan.MessageId }), events.Emit);
        await service.HandleAsync("mac", Envelope("microphone.stopSpeech", new { playbackId = oldId, sessionId = plan.SessionId, messageId = plan.MessageId }), events.Emit);
        await service.HandleAsync("another-mac", Envelope("microphone.stopSpeech", new { playbackId = currentId }), events.Emit);

        Assert.Equal(count, events.All.Count);
        await service.HandleAsync("mac", Envelope("microphone.toggleSpeechPause", new { playbackId = currentId, sessionId = plan.SessionId, messageId = plan.MessageId }), events.Emit);
        Assert.True((await events.Next("speech.pause")).Payload.GetProperty("paused").GetBoolean());
        await service.HandleAsync("mac", Envelope("microphone.stopSpeech", new { playbackId = currentId, sessionId = plan.SessionId, messageId = plan.MessageId }), events.Emit);
        Assert.Contains(events.All, item => item.Type == "speech.progress" && item.Payload.GetProperty("playbackId").GetGuid() == currentId
            && item.Payload.GetProperty("state").GetString() == "cancelled");
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task BrowserAcknowledgementsBoundPrefetchAndDriveRealPlaybackRangesAndCompletion()
    {
        using var gateway = new FakeGateway();
        var sessionId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var plan = BrowserSpeechPlan.Create(sessionId, messageId, "AI-Nachricht",
            "Der erste Satz. Der zweite Satz. Der dritte Satz.");
        await using var service = CreateService(gateway, plan);
        var events = new EventSink();
        Assert.True(await service.HandleAsync("mac", Envelope("microphone.speak", new { sessionId, messageId }), events.Emit));
        var first = await events.Next("speech.audio");
        var second = await events.Next("speech.audio");
        var playbackId = first.Payload.GetProperty("playbackId").GetGuid();
        Assert.Equal(0, first.Payload.GetProperty("sequence").GetInt32());
        Assert.Equal(1, second.Payload.GetProperty("sequence").GetInt32());
        Assert.Equal(2, gateway.Paragraphs.Count);
        Assert.DoesNotContain(events.All, item => item.Type == "speech.status"
            && item.Payload.GetProperty("status").GetString() == "Sprachausgabe wird wiedergegeben");
        var url = first.Payload.GetProperty("url").GetString()!;
        Assert.StartsWith("gateway-artifacts/", url);
        Assert.True(service.TryGetArtifact(Uri.UnescapeDataString(url["gateway-artifacts/".Length..]), out var artifact));
        Assert.Equal("audio/wav", artifact!.MediaType);

        await Progress(service, "mac", events, playbackId, 0, 1, "playing", .25);
        Assert.Contains(events.All, item => item.Type == "speech.progress"
            && item.Payload.GetProperty("state").GetString() == "playing"
            && item.Payload.GetProperty("sourceUnitIds")[0].GetString() == plan.Segments[0].SourceUnitIds[0]);
        await Progress(service, "other-device", events, playbackId, 0, 2, "ended");
        Assert.Equal(2, gateway.Paragraphs.Count);
        await Progress(service, "mac", events, playbackId, 0, 2, "ended");
        var third = await events.Next("speech.audio");
        Assert.Equal(2, third.Payload.GetProperty("sequence").GetInt32());
        await events.Next("speech.complete");
        Assert.Equal(3, gateway.Paragraphs.Count);
        Assert.DoesNotContain(events.All, item => item.Type == "speech.status"
            && item.Payload.GetProperty("status").GetString() == "Abgeschlossen");
        await Progress(service, "mac", events, playbackId, 1, 3, "ended");
        await Progress(service, "mac", events, playbackId, 2, 4, "ended");
        Assert.Contains(events.All, item => item.Type == "speech.status"
            && !item.Payload.GetProperty("active").GetBoolean()
            && item.Payload.GetProperty("status").GetString() == "Abgeschlossen");
        Assert.Equal(1, gateway.Ended);
        Assert.Equal(0, gateway.Cancelled);
    }

    [Fact]
    public async Task PauseAndStopAreIsolatedAndStopCancelsTheGatewayProducer()
    {
        using var gateway = new FakeGateway();
        var plan = BrowserSpeechPlan.Create(Guid.NewGuid(), Guid.NewGuid(), "AI-Nachricht",
            "Satz eins. Satz zwei. Satz drei.");
        await using var service = CreateService(gateway, plan);
        var mac = new EventSink();
        var secondMac = new EventSink();
        await service.HandleAsync("mac-one", Envelope("microphone.speak", new { text = "ignored" }), mac.Emit);
        var first = await mac.Next("speech.audio");
        await mac.Next("speech.audio");
        await service.HandleAsync("mac-two", Envelope("microphone.speak", new { text = "ignored" }), secondMac.Emit);
        await secondMac.Next("speech.audio");
        await secondMac.Next("speech.audio");
        await service.HandleAsync("mac-one", Envelope("microphone.toggleSpeechPause", new { }), mac.Emit);
        var paused = await mac.Next("speech.pause");
        Assert.True(paused.Payload.GetProperty("paused").GetBoolean());
        await Progress(service, "mac-one", mac, first.Payload.GetProperty("playbackId").GetGuid(), 0, 1, "ended");
        Assert.Equal(4, gateway.Paragraphs.Count);
        await service.HandleAsync("mac-one", Envelope("microphone.stopSpeech", new { }), mac.Emit);
        Assert.DoesNotContain(secondMac.All, item => item.Type == "speech.reset"
            && item.Payload.GetProperty("playbackId").ValueKind == JsonValueKind.Null);
        await service.DisposeAsync();
        Assert.Equal(2, gateway.Cancelled);
    }

    [Fact]
    public async Task AutomaticSpeechStartsBeforeRunCompletionAndNeverSpeaksToolDataOrRestartsStoppedMessage()
    {
        using var gateway = new FakeGateway();
        var unusedPlan = BrowserSpeechPlan.Create(Guid.NewGuid(), null, "Text", "Platzhalter.");
        await using var service = CreateService(gateway, unusedPlan);
        var events = new EventSink();
        var now = DateTimeOffset.UtcNow;
        var message = new ChatMessage(Guid.NewGuid(), Guid.NewGuid(), ChatRole.Assistant, "",
            MessageStatus.Streaming, now, now);
        service.ObserveAutomaticSpeech("mac", new(MissumAiAssistantUpdateKind.Started, message), events.Emit);
        message = message with { Content = "Die Erklärung beginnt. " };
        service.ObserveAutomaticSpeech("mac", new(MissumAiAssistantUpdateKind.Delta, message), events.Emit);
        var first = await events.Next("speech.audio");
        Assert.DoesNotContain(events.All, item => item.Type == "speech.complete");
        await Progress(service, "mac", events, first.Payload.GetProperty("playbackId").GetGuid(), 0, 1, "playing", .3);
        Assert.Contains(events.All, item => item.Type == "speech.progress"
            && item.Payload.GetProperty("sourceMessageId").GetGuid() == message.Id
            && item.Payload.GetProperty("state").GetString() == "playing"
            && item.Payload.GetProperty("sourceUnitIds").GetArrayLength() == 1);
        message = message with
        {
            Content = "Die Erklärung beginnt. \n```\nDO_NOT_SPEAK_CODE\n```\nDie Erklärung endet.",
            Status = MessageStatus.Completed,
            ToolSteps = [new("tool-secret", "coding.command", "completed", "DO_NOT_SPEAK_TOOL_DATA")],
        };
        service.ObserveAutomaticSpeech("mac", new(MissumAiAssistantUpdateKind.Completed, message), events.Emit);
        var second = await events.Next("speech.audio");
        await events.Next("speech.complete");
        Assert.All(gateway.Paragraphs, paragraph =>
        {
            Assert.DoesNotContain("DO_NOT_SPEAK", paragraph.Text);
            Assert.Equal(1.0, paragraph.Speed);
            Assert.All(paragraph.Parts!, part => Assert.Equal(1.0, part.Speed));
        });
        await service.HandleAsync("mac", Envelope("microphone.stopSpeech", new { }), events.Emit);
        var count = gateway.Paragraphs.Count;
        service.ObserveAutomaticSpeech("mac", new(MissumAiAssistantUpdateKind.Started, message), events.Emit);
        service.ObserveAutomaticSpeech("mac", new(MissumAiAssistantUpdateKind.Completed, message), events.Emit);
        Assert.Equal(count, gateway.Paragraphs.Count);
        Assert.Equal(message.Id, first.Payload.GetProperty("messageId").GetGuid());
        var sources = SpeechSourceSegmentation.CreateUnits(message.Content);
        Assert.Contains(events.All, item => item.Type == "speech.progress"
            && item.Payload.GetProperty("sourceMessageId").GetGuid() == message.Id
            && item.Payload.TryGetProperty("sourceUnits", out var units)
            && units.ValueKind == JsonValueKind.Array && units.GetArrayLength() > 0);
        Assert.Equal(sources[0].Id, first.Payload.GetProperty("timings")[0].GetProperty("sourceUnitIds")[0].GetString());
        Assert.Equal(sources[^1].Id, second.Payload.GetProperty("timings")[0].GetProperty("sourceUnitIds")[0].GetString());
    }

    [Fact]
    public async Task BrowserFeedbackRejectsStaleEventsAndReplacementPlaybackIds()
    {
        using var gateway = new FakeGateway();
        var plan = BrowserSpeechPlan.Create(Guid.NewGuid(), Guid.NewGuid(), "AI-Nachricht", "Ein Satz.");
        await using var service = CreateService(gateway, plan);
        var events = new EventSink();
        await service.HandleAsync("mac", Envelope("microphone.speak", new { text = "source" }), events.Emit);
        var audio = await events.Next("speech.audio");
        var id = audio.Payload.GetProperty("playbackId").GetGuid();
        await events.Next("speech.complete");
        await Progress(service, "mac", events, id, 0, 5, "playing");
        var count = events.All.Count;
        await Progress(service, "mac", events, id, 0, 4, "paused");
        await Progress(service, "mac", events, Guid.NewGuid(), 0, 6, "ended");
        Assert.Equal(count, events.All.Count);
    }

    [Fact]
    public async Task PauseCommandPreservesRequestedStateWhenLocalFeedbackArrivesFirst()
    {
        using var gateway = new FakeGateway();
        var plan = BrowserSpeechPlan.Create(Guid.NewGuid(), Guid.NewGuid(), "AI-Nachricht", "Ein Satz.");
        await using var service = CreateService(gateway, plan);
        var events = new EventSink();
        await service.HandleAsync("mac", Envelope("microphone.speak", new { text = "source" }), events.Emit);
        var audio = await events.Next("speech.audio");
        var id = audio.Payload.GetProperty("playbackId").GetGuid();
        await events.Next("speech.complete");
        await Progress(service, "mac", events, id, 0, 1, "paused");
        await service.HandleAsync("mac", Envelope("microphone.toggleSpeechPause", new { paused = true }), events.Emit);
        Assert.True((await events.Next("speech.pause")).Payload.GetProperty("paused").GetBoolean());
        await service.HandleAsync("mac", Envelope("microphone.toggleSpeechPause", new { paused = false }), events.Emit);
        Assert.False((await events.Next("speech.pause")).Payload.GetProperty("paused").GetBoolean());
    }

    [Fact]
    public async Task WrongVoiceProviderFailsWithoutAudioOrDesktopFallback()
    {
        using var gateway = new FakeGateway { Provider = "browser-system-voice" };
        var plan = BrowserSpeechPlan.Create(Guid.NewGuid(), Guid.NewGuid(), "AI-Nachricht", "Ein Satz.");
        await using var service = CreateService(gateway, plan);
        var events = new EventSink();
        await service.HandleAsync("mac", Envelope("microphone.speak", new { text = "source" }), events.Emit);
        await events.Next("speech.reset");
        var failed = await events.Next("speech.status", item => item.Payload.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.String);
        Assert.Contains("F5", failed.Payload.GetProperty("error").GetString());
        Assert.DoesNotContain(events.All, item => item.Type == "speech.audio");
        Assert.Empty(gateway.Paragraphs);
        await service.DisposeAsync();
        Assert.Equal(1, gateway.Cancelled);
    }

    [Fact]
    public void ReadFromAnchorKeepsTheExistingSourceRangesAndPunctuationRules()
    {
        var plan = BrowserSpeechPlan.Create(Guid.NewGuid(), Guid.NewGuid(), "AI-Nachricht",
            "# Überschrift\n\n„Erster Absatz.“\n\n„Zweiter Absatz.“", new("paragraph", 1));
        Assert.Single(plan.SourceUnits);
        Assert.Equal("paragraph", plan.SourceUnits[0].Kind);
        Assert.Equal(1, plan.SourceUnits[0].BlockIndex);
        Assert.DoesNotContain(plan.Segments, segment => segment.Text.Contains("Erster", StringComparison.Ordinal));
        Assert.All(plan.Segments, segment => Assert.False(SpeechSourceSegmentation.ContainsForbiddenSpeechQuotation(
            SpeechSourceSegmentation.PrepareForSynthesis(segment))));
        Assert.Throws<InvalidOperationException>(() => BrowserSpeechPlan.Create(Guid.NewGuid(), Guid.NewGuid(), "AI-Nachricht",
            "Text.", new("paragraph", 9)));
    }

    private static BrowserSpeechService CreateService(FakeGateway gateway, BrowserSpeechPlan plan) => new(
        _ => Task.FromResult(new MissumAiClient(new HttpClient(gateway, disposeHandler: false)
            { BaseAddress = new Uri("http://fixture/") }, ownsHttpClient: true)),
        (_, _) => Task.FromResult(plan));

    private static WebBridgeEnvelope Envelope(string type, object payload) =>
        new(1, type, Guid.NewGuid().ToString("D"), JsonSerializer.SerializeToElement(payload, Json), "mac");

    private static Task<bool> Progress(BrowserSpeechService service, string client, EventSink sink,
        Guid id, int sequence, long eventSequence, string state, double seconds = 0) =>
        service.HandleAsync(client, Envelope("speech.playbackProgress", new
        { playbackId = id, sequence, eventSequence, state, positionSeconds = seconds }), sink.Emit);

    private sealed record Event(string Type, JsonElement Payload);
    private sealed class EventSink
    {
        private readonly Channel<Event> _channel = Channel.CreateUnbounded<Event>();
        public ConcurrentQueue<Event> All { get; } = new();
        public Task Emit(string type, object payload, string? requestId)
        {
            var item = new Event(type, JsonSerializer.SerializeToElement(payload, Json));
            All.Enqueue(item);
            _channel.Writer.TryWrite(item);
            return Task.CompletedTask;
        }
        public async Task<Event> Next(string type, Func<Event, bool>? filter = null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (await _channel.Reader.WaitToReadAsync(timeout.Token))
                while (_channel.Reader.TryRead(out var item))
                    if (item.Type == type && (filter is null || filter(item))) return item;
            throw new InvalidOperationException("Missing expected speech event.");
        }
    }

    private sealed class FakeGateway : HttpMessageHandler
    {
        public ConcurrentQueue<SpeechParagraphRequest> Paragraphs { get; } = new();
        public string Provider { get; init; } = SpeechProviderIds.SupertonicF5Cuda;
        public int Ended;
        public int Cancelled;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var now = DateTimeOffset.UtcNow;
            if (path.EndsWith("/paragraphs", StringComparison.Ordinal))
            {
                var paragraph = (await request.Content!.ReadFromJsonAsync<SpeechParagraphRequest>(MissumAiProtocol.CreateJsonOptions(), cancellationToken))!;
                Paragraphs.Enqueue(paragraph);
                var artifact = new ArtifactDescriptor($"speech-{Guid.NewGuid():N}", "speech.wav", "audio/wav", 50,
                    new string('a', 64), now, now.AddHours(1));
                return Response(new SpeechParagraphResponse(artifact, Provider, paragraph.ParagraphIndex, 1, 44_100,
                    paragraph.Parts!.Select(part => new SpeechParagraphTiming(part.SegmentIndex, 0, 1)).ToArray(),
                    SpeechAlignmentStatus.Deterministic));
            }
            if (path.EndsWith("/end", StringComparison.Ordinal)) Interlocked.Increment(ref Ended);
            if (path.EndsWith("/cancel", StringComparison.Ordinal)) Interlocked.Increment(ref Cancelled);
            return Response(new SpeechSessionSnapshot($"session-{Guid.NewGuid():N}", "active", SpeechContentProfile.Prepared,
                Provider, false, now, now));
        }
        private static HttpResponseMessage Response<T>(T payload) => new(HttpStatusCode.OK)
        { Content = JsonContent.Create(payload, options: MissumAiProtocol.CreateJsonOptions()) };
    }
}
