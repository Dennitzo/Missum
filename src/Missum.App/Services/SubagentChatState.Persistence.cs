using System.Globalization;
using System.Text;
using System.Text.Json;
using Missum.Core.Models;

namespace Missum.App.Services;

public sealed partial record SubagentChatState
{
    private static readonly SemaphoreSlim[] TranscriptGates = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static readonly object TranscriptCacheLock = new();
    private static readonly Dictionary<string, TranscriptCache> TranscriptCacheEntries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, TranscriptWriter> TranscriptWriters = new(StringComparer.OrdinalIgnoreCase);
    private static long TranscriptCacheAccess;
    private static readonly Lazy<JsonSerializerOptions> TranscriptSnapshotOptions = new(static () => new(JsonOptions)
        { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip });
    private const int MaximumInlineCharacters = 32 * 1024;
    private const long MaximumJournalBytes = 8L * 1024 * 1024;
    // Shared 64 MiB inactive-payload budget: 32 MiB native UI + 32 MiB decoded subagent transcripts.
    private const long MaximumCacheCharacters = 32L * 1024 * 1024 / sizeof(char);

    private readonly record struct TranscriptStamp(long SnapshotBytes, DateTime SnapshotUpdated,
        long JournalBytes, DateTime JournalUpdated);
    private sealed record TranscriptCache(SubagentChatState State, long Sequence, int Patches, long JournalBytes,
        long Characters, long UsedAt, TranscriptStamp Stamp);
    private sealed class TranscriptWriter
    {
        internal int References;
        internal TranscriptCache? Baseline;
    }
    private sealed class TranscriptWriterLease(string path) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (TranscriptCacheLock)
            {
                if (!TranscriptWriters.TryGetValue(path, out var writer) || --writer.References > 0) return;
                TranscriptWriters.Remove(path);
                if (writer.Baseline is { } baseline && baseline.Characters <= MaximumCacheCharacters)
                    StoreInactiveTranscriptLocked(path, baseline);
            }
        }
    }
    private sealed record ToolPatch(AssistantToolStep Step, string? DetailAppend, string? DetailReplacement);
    private sealed record TranscriptPatch(long Sequence, SubagentChatState Metadata, ChatMessage Assistant,
        string? ContentAppend, string? ContentReplacement, IReadOnlyList<ToolPatch> Tools);

    private bool CanRemainInline()
    {
        if (PreviousMessages is { Count: > 0 } || (AssistantMessage.ToolSteps?.Count ?? 0) > 32) return false;
        return EstimateCharacters(this) < MaximumInlineCharacters;
    }

    private static long EstimateCharacters(SubagentChatState state) => state.UserMessage.Content.Length + (long)state.AssistantMessage.Content.Length
        + (state.AssistantMessage.ToolSteps ?? []).Sum(step => (long)(step.Detail?.Length ?? 0) + (step.InputJson?.Length ?? 0)
            + (step.OutputJson?.Length ?? 0) + (step.Explanation?.Length ?? 0) + 512)
        + (state.PreviousMessages ?? []).Sum(message => (long)message.Content.Length
            + (message.ToolSteps ?? []).Sum(step => (long)(step.Detail?.Length ?? 0) + (step.InputJson?.Length ?? 0) + (step.OutputJson?.Length ?? 0)));

    private static SemaphoreSlim TranscriptGate(string path) => TranscriptGates[(StringComparer.OrdinalIgnoreCase.GetHashCode(path) & int.MaxValue) % TranscriptGates.Length];

    internal IDisposable AcquireTranscriptWriter(string dataDirectory)
    {
        var path = Path.GetFullPath(Path.Combine(dataDirectory, "Subagents", StorageKey + ".json"));
        lock (TranscriptCacheLock)
        {
            if (!TranscriptWriters.TryGetValue(path, out var writer))
            {
                TranscriptCacheEntries.Remove(path, out var previous);
                TranscriptWriters[path] = writer = new() { Baseline = previous };
            }
            writer.References++;
        }
        return new TranscriptWriterLease(path);
    }

    private async Task PersistTranscriptAsync(string path)
    {
        path = Path.GetFullPath(path);
        var gate = TranscriptGate(path);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            TranscriptCache? cached;
            lock (TranscriptCacheLock) cached = FindCachedTranscriptLocked(path);
            var sequence = (cached?.Sequence ?? 0) + 1;
            var journal = path + ".journal.jsonl";
            var patch = cached is not null && cached.Patches < 2048 && cached.JournalBytes < MaximumJournalBytes && File.Exists(path)
                ? CreatePatch(cached.State, sequence) : null;
            int patches;
            long journalBytes;
            if (patch is null)
            {
                // On cache eviction/restart, choose a new snapshot sequence above any prior append journal.
                if (cached is null && File.Exists(path))
                {
                    var restored = await ReadTranscriptCoreAsync(path, CancellationToken.None).ConfigureAwait(false);
                    sequence = restored.Sequence + 1;
                }
                var json = JsonSerializer.Serialize(this, JsonOptions);
                var snapshot = "{\"persistenceSequence\":" + sequence.ToString(CultureInfo.InvariantCulture) + "," + json[1..];
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllTextAsync(temporary, snapshot, new UTF8Encoding(false)).ConfigureAwait(false);
                    File.Move(temporary, path, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                if (File.Exists(journal)) File.Delete(journal);
                patches = 0;
                journalBytes = 0;
            }
            else
            {
                var json = JsonSerializer.Serialize(patch, JsonOptions) + "\n";
                try { await File.AppendAllTextAsync(journal, json, new UTF8Encoding(false)).ConfigureAwait(false); }
                catch
                {
                    // A subsequent retry must recover the last complete journal record before starting a new checkpoint.
                    lock (TranscriptCacheLock) InvalidateCachedTranscriptLocked(path);
                    throw;
                }
                patches = cached!.Patches + 1;
                journalBytes = cached.JournalBytes + Encoding.UTF8.GetByteCount(json);
            }
            CacheTranscript(path, new(this, sequence, patches, journalBytes, EstimateCharacters(this), 0, ReadTranscriptStamp(path)));
        }
        finally { gate.Release(); }
    }

    private TranscriptPatch? CreatePatch(SubagentChatState previous, long sequence)
    {
        // Changes to older attempts, artifacts or ownership are infrequent and receive a complete checkpoint.
        if (UserMessage != previous.UserMessage || ParentRunId != previous.ParentRunId || RunId != previous.RunId
            || !ReferenceEquals(PreviousMessages, previous.PreviousMessages) || !ReferenceEquals(Artifacts, previous.Artifacts)
            || !ReferenceEquals(PreviousMessageArtifacts, previous.PreviousMessageArtifacts)
            || !ReferenceEquals(PreviousRunIds, previous.PreviousRunIds)
            || !ReferenceEquals(AuthorizedConsumerRunIds, previous.AuthorizedConsumerRunIds)) return null;
        var oldSteps = previous.AssistantMessage.ToolSteps ?? [];
        var steps = AssistantMessage.ToolSteps ?? [];
        if (steps.Count < oldSteps.Count) return null;
        var changes = new List<ToolPatch>();
        for (var index = 0; index < steps.Count; index++)
        {
            var old = index < oldSteps.Count ? oldSteps[index] : null;
            var current = steps[index];
            if (old is not null && old.Id != current.Id) return null;
            if (current == old) continue;
            string? append = null, replacement = null;
            if (current.Detail != old?.Detail)
            {
                if (old?.Detail is { } detail && current.Detail?.StartsWith(detail, StringComparison.Ordinal) == true)
                    append = current.Detail[detail.Length..];
                else replacement = current.Detail;
            }
            changes.Add(new(current with
            {
                Detail = null,
                InputJson = current.InputJson == old?.InputJson ? null : current.InputJson,
                OutputJson = current.OutputJson == old?.OutputJson ? null : current.OutputJson,
            }, append, replacement));
        }
        var appended = AssistantMessage.Content.StartsWith(previous.AssistantMessage.Content, StringComparison.Ordinal);
        return new(sequence, this with
        {
            UserMessage = UserMessage with { Content = "", ToolSteps = [] },
            AssistantMessage = AssistantMessage with { Content = "", ToolSteps = [] },
            PreviousMessages = null, PreviousMessageArtifacts = null, PreviousRunIds = null,
            Artifacts = null, AuthorizedConsumerRunIds = null,
        }, AssistantMessage with { Content = "", ToolSteps = [] },
            appended ? AssistantMessage.Content[previous.AssistantMessage.Content.Length..] : null,
            appended ? null : AssistantMessage.Content, changes);
    }

    private static async Task<SubagentChatState?> ReadTranscriptAsync(string path, CancellationToken token)
    {
        path = Path.GetFullPath(path);
        var gate = TranscriptGate(path);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var stamp = ReadTranscriptStamp(path);
            lock (TranscriptCacheLock)
            {
                if (FindCachedTranscriptLocked(path) is { } cached && cached.Stamp == stamp)
                {
                    CacheTranscript(path, cached);
                    return cached.State;
                }
                InvalidateCachedTranscriptLocked(path);
            }
            var restored = await ReadTranscriptCoreAsync(path, token).ConfigureAwait(false);
            if (restored.State is { } state && stamp == ReadTranscriptStamp(path))
                // A cold recovery may have ignored an incomplete final journal
                // append. Preserve the existing next-write checkpoint behavior;
                // never append after a possibly truncated recovered tail.
                CacheTranscript(path, new(state, restored.Sequence, int.MaxValue, stamp.JournalBytes,
                    EstimateCharacters(state), 0, stamp));
            return restored.State;
        }
        finally { gate.Release(); }
    }

    private static TranscriptStamp ReadTranscriptStamp(string path)
    {
        var snapshot = new FileInfo(path);
        var journal = new FileInfo(path + ".journal.jsonl");
        return new(snapshot.Length, snapshot.LastWriteTimeUtc,
            journal.Exists ? journal.Length : 0, journal.Exists ? journal.LastWriteTimeUtc : DateTime.MinValue);
    }

    private static void CacheTranscript(string path, TranscriptCache entry)
    {
        lock (TranscriptCacheLock)
        {
            if (TranscriptWriters.TryGetValue(path, out var writer))
            {
                // This is an explicitly owned active writer baseline, not an
                // inactive decoded view. Its lease ends with the stream/attempt.
                writer.Baseline = entry with { UsedAt = ++TranscriptCacheAccess };
                TranscriptCacheEntries.Remove(path);
                return;
            }
            StoreInactiveTranscriptLocked(path, entry);
        }
    }

    private static TranscriptCache? FindCachedTranscriptLocked(string path) =>
        TranscriptWriters.TryGetValue(path, out var writer) ? writer.Baseline : TranscriptCacheEntries.GetValueOrDefault(path);

    private static void InvalidateCachedTranscriptLocked(string path)
    {
        TranscriptCacheEntries.Remove(path);
        if (TranscriptWriters.TryGetValue(path, out var writer)) writer.Baseline = null;
    }

    private static void StoreInactiveTranscriptLocked(string path, TranscriptCache entry)
    {
        // A monotonic access ordinal also distinguishes reads within one
        // clock tick, so frequently opened transcripts are genuinely recent.
        TranscriptCacheEntries[path] = entry with { UsedAt = ++TranscriptCacheAccess };
        while (TranscriptCacheEntries.Count > 8 || TranscriptCacheEntries.Values.Sum(item => item.Characters) > MaximumCacheCharacters)
        {
            var oldest = TranscriptCacheEntries.MinBy(pair => pair.Value.UsedAt).Key;
            TranscriptCacheEntries.Remove(oldest);
        }
    }

    private static async Task<(SubagentChatState? State, long Sequence)> ReadTranscriptCoreAsync(string path, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
        var state = Deserialize(document.RootElement.GetRawText(), TranscriptSnapshotOptions.Value);
        var sequence = document.RootElement.TryGetProperty("persistenceSequence", out var number) ? number.GetInt64() : 0;
        var journal = path + ".journal.jsonl";
        if (state is null || !File.Exists(journal)) return (state, sequence);
        using var reader = new StreamReader(journal, Encoding.UTF8);
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0) continue;
            TranscriptPatch? patch;
            try { patch = JsonSerializer.Deserialize<TranscriptPatch>(line, JsonOptions); }
            catch (JsonException)
            {
                // A crash can leave only the final append incomplete. Read asynchronously to distinguish it from a corrupt middle record.
                if (await reader.ReadLineAsync(token).ConfigureAwait(false) is null) break;
                throw;
            }
            if (patch is null || patch.Sequence <= sequence) continue;
            if (patch.Sequence != sequence + 1 || patch.Metadata.RunId != state.RunId || patch.Metadata.SessionId != state.SessionId)
                throw new InvalidDataException("Die Subagenten-Chronologie ist unvollständig.");
            var message = patch.Assistant with
            {
                Content = patch.ContentReplacement ?? state.AssistantMessage.Content + patch.ContentAppend,
                ToolSteps = state.AssistantMessage.ToolSteps,
            };
            foreach (var change in patch.Tools)
            {
                var old = message.ToolSteps?.FirstOrDefault(step => step.Id == change.Step.Id);
                var detail = change.DetailReplacement ?? (change.DetailAppend is null ? old?.Detail : old?.Detail + change.DetailAppend);
                message = AddStep(message, change.Step with { Detail = detail });
            }
            state = patch.Metadata with
            {
                UserMessage = state.UserMessage, AssistantMessage = message,
                PreviousMessages = state.PreviousMessages, PreviousMessageArtifacts = state.PreviousMessageArtifacts,
                PreviousRunIds = state.PreviousRunIds, Artifacts = state.Artifacts, AuthorizedConsumerRunIds = state.AuthorizedConsumerRunIds,
            };
            sequence = patch.Sequence;
        }
        return (state, sequence);
    }
}
