using System.Text.Json;
using Microsoft.Data.Sqlite;
using Missum.Core.Models;

namespace Missum.Infrastructure.Repositories;

internal static class ConversationToolStepStorage
{
    // Legacy messages are read without a global migration. An atomic per-message import selects the indexed rows instead.
    internal const string CompleteStepsSql = """
        CASE WHEN EXISTS(SELECT 1 FROM chat_tool_step_messages normalized WHERE normalized.message_id=m.id)
        THEN (SELECT json_group_array(json(step_json)) FROM
            (SELECT step_json FROM chat_tool_steps WHERE message_id=m.id ORDER BY ordinal))
        ELSE m.tool_steps_json END
        """;

    internal static async Task EnsureNormalizedAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid messageId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$message", messageId.ToString("D"));
        command.CommandText = "SELECT 1 FROM chat_tool_step_messages WHERE message_id=$message;";
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not null) return;
        command.CommandText = "SELECT tool_steps_json FROM chat_messages WHERE id=$message;";
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not string legacy)
            throw new InvalidOperationException("Die Werkzeugnachricht wurde nicht gefunden.");
        var steps = JsonSerializer.Deserialize<AssistantToolStep[]>(legacy, JsonSerializerOptions.Web) ?? [];
        using var legacyDocument = JsonDocument.Parse(legacy);
        // Clear the duplicate blob before inserting indexed rows. The invalidation trigger runs while no imported rows exist;
        // all of this is one transaction, so cancellation restores the complete original blob.
        command.CommandText = "UPDATE chat_messages SET tool_steps_json='[]' WHERE id=$message AND tool_steps_json<>'[]';";
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        command.CommandText = """
            INSERT INTO chat_tool_steps(message_id,step_id,ordinal,content_offset,tool_name,child_agent_id,header_json,step_json)
            VALUES($message,$id,$ordinal,$offset,json_extract($header,'$.tool'),json_extract($header,'$.outputJson')->>'$.agentId',$header,$step);
            """;
        command.Parameters.Add("$id", SqliteType.Text);
        command.Parameters.Add("$ordinal", SqliteType.Integer);
        command.Parameters.Add("$header", SqliteType.Text);
        command.Parameters.Add("$offset", SqliteType.Integer);
        command.Parameters.Add("$step", SqliteType.Text);
        for (var index = 0; index < steps.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            command.Parameters["$id"].Value = steps[index].Id;
            command.Parameters["$ordinal"].Value = index;
            command.Parameters["$offset"].Value = (object?)steps[index].ContentOffset ?? DBNull.Value;
            command.Parameters["$header"].Value = JsonSerializer.Serialize(Header(steps[index]), JsonSerializerOptions.Web);
            command.Parameters["$step"].Value = legacyDocument.RootElement[index].GetRawText();
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$message", messageId.ToString("D"));
        command.CommandText = "INSERT INTO chat_tool_step_messages(message_id) VALUES($message);";
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    internal static async Task<(AssistantToolStep Step, bool Changed)> UpsertChangeAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid messageId, AssistantToolStep incoming, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT step_json FROM chat_tool_steps WHERE message_id=$message AND step_id=$id;";
        command.Parameters.AddWithValue("$message", messageId.ToString("D"));
        command.Parameters.AddWithValue("$id", incoming.Id);
        var json = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
        var previous = json is null ? null : JsonSerializer.Deserialize<AssistantToolStep>(json, JsonSerializerOptions.Web);
        var merged = AssistantToolStep.Merge(previous, incoming);
        if (previous == merged) return (merged, false);
        command.CommandText = """
            INSERT INTO chat_tool_steps(message_id,step_id,ordinal,content_offset,tool_name,child_agent_id,header_json,step_json)
            VALUES($message,$id,(SELECT COALESCE(MAX(ordinal)+1,0) FROM chat_tool_steps WHERE message_id=$message),$offset,
                json_extract($header,'$.tool'),json_extract($header,'$.outputJson')->>'$.agentId',$header,$step)
            ON CONFLICT(message_id,step_id) DO UPDATE SET content_offset=excluded.content_offset,tool_name=excluded.tool_name,
                child_agent_id=excluded.child_agent_id,header_json=excluded.header_json,step_json=excluded.step_json;
            """;
        command.Parameters.AddWithValue("$header", JsonSerializer.Serialize(Header(merged), JsonSerializerOptions.Web));
        command.Parameters.AddWithValue("$offset", (object?)merged.ContentOffset ?? DBNull.Value);
        command.Parameters.AddWithValue("$step", JsonSerializer.Serialize(merged, JsonSerializerOptions.Web));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        return (merged, true);
    }

    internal static async Task<IReadOnlyList<AssistantToolStep>> ReadAllAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid messageId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT step_json FROM chat_tool_steps WHERE message_id=$message ORDER BY ordinal;";
        command.Parameters.AddWithValue("$message", messageId.ToString("D"));
        var steps = new List<AssistantToolStep>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            if (JsonSerializer.Deserialize<AssistantToolStep>(reader.GetString(0), JsonSerializerOptions.Web) is { } step) steps.Add(step);
        return steps;
    }

    internal static AssistantToolStep Header(AssistantToolStep step)
    {
        var narration = step.Tool is "assistant.narration" or "assistant.progress";
        return step with
        {
            DisplayHeader = AssistantToolStepHeaders.Create(step),
            Detail = narration ? step.Detail : null, PreviewHtml = null, InputJson = null,
            OutputJson = CompactReceipt(step), Explanation = step.Explanation is { Length: > 400 } text ? text[..400] : step.Explanation,
            DetailsDeferred = !narration,
        };
    }

    private static string? CompactReceipt(AssistantToolStep step)
    {
        if (step.OutputJson is null || step.Tool is not ("assistant.continuation" or "subagent" or "subagent.completed")) return null;
        using var document = JsonDocument.Parse(step.OutputJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
        var fields = step.Tool == "assistant.continuation" ? ContinuationFields : SubagentFields;
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in fields)
            if (document.RootElement.TryGetProperty(field, out var value)) values[field] = value.Clone();
        if (!values.ContainsKey("attemptStartedAt") && document.RootElement.TryGetProperty("assistantMessage", out var assistant)
            && assistant.ValueKind == JsonValueKind.Object && assistant.TryGetProperty("createdAt", out var at))
            values["attemptStartedAt"] = at.Clone();
        return JsonSerializer.Serialize(values, JsonSerializerOptions.Web);
    }

    private static readonly string[] ContinuationFields = ["sessionId", "messageId", "localRunId", "priorActiveMilliseconds"];
    private static readonly string[] SubagentFields = ["agentId", "runId", "parentRunId", "sessionId", "title", "status",
        "resultDelivered", "projectionRevision", "transcriptRevision", "attemptStartedAt", "subagentStorageKey", "kind", "originToolStepId", "originMessageId", "gpuIndex"];
}
