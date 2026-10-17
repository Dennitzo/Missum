using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Runs;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Models;

internal sealed record ProjectCacheBinding(ProjectContextRef Context, string Epoch, string WorkspacePath);

public sealed partial class ModelRuntimeClient
{
    private readonly HashSet<string> _projectPreparedBranches = new(StringComparer.Ordinal);

    private async Task<bool> PrepareProjectSessionCacheAsync(string model, string key, string? sessionId,
        string? parentSessionId, ProjectCacheBinding binding, IReadOnlyList<LmChatMessage> messages,
        Dictionary<string, object?> inference, CancellationToken cancellationToken)
    {
        var hint = model + "\n" + key + "\n" + binding.Epoch;
        lock (_cacheLock)
            if (_projectPreparedBranches.Contains(hint)) return false;
        var prefix = PrepareLanguageBoundMessages(messages).TakeWhileInclusive(ProjectContextPolicy.IsBase);
        if (prefix.Count == 0 || !ProjectContextPolicy.IsBase(prefix[^1])) return false;
        var prefill = new Dictionary<string, object?>(inference, StringComparer.Ordinal)
        {
            ["messages"] = prefix.Select(ToOpenAiMessage).ToArray(),
            ["add_generation_prompt"] = false,
        };
        // Native token comparison, rather than this content-addressed hint, is
        // authoritative. Tool schemas and reasoning/template options are included.
        var prefixHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            binding.WorkspacePath + "\n" + JsonSerializer.Serialize(prefill)))).ToLowerInvariant();
        var path = new UriBuilder(_options.ModelRuntimeUri)
        { Port = _options.ModelRuntimeUri.Port + 1, Path = "/sessions/project-prepare", Query = "" }.Uri.AbsoluteUri;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            using var response = await SendJsonAsync(HttpMethod.Post, path, new
            {
                model, sessionKey = key, sessionId, parentSessionId, binding.Context.ProjectKey,
                cacheEpoch = binding.Epoch, prefixHash, prefill, firstInference = inference,
            }, timeout.Token, bufferContent: true).ConfigureAwait(false);
            using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false),
                cancellationToken: timeout.Token).ConfigureAwait(false);
            var status = result.RootElement.TryGetProperty("status", out var value) ? value.GetString() : null;
            if (status is not ("forked" or "restored" or "resident")) return false;
            lock (_cacheLock)
            {
                if (_projectPreparedBranches.Count >= 128) _projectPreparedBranches.Clear();
                _projectPreparedBranches.Add(hint);
            }
            LogSessionCache(_logger, "project-prepare", status, null);
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            LogSessionCache(_logger, "project-prepare", "unavailable", exception);
            // Closing the control transport cancels native prefix preparation.
            // Re-enter its manager lock before releasing our turn lease, so a
            // new inference cannot overlap the cancelled native prompt batch.
            if (cancellationToken.IsCancellationRequested)
                await UpdateSessionCacheAsync("prepare", model, key, sessionId: sessionId,
                    parentSessionId: parentSessionId, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    internal async Task FlushSessionCacheAsync(string modelId, string key, string? runtimeInstanceId = null)
    {
        var model = runtimeInstanceId ?? modelId;
        var lease = await AcquireTurnAsync(model, CancellationToken.None).ConfigureAwait(false);
        try { await UpdateSessionCacheAsync("save", model, key, cancellationToken: CancellationToken.None).ConfigureAwait(false); }
        finally { lease.Release(); }
    }

    public async Task<string> ResetContextCacheEpochAsync(string epoch, CancellationToken cancellationToken = default)
    {
        var lease = await AcquireTurnAsync(null, cancellationToken).ConfigureAwait(false);
        try
        {
            var path = new UriBuilder(_options.ModelRuntimeUri)
            { Port = _options.ModelRuntimeUri.Port + 1, Path = "/sessions/reset-epoch", Query = "" }.Uri.AbsoluteUri;
            using var response = await SendJsonAsync(HttpMethod.Post, path, new { epoch }, cancellationToken, bufferContent: true).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var status = json.RootElement.GetProperty("status").GetString() ?? "unavailable";
            if (status is "reset" or "unchanged")
                lock (_cacheLock) _projectPreparedBranches.Clear();
            return status;
        }
        finally { lease.Release(); }
    }
}

internal static class ProjectContextPrefix
{
    internal static List<LmChatMessage> TakeWhileInclusive(this IReadOnlyList<LmChatMessage> messages,
        Func<LmChatMessage, bool> last)
    {
        var result = new List<LmChatMessage>();
        foreach (var message in messages)
        {
            result.Add(message);
            if (last(message)) break;
        }
        return result;
    }
}
