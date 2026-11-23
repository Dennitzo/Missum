using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Missum.Ai.Client;
using Missum.Ai.Contracts;

namespace Missum.App.Services.Meetings;

/// <summary>
/// Turns every participant's microphone stream into transcript entries. Audio
/// is cut into utterances at speech pauses on the host and each utterance is
/// recognized through the same live-caption speech service (Whisper,
/// dictation profile) that Missum's voice control uses.
/// </summary>
public sealed class MeetingTranscriptionHub : IAsyncDisposable
{
    private const int SampleRate = MissumAiProtocol.LiveCaptionSampleRate;
    private const int BytesPerMillisecond = SampleRate * 2 / 1000;
    private const int WindowMilliseconds = 6_000;

    private readonly MeetingHostService _host;
    private readonly MissumAiConnectionService _connection;
    private readonly SettingsCoordinator _settings;
    private readonly ILogger<MeetingTranscriptionHub> _logger;
    private readonly ConcurrentDictionary<(Guid Meeting, Guid Speaker), UtteranceSegmenter> _segmenters = new();
    private readonly Channel<Utterance> _queue = Channel.CreateBounded<Utterance>(new BoundedChannelOptions(64)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest,
    });
    private readonly Dictionary<Guid, (string SessionId, long Sequence)> _captionSessions = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _worker;

    public MeetingTranscriptionHub(
        MeetingHostService host,
        MissumAiConnectionService connection,
        SettingsCoordinator settings,
        ILogger<MeetingTranscriptionHub> logger)
    {
        _host = host;
        _connection = connection;
        _settings = settings;
        _logger = logger;
        _host.AudioReceived += OnAudio;
        _worker = Task.Run(() => ProcessAsync(_lifetime.Token));
    }

    private void OnAudio(Guid meetingId, Guid speakerId, string speakerName, byte[] pcm)
    {
        var segmenter = _segmenters.GetOrAdd((meetingId, speakerId), static _ => new UtteranceSegmenter());
        foreach (var utterance in segmenter.Push(pcm))
            _queue.Writer.TryWrite(new Utterance(meetingId, speakerId, speakerName, utterance, DateTimeOffset.UtcNow));
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        MissumAiClient? client = null;
        try
        {
            await foreach (var utterance in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    client ??= await _connection.CreateClientAsync(cancellationToken).ConfigureAwait(false);
                    var text = await RecognizeAsync(client, utterance, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    _host.ApplyLocal(utterance.MeetingId, new MeetingOp
                    {
                        Type = MeetingOpTypes.TranscriptAdd,
                        ActorId = utterance.SpeakerId,
                        ActorName = utterance.SpeakerName,
                        Transcript = new MeetingTranscriptEntry(Guid.NewGuid(), utterance.SpeakerId, utterance.SpeakerName, text.Trim(), utterance.At),
                    });
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    MeetingLog.SpeechFailed(_logger, exception);
                    client?.Dispose();
                    client = null;
                    lock (_captionSessions) _captionSessions.Clear();
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { client?.Dispose(); }
    }

    private async Task<string?> RecognizeAsync(MissumAiClient client, Utterance utterance, CancellationToken cancellationToken)
    {
        (string SessionId, long Sequence) session;
        lock (_captionSessions) _captionSessions.TryGetValue(utterance.SpeakerId, out session);
        if (session.SessionId is null)
        {
            var language = _settings.Current.LiveCaptionLanguage;
            var created = await client.CreateLiveCaptionSessionAsync(new LiveCaptionSessionRequest(
                string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase) ? null : language,
                LiveCaptionMode.Transcribe,
                SampleRate,
                1,
                WindowMilliseconds,
                0,
                LiveCaptionProfile.Dictation), cancellationToken).ConfigureAwait(false);
            session = (created.SessionId, 0);
        }
        var wave = MicrophoneTranscriptionService.CreatePcm16Wave(utterance.Pcm);
        var response = await client.SendLiveCaptionChunkAsync(
            session.SessionId,
            session.Sequence,
            wave,
            new LiveCaptionChunkMetadata(Guid.NewGuid().ToString("N"), 0, 0, true),
            cancellationToken).ConfigureAwait(false);
        lock (_captionSessions) _captionSessions[utterance.SpeakerId] = (session.SessionId, session.Sequence + 1);
        return FirstNonEmpty(response.StableText, response.Text, response.Transcript);
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    public async ValueTask DisposeAsync()
    {
        _host.AudioReceived -= OnAudio;
        _queue.Writer.TryComplete();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try { await _worker.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        _lifetime.Dispose();
    }

    private sealed record Utterance(Guid MeetingId, Guid SpeakerId, string SpeakerName, byte[] Pcm, DateTimeOffset At);
}

/// <summary>
/// Energy-based voice activity detection over PCM16 mono 16 kHz. It keeps a
/// short pre-roll so first syllables survive, ends an utterance after a pause
/// and splits long speech at the recognizer's window size.
/// </summary>
public sealed class UtteranceSegmenter
{
    internal const int BytesPerMillisecond = 32;
    internal const double SpeechThreshold = 0.018;
    internal const int PauseMilliseconds = 750;
    internal const int MinimumSpeechMilliseconds = 350;
    internal const int MaximumMilliseconds = 6_000;
    private const int PreRollMilliseconds = 300;

    private readonly List<byte> _current = [];
    private readonly Queue<byte[]> _preRoll = new();
    private int _preRollBytes;
    private bool _speaking;
    private int _silence;
    private int _speech;

    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> pcm)
    {
        var finished = new List<byte[]>();
        if (pcm.Length < 2) return finished;
        var milliseconds = pcm.Length / BytesPerMillisecond;
        var loud = Rms(pcm) >= SpeechThreshold;
        lock (_current)
        {
            if (!_speaking)
            {
                if (!loud)
                {
                    _preRoll.Enqueue(pcm.ToArray());
                    _preRollBytes += pcm.Length;
                    while (_preRollBytes > PreRollMilliseconds * BytesPerMillisecond && _preRoll.Count > 1)
                        _preRollBytes -= _preRoll.Dequeue().Length;
                    return finished;
                }
                _speaking = true;
                _silence = 0;
                _speech = 0;
                foreach (var chunk in _preRoll) _current.AddRange(chunk);
                _preRoll.Clear();
                _preRollBytes = 0;
            }

            _current.AddRange(pcm);
            if (loud) { _speech += milliseconds; _silence = 0; }
            else _silence += milliseconds;

            var length = _current.Count / BytesPerMillisecond;
            if (_silence >= PauseMilliseconds || length >= MaximumMilliseconds)
            {
                if (_speech >= MinimumSpeechMilliseconds) finished.Add(_current.ToArray());
                _current.Clear();
                // A forced split keeps listening; a pause returns to idle.
                _speaking = _silence < PauseMilliseconds;
                _speech = 0;
                _silence = 0;
            }
        }
        return finished;
    }

    internal static double Rms(ReadOnlySpan<byte> pcm)
    {
        double sum = 0;
        var samples = pcm.Length / 2;
        for (var i = 0; i < samples; i++)
        {
            var sample = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8)) / 32768.0;
            sum += sample * sample;
        }
        return samples == 0 ? 0 : Math.Sqrt(sum / samples);
    }
}
