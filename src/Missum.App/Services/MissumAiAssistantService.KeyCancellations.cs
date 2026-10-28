using System.Text.Json;

namespace Missum.App.Services;

public sealed partial class MissumAiAssistantService
{
    private sealed record PendingRunKeyCancellation(Guid SessionId, string IdempotencyKey);
    private string PendingRunKeysPath => Path.Combine(settings.DataDirectory, "Diagnostics", "pending-run-key-cancellations.json");

    private async Task SetPendingRunKeyCancellationAsync(Guid sessionId, string key)
    {
        if (sessionId == Guid.Empty || string.IsNullOrWhiteSpace(key))
            throw new InvalidDataException("Der abzubrechende Modellauftrag besitzt keinen gültigen Sitzungsschlüssel.");
        await _startupCleanupGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var pending = await ReadPendingRunKeysAsync(CancellationToken.None).ConfigureAwait(false);
            await PersistPendingRunKeysAsync(pending.Append(new(sessionId, key)).Distinct().ToArray()).ConfigureAwait(false);
        }
        finally { _startupCleanupGate.Release(); }
    }

    private async Task RetryPendingRunKeyCancellationsAsync(CancellationToken token)
    {
        await _startupCleanupGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var pending = await ReadPendingRunKeysAsync(token).ConfigureAwait(false);
            if (pending.Length == 0) return;
            var remaining = new List<PendingRunKeyCancellation>();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                // This endpoint only creates a cancellation tombstone or stops
                // an already accepted job. It never submits inference work.
                using var client = await connection.CreateClientAsync(baseAddressOverride: null,
                    ensureProfileNativeRuntime: false, timeout.Token).ConfigureAwait(false);
                foreach (var item in pending)
                    try
                    {
                        await client.CancelRunByIdempotencyKeyAsync(item.SessionId.ToString("D"), item.IdempotencyKey, timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        remaining.Add(item);
                        RunDiagnostic(logger, item.IdempotencyKey, "cancel-by-key request remains pending", exception);
                    }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                remaining.AddRange(pending);
                RunDiagnostic(logger, "cancel-by-key", "cancel-by-key connection remains pending", exception);
            }
            await PersistPendingRunKeysAsync(remaining.Distinct().ToArray()).ConfigureAwait(false);
        }
        finally { _startupCleanupGate.Release(); }
    }

    private async Task<PendingRunKeyCancellation[]> ReadPendingRunKeysAsync(CancellationToken token)
    {
        if (!File.Exists(PendingRunKeysPath)) return [];
        // A damaged cancellation outbox must never silently permit a new run.
        return JsonSerializer.Deserialize<PendingRunKeyCancellation[]>(await File.ReadAllTextAsync(PendingRunKeysPath, token).ConfigureAwait(false), JsonOptions)
            ?? throw new InvalidDataException("Die gespeicherten Modellabbrüche konnten nicht gelesen werden.");
    }

    private async Task PersistPendingRunKeysAsync(PendingRunKeyCancellation[] pending)
    {
        var path = PendingRunKeysPath;
        if (pending.Length == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(pending, JsonOptions), CancellationToken.None).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
