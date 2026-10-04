using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunRepository
{
    internal async Task<AgentRunCheckpoint?> GetSubagentContinuationCheckpointAsync(string childRunId,
        CancellationToken token = default)
    {
        var checkpoint = await GetCheckpointAsync(childRunId, token).ConfigureAwait(false);
        return checkpoint is null ? null : RestoreInterruptedVisibleTail(checkpoint,
            await GetContinuationEventsAsync(childRunId, 0, token).ConfigureAwait(false));
    }

    // Authorization comes from the gateway's recorded context adoption, never
    // from a run ID invented or quoted in model/user text.
    internal async Task<(RunSnapshot Snapshot, RunRequest Request)?> GetAuthorizedSubagentContinuationAsync(
        string parentRunId, string childRunId, CancellationToken token = default)
    {
        var parent = await GetAsync(parentRunId, token).ConfigureAwait(false);
        var parentRequest = await GetRequestAsync(parentRunId, token).ConfigureAwait(false);
        var source = await GetAsync(childRunId, token).ConfigureAwait(false);
        var sourceRequest = await GetRequestAsync(childRunId, token).ConfigureAwait(false);
        if (parent?.State is not (RunState.Queued or RunState.Running or RunState.WaitingForClient)
            || parentRequest is null || parentRequest.Subagent is not null || source is null
            || sourceRequest?.Subagent is not { } relation
            || !SameSubagentContinuationScope(sourceRequest, parentRequest))
            return null;
        if (relation.ParentRunId == parentRunId) return (source, sourceRequest);
        await using var connection = await _database.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT json_extract(event.data_json, '$.sourceParentRunId')
            FROM run_events event, json_each(event.data_json, '$.children') child
            WHERE event.run_id = $parent AND event.event_type = $type
              AND json_extract(child.value, '$.runId') = $child
            ORDER BY event.id DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$parent", parentRunId);
        command.Parameters.AddWithValue("$child", childRunId);
        command.Parameters.AddWithValue("$type", SubagentToolNames.ContinuationStateEvent);
        var sourceParent = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
        if (sourceParent is null || relation.ParentRunId != sourceParent) return null;
        return (source, sourceRequest);
    }

    /// <summary>
    /// Carries forward only the predecessor's owned or explicitly adopted branches.
    /// A newer owned attempt replaces an older adopted attempt of the same agent.
    /// </summary>
    internal async Task<IReadOnlyList<(RunSnapshot Snapshot, RunRequest Request)>> GetSubagentContinuationCandidatesAsync(
        string sourceParentRunId, CancellationToken token = default)
    {
        var parentRequest = await GetRequestAsync(sourceParentRunId, token).ConfigureAwait(false);
        if (parentRequest is null || parentRequest.Subagent is not null || string.IsNullOrWhiteSpace(parentRequest.SessionId))
            return [];
        var selected = new Dictionary<string, (RunSnapshot Snapshot, RunRequest Request)>(StringComparer.Ordinal);
        var direct = await GetSubagentRunsAsync(sourceParentRunId, cancellationToken: token).ConfigureAwait(false);
        foreach (var child in direct.OrderByDescending(item => item.Snapshot.CreatedAt))
            if (child.Request.Subagent is { } relation && SameSubagentContinuationScope(child.Request, parentRequest))
                selected.TryAdd(relation.AgentId, child);

        var adopted = new List<(string RunId, string SourceParentRunId)>();
        await using (var connection = await _database.OpenConnectionAsync(token).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT json_extract(child.value, '$.runId'), json_extract(event.data_json, '$.sourceParentRunId')
                FROM run_events event, json_each(event.data_json, '$.children') child
                WHERE event.run_id = $parent AND event.event_type = $type
                ORDER BY event.id DESC;
                """;
            command.Parameters.AddWithValue("$parent", sourceParentRunId);
            command.Parameters.AddWithValue("$type", SubagentToolNames.ContinuationStateEvent);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                if (!reader.IsDBNull(0) && !reader.IsDBNull(1))
                    adopted.Add((reader.GetString(0), reader.GetString(1)));
        }
        var inherited = new List<(RunSnapshot Snapshot, RunRequest Request)>();
        foreach (var item in adopted.Distinct())
        {
            var child = await GetAsync(item.RunId, token).ConfigureAwait(false);
            var request = await GetRequestAsync(item.RunId, token).ConfigureAwait(false);
            if (child is not null && request?.Subagent is { } relation
                && relation.ParentRunId == item.SourceParentRunId
                && SameSubagentContinuationScope(request, parentRequest))
                inherited.Add((child, request));
        }
        foreach (var child in inherited.OrderByDescending(item => item.Snapshot.CreatedAt))
            selected.TryAdd(child.Request.Subagent!.AgentId, child);
        return selected.Values.OrderBy(item => item.Snapshot.CreatedAt).ToArray();
    }

    private static bool SameSubagentContinuationScope(RunRequest child, RunRequest parent) =>
        child.Subagent is { } relation && relation.ParentSessionId == parent.SessionId
        && child.Mode == parent.Mode && !string.IsNullOrWhiteSpace(parent.SessionId)
        && string.Equals(Workspace(child), Workspace(parent), StringComparison.OrdinalIgnoreCase);

    private static string? Workspace(RunRequest request) =>
            (request.Mode == RunMode.Coding ? request.CodingOptions?.WorkspacePath : request.WorkspacePath)
                ?.Replace('\\', '/').TrimEnd('/');
}
