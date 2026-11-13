namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunRepository
{
    // Check durable receipts belonging to this run. Context compaction or a restart must not
    // turn an outstanding screenshot into an apparent verification.
    internal Task<string?> GetBlenderCompletionRepairAsync(string runId, CancellationToken cancellationToken) =>
        GetIntegrationCompletionRepairAsync(runId, "blender.status",
            "blender.scene.edit", "blender.custom.run", "blender.custom.run", "blender.custom.run",
            "blender.custom.run", "blender.view.capture (view=window)", cancellationToken);

    internal Task<string?> GetUnrealCompletionRepairAsync(string runId, CancellationToken cancellationToken) =>
        GetIntegrationCompletionRepairAsync(runId, "unreal.status",
            "unreal.blueprint.create", "unreal.blueprint.edit", "unreal.blueprint.save", "unreal.camera.create",
            "unreal.blueprint.edit", "unreal.view.capture", cancellationToken);

    private async Task<string?> GetIntegrationCompletionRepairAsync(string runId, string statusTool,
        string mutation1, string mutation2, string mutation3, string mutation4, string editTool, string captureTool,
        CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH receipts AS (
                SELECT r.rowid AS sequence, p.name, r.result_json AS payload
                FROM client_tool_results r JOIN client_tool_proposals p ON p.proposal_id=r.proposal_id
                WHERE r.run_id=$run AND p.run_id=$run
                  AND p.name IN ($mutation1,$mutation2,$mutation3,$mutation4,$status)
                  AND json_extract(r.result_json,'$.status')='completed'
            ), latest_change AS (
                SELECT sequence, json_extract(payload,'$.result.operationId') AS operation_id FROM receipts
                WHERE name IN ($mutation1,$mutation2,$mutation3,$mutation4)
                  AND json_extract(payload,'$.result.visualCheck.required')=1
                  AND coalesce(json_extract(payload,'$.result.deduplicated'),0)=0
                  AND length(json_extract(payload,'$.result.operationId'))>0
                ORDER BY sequence DESC LIMIT 1
            )
            SELECT (
                SELECT json_extract(payload,'$.result.visionReview.verdict') FROM receipts
                WHERE name=$status AND sequence>latest_change.sequence
                  AND json_extract(payload,'$.result.visionReview.matchesCapture')=1
                  AND json_extract(payload,'$.result.visionReview.operationId')=latest_change.operation_id
                ORDER BY sequence DESC LIMIT 1
            ), (
                SELECT count(DISTINCT json_extract(payload,'$.result.visionReview.operationId')) FROM receipts
                WHERE name=$status AND json_extract(payload,'$.result.visionReview.matchesCapture')=1
                  AND json_extract(payload,'$.result.visionReview.verdict')='deviation'
            ) FROM latest_change;
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$status", statusTool);
        command.Parameters.AddWithValue("$mutation1", mutation1);
        command.Parameters.AddWithValue("$mutation2", mutation2);
        command.Parameters.AddWithValue("$mutation3", mutation3);
        command.Parameters.AddWithValue("$mutation4", mutation4);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var verdict = reader.IsDBNull(0) ? null : reader.GetString(0);
        return verdict switch
        {
            "verified" or "unavailable" => null,
            "deviation" when reader.GetInt64(1) >= 3 => null,
            "deviation" => $"Die letzte Sichtprüfung meldet eine Abweichung. Arbeite den konkreten Befund mit {editTool} nach, speichere und prüfe anschließend eine neue Aufnahme. Beende den Auftrag erst mit dem tatsächlichen Prüfergebnis.",
            _ => $"Für die letzte Änderung fehlt noch der passende Sichtbefund. Nimm {captureTool}, lade das Bild mit image.input, prüfe genau diesen Upload mit media.analyze und melde operationId, SHA-256 und Befund über {statusTool} visionReview. Vergleiche vorhandene Bildvorlagen mit referenceUploadIds. Bei einem konkreten technischen Fehler melde unavailable.",
        };
    }
}
