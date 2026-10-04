using System.Text.Json;

namespace Missum.Ai.Server.Core.Models;

public sealed record SubagentRuntimeAvailability(
    bool Allowed, string? Reason, string ModelId, string? InstanceId, int? GpuIndex,
    int ContextLength = 0, long? RequiredBytes = null, long? FreeBytes = null);

public sealed record SubagentRuntimePreparation(
    string InstanceId, string ModelId, int GpuIndex, string CacheStatus, int CachedTokens,
    int? SourceCachedTokens = null, int? PreparationSampledTokens = null, int? EvaluatedGeneratedTokens = null);

public sealed partial class ModelRuntimeClient
{
    public async Task<SubagentRuntimeAvailability> GetSubagentAvailabilityAsync(
        string modelId, int contextLength = 0, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var installed = ResolveInstalledModel(await GetRuntimeModelsAsync(timeout.Token).ConfigureAwait(false), modelId);
            if (installed is null || installed.IsSubagent)
                return new(false, "subagent.model_not_installed", modelId, null, null);
            using var response = await SendJsonAsync(HttpMethod.Post, SubagentControlUri("status"),
                new { model = installed.Id, contextLength = contextLength > 0 ? (int?)contextLength : null }, timeout.Token,
                bufferContent: true).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false),
                cancellationToken: timeout.Token).ConfigureAwait(false);
            return ReadSubagentAvailability(json.RootElement, installed.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            return new(false, "subagent.runtime_control_unavailable", modelId, null, null);
        }
    }

    public Task<SubagentRuntimePreparation> PrepareSubagentAsync(
        string modelId, string? parentSessionCacheKey, string childSessionCacheKey,
        CancellationToken cancellationToken = default) =>
        PrepareSubagentCoreAsync(modelId, parentSessionCacheKey, childSessionCacheKey, null, null, null, cancellationToken);

    public Task<SubagentRuntimePreparation> PrepareSubagentAsync(
        string modelId, string? parentSessionCacheKey, string childSessionCacheKey,
        string? childSessionId, string? parentSessionId, CancellationToken cancellationToken = default) =>
        PrepareSubagentCoreAsync(modelId, parentSessionCacheKey, childSessionCacheKey, null,
            childSessionId, parentSessionId, cancellationToken);

    public Task<SubagentRuntimePreparation> PrepareSubagentAsync(
        string modelId, string? parentSessionCacheKey, string childSessionCacheKey,
        IReadOnlyList<LmChatMessage> messages, IReadOnlyList<LmToolDefinition> tools,
        string modelRole, string? reasoningEffort, CancellationToken cancellationToken = default) =>
        PrepareSubagentAsync(modelId, parentSessionCacheKey, childSessionCacheKey, messages, tools,
            modelRole, reasoningEffort, null, null, cancellationToken);

    public Task<SubagentRuntimePreparation> PrepareSubagentAsync(
        string modelId, string? parentSessionCacheKey, string childSessionCacheKey,
        IReadOnlyList<LmChatMessage> messages, IReadOnlyList<LmToolDefinition> tools,
        string modelRole, string? reasoningEffort, string? childSessionId, string? parentSessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(tools);
        // Use exactly the same wire messages, schemas and reasoning kwargs as
        // the child's first inference. Only its generation prefill is omitted.
        var prefill = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["messages"] = PrepareLanguageBoundMessages(messages).Select(ToOpenAiMessage).ToArray(),
            ["parallel_tool_calls"] = string.Equals(modelRole, "coding", StringComparison.Ordinal),
            ["add_generation_prompt"] = false,
        };
        ApplyReasoningSettings(prefill, modelId, modelRole, reasoningEffort);
        if (tools.Count > 0)
        {
            prefill["tools"] = PrepareTransportTools(modelId, tools);
            prefill["tool_choice"] = "auto";
        }
        return PrepareSubagentCoreAsync(modelId, parentSessionCacheKey, childSessionCacheKey, prefill,
            childSessionId, parentSessionId, cancellationToken);
    }

    private async Task<SubagentRuntimePreparation> PrepareSubagentCoreAsync(
        string modelId, string? parentSessionCacheKey, string childSessionCacheKey,
        IReadOnlyDictionary<string, object?>? prefill, string? childSessionId, string? parentSessionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childSessionCacheKey);
        // Fork an idle immutable prefix while retaining both turn leases. The
        // instances then use independent leases and snapshot paths in parallel.
        var turnLease = await AcquireTurnAsync(null, cancellationToken).ConfigureAwait(false);
        try
        {
            await _modelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(5));
                var installed = ResolveInstalledModel(await GetRuntimeModelsAsync(timeout.Token).ConfigureAwait(false), modelId)
                    ?? throw new FileNotFoundException($"Das lokale Modell '{modelId}' ist nicht installiert.");
                using var response = await SendJsonAsync(HttpMethod.Post, SubagentControlUri("prepare"),
                    new { model = installed.Id, parentSessionCacheKey, childSessionCacheKey, prefill,
                        childSessionId, parentSessionId }, timeout.Token,
                    bufferContent: true).ConfigureAwait(false);
                using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false),
                    cancellationToken: timeout.Token).ConfigureAwait(false);
                var availability = ReadSubagentAvailability(json.RootElement, installed.Id);
                if (!availability.Allowed || availability.InstanceId is null || availability.GpuIndex != 1)
                    throw new InvalidOperationException(availability.Reason ?? "subagent.runtime_unavailable");
                var cacheStatus = json.RootElement.TryGetProperty("cacheStatus", out var cache) ? cache.GetString() ?? "miss" : "miss";
                if (prefill is not null && cacheStatus is not ("forked" or "restored" or "resident"))
                    throw new InvalidOperationException(json.RootElement.TryGetProperty("detail", out var detail)
                        ? detail.GetString() : "Der kanonische KV-Kontext konnte nicht übernommen werden.");
                await GetRuntimeModelsAsync(timeout.Token).ConfigureAwait(false);
                InvalidateStatus();
                return new(availability.InstanceId, availability.ModelId, availability.GpuIndex.Value,
                    cacheStatus,
                    json.RootElement.TryGetProperty("cachedTokens", out var tokens) && tokens.TryGetInt32(out var cached) ? cached : 0,
                    ReadNullableCount("sourceCachedTokens"), ReadNullableCount("preparationSampledTokens"), ReadNullableCount("evaluatedGeneratedTokens"));

                int? ReadNullableCount(string name) => json.RootElement.TryGetProperty(name, out var value)
                    && value.TryGetInt32(out var count) ? count : null;
            }
            finally { _modelGate.Release(); }
        }
        finally { turnLease.Release(); }
    }

    private string SubagentControlUri(string action) => new UriBuilder(_options.ModelRuntimeUri)
        { Port = _options.ModelRuntimeUri.Port + 1, Path = "/agents/" + action, Query = "" }.Uri.AbsoluteUri;

    private async Task WaitForNativeModelLoadedAsync(string instanceId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var current = (await GetRuntimeModelsAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(model => model.Id == instanceId);
            if (current?.State is "loaded" or "sleeping") return;
            if (current is null || current.State == "failed")
                throw new InvalidOperationException($"Das native Modell '{instanceId}' konnte nicht geladen werden.");
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    internal static SubagentRuntimeAvailability ReadSubagentAvailability(JsonElement root, string modelId)
    {
        if (!root.TryGetProperty("allowed", out var allowed) || allowed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new JsonException("Native subagent response requires a boolean allowed field.");
        return new(allowed.GetBoolean(),
            root.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null,
            root.TryGetProperty("modelId", out var model) ? model.GetString() ?? modelId : modelId,
            root.TryGetProperty("instanceId", out var instance) && instance.ValueKind == JsonValueKind.String ? instance.GetString() : null,
            root.TryGetProperty("gpuIndex", out var gpu) && gpu.TryGetInt32(out var index) ? index : null,
            root.TryGetProperty("contextLength", out var context) && context.TryGetInt32(out var length) ? length : 0,
            root.TryGetProperty("requiredBytes", out var required) && required.TryGetInt64(out var bytes) ? bytes : null,
            root.TryGetProperty("freeBytes", out var free) && free.TryGetInt64(out var freeBytes) ? freeBytes : null);
    }
}
