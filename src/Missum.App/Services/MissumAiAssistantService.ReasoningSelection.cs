using Missum.Ai.Client;
using Missum.Ai.Contracts;
using Missum.Core.Models;

namespace Missum.App.Services;

public sealed record ComposerReasoningOptions(string ModelId, string Role, string? Selected,
    IReadOnlyList<string> Levels, string? DefaultLevel, bool Available, string? Detail = null,
    int MaximumContextTokens = 0, int SelectedContextTokens = 0,
    int? TrainingContextTokens = null, int? LoadedContextTokens = null);

public sealed partial class MissumAiAssistantService
{
    internal static string ReasoningKey(string modelId, string role) => "text:" + modelId.ToLowerInvariant();

    private static string? ExplicitReasoningLevel(string? value)
    {
        var level = value?.Trim().ToLowerInvariant();
        return level is { Length: > 0 and <= 32 } && level != "auto" && char.IsAsciiLetterLower(level[0])
            && level.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '_' or '-')
            ? level : null;
    }

    internal static string? StoredReasoning(AppSettings current, string modelId, string role) =>
        current.ReasoningEffortsByModel.TryGetValue(ReasoningKey(modelId, role), out var value) ? ExplicitReasoningLevel(value)
        : current.ReasoningEffortsByModel.TryGetValue("general:" + modelId.ToLowerInvariant(), out value) ? ExplicitReasoningLevel(value)
        : current.ReasoningEffortsByModel.TryGetValue("coding:" + modelId.ToLowerInvariant(), out value) ? ExplicitReasoningLevel(value) : null;

    internal static ComposerReasoningOptions ResolveComposerReasoning(AppSettings current, string modelId,
        string role, ModelStatusSnapshot snapshot)
    {
        var model = snapshot.Models.FirstOrDefault(item => item.Id == modelId && item.Role == role);
        var levels = snapshot.ProviderReachable
            ? (model?.ReasoningEfforts ?? []).Select(ExplicitReasoningLevel).OfType<string>().Distinct(StringComparer.Ordinal).ToArray()
            : [];
        var stored = StoredReasoning(current, modelId, role);
        var modelDefault = ExplicitReasoningLevel(model?.DefaultReasoningEffort);
        var selected = stored is not null && levels.Contains(stored) ? stored
            : modelDefault is not null && levels.Contains(modelDefault) ? modelDefault : levels.FirstOrDefault();
        return new(modelId, role, selected, levels,
            modelDefault is not null && levels.Contains(modelDefault) ? modelDefault : null, selected is not null,
            !snapshot.ProviderReachable ? "Modellinformationen momentan nicht erreichbar."
                : levels.Length == 0 ? "Das Modell bietet keine wählbaren Reasoning-Stufen." : null,
            model?.MaximumContextTokens ?? model?.ContextTokens ?? ModelContextProfiles.ResolveMaximum(modelId, role),
            ModelContextSelection.Resolve(current, modelId, model?.MaximumContextTokens ?? model?.ContextTokens ?? ModelContextProfiles.ResolveMaximum(modelId, role)),
            model?.TrainingContextTokens, model?.LoadedContextTokens);
    }

    public async Task<ComposerReasoningOptions> GetReasoningOptionsAsync(string modelId, string role, CancellationToken cancellationToken)
    {
        using var client = await connection.CreateClientAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await client.GetModelStatusAsync(cancellationToken).ConfigureAwait(false);
        foreach (var model in snapshot.Models) ModelContextProfiles.RegisterMaximum(model.Id, model.MaximumContextTokens ?? model.ContextTokens, model.TrainingContextTokens);
        return ResolveComposerReasoning(CurrentSettings, modelId, role, snapshot);
    }

    internal Task PersistSharedReasoningAsync(string modelId, string role, string effort, CancellationToken cancellationToken) =>
        settings.UpdateAsync(current =>
        {
            var choices = new Dictionary<string, string>(current.ReasoningEffortsByModel, StringComparer.OrdinalIgnoreCase)
            {
                [ReasoningKey(modelId, role)] = effort,
            };
            return current with { ReasoningEffortsByModel = choices };
        }, cancellationToken);

    internal Task<string?> ResolveRequestedReasoningAsync(MissumAiClient client, string modelId, string role, CancellationToken cancellationToken) =>
        ResolveRequestedReasoningAsync(client, modelId, role, RuntimeBusyPollInterval, RuntimeBusyMaximumWait, cancellationToken);

    // Loading weights can keep the native router from answering the gateway's
    // five-second status probe. That is a busy runtime, not a failed request.
    private static readonly TimeSpan RuntimeBusyPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RuntimeBusyMaximumWait = TimeSpan.FromMinutes(15);

    internal async Task<string?> ResolveRequestedReasoningAsync(MissumAiClient client, string modelId, string role,
        TimeSpan pollInterval, TimeSpan maximumWait, CancellationToken cancellationToken)
    {
        var status = await client.GetModelStatusAsync(cancellationToken).ConfigureAwait(false);
        var busy = System.Diagnostics.Stopwatch.StartNew();
        // Without a previously reachable catalog entry, wait for the load to answer.
        while (!status.ProviderReachable && IsBusyModelRuntime(status) && !ListsModel(status, modelId, role)
            && busy.Elapsed < maximumWait)
        {
            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
            status = await client.GetModelStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        if (!status.ProviderReachable)
        {
            if (!IsBusyModelRuntime(status) || !ListsModel(status, modelId, role))
                throw new InvalidOperationException(status.ErrorMessage ?? "Reasoning-Kompatibilität des Modells ist nicht erreichbar.");
            // The gateway's last reachable catalog still describes the model it is
            // loading; the run itself waits for that load before generating.
            status = status with { ProviderReachable = true };
        }
        return ResolveComposerReasoning(CurrentSettings, modelId, role, status).Selected;
    }

    internal static bool IsBusyModelRuntime(ModelStatusSnapshot status) =>
        string.Equals(status.ErrorCode, "modelRuntime.timeout", StringComparison.Ordinal);

    private static bool ListsModel(ModelStatusSnapshot status, string modelId, string role) =>
        status.Models.Any(item => item.Id == modelId && item.Role == role);
}
