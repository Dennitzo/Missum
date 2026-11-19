using System.Net;
using System.Diagnostics;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Models;

public sealed partial class ModelRuntimeClient
{
    // Matches PREFILL_CHECKPOINT_TOKENS in the native supervisor.
    private const int NativePrefillCheckpointTokens = 65_536;
    // The supervisor owns the physical slot. Older or external runtimes keep
    // using their ordinary chat endpoint without this optional optimization.
    private sealed record NativeCachePrefill(int? CachedTokens, int? EvaluatedTokens, double Milliseconds);

    private async Task<NativeCachePrefill?> PrefillSessionCacheAsync(string model, string sessionKey,
        Dictionary<string, object?> requestBody, CancellationToken cancellationToken)
    {
        lock (_cacheLock)
            if (!_runtimeCatalog.TryGetValue(model, out var entry) || !entry.ManagedGpuPlacement) return null;

        var endpoint = new UriBuilder(_options.ModelRuntimeUri)
        { Port = _options.ModelRuntimeUri.Port + 1, Path = "/sessions/prefill", Query = "" }.Uri.AbsoluteUri;
        var stopwatch = Stopwatch.StartNew();
        var barrierConfirmed = false;
        try
        {
            using var response = await SendJsonAsync(HttpMethod.Post, endpoint,
                new { model, sessionKey, requestBody }, cancellationToken, bufferContent: true).ConfigureAwait(false);
            using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!result.RootElement.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String)
                throw new JsonException("Native prefill response has no status.");
            var value = status.GetString();
            LogSessionCache(_logger, "prefill", value ?? "unknown", null);
            if (value == "cancelled") throw new OperationCanceledException("Native prefill was cancelled.", cancellationToken);
            if (value is not ("prepared" or "skipped" or "unavailable"))
                throw new JsonException("Native prefill returned an unknown status.");
            if (value == "unavailable")
            {
                await WaitForPrefillBarrierAsync(model).ConfigureAwait(false);
                barrierConfirmed = true;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var prefillStarted = value == "prepared" || value == "unavailable"
                && result.RootElement.TryGetProperty("started", out var started) && started.ValueKind == JsonValueKind.True;
            if (prefillStarted)
            {
                static int? Counter(JsonElement root, string name) => root.TryGetProperty(name, out var item)
                    && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number) && number >= 0 ? number : null;
                return new(Counter(result.RootElement, "cachedTokens"), Counter(result.RootElement, "evaluatedTokens"),
                    stopwatch.Elapsed.TotalMilliseconds);
            }
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            // A supervisor predating the endpoint cannot have started a prefill.
            LogSessionCache(_logger, "prefill", "unsupported", null);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            // Closing the request is not an acknowledgement that native work
            // stopped. Keep the turn lease until the supervisor drains the slot.
            if (!barrierConfirmed) await WaitForPrefillBarrierAsync(model).ConfigureAwait(false);
            if (exception is OperationCanceledException || cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("Native prefill was cancelled.", exception, cancellationToken);
            LogSessionCache(_logger, "prefill", "unavailable", exception);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }

    private async Task WaitForPrefillBarrierAsync(string model)
    {
        var endpoint = new UriBuilder(_options.ModelRuntimeUri)
        { Port = _options.ModelRuntimeUri.Port + 1, Path = "/sessions/barrier", Query = "" }.Uri.AbsoluteUri;
        // The turn remains locked while cleanup runs, even if the caller has
        // cancelled. Only retry the idle check, never the prefill itself.
        const int maximumAttempts = 3;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var response = await SendJsonAsync(HttpMethod.Post, endpoint, new { model }, timeout.Token,
                        bufferContent: true).ConfigureAwait(false);
                    using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false),
                        cancellationToken: timeout.Token).ConfigureAwait(false);
                    if (result.RootElement.ValueKind != JsonValueKind.Object
                        || !result.RootElement.TryGetProperty("status", out var status)
                        || status.ValueKind != JsonValueKind.String || status.GetString() != "idle")
                        throw new NativePrefillBarrierException();
                    return;
                }
                catch (Exception exception) when (attempt < maximumAttempts && !timeout.IsCancellationRequested
                    && IsRecoverableProviderTransportFailure(exception))
                {
                    LogSessionCache(_logger, "barrier", "transport_retry", exception);
                    await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), timeout.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            throw new NativePrefillBarrierException(exception);
        }
    }
}

internal sealed class NativePrefillBarrierException(Exception? innerException = null)
    : InvalidOperationException(
        "Nach dem Befüllen des KV-Caches konnte das Gateway keinen freien Modell-Slot bestätigen. "
        + "Prüfe die native Windows-llama-Runtime und setze den Lauf anschließend fort.", innerException);
