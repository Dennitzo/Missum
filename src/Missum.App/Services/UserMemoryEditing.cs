using Missum.Core.Memory;

namespace Missum.App.Services;

/// <summary>What the settings editor changes: one memory per line, compared with what was shown.</summary>
public sealed record UserMemoryEditPlan(
    IReadOnlyList<(UserMemoryEntry Entry, string Content)> Revised,
    IReadOnlyList<UserMemoryEntry> Forgotten,
    IReadOnlyList<string> Added)
{
    public bool IsEmpty => Revised.Count == 0 && Forgotten.Count == 0 && Added.Count == 0;
}

/// <summary>
/// Editing the cross-session user profile from the settings: the global, active memories are shown
/// as one statement per line. Applying compares the edited lines with the shown entries: unchanged
/// lines stay, a changed line becomes a revision of the entry it replaced (same position among the
/// changes), removed lines are forgotten and new lines are stored as active memories.
/// </summary>
public static class UserMemoryEditing
{
    public const string Origin = "settings";

    public static async Task<IReadOnlyList<UserMemoryEntry>> ListGlobalAsync(IUserMemoryStore store, CancellationToken cancellationToken)
    {
        var entries = await store.ListAsync(new UserMemoryQuery(Status: UserMemoryStatus.Active, ProjectKey: null, IncludeGlobal: true, Limit: 400), cancellationToken)
            .ConfigureAwait(false);
        return entries.Where(e => e.Scope == UserMemoryScopeKind.Global && e.IsEffective).OrderBy(e => e.CreatedAt).ToList();
    }

    public static string ToText(IEnumerable<UserMemoryEntry> entries) => string.Join(Environment.NewLine, entries.Select(e => OneLine(e.Content)));

    public static UserMemoryEditPlan Plan(IReadOnlyList<UserMemoryEntry> shown, string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var remaining = new List<string>(lines);
        var removed = new List<UserMemoryEntry>();
        foreach (var entry in shown)
        {
            var index = remaining.FindIndex(line => string.Equals(line, OneLine(entry.Content), StringComparison.Ordinal));
            if (index >= 0) remaining.RemoveAt(index);
            else removed.Add(entry);
        }
        // A changed line revises the entry it most resembles; unrelated lines are deletions and additions.
        var revised = new List<(UserMemoryEntry, string)>();
        var forgotten = new List<UserMemoryEntry>();
        foreach (var entry in removed)
        {
            var best = remaining.Select((line, index) => (line, index, score: Similarity(OneLine(entry.Content), line)))
                .Where(candidate => candidate.score >= .3)
                .OrderByDescending(candidate => candidate.score)
                .FirstOrDefault();
            if (best.line is null) { forgotten.Add(entry); continue; }
            revised.Add((entry, best.line));
            remaining.RemoveAt(best.index);
        }
        return new UserMemoryEditPlan(revised, forgotten, remaining);
    }

    /// <summary>Share of common words (Jaccard), case-insensitive.</summary>
    internal static double Similarity(string a, string b)
    {
        static HashSet<string> Words(string text) => text.ToLowerInvariant()
            .Split([' ', '.', ',', ';', ':', '!', '?', '-', '(', ')', '"'], StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        var left = Words(a);
        var right = Words(b);
        if (left.Count == 0 || right.Count == 0) return 0;
        return left.Intersect(right).Count() / (double)left.Union(right).Count();
    }

    public static async Task ApplyAsync(IUserMemoryStore store, UserMemoryEditPlan plan, CancellationToken cancellationToken)
    {
        foreach (var (entry, content) in plan.Revised)
            await store.ReviseAsync(entry.Id, entry.Revision,
                new UserMemoryDraft(entry.Kind, UserMemoryScopeKind.Global, content, Origin, UserMemoryStatus.Active, SupersedesId: entry.Id),
                cancellationToken).ConfigureAwait(false);
        foreach (var entry in plan.Forgotten)
            await store.ForgetAsync(entry.Id, entry.Revision, cancellationToken).ConfigureAwait(false);
        foreach (var content in plan.Added)
            await store.SaveAsync(new UserMemoryDraft(UserMemoryKind.Preference, UserMemoryScopeKind.Global, content, Origin, UserMemoryStatus.Active), cancellationToken)
                .ConfigureAwait(false);
    }

    private static string OneLine(string content) => content.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
}
