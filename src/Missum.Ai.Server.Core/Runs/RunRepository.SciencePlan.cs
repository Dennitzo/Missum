using System.Globalization;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunRepository
{
    // Old checkpoints did not record the entrance flag. Only this run's actual
    // successful receipt can establish it; inherited session messages cannot.
    internal async Task<bool> HasSuccessfulInitialSciencePlanAsync(string runId, CancellationToken token)
    {
        await using var connection = await _database.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM run_events
                WHERE run_id=$run AND event_type=$type
                  AND json_extract(data_json,'$.tool')=$tool
                  AND json_extract(data_json,'$.success')=1
                  AND json_array_length(json_extract(data_json,'$.result.storedSteps'))>0);
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$type", RunEventTypes.ServerToolCompleted);
        command.Parameters.AddWithValue("$tool", CodingWorkingStateTools.PlanTool);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
    }
}
