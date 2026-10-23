using System.Text.Json;
using Microsoft.Data.Sqlite;
using Missum.Core.Models;

namespace Missum.Infrastructure.Repositories;

public sealed partial class SqliteConversationSnapshotRepository
{
    /// <summary>Fetches plan receipts independently of chat pagination and never loads unrelated tool details.</summary>
    public Task<AssistantToolStep?> GetWorkingPlanAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        ReadWorkingPlanAsync(sessionId, null, cancellationToken);

    public Task<AssistantToolStep?> GetWorkingPlanAtAsync(Guid sessionId, Guid messageId, string stepId,
        CancellationToken cancellationToken = default) => ReadWorkingPlanAsync(sessionId, (messageId, stepId), cancellationToken);

    private async Task<AssistantToolStep?> ReadWorkingPlanAsync(Guid sessionId, (Guid MessageId, string StepId)? boundary,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        var sql = WorkingPlanReceiptsSql;
        if (boundary is { } selected)
        {
            command.Parameters.AddWithValue("$message", selected.MessageId.ToString("D"));
            command.Parameters.AddWithValue("$step", selected.StepId);
            command.CommandText = """
                SELECT m.created_at,m.id,step.ordinal FROM chat_messages m
                JOIN chat_tool_steps step ON step.message_id=m.id
                WHERE m.session_id=$session AND m.id=$message AND step.step_id=$step AND step.tool_name='coding.updatePlan'
                UNION ALL
                SELECT m.created_at,m.id,CAST(s.key AS INTEGER) FROM chat_messages m,json_each(m.tool_steps_json) s
                WHERE m.session_id=$session AND m.id=$message AND json_extract(s.value,'$.id')=$step
                    AND json_extract(s.value,'$.tool')='coding.updatePlan'
                    AND NOT EXISTS(SELECT 1 FROM chat_tool_step_messages normalized WHERE normalized.message_id=m.id)
                LIMIT 1;
                """;
            await using var selectedReader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await selectedReader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            command.Parameters.AddWithValue("$throughAt", selectedReader.GetString(0));
            command.Parameters.AddWithValue("$throughMessage", selectedReader.GetString(1));
            command.Parameters.AddWithValue("$throughOrdinal", selectedReader.GetInt64(2));
            sql += " AND (message_at,message_id,ordinal)<=($throughAt,$throughMessage,$throughOrdinal)";
        }
        sql += " ORDER BY message_at DESC,message_id DESC,ordinal DESC";
        command.CommandText = sql + " LIMIT 1;";
        var latestJson = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (latestJson is null) return null;
        var latest = JsonSerializer.Deserialize<AssistantToolStep>(latestJson, JsonSerializerOptions.Web)!;
        var output = PlanJson(latest.OutputJson);
        if (PlanProperty(output, "storedSteps").ValueKind == JsonValueKind.Array
            && PlanProperty(output, "storedAcceptanceCriteria").ValueKind == JsonValueKind.Array)
            return CompactWorkingPlan(latest, output);

        // Old gateways returned deltas. Reconstruct only plan calls, backwards to the nearest full receipt.
        // Once upgraded, normal snapshots take the single-receipt path above.
        var receipts = new List<AssistantToolStep>();
        command.CommandText = sql + ";";
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var receipt = JsonSerializer.Deserialize<AssistantToolStep>(reader.GetString(0), JsonSerializerOptions.Web)!;
                receipts.Add(receipt);
                var result = PlanJson(receipt.OutputJson);
                if (PlanProperty(result, "storedSteps").ValueKind == JsonValueKind.Array
                    && PlanProperty(result, "storedAcceptanceCriteria").ValueKind == JsonValueKind.Array) break;
            }
        var steps = new List<JsonElement>();
        var criteria = new List<JsonElement>();
        for (var index = receipts.Count - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receipt = receipts[index];
            var result = PlanJson(receipt.OutputJson);
            var input = PlanJson(receipt.InputJson);
            MergePlanList(steps, result, input, "steps", "storedSteps", "updatedSteps", "storedStepIds");
            MergePlanList(criteria, result, input, "acceptanceCriteria", "storedAcceptanceCriteria", "updatedAcceptanceCriteria", "storedAcceptanceCriterionIds");
        }
        return CompactWorkingPlan(latest, JsonSerializer.SerializeToElement(new { storedSteps = steps, storedAcceptanceCriteria = criteria }));
    }

    private const string WorkingPlanReceiptsSql = """
        SELECT payload FROM (
            SELECT step.step_json AS payload,m.created_at AS message_at,m.id AS message_id,step.ordinal AS ordinal
            FROM chat_messages m JOIN chat_tool_steps step ON step.message_id=m.id
            WHERE m.session_id=$session AND step.tool_name='coding.updatePlan'
                AND json_extract(step.header_json,'$.status')='completed'
            UNION ALL
            SELECT s.value,m.created_at,m.id,CAST(s.key AS INTEGER)
            FROM chat_messages m,json_each(m.tool_steps_json) s
            WHERE m.session_id=$session AND json_extract(s.value,'$.tool')='coding.updatePlan'
                AND json_extract(s.value,'$.status')='completed'
                AND NOT EXISTS(SELECT 1 FROM chat_tool_step_messages normalized WHERE normalized.message_id=m.id)
        )
        WHERE CASE WHEN json_valid(json_extract(payload,'$.outputJson'))
            THEN COALESCE(json_extract(json_extract(payload,'$.outputJson'),'$.success'),1) ELSE 0 END
        """;

    private static AssistantToolStep CompactWorkingPlan(AssistantToolStep step, JsonElement output) => step with
    {
        Detail = null, PreviewHtml = null, InputJson = null, Explanation = null,
        OutputJson = JsonSerializer.Serialize(new
        {
            success = true,
            storedSteps = PlanProperty(output, "storedSteps"),
            storedAcceptanceCriteria = PlanProperty(output, "storedAcceptanceCriteria"),
        }),
    };

    private static void MergePlanList(List<JsonElement> items, JsonElement output, JsonElement input,
        string inputKey, string storedKey, string changedKey, string idsKey)
    {
        if (PlanProperty(output, storedKey) is { ValueKind: JsonValueKind.Array } stored)
        {
            items.Clear();
            items.AddRange(stored.EnumerateArray());
        }
        else
        {
            Merge(PlanProperty(input, inputKey));
            Merge(PlanProperty(output, changedKey));
        }
        if (PlanProperty(output, idsKey) is { ValueKind: JsonValueKind.Array } ids)
        {
            var current = items.ToArray();
            items.Clear();
            foreach (var id in ids.EnumerateArray().Where(id => id.ValueKind == JsonValueKind.String))
                if (current.FirstOrDefault(item => PlanText(item, "id") == id.GetString()) is { ValueKind: JsonValueKind.Object } item)
                    items.Add(item);
        }

        void Merge(JsonElement changes)
        {
            if (changes.ValueKind != JsonValueKind.Array) return;
            foreach (var item in changes.EnumerateArray())
            {
                var id = PlanText(item, "id");
                if (id.Length == 0) continue;
                var index = items.FindIndex(existing => PlanText(existing, "id") == id);
                var fields = index >= 0 ? items[index].EnumerateObject().ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal)
                    : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var field in item.EnumerateObject()) fields[field.Name] = field.Value;
                var merged = JsonSerializer.SerializeToElement(fields);
                if (index >= 0) items[index] = merged;
                else items.Add(merged);
            }
        }
    }

    private static JsonElement PlanProperty(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var found) ? found : default;
    private static string PlanText(JsonElement value, string key) => PlanProperty(value, key) is { ValueKind: JsonValueKind.String } text ? text.GetString() ?? "" : "";
    private static JsonElement PlanJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<JsonElement>(json); }
        catch (JsonException) { return default; }
    }
}
