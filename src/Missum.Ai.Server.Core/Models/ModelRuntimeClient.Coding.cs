using Missum.Ai.Contracts;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Models;

public sealed partial class ModelRuntimeClient
{
    internal static bool IsCodingModel(string modelId) => modelId.StartsWith("coding/", StringComparison.Ordinal);

    public async Task<ModelStatusSnapshot> GetCodingStatusAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return status with { Models = status.Models.Where(static model => model.Role == "coding").ToArray() };
    }

    public async Task<CodingModelCatalogResponse> GetCodingModelsAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetCodingStatusAsync(cancellationToken).ConfigureAwait(false);
        var modelRoot = status.ProviderReachable
            ? await ReadNativeModelRootAsync(cancellationToken).ConfigureAwait(false) ?? _options.CodingModelRoot
            : _options.CodingModelRoot;
        return new CodingModelCatalogResponse(status.Models, modelRoot, status.ProviderReachable,
            !status.ProviderReachable ? status.ErrorMessage ?? "Windows llama.cpp ist nicht erreichbar. Nativen Missum-Llama-Prozess und Modellordner prüfen."
            : status.Models.Count == 0 ? "Keine vollständigen lokalen GGUF-Sprachmodelle im gewählten Modellordner gefunden."
            : null, status.CheckedAt);
    }

    private async Task<string?> ReadNativeModelRootAsync(CancellationToken cancellationToken)
    {
        // A saved native preference can change independently from Docker's
        // environment. Read the owning Windows supervisor rather than reporting
        // the gateway's old startup path. Older supervisors remain compatible.
        if (_options.ModelRuntimeUri.Port == ushort.MaxValue) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var endpoint = new UriBuilder(_options.ModelRuntimeUri)
        { Port = _options.ModelRuntimeUri.Port + 1, Path = "/runtime", Query = "" }.Uri;
        try
        {
            using var response = await _httpClient.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false),
                cancellationToken: timeout.Token).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("modelRoot", out var root)
                || root.ValueKind != JsonValueKind.String) return null;
            var path = root.GetString();
            // This is Windows path metadata consumed by a Linux gateway as
            // well: do not reinterpret UNC or drive paths with host Path APIs.
            return string.IsNullOrWhiteSpace(path) || path.Length > 32767 || path.Any(char.IsControl) ? null : path;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<ModelRuntimeStatus> ReadCodingModels(JsonElement root, int contextLength) =>
        BuildRuntimeStatuses(ReadRuntimeModels(root, contextLength)).Where(static model => model.Role == "coding").ToArray();
}
