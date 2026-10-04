using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Missum.App.Services;

/// <summary>Retains each agent's planet identity across sessions, windows and application restarts.</summary>
public sealed class SubagentPlanetIdentityStore
{
    public const string IdentityVersion = "planets-v1";
    private static readonly ConcurrentDictionary<string, SharedState> States = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SharedState _state;

    public SubagentPlanetIdentityStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        StoragePath = Path.GetFullPath(Path.Combine(dataDirectory, "SubagentPlanets.json"));
        _state = States.GetOrAdd(StoragePath, static _ => new SharedState());
        lock (_state.Gate)
        {
            if (_state.Loaded) return;
            Reload();
            _state.Loaded = true;
        }
    }

    public string StoragePath { get; }
    public int Count { get { lock (_state.Gate) return _state.Assignments.Count; } }

    public int GetOrAssign(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        lock (_state.Gate)
        {
            // Rendering existing agents performs no filesystem access.
            if (_state.Assignments.TryGetValue(agentId, out var existing)) return existing;
            Reload();
            if (_state.Assignments.TryGetValue(agentId, out existing)) return existing;
            var assigned = Assign(agentId);
            Persist();
            return assigned;
        }
    }

    public void EnsureAssigned(IEnumerable<string> agentIds)
    {
        ArgumentNullException.ThrowIfNull(agentIds);
        var ordered = agentIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        lock (_state.Gate)
        {
            if (ordered.All(_state.Assignments.ContainsKey)) return;
            Reload();
            if (ordered.LongCount(id => !_state.Assignments.ContainsKey(id)) > (long)int.MaxValue + 1 - _state.NextIndex)
                throw new InvalidOperationException("Die eindeutigen Subagenten-Planetenkennungen sind ausgeschöpft.");
            var changed = false;
            foreach (var id in ordered)
            {
                if (_state.Assignments.ContainsKey(id)) continue;
                Assign(id);
                changed = true;
            }
            if (changed) Persist();
        }
    }

    private int Assign(string agentId)
    {
        // A palette is generated from the complete index; identities are never reduced modulo a palette size.
        if (_state.NextIndex > int.MaxValue)
            throw new InvalidOperationException("Die eindeutigen Subagenten-Planetenkennungen sind ausgeschöpft.");
        var index = (int)_state.NextIndex;
        _state.NextIndex++;
        _state.Assignments.Add(agentId, index);
        return index;
    }

    private void Reload()
    {
        // Last-known assignments win if external damage would otherwise change an existing agent's identity.
        var used = _state.Assignments.Values.ToHashSet();
        Merge(Read(StoragePath), used);
        Merge(Read(StoragePath + ".bak"), used);
        if (used.Count != 0) _state.NextIndex = Math.Max(_state.NextIndex, (long)used.Max() + 1);
    }

    private void Merge(Snapshot? snapshot, HashSet<int> used)
    {
        if (snapshot is null) return;
        _state.NextIndex = Math.Max(_state.NextIndex, snapshot.NextIndex);
        foreach (var pair in snapshot.Assignments.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (_state.Assignments.TryGetValue(pair.Key, out var previous))
            {
                if (previous != pair.Value) Trace.TraceWarning("Subagent planet identity changed in {0}; retaining cached identity.", StoragePath);
                continue;
            }
            if (!used.Add(pair.Value))
            {
                Trace.TraceWarning("Duplicate subagent planet identity in {0}; ignoring damaged entry.", StoragePath);
                continue;
            }
            _state.Assignments.Add(pair.Key, pair.Value);
        }
    }

    private static Snapshot? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("version", out var version)
                && (version.ValueKind != JsonValueKind.String || version.GetString() != IdentityVersion))
            {
                Trace.TraceWarning("Unsupported subagent planet identity version in {0}.", path);
                return null;
            }
            var nextIndex = 0L;
            if (root.TryGetProperty("nextIndex", out var next) && next.ValueKind == JsonValueKind.Number
                && next.TryGetInt64(out var candidate) && candidate is >= 0 and <= (long)int.MaxValue + 1)
                nextIndex = candidate;
            var assignments = new Dictionary<string, int>(StringComparer.Ordinal);
            if (root.TryGetProperty("assignments", out var map) && map.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in map.EnumerateObject())
                {
                    if (!string.IsNullOrWhiteSpace(entry.Name) && entry.Value.ValueKind == JsonValueKind.Number
                        && entry.Value.TryGetInt32(out var index) && index >= 0)
                        assignments.TryAdd(entry.Name, index);
                    else Trace.TraceWarning("Invalid subagent planet identity in {0}; ignoring damaged entry.", path);
                }
            }
            return new Snapshot(IdentityVersion, nextIndex, assignments);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Cannot read subagent planet identities at {0}: {1}", path, exception.Message);
            return null;
        }
    }

    private void Persist()
    {
        var snapshot = new Snapshot(IdentityVersion, _state.NextIndex,
            _state.Assignments.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        // Keep usable identities in memory even when a profile is temporarily unwritable.
        try { WriteAtomically(StoragePath, snapshot); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Cannot save subagent planet identities at {0}: {1}", StoragePath, exception.Message);
            return;
        }
        try { WriteAtomically(StoragePath + ".bak", snapshot); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Cannot back up subagent planet identities at {0}: {1}", StoragePath, exception.Message);
        }
    }

    private static void WriteAtomically(string path, Snapshot snapshot)
    {
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       16 * 1024, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, snapshot, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { Trace.TraceWarning("Cannot remove temporary subagent planet identity file {0}: {1}", temporaryPath, exception.Message); }
        }
    }

    private sealed record Snapshot(string Version, long NextIndex, Dictionary<string, int> Assignments);
    private sealed class SharedState
    {
        internal object Gate { get; } = new();
        internal Dictionary<string, int> Assignments { get; } = new(StringComparer.Ordinal);
        internal long NextIndex { get; set; }
        internal bool Loaded { get; set; }
    }
}
