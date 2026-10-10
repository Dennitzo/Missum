using Missum.Core.Models;

namespace Missum.App.Services;

public sealed partial class MissumAiAssistantService
{
    private readonly object _automaticSpeechGate = new();
    private SpeechStreamingSession? _automaticSpeechSession;
    private Guid? _automaticSpeechRunId;

    internal void ObserveAutomaticSpeech(MissumAiAssistantUpdate update,
        Func<MissumAiSpeechUpdate, Task> status,
        Func<SpeechPlaybackProgress, Task> progress)
    {
        if (Volatile.Read(ref _disposed) != 0 || update.Message.Role != ChatRole.Assistant) return;
        lock (_automaticSpeechGate)
        {
            // Replayed Started events cannot undo a footer Stop for this run.
            // A new continuation of the same message has a different run ID.
            if (update.Kind == MissumAiAssistantUpdateKind.Started
                && _automaticSpeechSession?.MessageId == update.Message.Id
                && _automaticSpeechSession.WasCancelled && _automaticSpeechRunId == update.LocalRunId) return;
            if (update.Kind == MissumAiAssistantUpdateKind.Started
                && (_automaticSpeechSession?.MessageId != update.Message.Id || _automaticSpeechSession.WasCancelled))
            {
                _automaticSpeechSession?.Cancel();
                SpeechStreamingSession? session = null;
                session = new(update.Message, async (text, token) =>
                {
                    if (!IsCurrentAutomaticSpeech(session)) return;
                    await SpeakCoreAsync(update.Message.SessionId, text, null,
                        speech =>
                        {
                            if (!IsCurrentAutomaticSpeech(session) || session!.WasCancelled && speech.IsActive) return Task.CompletedTask;
                            if (!speech.IsActive)
                            {
                                if (speech.Status == "Abgebrochen") session!.Cancel();
                                // The queue owns completion and stop state across all chunks.
                                return speech.Error is null && !session!.WasCancelled
                                    ? status(AutomaticSpeechStatus(session, new(true, "Antwort wird fortlaufend vorgelesen", "Warte auf weiteren Antworttext.")))
                                    : Task.CompletedTask;
                            }
                            return status(AutomaticSpeechStatus(session!, speech));
                        },
                        playback => IsCurrentAutomaticSpeech(session) ? progress(playback with
                        { ControlMessageId = session!.MessageId, ControlPlaybackId = session.PlaybackId, OwnerClientId = "desktop" }) : Task.CompletedTask,
                        null, null,
                        // Never resolve an attachment or the full saved message for a delta.
                        // Live source ranges are deliberately omitted; a chunk is not the
                        // complete message used by the existing read-from-here highlighter.
                        new(text, "AI-Antwort (live)", null, null, update.Message.ContentProfile), token).ConfigureAwait(false);
                }, microphone.WaitForSpeechResumeAsync, microphone.BeginSpeechSession(resetPause: true),
                    CancellationToken.None);
                _automaticSpeechSession = session;
                _automaticSpeechRunId = update.LocalRunId;
                _ = PublishAutomaticSpeechStartAsync(session, status);
                _ = ObserveAutomaticSpeechCompletionAsync(session, status);
            }
            _automaticSpeechSession?.Observe(update);
        }
    }

    private bool IsCurrentAutomaticSpeech(SpeechStreamingSession? session)
    {
        lock (_automaticSpeechGate) return session is not null && ReferenceEquals(_automaticSpeechSession, session);
    }

    private async Task ObserveAutomaticSpeechCompletionAsync(SpeechStreamingSession session, Func<MissumAiSpeechUpdate, Task> status)
    {
        await session.Completion.ConfigureAwait(false);
        if (!IsCurrentAutomaticSpeech(session)) return;
        try
        {
            await status(AutomaticSpeechStatus(session, new(false, session.Failure is not null ? "Vorlesen fehlgeschlagen"
                : session.WasCancelled ? "Abgebrochen" : "Abgeschlossen", Error: session.Failure?.Message))).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The WebView may have been detached while audio was finishing.
            RunDiagnostic(logger, session.MessageId.ToString("D"), "automatic-speech-status-detached", exception);
        }
    }

    private void CancelAutomaticSpeech()
    {
        lock (_automaticSpeechGate) _automaticSpeechSession?.Cancel();
    }

    private static MissumAiSpeechUpdate AutomaticSpeechStatus(SpeechStreamingSession session, MissumAiSpeechUpdate status)
        => status with { SessionId = session.SessionId, SourceMessageId = session.MessageId,
            PlaybackId = session.PlaybackId, IsAutomatic = true };

    private async Task PublishAutomaticSpeechStartAsync(SpeechStreamingSession session, Func<MissumAiSpeechUpdate, Task> status)
    {
        try
        {
            if (IsCurrentAutomaticSpeech(session) && !session.WasCancelled)
                await status(AutomaticSpeechStatus(session, new(true, "Antwort wird fortlaufend vorgelesen", "Warte auf Antworttext."))).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { RunDiagnostic(logger, session.MessageId.ToString("D"), "automatic-speech-start-detached", exception); }
    }
}
