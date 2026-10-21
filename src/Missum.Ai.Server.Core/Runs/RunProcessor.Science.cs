using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunProcessor
{
    // Only checked file bindings are refreshed, never the project tree or its chat history.
    // Stable proposal IDs make interruption/reconnection idempotent. No pending model
    // call is invented: these are host-owned readonly verification operations.
    private async Task<CodingWorkingState> RefreshScienceInputsAsync(string runId, string operationId, JsonElement arguments,
        CodingWorkingState state, Func<CodingWorkingState, Task> commit, Func<string?, Task> pending, CancellationToken token)
    {
        if (state.Science is null) return state;
        var projected = CodingWorkingStateReducer.ApplyPlanUpdate(state, arguments, verifyScienceReceipts: false);
        var closing = projected.Plan.Where(step => step.Status == "completed" && !state.Plan.Any(old => old.Id == step.Id && old.Status == "completed"))
            .Select(step => step.Id).ToHashSet(StringComparer.Ordinal);
        if (closing.Count > 0 && projected.Plan.All(step => step.Status == "completed"))
            closing.UnionWith(projected.Science!.Stages.Select(stage => stage.Id));
        var paths = projected.Science!.Stages.Where(stage => closing.Contains(stage.Id)).SelectMany(stage => stage.Checks)
            .Where(check => check.Required || check.EvidenceIds.Count > 0).SelectMany(check => check.Paths.Concat(check.EvidenceIds.SelectMany(id =>
            {
                var receipt = CodingWorkingStateReducer.ResolveEvidence(state, id);
                return new[] { receipt?.VerificationPath, receipt?.Tool is "coding.read" or "math.formalProof" ? receipt.Path : null }.OfType<string>();
            }))).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        foreach (var path in paths)
        {
            var suffix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(operationId + "\n" + path)))[..32];
            var id = "proposal-" + suffix;
            var proposal = await _repository.GetToolProposalAsync(id, runId, token).ConfigureAwait(false);
            if (proposal is null)
            {
                proposal = new(id, runId, ClientToolNames.CodingRead, JsonSerializer.SerializeToElement(new { path, startLine = 1, endLine = 1 }),
                    ToolRiskClass.ReadOnly, "Science-Prüfung: aktuellen Dateistand bestätigen · " + path, DateTimeOffset.MaxValue);
                await _repository.SaveToolProposalAsync(proposal, token).ConfigureAwait(false);
            }
            if (await _repository.GetClientToolResultAsync(id, token).ConfigureAwait(false) is null)
                await pending(id).ConfigureAwait(false);
            var result = await GetClientToolResultOrSuspendAsync(runId, id, token).ConfigureAwait(false);
            // Replayed file receipts must not rewrite newer observations after steering.
            if (!state.Evidence.Any(item => item.Id == id))
            {
                state = CodingWorkingStateReducer.ObserveToolResult(state, new LmToolCall(id, ClientToolNames.CodingRead, proposal.Arguments), SerializeClientToolResult(result));
                await commit(state).ConfigureAwait(false);
            }
            if (result.Status != "completed") throw new ArgumentException("Science-Prüfung konnte Datei " + path + " nicht bestätigen: " + result.Message);
        }
        await pending(null).ConfigureAwait(false);
        return state;
    }
}
