using Missum.Ai.Contracts;
using Missum.Core.Models;

namespace Missum.App.Services;

public sealed partial class MissumAiAssistantService
{
    private async Task PrepareSubmittedModelAsync(PromptTriggerAction? action, CancellationToken cancellationToken,
        string? frozenModel = null, int? frozenContext = null, bool gatewayReady = false)
    {
        if (action is PromptTriggerAction.ImageGeneration or PromptTriggerAction.TextToSpeech
            or PromptTriggerAction.Transcription or PromptTriggerAction.VoiceInput
            or PromptTriggerAction.LiveCaptions or PromptTriggerAction.LiveTranslation) return;
        var selected = frozenModel ?? CurrentSettings.SelectedModel?.Trim();
        if (string.IsNullOrWhiteSpace(selected)) return;
        var context = frozenContext ?? CurrentSettings.ContextLimitsByModel.GetValueOrDefault(selected);
        try
        {
            using var client = await connection.CreateClientAsync(cancellationToken).ConfigureAwait(false);
            if (!gatewayReady)
                await connection.WaitForGatewayAsync(client, cancellationToken).ConfigureAwait(false);
            await client.PrepareModelAsync(new(selected, context), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Preparation is speculative and compatible with older gateways.
            // The normal run still owns model validation and visible failures.
            RunDiagnostic(logger, selected, "early model preparation unavailable", exception);
        }
    }
}
