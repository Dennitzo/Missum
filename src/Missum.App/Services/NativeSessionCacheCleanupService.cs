using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Missum.Core.Models;

namespace Missum.App.Services;

/// <summary>Durable, owner-scoped KV deletion. Deleting a project never starts an AI runtime.</summary>
public sealed class NativeSessionCacheCleanupService
{
    private static readonly ConcurrentDictionary<string, SharedState> States = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex IdentityPattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly string[] ModelRoles = ["general", "coding"];
    private static readonly string[] RuntimeStateFiles = ["runtime.json", "supervisor.json"];
    private static readonly string[] ProcessIdFields = ["pid", "processId", "supervisorPid", "serverPid"];
    private readonly AssistantRuntimeProfile _profile;
    private readonly Func<CacheDeletion, CancellationToken, Task<bool>> _send;
    private readonly Func<bool> _runtimeStopped;
    private readonly SharedState _state;

    public NativeSessionCacheCleanupService(AssistantRuntimeProfile profile)
        : this(profile, null, null) { }

    internal NativeSessionCacheCleanupService(AssistantRuntimeProfile profile,
        Func<CacheDeletion, CancellationToken, Task<bool>>? send, Func<bool>? runtimeStopped)
    {
        _profile = profile;
        QueuePath = Path.Combine(Path.GetFullPath(profile.DataDirectory), "NativeCacheDeletions.json");
        _state = States.GetOrAdd(QueuePath, static _ => new());
        _send = send ?? SendAsync;
        _runtimeStopped = runtimeStopped ?? IsRuntimeStopped;
    }

    public string QueuePath { get; }

