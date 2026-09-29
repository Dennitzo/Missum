using System.Text.Json;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Core.Coding;

internal sealed record CodingRejectedDuplicate(LmToolCall Call, string RepresentativeId, string Error);
internal sealed record CodingToolBatch(IReadOnlyList<LmToolCall> Calls,
    IReadOnlyList<CodingRejectedDuplicate> RejectedDuplicates, bool AllInvalid);

/// <summary>Bounds feedback for identical malformed calls, never deduplicates executable operations.</summary>
internal static class CodingToolBatchRecovery
{
    internal const int MaximumInvalidTurns = 3;

    internal static CodingToolBatch Prepare(IReadOnlyList<LmToolCall> calls,
        AgentToolCatalog catalog, IReadOnlyList<AgentToolSpec> availableTools)
    {
        var retained = new List<LmToolCall>(calls.Count);
        var rejected = new List<CodingRejectedDuplicate>();
        var invalid = new Dictionary<string, string>(StringComparer.Ordinal);
        var anyValid = false;
        foreach (var call in calls)
        {
            try
            {
                catalog.Validate(catalog.Resolve(call.Name, availableTools), call.Arguments);
                anyValid = true;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException)
            {
                var signature = call.Name + "\n" + CodingWorkingStateReducer.ArgumentHash(call.Arguments);
                if (invalid.TryGetValue(signature, out var representative))
                {
                    rejected.Add(new(call, representative, exception.Message));
                    continue;
                }
                invalid.Add(signature, call.Id);
            }
            retained.Add(call);
        }
        return new(retained, rejected, calls.Count > 0 && !anyValid);
    }
}

public sealed class CodingInvalidToolLoopException()
    : InvalidOperationException("Das Modell hat in drei aufeinanderfolgenden Werkzeugturns ausschließlich ungültige Aufrufe geliefert. "
        + "Die fehlerhaften Aufrufe wurden nicht ausgeführt und keine fehlenden Argumente ergänzt. "
        + "Bereits ausgeführte Änderungen und der Arbeitsstand bleiben gespeichert; der Auftrag ist noch nicht abgeschlossen.");
