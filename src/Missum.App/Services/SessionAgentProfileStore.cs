using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Core.Models;

namespace Missum.App.Services;

/// <summary>Small per-session UI selections; execution state belongs to the durable gateway run.</summary>
internal sealed class SessionAgentProfileStore(string dataDirectory)
{
    private static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 16).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private sealed record Selection(Guid SessionId, string? ProfileId, DateTimeOffset UpdatedAt);
    private sealed record Goal(Guid SessionId, Guid MessageId, SpecializedAgentOptions Agent);

    private string PathFor(Guid sessionId)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("Die Agentenauswahl benötigt eine Sitzung.", nameof(sessionId));
        return Path.Combine(dataDirectory, "AgentProfiles", sessionId.ToString("N") + ".json");
    }

    internal async Task<string?> ReadAsync(Guid sessionId, CancellationToken token = default)
    {
        var path = PathFor(sessionId);
        if (!File.Exists(path)) return null;
        try
        {
            var selection = JsonSerializer.Deserialize<Selection>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false), JsonOptions);
            if (selection is null || selection.SessionId != sessionId) return null;
            return SpecializedAgentProfiles.BuiltIn.Any(profile => profile.Id == selection.ProfileId) ? selection.ProfileId : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    internal async Task WriteAsync(Guid sessionId, string? profileId, CancellationToken token = default)
    {
        if (profileId is not null && !SpecializedAgentProfiles.BuiltIn.Any(profile => profile.Id == profileId))
            throw new ArgumentException("Unbekanntes Agentenprofil.", nameof(profileId));
        await SaveAtomicAsync(PathFor(sessionId), sessionId,
            new Selection(sessionId, profileId, DateTimeOffset.UtcNow), token).ConfigureAwait(false);
    }

    internal async Task<SpecializedAgentOptions?> ReadGoalAsync(Guid sessionId, Guid messageId, CancellationToken token = default)
    {
        var path = GoalPath(sessionId, messageId);
        if (!File.Exists(path)) return null;
        try
        {
            var goal = JsonSerializer.Deserialize<Goal>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false), JsonOptions);
            return goal?.SessionId == sessionId && goal.MessageId == messageId ? goal.Agent : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    internal Task WriteGoalAsync(Guid sessionId, Guid messageId, SpecializedAgentOptions agent, CancellationToken token = default) =>
        SaveAtomicAsync(GoalPath(sessionId, messageId), sessionId, new Goal(sessionId, messageId, agent), token);

    private string GoalPath(Guid sessionId, Guid messageId)
    {
        _ = PathFor(sessionId);
        if (messageId == Guid.Empty) throw new ArgumentException("Das Agentenziel benötigt eine Nachricht.", nameof(messageId));
        return Path.Combine(dataDirectory, "AgentProfiles", sessionId.ToString("N"), messageId.ToString("N") + ".json");
    }

    private static async Task SaveAtomicAsync<T>(string path, Guid sessionId, T value, CancellationToken token)
    {
        var gate = Gates[(sessionId.GetHashCode() & int.MaxValue) % Gates.Length];
        await gate.WaitAsync(token).ConfigureAwait(false);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(temporary,
                JsonSerializer.Serialize(value, JsonOptions), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally { gate.Release(); }
        }
    }
}

internal static class SpecializedAgentSelection
{
    internal static SpecializedAgentOptions? Capture(string? profileId, ChatMode mode, string goal)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return null;
        if (!SpecializedAgentProfiles.BuiltIn.Any(profile => profile.Id == profileId)) throw new ArgumentException("Unbekanntes Agentenprofil.", nameof(profileId));
        if (mode is not (ChatMode.Coding or ChatMode.ClaudeScience))
            throw new InvalidOperationException("Spezialisierte Agenten sind in Codex und Claude Science verfügbar.");
        if (string.IsNullOrWhiteSpace(goal) || goal.Length > 100_000)
            throw new ArgumentException("Das Agentenprofil benötigt ein Ziel mit höchstens 100.000 Zeichen.", nameof(goal));
        return new(profileId, goal);
    }
}