    public async Task DeleteSessionsAsync(IEnumerable<ChatSession> deletedSessions, CancellationToken cancellationToken = default)
    {
        var sessions = deletedSessions.DistinctBy(static session => session.Id).ToArray();
        if (sessions.Length == 0) return;
        await _state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var queued = ReadQueue();
            foreach (var session in sessions)
            {
                var id = session.Id.ToString("D");
                var keys = ModelRoles.SelectMany(role =>
                    new[] { session.CodingWorkspacePath, null }.Select(workspace => BuildSessionKey(id, role, workspace)));
                queued[id] = queued.TryGetValue(id, out var previous)
                    ? previous with { SessionKeys = previous.SessionKeys.Concat(keys).Distinct(StringComparer.Ordinal).ToArray() }
                    : new([id], keys.Distinct(StringComparer.Ordinal).ToArray());
            }
            WriteQueue(queued);
            // A known stopped profile can be cleaned immediately without waiting
            // for networking. Keep the queue until the supervisor commits tombstones.
            if (_runtimeStopped())
            {
                DeleteOwnedFiles(queued.Values);
                return;
            }
            if (DateTimeOffset.UtcNow >= _state.NextAttempt)
                _ = await FlushLockedAsync(queued, cancellationToken).ConfigureAwait(false);
        }
        finally { _state.Gate.Release(); }
    }

    /// <summary>Called after runtime startup and before the first model request.</summary>
    public async Task<bool> FlushAsync(CancellationToken cancellationToken = default)
    {
        await _state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await FlushLockedAsync(ReadQueue(), cancellationToken).ConfigureAwait(false); }
        finally { _state.Gate.Release(); }
    }

    private async Task<bool> FlushLockedAsync(Dictionary<string, CacheDeletion> queued, CancellationToken cancellationToken)
    {
        foreach (var batch in queued.ToArray().Chunk(64))
        {
            var request = new CacheDeletion(batch.SelectMany(static entry => entry.Value.SessionIds).Distinct(StringComparer.Ordinal).ToArray(),
                batch.SelectMany(static entry => entry.Value.SessionKeys).Distinct(StringComparer.Ordinal).ToArray());
            var deleted = false;
            try { deleted = await _send(request, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException
                || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                Trace.TraceWarning("KV-Cache-Löschung wartet auf die native Runtime: {0}", exception.Message);
            }
            if (!deleted)
            {
                _state.NextAttempt = DateTimeOffset.UtcNow.AddSeconds(10);
                return false;
            }
            foreach (var entry in batch) queued.Remove(entry.Key);
            WriteQueue(queued);
        }
        _state.NextAttempt = DateTimeOffset.MinValue;
        return true;
    }

    private async Task<bool> SendAsync(CacheDeletion deletion, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
        using var response = await http.PostAsJsonAsync($"http://127.0.0.1:{_profile.NativeControlPort}/sessions/delete",
            deletion, JsonOptions, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return false;
        using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false),
            cancellationToken: timeout.Token).ConfigureAwait(false);
        return result.RootElement.TryGetProperty("status", out var status) && status.GetString() == "deleted";
    }

    private Dictionary<string, CacheDeletion> ReadQueue()
    {
        var queued = File.Exists(QueuePath)
            ? JsonSerializer.Deserialize<Dictionary<string, CacheDeletion>>(File.ReadAllText(QueuePath), JsonOptions)
                ?? throw new JsonException("Die gespeicherte KV-Löschliste ist ungültig.")
            : new Dictionary<string, CacheDeletion>(StringComparer.Ordinal);
        foreach (var (id, pending) in _state.PendingEntries)
            queued[id] = queued.TryGetValue(id, out var saved)
                ? new(saved.SessionIds.Concat(pending.SessionIds).Distinct(StringComparer.Ordinal).ToArray(),
                    saved.SessionKeys.Concat(pending.SessionKeys).Distinct(StringComparer.Ordinal).ToArray())
                : pending;
        return queued;
    }

    private void WriteQueue(Dictionary<string, CacheDeletion> queued)
    {
        // Retain the committed deletion in memory even if a full/unwritable
        // disk prevents journaling. A later flush retries durable persistence.
        _state.PendingEntries = new(queued, StringComparer.Ordinal);
        Directory.CreateDirectory(Path.GetDirectoryName(QueuePath)!);
        var temporary = QueuePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, queued, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, QueuePath, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private bool IsRuntimeStopped()
    {
        foreach (var file in RuntimeStateFiles)
        {
            var path = Path.Combine(_profile.NativeStateDirectory, file);
            if (!File.Exists(path)) continue;
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                var recognizedOwner = false;
                foreach (var field in ProcessIdFields)
                {
                    if (!json.RootElement.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.Number
                        || !value.TryGetInt32(out var pid) || pid <= 0) continue;
                    recognizedOwner = true;
                    try { using var process = Process.GetProcessById(pid); if (!process.HasExited) return false; }
                    catch (ArgumentException) { }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                    { return false; }
                }
                if (!recognizedOwner) return false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return false; // Unknown ownership must never authorize offline file deletion.
            }
        }
        return true;
    }

    private void DeleteOwnedFiles(IEnumerable<CacheDeletion> deletions)
    {
        var root = Path.GetFullPath(Path.Combine(_profile.NativeStateDirectory, "session-cache"));
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return;
        var sessionIds = deletions.SelectMany(static entry => entry.SessionIds).ToHashSet(StringComparer.Ordinal);
        var keys = deletions.SelectMany(static entry => entry.SessionKeys).Select(CanonicalKey).ToHashSet(StringComparer.Ordinal);
        var owners = new Dictionary<string, CacheOwner>(StringComparer.Ordinal);
        var ledger = Path.Combine(root, "ownership.json");
        if (File.Exists(ledger))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(ledger));
            if (json.RootElement.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Object)
                foreach (var entry in entries.EnumerateObject()) ReadOwner(entry.Name, entry.Value);
        }
        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
        {
            var identity = Path.GetFileNameWithoutExtension(path);
            if (!IdentityPattern.IsMatch(identity) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            try { using var json = JsonDocument.Parse(File.ReadAllText(path)); ReadOwner(identity, json.RootElement); }
            catch (JsonException) { }
        }
        var selected = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var previous = selected.Count;
            foreach (var (identity, owner) in owners)
            {
                if (!sessionIds.Contains(owner.SessionId ?? "") && !sessionIds.Contains(owner.ParentSessionId ?? "")
                    && !keys.Contains(CanonicalKey(owner.SessionKey ?? "")) && !keys.Contains(CanonicalKey(owner.ParentSessionKey ?? ""))) continue;
                selected.Add(identity);
                if (owner.SessionKey is not null) keys.Add(CanonicalKey(owner.SessionKey));
                if (owner.SessionId is not null) sessionIds.Add(owner.SessionId);
            }
            if (previous == selected.Count) break;
        }
        foreach (var identity in selected)
            foreach (var path in new[] { Path.Combine(root, identity + ".bin"), Path.Combine(root, identity + ".json") }
                .Concat(Directory.EnumerateFiles(root, identity + ".*.pending")))
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) File.Delete(path);

        void ReadOwner(string identity, JsonElement entry)
        {
            if (!IdentityPattern.IsMatch(identity) || entry.ValueKind != JsonValueKind.Object) return;
            static string? Text(JsonElement item, string property) => item.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var key = Text(entry, "sessionKey");
            var sessionId = Text(entry, "sessionId");
            var parentId = Text(entry, "parentSessionId");
            if (key is null && sessionId is null && parentId is null) return;
            owners[identity] = new(key, sessionId, parentId, Text(entry, "parentSessionKey"));
        }
    }

    internal static string BuildSessionKey(string sessionId, string role, string? workspacePath)
    {
        var workspace = (workspacePath ?? "").Trim().Replace('\\', '/').TrimEnd('/').ToUpperInvariant();
        var scope = JsonSerializer.Serialize(new[] { sessionId, role.Trim().ToLowerInvariant(), workspace });
        return "missum-session-v2-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope))).ToLowerInvariant();
    }

    private static string CanonicalKey(string key) => key.StartsWith("missum-session-v2-", StringComparison.Ordinal)
        ? "go-session-v2-" + key["missum-session-v2-".Length..] : key;

    internal sealed record CacheDeletion(string[] SessionIds, string[] SessionKeys);
    private sealed record CacheOwner(string? SessionKey, string? SessionId, string? ParentSessionId, string? ParentSessionKey);
    private sealed class SharedState
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal DateTimeOffset NextAttempt { get; set; }
        internal Dictionary<string, CacheDeletion> PendingEntries { get; set; } = new(StringComparer.Ordinal);
    }
}
