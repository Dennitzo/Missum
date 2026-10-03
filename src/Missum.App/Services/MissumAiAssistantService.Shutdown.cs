namespace Missum.App.Services;

public sealed partial class MissumAiAssistantService
{
    public async Task StopPersistedRunsForShutdownAsync(CancellationToken cancellationToken = default)
    {
        await _startupCleanupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var serverRunIds = await StopPersistedRunsLocallyAsync(runs, chats,
                applicationShutdown: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            _startupServerRunIds.UnionWith(serverRunIds);
            foreach (var serverRunId in serverRunIds)
                if (await runs.GetByServerRunIdAsync(serverRunId, cancellationToken).ConfigureAwait(false) is { } run)
                    await EndResearchProgressAsync(run, "cancelled").ConfigureAwait(false);
            // Cancelled local rows no longer occur in ListResumableAsync. Keep
            // unconfirmed remote IDs for the final scan and the next launch.
            await PersistPendingRunCancellationsAsync(_startupServerRunIds).ConfigureAwait(false);
            var remaining = await CancelPersistedServerRunsAsync(
                _startupServerRunIds.Order(StringComparer.Ordinal).ToArray(), "shutdown", cancellationToken).ConfigureAwait(false);
            _startupServerRunIds.Clear();
            _startupServerRunIds.UnionWith(remaining);
            await PersistPendingRunCancellationsAsync(_startupServerRunIds).ConfigureAwait(false);
            if (remaining.Count > 0)
                throw new InvalidOperationException($"Der Gateway-Abbruch für {remaining.Count} AI-Läufe ist noch nicht bestätigt. Missum versucht ihn vor dem Dienststopp und beim nächsten Start erneut.");
        }
        finally { _startupCleanupGate.Release(); }
    }

    private string PendingRunCancellationPath => Path.Combine(settings.DataDirectory, "Diagnostics", "pending-run-cancellations.json");

    private async Task<IReadOnlyList<string>> ReadPendingRunCancellationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var path = PendingRunCancellationPath;
            if (!File.Exists(path)) return [];
            var ids = System.Text.Json.JsonSerializer.Deserialize<string[]>(
                await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions) ?? [];
            return ids.Where(static id => !string.IsNullOrWhiteSpace(id) && id.Length <= 512
                    && !id.Any(char.IsControl))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            RunDiagnostic(logger, "shutdown", "Ausstehende Gateway-Abbrüche konnten nicht geladen werden.", exception);
            return [];
        }
    }

    private async Task PersistPendingRunCancellationsAsync(IEnumerable<string> ids)
    {
        var path = PendingRunCancellationPath;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var pending = ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (pending.Length == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(temporary, System.Text.Json.JsonSerializer.Serialize(pending, JsonOptions),
                CancellationToken.None).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RunDiagnostic(logger, "shutdown", "Ausstehende Gateway-Abbrüche konnten nicht gespeichert werden.", exception);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
