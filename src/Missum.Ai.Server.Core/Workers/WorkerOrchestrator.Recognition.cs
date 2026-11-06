using Missum.Ai.Contracts;

namespace Missum.Ai.Server.Core.Workers;

public sealed partial class WorkerOrchestrator
{
    private readonly SemaphoreSlim _recognitionGate = new(1, 1);
    private readonly Dictionary<string, LiveCaptionProfile> _recognitionOwners = new(StringComparer.Ordinal);

    private async Task AcquireRecognitionAsync(string owner, LiveCaptionProfile profile, CancellationToken cancellationToken)
    {
        await _recognitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_recognitionOwners.ContainsKey(owner)) return;
            try
            {
                if (_recognitionOwners.Count == 0)
                    _ = await _workers.LoadSpeechComponentAsync("stt", cancellationToken).ConfigureAwait(false);
                if (profile == LiveCaptionProfile.Captions && !_recognitionOwners.ContainsValue(LiveCaptionProfile.Captions))
                    _ = await _workers.LoadSpeechComponentAsync("speaker", cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                _recognitionOwners.Add(owner, profile);
            }
            catch
            {
                // A cancelled HTTP load can finish in the worker after the caller
                // leaves. Its release endpoint drains this work before unloading.
                if (_recognitionOwners.Count == 0) await TryReleaseRecognitionAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally { _recognitionGate.Release(); }
    }

    public async Task ReleaseLiveCaptionResourcesAsync(string sessionId) =>
        await ReleaseRecognitionAsync(sessionId).ConfigureAwait(false);

    private async Task ReleaseRecognitionAsync(string owner)
    {
        await _recognitionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_recognitionOwners.Remove(owner) && _recognitionOwners.Count == 0)
                await TryReleaseRecognitionAsync().ConfigureAwait(false);
        }
        finally { _recognitionGate.Release(); }
    }

    private async Task ReleaseIdleRecognitionAsync()
    {
        await _recognitionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            // Clear orphaned recognition from a previous gateway instance, but
            // preserve explicit clients that connected during gateway startup.
            if (_recognitionOwners.Count == 0) await TryReleaseRecognitionAsync().ConfigureAwait(false);
        }
        finally { _recognitionGate.Release(); }
    }

    private async Task TryReleaseRecognitionAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            _ = await _workers.ReleaseRecognitionAsync(timeout.Token).ConfigureAwait(false);
            _runtime.WriteLog("Information", "speech.recognition.release", "Nicht mehr benötigte Spracherkennungsmodelle werden entladen; Vorlesen bleibt verfügbar.");
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            _runtime.WriteLog("Warning", "speech.recognition.release.failed", "Spracherkennung konnte nicht entladen werden: " + exception.Message);
        }
    }
}
