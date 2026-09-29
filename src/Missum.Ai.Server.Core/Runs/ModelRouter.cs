using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Configuration;
using Missum.Ai.Server.Core.Models;
using Microsoft.Extensions.Options;

namespace Missum.Ai.Server.Core.Runs;

public sealed class ModelRouter
{
    private readonly MissumAiServerOptions _options;
    private readonly ModelRuntimeClient? _modelRuntime;

    public ModelRouter(IOptions<MissumAiServerOptions> options, ModelRuntimeClient? modelRuntime = null)
    {
        _options = options.Value;
        _modelRuntime = modelRuntime;
    }

    public async Task<ModelSelection> SelectAsync(
        RunRequest request,
        CancellationToken cancellationToken = default)
    {
        var selected = Select(request);
        if (_modelRuntime is null)
        {
            return selected;
        }

        var status = request.Mode == RunMode.Coding
            ? await _modelRuntime.GetCodingStatusAsync(cancellationToken).ConfigureAwait(false)
            : await _modelRuntime.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.ProviderReachable)
        {
            throw new HttpRequestException(request.Mode == RunMode.Coding
                ? "Der native llama Coding-Dienst ist nicht erreichbar."
                : "native llama model status is unavailable.");
        }
        var model = ResolveRuntimeModel(status.Models, selected, HasExplicitModelSelection(request))
            ?? throw new InvalidOperationException(
                $"The selected {selected.Role} model '{selected.ModelId}' is not available.");
        return selected with { ModelId = model.Id, ContextLength = Math.Max(2_048, model.ContextTokens) };
    }

    public ModelSelection Select(RunRequest request) => request.Mode == RunMode.Coding
        ? new ModelSelection(
            string.IsNullOrWhiteSpace(request.PreferredCodingModelId) ? _options.CodingModelId : request.PreferredCodingModelId.Trim(),
            "coding",
            _options.CodingContextLength)
        : SelectGeneral(request);

    private ModelSelection SelectGeneral(RunRequest request)
    {
        var modelId = string.IsNullOrWhiteSpace(request.PreferredGeneralModelId)
            ? _options.GeneralModelId
            : request.PreferredGeneralModelId.Trim();
        return new ModelSelection(modelId, "general", _options.GeneralContextLength);
    }

    internal static ModelRuntimeStatus? ResolveRuntimeModel(
        IReadOnlyList<ModelRuntimeStatus> models,
        ModelSelection selected,
        bool hasExplicitSelection)
    {
        var configured = ModelRuntimeClient.ResolveModelStatus(models, selected.ModelId, selected.Role);
        if (configured is not null || hasExplicitSelection) return configured;

        // Portable installations discover their local catalog at runtime. A stale
        // built-in default must not make a valid on-demand catalog unusable when
        // the client deliberately left the model choice open.
        return models.FirstOrDefault(model =>
            model.Downloaded && string.Equals(model.Role, selected.Role, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasExplicitModelSelection(RunRequest request) => request.Mode == RunMode.Coding
        ? !string.IsNullOrWhiteSpace(request.PreferredCodingModelId)
        : !string.IsNullOrWhiteSpace(request.PreferredGeneralModelId);

}

public sealed record ModelSelection(string ModelId, string Role, int ContextLength);
