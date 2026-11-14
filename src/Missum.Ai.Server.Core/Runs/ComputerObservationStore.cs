using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Missum.Ai.Server.Core.Runs;

// Ephemeral screenshot transfer. Neither screenshot bytes nor data URLs belong
// in run checkpoints, tool receipts, telemetry, or the durable upload store.
public sealed class ComputerObservationStore : IDisposable
{
    public const int MaximumBytes = 7 * 1024 * 1024;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Timer _cleanup;

    public ComputerObservationStore() => _cleanup = new Timer(_ => RemoveExpired(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

    public string Put(string runId, string proposalId, byte[] png)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);
        if (png.Length is < 8 or > MaximumBytes || !png.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}))
            throw new ArgumentException("Computer-Beobachtung muss ein PNG bis maximal 7 MiB sein.");
        lock (_gate)
        {
            RemoveExpired();
            if (_entries.Values.Any(entry => entry.RunId == runId && entry.ProposalId == proposalId))
                throw new InvalidOperationException("Diese Computer-Beobachtung wurde bereits übertragen.");
            foreach (var prior in _entries.Where(pair => pair.Value.RunId == runId)) _entries.TryRemove(prior.Key, out _);
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            if (!_entries.TryAdd(id, new(runId, proposalId, png, DateTimeOffset.UtcNow.Add(Lifetime))))
                throw new InvalidOperationException("Computer-Beobachtung konnte nicht gespeichert werden.");
            return id;
        }
    }

    public byte[]? Get(string runId, string id, string? proposalId = null)
    {
        if (!_entries.TryGetValue(id, out var entry) || entry.RunId != runId
            || proposalId is not null && entry.ProposalId != proposalId) return null;
        if (entry.ExpiresAt > DateTimeOffset.UtcNow) return entry.Png;
        _entries.TryRemove(id, out _);
        return null;
    }

    public void RemoveRun(string runId)
    {
        foreach (var pair in _entries.Where(pair => pair.Value.RunId == runId)) _entries.TryRemove(pair.Key, out _);
    }

    private void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _entries.Where(pair => pair.Value.ExpiresAt <= now)) _entries.TryRemove(pair.Key, out _);
    }

    private sealed record Entry(string RunId, string ProposalId, byte[] Png, DateTimeOffset ExpiresAt);

    public void Dispose() { _cleanup.Dispose(); _entries.Clear(); }
}
