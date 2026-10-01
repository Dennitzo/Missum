using Missum.Ai.Contracts;

namespace Missum.App.Services;

public sealed partial class MissumAiAssistantService
{
    private RunModelSelectionRequest? _pendingModelSelection;
    private readonly SemaphoreSlim _selectionRequestGate = new(1, 1);

    public async Task RequestLiveModelSelectionAsync(CancellationToken cancellationToken = default)
    {
        if (ActiveSessionId is not { } session || string.IsNullOrWhiteSpace(settings.Current.SelectedModel))
        { _pendingModelSelection = null; return; }
        var role = UsesCodingAgent(_activeRunAction) ? "coding" : "general";
        _pendingModelSelection = new(session.ToString("D"), settings.Current.SelectedModel, StoredReasoning(settings.Current, settings.Current.SelectedModel, role));
        await FlushLiveModelSelectionAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task FlushLiveModelSelectionAsync(CancellationToken token)
    {
        await _selectionRequestGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var selection = _pendingModelSelection;
            var runId = _activeServerRunId;
            if (selection is null || runId is null || ActiveSessionId?.ToString("D") != selection.SessionId) return;
            using var client = await connection.CreateClientAsync(token).ConfigureAwait(false);
            await client.SelectRunModelAsync(runId, selection, token).ConfigureAwait(false);
            if (ReferenceEquals(selection, _pendingModelSelection)) _pendingModelSelection = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException && exception is not OutOfMemoryException)
        {
            // A settings update must never abort an already accepted inference.
            // The persisted global selection still applies to the next prompt.
            RunDiagnostic(logger, _activeServerRunId ?? "selection", "live model selection deferred", exception);
        }
        finally { _selectionRequestGate.Release(); }
    }
}
