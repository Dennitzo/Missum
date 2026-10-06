using System.Collections.Concurrent;
using System.Text.Json;
using Missum.Ai.Client;
using Missum.Ai.Contracts;
using Missum.Core.Models;

namespace Missum.App.Services;

/// <summary>Produces the desktop F5 speech plan for playback exclusively in its requesting browser.</summary>
public sealed class BrowserSpeechService : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<MissumAiClient>> _createClient;
    private readonly Func<BrowserSpeechRequest, CancellationToken, Task<BrowserSpeechPlan>> _preparePlan;
    private readonly ConcurrentDictionary<string, Playback> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ArtifactDescriptor> _artifacts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Uri> _artifactGateways = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _artifactOrder = new();
    private readonly ConcurrentDictionary<string, Guid> _lastAutomaticMessages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, Task> _workers = new();
    private int _disposed;

    public BrowserSpeechService(MissumAiAssistantService assistant, MissumAiConnectionService connection)
        : this(connection.CreateClientAsync, assistant.PrepareBrowserSpeechAsync) { }

    internal BrowserSpeechService(Func<CancellationToken, Task<MissumAiClient>> createClient,
        Func<BrowserSpeechRequest, CancellationToken, Task<BrowserSpeechPlan>> preparePlan)
    {
        _createClient = createClient;
        _preparePlan = preparePlan;
    }

    public bool TryGetArtifact(string artifactId, out ArtifactDescriptor? artifact) =>
        _artifacts.TryGetValue(artifactId, out artifact);

    public bool TryGetArtifactGateway(string artifactId, out Uri? gateway) =>
        _artifactGateways.TryGetValue(artifactId, out gateway);

    public async Task<bool> HandleAsync(string clientId, WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        switch (envelope.Type)
        {
            case "microphone.speak":
                var request = ParseRequest(envelope.Payload);
                var manual = Replace(clientId, emit, envelope.RequestId, cancellationToken);
                Track(manual, RunManualAsync(manual, request));
                return true;
            case "microphone.stopSpeech":
                await StopAsync(clientId, emit, envelope.RequestId).ConfigureAwait(false);
                return true;
            case "microphone.toggleSpeechPause":
                if (_clients.TryGetValue(clientId, out var paused))
                {
                    var isPaused = envelope.Payload.TryGetProperty("paused", out var requestedPause)
                        && requestedPause.ValueKind is JsonValueKind.True or JsonValueKind.False
                            ? requestedPause.GetBoolean() : paused.TogglePause();
                    paused.SetPaused(isPaused);
                    await emit("speech.pause", new { playbackId = paused.Id, paused = isPaused }, envelope.RequestId)
                        .ConfigureAwait(false);
                }
                return true;
            case "speech.playbackProgress":
                await ObservePlaybackAsync(clientId, envelope.Payload).ConfigureAwait(false);
                return true;
            default:
                return false;
        }
    }

    public void ObserveAutomaticSpeech(string clientId, MissumAiAssistantUpdate update,
        Func<string, object, string?, Task> emit, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0 || update.Message.Role != ChatRole.Assistant) return;
        if (update.Kind == MissumAiAssistantUpdateKind.Started)
        {
            if (_lastAutomaticMessages.TryGetValue(clientId, out var last) && last == update.Message.Id) return;
            _lastAutomaticMessages[clientId] = update.Message.Id;
            var playback = Replace(clientId, emit, null, cancellationToken);
            playback.SessionId = update.Message.SessionId;
            playback.MessageId = update.Message.Id;
            playback.CurrentMessage = update.Message;
            playback.SourceKind = "AI-Antwort (live)";
            playback.Ready = ResetAsync(playback);
            var automatic = new SpeechStreamingSession(update.Message, async (text, token) =>
            {
                await playback.Ready.ConfigureAwait(false);
                var plan = CreateAutomaticPlan(playback, text);
                await ProducePlanAsync(playback, plan, token).ConfigureAwait(false);
            }, playback.WaitForResumeAsync, cancellationToken: playback.Token);
            playback.Automatic = automatic;
            Track(playback, FinishAutomaticAsync(playback, automatic));
        }
        if (_clients.TryGetValue(clientId, out var current)
            && current.Automatic?.MessageId == update.Message.Id)
        {
            current.CurrentMessage = update.Message;
            current.Automatic.Observe(update);
        }
    }

    private static BrowserSpeechPlan CreateAutomaticPlan(Playback playback, string text)
    {
        var message = playback.CurrentMessage
            ?? throw new InvalidOperationException("Der Nachrichtenstand für die laufende Sprachausgabe fehlt.");
        var chunk = BrowserSpeechPlan.Create(message.SessionId, message.Id, "AI-Antwort (live)", text);
        var units = SpeechSourceSegmentation.CreateUnits(message.Content);
        var segments = new List<PreparedSpeechSegment>(chunk.Segments.Count);
        foreach (var segment in chunk.Segments)
        {
            var spoken = SpeechSourceSegmentation.PrepareForSynthesis(segment).Trim();
            SpeechSourceUnit? matched = null;
            for (var index = playback.AutomaticUnitIndex; index < units.Count; index++)
            {
                var candidate = SpeechSourceSegmentation.NormalizeSpeechPunctuation(
                    MicrophoneTranscriptionService.PrepareSpeechText(units[index].SpeechText)).Trim();
                var offset = index == playback.AutomaticUnitIndex ? Math.Min(playback.AutomaticUnitOffset, candidate.Length) : 0;
                var found = candidate.IndexOf(spoken, offset, StringComparison.Ordinal);
                if (found < 0) continue;
                matched = units[index];
                playback.AutomaticUnitIndex = index;
                playback.AutomaticUnitOffset = found + spoken.Length;
                break;
            }
            // Narrative tool blocks are not part of the visible answer. Omit
            // their range rather than highlight an unrelated chunk-local b1/u1.
            segments.Add(segment with
            {
                SourceUnitIds = matched is null ? [] : [matched.Id],
                PlaybackBatchId = matched?.BlockId,
            });
        }
        return new(message.SessionId, message.Id, "AI-Antwort (live)", units, segments,
            SpeechSourceSegmentation.CreatePlaybackBatches(segments));
    }

    private Playback Replace(string clientId, Func<string, object, string?, Task> emit,
        string? requestId, CancellationToken token)
    {
        var playback = new Playback(clientId, emit, requestId, token);
        _clients.AddOrUpdate(clientId, playback, (_, previous) =>
        {
            previous.Cancel();
            if (Volatile.Read(ref previous.ProducerExited) != 0) previous.Dispose();
            return playback;
        });
        return playback;
    }

    private void Track(Playback playback, Task worker)
    {
        _workers[playback.Id] = worker;
        _ = RemoveWorkerAsync(playback.Id, worker);
    }

    private async Task RemoveWorkerAsync(Guid id, Task worker)
    {
        try { await worker.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        finally { _workers.TryRemove(id, out _); }
    }

    private bool IsCurrent(Playback playback) => !playback.Token.IsCancellationRequested
        && _clients.TryGetValue(playback.ClientId, out var current) && ReferenceEquals(playback, current);

    private async Task ResetAsync(Playback playback)
    {
        await playback.Emit("speech.reset", new { playbackId = playback.Id, firstSequence = 0 }, playback.RequestId)
            .ConfigureAwait(false);
        await StatusAsync(playback, true, "Vorlesen wird vorbereitet").ConfigureAwait(false);
    }

    private async Task RunManualAsync(Playback playback, BrowserSpeechRequest request)
    {
        try
        {
            await ResetAsync(playback).ConfigureAwait(false);
            var plan = await _preparePlan(request, playback.Token).ConfigureAwait(false);
            await ProducePlanAsync(playback, plan, playback.Token).ConfigureAwait(false);
            await FinishProducingAsync(playback).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (playback.Token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await FailAsync(playback, exception.Message).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseGatewayAsync(playback).ConfigureAwait(false);
            Volatile.Write(ref playback.ProducerExited, 1);
            if (playback.Token.IsCancellationRequested || Volatile.Read(ref playback.CompletionSent) != 0) playback.Dispose();
        }
    }

    private async Task FinishAutomaticAsync(Playback playback, SpeechStreamingSession session)
    {
        try
        {
            await playback.Ready.ConfigureAwait(false);
            await session.Completion.ConfigureAwait(false);
            if (session.Failure is not null) throw session.Failure;
            if (session.WasCancelled)
            {
                if (IsCurrent(playback))
                    await StopAsync(playback.ClientId, playback.Emit, playback.RequestId).ConfigureAwait(false);
                return;
            }
            await FinishProducingAsync(playback).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (playback.Token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await FailAsync(playback, exception.Message).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseGatewayAsync(playback).ConfigureAwait(false);
            Volatile.Write(ref playback.ProducerExited, 1);
            if (playback.Token.IsCancellationRequested || Volatile.Read(ref playback.CompletionSent) != 0) playback.Dispose();
        }
    }

    private async Task ProducePlanAsync(Playback playback, BrowserSpeechPlan plan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        playback.SessionId = plan.SessionId;
        playback.MessageId = plan.MessageId;
        playback.SourceKind = plan.SourceKind;
        var offset = playback.SegmentCount;
        playback.SegmentCount += plan.Segments.Count;
        await ProgressAsync(playback, 0, [], SpeechPlaybackState.Buffering,
            plan.MessageId is null ? null : plan.SourceUnits).ConfigureAwait(false);
        if (playback.Client is null)
        {
            playback.Client = await _createClient(token).ConfigureAwait(false);
            var session = await playback.Client.CreateSpeechSessionAsync(
                new SpeechSessionRequest(SpeechContentProfile.Prepared, "de"), token).ConfigureAwait(false);
            playback.GatewaySessionId = session.SessionId;
            if (!string.Equals(session.Provider, SpeechProviderIds.SupertonicF5Cuda, StringComparison.Ordinal))
                throw new InvalidDataException("Die Sprachausgabe verwendet nicht das konfigurierte Supertonic-F5-Modell.");
        }
        foreach (var batch in plan.Batches)
        {
            // The browser owns playback. Two outstanding WAVs means exactly
            // the current section and one preloaded successor, even when blocked.
            await playback.WaitForSlotAsync(token).ConfigureAwait(false);
            var parts = batch.SegmentIndexes.Select(index => new SpeechParagraphPart(index + offset,
                SpeechSourceSegmentation.PrepareForSynthesis(plan.Segments[index]), 1.0, 0, 0)).ToArray();
            var text = string.Join(' ', parts.Select(part => part.Text));
            if (string.IsNullOrWhiteSpace(text) || text.Length > SpeechSourceSegmentation.MaximumSegmentCharacters)
                throw new InvalidDataException("Ein vorbereitetes Sprachsegment ist leer oder zu lang.");
            var sequence = playback.NextSequence++;
            var result = await playback.Client.SynthesizeSpeechParagraphAsync(playback.GatewaySessionId!,
                new SpeechParagraphRequest(text, sequence, 1.0, parts), token).ConfigureAwait(false);
            if (!string.Equals(result.Provider, SpeechProviderIds.SupertonicF5Cuda, StringComparison.Ordinal)
                || !double.IsFinite(result.DurationSeconds) || result.DurationSeconds <= 0
                || !string.Equals(result.Artifact.MediaType, "audio/wav", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(result.Artifact.ArtifactId))
                throw new InvalidDataException("Das Gateway hat keine gültige Supertonic-F5-Audiodatei geliefert.");
            var timings = PrepareTimings(result, parts, plan, offset);
            var frame = new AudioFrame(sequence, result.DurationSeconds, timings);
            playback.AddFrame(frame);
            RegisterArtifact(result.Artifact, playback.Client.BaseAddress
                ?? throw new InvalidOperationException("Die Gatewayadresse für die Sprachausgabe fehlt."));
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(playback)) return;
            await playback.Emit("speech.audio", new
            {
                playbackId = playback.Id, sequence,
                url = $"gateway-artifacts/{Uri.EscapeDataString(result.Artifact.ArtifactId)}",
                durationSeconds = result.DurationSeconds, timings,
                pauseAfterMilliseconds = batch.PauseAfterMilliseconds,
                messageId = plan.MessageId,
            }, playback.RequestId).ConfigureAwait(false);
        }
    }

    private static List<BrowserTiming> PrepareTimings(SpeechParagraphResponse result,
        SpeechParagraphPart[] parts, BrowserSpeechPlan plan, int offset)
    {
        var output = new List<BrowserTiming>();
        foreach (var part in parts)
        {
            var matching = result.Timings?.Where(timing => timing.SegmentIndex == part.SegmentIndex).ToArray() ?? [];
            if (matching.Length > 1 || (parts.Length > 1 && matching.Length != 1))
                throw new InvalidDataException("Die Satzmarkierung im erzeugten Audio ist nicht eindeutig.");
            var start = matching.FirstOrDefault()?.StartSeconds ?? 0;
            var end = matching.FirstOrDefault()?.EndSeconds ?? result.DurationSeconds;
            if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start
                || end > result.DurationSeconds + 0.05)
                throw new InvalidDataException("Das Gateway hat ungültige Satzzeitmarken geliefert.");
            IReadOnlyList<string> ids = plan.MessageId is null ? [] : plan.Segments[part.SegmentIndex - offset].SourceUnitIds.Take(1).ToArray();
            output.Add(new(part.SegmentIndex, start, end, ids));
        }
        return output;
    }

    private void RegisterArtifact(ArtifactDescriptor artifact, Uri gateway)
    {
        _artifactGateways[artifact.ArtifactId] = gateway;
        if (_artifacts.TryAdd(artifact.ArtifactId, artifact)) _artifactOrder.Enqueue(artifact.ArtifactId);
        while (_artifacts.Count > 4096 && _artifactOrder.TryDequeue(out var expired))
        {
            _artifacts.TryRemove(expired, out _);
            _artifactGateways.TryRemove(expired, out _);
        }
    }

    private async Task FinishProducingAsync(Playback playback)
    {
        if (!IsCurrent(playback)) return;
        if (playback.Client is not null && playback.GatewaySessionId is not null)
        {
            _ = await playback.Client.EndSpeechSessionAsync(playback.GatewaySessionId, playback.Token).ConfigureAwait(false);
            playback.GatewayFinished = true;
        }
        playback.ProductionFinished = true;
        await playback.Emit("speech.complete", new { playbackId = playback.Id }, playback.RequestId).ConfigureAwait(false);
        await CompleteIfPlayedAsync(playback).ConfigureAwait(false);
    }

    private async Task ObservePlaybackAsync(string clientId, JsonElement payload)
    {
        if (!_clients.TryGetValue(clientId, out var playback)
            || !TryGuid(payload, "playbackId", out var id) || id != playback.Id
            || !payload.TryGetProperty("sequence", out var sequenceValue) || !sequenceValue.TryGetInt32(out var sequence)
            || !payload.TryGetProperty("eventSequence", out var eventValue) || !eventValue.TryGetInt64(out var eventSequence)
            || !payload.TryGetProperty("state", out var stateValue) || stateValue.ValueKind != JsonValueKind.String) return;
        var state = stateValue.GetString();
        if (state is not ("playing" or "paused" or "ended" or "blocked" or "failed")) return;
        if (!playback.AcceptProgress(sequence, eventSequence, out var frame)) return;
        var position = payload.TryGetProperty("positionSeconds", out var positionValue) && positionValue.TryGetDouble(out var seconds)
            && double.IsFinite(seconds) ? Math.Clamp(seconds, 0, frame!.Duration) : 0;
        if (state == "failed") { await FailAsync(playback, "Die Audiodatei konnte im Browser nicht wiedergegeben werden.").ConfigureAwait(false); return; }
        if (state == "blocked") { await StatusAsync(playback, true, "Wiedergabe wartet auf Start im Browser").ConfigureAwait(false); return; }
        if (state == "ended")
        {
            playback.CompleteFrame(sequence);
            await CompleteIfPlayedAsync(playback).ConfigureAwait(false);
            return;
        }
        playback.SetPaused(state == "paused");
        var timing = frame!.Timings.LastOrDefault(item => position >= item.StartSeconds) ?? frame.Timings[0];
        await ProgressAsync(playback, timing.SegmentIndex, timing.SourceUnitIds,
            state == "paused" ? SpeechPlaybackState.Paused : SpeechPlaybackState.Playing).ConfigureAwait(false);
        await StatusAsync(playback, true, state == "paused" ? "Sprachausgabe pausiert" : "Sprachausgabe wird wiedergegeben")
            .ConfigureAwait(false);
    }

    private async Task CompleteIfPlayedAsync(Playback playback)
    {
        if (!playback.ProductionFinished || playback.OutstandingCount != 0 || !IsCurrent(playback)
            || Interlocked.Exchange(ref playback.CompletionSent, 1) != 0) return;
        await ProgressAsync(playback, playback.SegmentCount, [], SpeechPlaybackState.Completed).ConfigureAwait(false);
        await StatusAsync(playback, false, "Abgeschlossen").ConfigureAwait(false);
        if (Volatile.Read(ref playback.ProducerExited) != 0) playback.Dispose();
    }

    private Task ProgressAsync(Playback playback, int segment, IReadOnlyList<string> ids,
        SpeechPlaybackState state, IReadOnlyList<SpeechSourceUnit>? units = null)
    {
        if (!IsCurrent(playback)) return Task.CompletedTask;
        return playback.Emit("speech.progress", SpeechPlaybackProgressBridge.ToPayload(new(playback.SessionId,
            playback.MessageId, playback.SourceKind, playback.Id, Interlocked.Increment(ref playback.ProgressSequence),
            segment, playback.SegmentCount, ids, state, units)), playback.RequestId);
    }

    private Task StatusAsync(Playback playback, bool active, string status, string? error = null) =>
        IsCurrent(playback) ? playback.Emit("speech.status", new
        { active, status, model = "Supertonic F5 Ultra", cacheHit = false, error }, playback.RequestId) : Task.CompletedTask;

    private async Task FailAsync(Playback playback, string error)
    {
        if (!IsCurrent(playback)) return;
        try
        {
            await ProgressAsync(playback, 0, [], SpeechPlaybackState.Cancelled).ConfigureAwait(false);
            await StatusAsync(playback, false, "Vorlesen fehlgeschlagen", error).ConfigureAwait(false);
            await playback.Emit("speech.reset", new { playbackId = (Guid?)null }, playback.RequestId).ConfigureAwait(false);
        }
        finally
        {
            playback.Cancel();
            if (Volatile.Read(ref playback.ProducerExited) != 0) playback.Dispose();
        }
    }

    private async Task StopAsync(string clientId, Func<string, object, string?, Task> emit, string? requestId)
    {
        if (_clients.TryRemove(clientId, out var playback))
        {
            playback.Cancel();
            await emit("speech.progress", SpeechPlaybackProgressBridge.ToPayload(new(playback.SessionId,
                playback.MessageId, playback.SourceKind, playback.Id, Interlocked.Increment(ref playback.ProgressSequence),
                0, playback.SegmentCount, [], SpeechPlaybackState.Cancelled)), requestId).ConfigureAwait(false);
            if (Volatile.Read(ref playback.ProducerExited) != 0) playback.Dispose();
        }
        await emit("speech.reset", new { playbackId = (Guid?)null }, requestId).ConfigureAwait(false);
        await emit("speech.status", new { active = false, status = "Abgebrochen" }, requestId).ConfigureAwait(false);
    }

    private static async Task ReleaseGatewayAsync(Playback playback)
    {
        if (playback.Client is not { } client) return;
        try
        {
            if (!playback.GatewayFinished && playback.GatewaySessionId is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                _ = await client.CancelSpeechSessionAsync(playback.GatewaySessionId, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        finally { client.Dispose(); playback.Client = null; }
    }

    private static BrowserSpeechRequest ParseRequest(JsonElement payload)
    {
        var text = OptionalText(payload, "text", 100_000);
        _ = TryGuid(payload, "sessionId", out var sessionId);
        Guid? messageId = TryGuid(payload, "messageId", out var requestedId) ? requestedId : null;
        SpeechStartAnchor? anchor = null;
        var anchorSource = payload.TryGetProperty("startAnchor", out var suppliedAnchor) ? suppliedAnchor : payload;
        if (anchorSource.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Die gewählte Vorlesestelle ist ungültig.");
        if (anchorSource.TryGetProperty("kind", out _))
        {
            var kind = OptionalText(anchorSource, "kind", 32);
            if (!anchorSource.TryGetProperty("blockIndex", out var index) || !index.TryGetInt32(out var blockIndex)
                || blockIndex < 0 || kind is not ("heading" or "paragraph" or "listItem" or "tableRow" or "quote" or "math" or "code"))
                throw new InvalidOperationException("Die gewählte Vorlesestelle ist ungültig.");
            anchor = new(kind, blockIndex);
        }
        DateTimeOffset? updated = payload.TryGetProperty("messageUpdatedAt", out var updatedValue) && updatedValue.ValueKind == JsonValueKind.String
            && updatedValue.TryGetDateTimeOffset(out var timestamp) ? timestamp : null;
        if (messageId is not null && sessionId == Guid.Empty)
            throw new InvalidOperationException("Für die gewählte Nachricht fehlt die Sitzung.");
        if (anchor is not null && (messageId is null || updated is null))
            throw new InvalidOperationException("Der Nachrichtenstand für den Vorlesestart fehlt.");
        if (messageId is null && string.IsNullOrWhiteSpace(text) && sessionId == Guid.Empty)
            throw new InvalidOperationException("Es ist kein vorlesbarer Text vorhanden.");
        return new(sessionId, text, messageId, anchor, updated, OptionalText(payload, "messageExcerpt", 100_000));
    }

    private static string? OptionalText(JsonElement payload, string name, int maximum)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length > maximum)
            throw new InvalidOperationException($"Das Feld {name} ist ungültig oder zu lang.");
        return text;
    }

    private static bool TryGuid(JsonElement payload, string name, out Guid id)
    {
        id = Guid.Empty;
        return payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.TryGetGuid(out id) && id != Guid.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var playback in _clients.Values) playback.Cancel();
        try { await Task.WhenAll(_workers.Values).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        foreach (var playback in _clients.Values) playback.Dispose();
        _clients.Clear();
        _artifacts.Clear();
        _artifactGateways.Clear();
    }

    private sealed record BrowserTiming(int SegmentIndex, double StartSeconds, double EndSeconds,
        IReadOnlyList<string> SourceUnitIds);
    private sealed record AudioFrame(int Sequence, double Duration, IReadOnlyList<BrowserTiming> Timings);

    private sealed class Playback : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellation;
        private readonly CancellationToken _token;
        private readonly SemaphoreSlim _changed = new(0);
        private readonly Dictionary<int, AudioFrame> _frames = [];
        private readonly HashSet<int> _completed = [];
        private TaskCompletionSource? _resume;
        private long _lastFeedback;
        private int _disposed;
        public Playback(string clientId, Func<string, object, string?, Task> emit,
            string? requestId, CancellationToken token)
        {
            ClientId = clientId;
            Emit = emit;
            RequestId = requestId;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            _token = _cancellation.Token;
        }
        public Guid Id { get; } = Guid.NewGuid();
        public string ClientId { get; }
        public Func<string, object, string?, Task> Emit { get; }
        public string? RequestId { get; }
        public CancellationToken Token => _token;
        public Guid SessionId { get; set; }
        public Guid? MessageId { get; set; }
        public string SourceKind { get; set; } = "Vorlesen";
        public int SegmentCount { get; set; }
        public int NextSequence { get; set; }
        public long ProgressSequence;
        public int CompletionSent;
        public int ProducerExited;
        public bool ProductionFinished { get; set; }
        public bool GatewayFinished { get; set; }
        public MissumAiClient? Client { get; set; }
        public string? GatewaySessionId { get; set; }
        public SpeechStreamingSession? Automatic { get; set; }
        public ChatMessage? CurrentMessage { get; set; }
        public int AutomaticUnitIndex { get; set; }
        public int AutomaticUnitOffset { get; set; }
        public Task Ready { get; set; } = Task.CompletedTask;
        public int OutstandingCount { get { lock (_gate) return _frames.Count - _completed.Count; } }

        public void AddFrame(AudioFrame frame) { lock (_gate) _frames.Add(frame.Sequence, frame); }
        public bool AcceptProgress(int sequence, long eventSequence, out AudioFrame? frame)
        {
            lock (_gate)
            {
                frame = null;
                if (eventSequence <= _lastFeedback || _completed.Contains(sequence) || !_frames.TryGetValue(sequence, out frame)) return false;
                _lastFeedback = eventSequence;
                return true;
            }
        }
        public void CompleteFrame(int sequence) { lock (_gate) _completed.Add(sequence); _changed.Release(); }
        public async Task WaitForSlotAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                await WaitForResumeAsync(cancellationToken).ConfigureAwait(false);
                if (OutstandingCount < 2) return;
                await _changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        public Task WaitForResumeAsync(CancellationToken cancellationToken)
        {
            lock (_gate) return _resume?.Task.WaitAsync(cancellationToken) ?? Task.CompletedTask;
        }
        public bool TogglePause()
        {
            lock (_gate) { var paused = _resume is null; SetPaused(paused); return paused; }
        }
        public void SetPaused(bool paused)
        {
            lock (_gate)
            {
                if (paused) _resume ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                else { _resume?.TrySetResult(); _resume = null; }
            }
        }
        public void Cancel()
        {
            try { _cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            Automatic?.Cancel();
            SetPaused(false);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Automatic?.Dispose();
            _cancellation.Dispose();
            _changed.Dispose();
        }
    }
}
