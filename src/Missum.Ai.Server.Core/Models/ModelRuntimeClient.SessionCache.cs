using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Models;

public sealed partial class ModelRuntimeClient
{
    private Task<string?> SaveInterruptedSessionCacheAsync(string? preparedModelId, string? sessionCacheKey, int? promptTokens)
    {
        // The HTTP stream has already been disposed by the caller. The native
        // supervisor drains the brief cancellation/slot-idle race before saving.
        // Keep the turn gate until it replies so a follow-up cannot overwrite
        // the slot while its interrupted prefix is being snapshotted.
        // The full requested prompt count is needed for every model: a Stop
        // during prefill must not overwrite a larger durable cache with only
        // its first processed batches. Exact tail recovery remains model-specific.
        return string.IsNullOrEmpty(preparedModelId) || string.IsNullOrEmpty(sessionCacheKey)
            ? Task.FromResult<string?>(null)
            : UpdateSessionCacheAsync("save", preparedModelId, sessionCacheKey, interruptedPromptTokens: promptTokens,
                cancellationToken: CancellationToken.None);
    }

    internal static string BuildSessionCacheKey(
        string sessionId,
        string role,
        string? workspacePath = null,
        string? profileFingerprint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        // A UI tab, run ID, model selection or process lifetime must not change
        // this identity. The supervisor separately binds snapshots to the exact
        // native model, binary, template and cache configuration.
        var workspace = (workspacePath ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/').ToUpperInvariant();
        var modelRole = role.Trim().ToLowerInvariant();
        // Keep existing snapshots addressable when no profile is delivered. A
        // changed profile owns a separate snapshot of its evaluated prefix.
        var scope = JsonSerializer.Serialize(profileFingerprint is null
            ? new[] { sessionId, modelRole, workspace }
            : new[] { sessionId, modelRole, workspace, profileFingerprint });
        return (profileFingerprint is null ? "missum-session-v3-" : "missum-session-v4-")
            + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope))).ToLowerInvariant();
    }
}
