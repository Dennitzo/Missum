using Microsoft.Data.Sqlite;
using Missum.Ai.Server.Core.Coding;
using System.Globalization;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunRepository
{
    private async Task ArchivePlanEvidenceAsync(SqliteConnection connection, SqliteTransaction transaction,
        string runId, CodingWorkingState? state, CancellationToken token)
    {
        if (state is null || state.Evidence.Count == 0) return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(sequence), -1) FROM run_plan_evidence WHERE run_id=$run;";
        command.Parameters.AddWithValue("$run", runId);
        var savedSequence = Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture);
        command.CommandText = """
            INSERT OR IGNORE INTO run_plan_evidence(run_id,evidence_id,sequence,is_work,kinds,path,receipt_json)
            SELECT $run,$id,$sequence,$work,$kinds,$path,$json
            WHERE EXISTS (SELECT 1 FROM runs WHERE run_id=$run AND state IN ('Queued','Running','WaitingForClient','Interrupted'));
            """;
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var sequence = command.Parameters.Add("$sequence", SqliteType.Integer);
        var work = command.Parameters.Add("$work", SqliteType.Integer);
        var kinds = command.Parameters.Add("$kinds", SqliteType.Text);
        var path = command.Parameters.Add("$path", SqliteType.Text);
        var json = command.Parameters.Add("$json", SqliteType.Text);
        foreach (var receipt in state.Evidence.Where(receipt => receipt.Sequence > savedSequence))
        {
            id.Value = receipt.Id;
            sequence.Value = receipt.Sequence;
            work.Value = ScienceStagePolicy.IsWorkReceipt(receipt);
            kinds.Value = "|" + string.Join('|', ScienceStagePolicy.EligibleKinds(receipt)) + "|";
            path.Value = (object?)ScienceStagePolicy.NormalizeProjectPath(receipt.VerificationPath ?? receipt.Path)?.ToUpperInvariant() ?? DBNull.Value;
            json.Value = JsonSerializer.Serialize(receipt, _database.JsonOptions);
            _ = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }

    internal async Task<JsonElement> QueryPlanEvidenceAsync(string runId, CodingWorkingState state,
        JsonElement arguments, CancellationToken token)
    {
        var query = CodingWorkingStateTools.ReadEvidenceQuery(state, arguments);
        await using var connection = await _database.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT receipt_json FROM run_plan_evidence
            WHERE run_id=$run AND is_work=1 AND sequence>$after
                AND ($kind IS NULL OR instr(kinds,$kind)>0)
                AND ($path IS NULL OR path=$path COLLATE NOCASE)
                AND ($paths IS NULL OR path IN (SELECT value FROM json_each($paths)))
                AND ($before IS NULL OR sequence<$before OR sequence=$before AND evidence_id>$id)
            ORDER BY sequence DESC,evidence_id LIMIT 11;
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$kind", query.Kind is null ? DBNull.Value : "|" + query.Kind + "|");
        command.Parameters.AddWithValue("$path", (object?)query.Path?.ToUpperInvariant() ?? DBNull.Value);
        command.Parameters.AddWithValue("$paths", query.Kind == "publication" && query.Paths is { Count: > 0 }
            ? JsonSerializer.Serialize(query.Paths.Select(path => ScienceStagePolicy.NormalizeProjectPath(path)?.ToUpperInvariant())) : DBNull.Value);
        command.Parameters.AddWithValue("$after", query.AfterSequence);
        command.Parameters.AddWithValue("$before", (object?)query.BeforeSequence ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", (object?)query.BeforeId ?? DBNull.Value);
        var receipts = new List<CodingToolEvidence>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var receipt = JsonSerializer.Deserialize<CodingToolEvidence>(reader.GetString(0), _checkpointJsonOptions)!;
            if (ScienceStagePolicy.FindCandidates(state with { Evidence = [receipt] }, query.Kind, query.Paths, query.AfterSequence).Length == 0)
                continue;
            receipts.Add(receipt);
            if (receipts.Count == 11) break;
        }
        // The first query after an upgrade can precede the next checkpoint.
        // Merge its existing bounded state without replacing archived results.
        var merged = receipts.Concat(ScienceStagePolicy.FindCandidates(state, query.Kind, query.Paths, query.AfterSequence))
            .DistinctBy(receipt => receipt.Id)
            .Where(receipt => (query.BeforeSequence is null || receipt.Sequence < query.BeforeSequence
                    || receipt.Sequence == query.BeforeSequence && string.CompareOrdinal(receipt.Id, query.BeforeId) > 0)
                && (query.Path is null || string.Equals(ScienceStagePolicy.NormalizeProjectPath(receipt.VerificationPath ?? receipt.Path),
                    query.Path, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(receipt => receipt.Sequence).ThenBy(receipt => receipt.Id, StringComparer.Ordinal).Take(11).ToArray();
        return CodingWorkingStateTools.EvidenceQueryReceipt(state, merged);
    }

    internal async Task<CodingWorkingState> RestorePlanEvidenceAsync(string runId, CodingWorkingState state,
        JsonElement arguments, CancellationToken token)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        Collect(arguments);
        if (ids.Count == 0) return state;
        var receipts = state.Evidence.ToList();
        await using var connection = await _database.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT receipt_json FROM run_plan_evidence
            WHERE run_id=$run AND (evidence_id=$id OR json_extract(receipt_json,'$.outputEvidenceId')=$id);
            """;
        command.Parameters.AddWithValue("$run", runId);
        var idParameter = command.Parameters.Add("$id", SqliteType.Text);
        foreach (var id in ids)
        {
            idParameter.Value = id;
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var receipt = JsonSerializer.Deserialize<CodingToolEvidence>(reader.GetString(0), _checkpointJsonOptions)!;
                if (!receipts.Any(existing => existing.Id == receipt.Id)) receipts.Add(receipt);
            }
        }
        return state with { Evidence = receipts };

        void Collect(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Array)
                foreach (var child in value.EnumerateArray()) Collect(child);
            else if (value.ValueKind == JsonValueKind.Object)
                foreach (var field in value.EnumerateObject())
                {
                    if (field.Name == "evidenceIds" && field.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var id in field.Value.EnumerateArray())
                            if (id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 and <= 100 } text
                                && !state.Evidence.Any(receipt => receipt.Id == text)) ids.Add(text);
                    }
                    else Collect(field.Value);
                }
        }
    }
}
