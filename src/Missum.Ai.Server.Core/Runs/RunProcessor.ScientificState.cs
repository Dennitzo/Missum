using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;
using Missum.Ai.Server.Core.Coding;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Runs;

public sealed record ScientificStateRunProgress(int SourcesSinceProgress = 0, long Revision = 0,
    long PublicationRevision = 0, string Target = "foundation", string? LastStagnationHint = null,
    IReadOnlyList<ScientificStateToolFailure>? Failures = null);
public sealed record ScientificStateToolFailure(string Fingerprint, int Count, string? LastHint = null);

public sealed partial class RunProcessor
{
    private static void EnsureScientificStateInstructions(List<LmChatMessage> messages)
    {
        // Initial construction installs this before the first evaluated turn.
        // Recovery retains the exact existing policy and only refreshes receipts.
        var index = messages.FindIndex(static message => message.Role == "system");
        if (index >= 0 && messages[index].Content?.Contains(ScientificStateAgentPolicy.Instructions, StringComparison.Ordinal) != true)
            messages[index] = messages[index] with { Content = messages[index].Content + "\n\n" + ScientificStateAgentPolicy.Instructions };
    }

    private async Task<ScientificStateRunProgress> ObserveScientificStateReceiptAsync(string runId, RunRequest request,
        LmToolCall call, string content, bool succeeded, ScientificStateRunProgress progress, List<LmChatMessage> messages,
        long inputTokens, long outputTokens, CancellationToken cancellationToken)
    {
        if (!ScientificStateCompletionPolicy.Enabled(request)) return progress;
        var readable = ScientificStateCompletionPolicy.TryReceipt(content, out var result, out var successful);
        var canonical = readable && ScientificStateCompletionPolicy.Text(result, "protocol") == ScientificStateCompletionPolicy.Protocol
            && ScientificStateCompletionPolicy.Text(result, "projectId") == ScientificStateCompletionPolicy.ProjectId(request);
        successful |= succeeded && (!readable || !result.TryGetProperty("success", out var declaredSuccess)
            || declaredSuccess.ValueKind != JsonValueKind.False);
        if (canonical)
            progress = progress with
            {
                Revision = Math.Max(progress.Revision, ScientificStateCompletionPolicy.Number(result, "revision")),
                PublicationRevision = Math.Max(progress.PublicationRevision, ScientificStateCompletionPolicy.Number(result, "publicationRevision")),
            };
        var accepted = canonical && successful && call.Name == ClientToolNames.ResearchUpdate
            && (result.TryGetProperty("changedIds", out var changed) && changed.ValueKind == JsonValueKind.Array && changed.GetArrayLength() > 0
                || ScientificStateCompletionPolicy.Boolean(result, "publicationChanged"))
            && !ScientificStateCompletionPolicy.Boolean(result, "replayed");
        var checkedResult = successful && (call.Name is ClientToolNames.ResearchDeliverablesVerify
            or ClientToolNames.ResearchCodeExecute or ClientToolNames.ResearchCodeTest or ClientToolNames.ResearchCodeBenchmark
            or ClientToolNames.MathSymbolic or ClientToolNames.MathNumeric or ClientToolNames.MathSmt or ClientToolNames.MathFormalProof
            || call.Name == "math.evaluate");
        if (accepted || checkedResult)
            progress = progress with { SourcesSinceProgress = 0, Failures = [] };
        if (canonical && result.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var item in items.EnumerateArray())
                if (ScientificStateCompletionPolicy.Text(item, "kind") is "hypothesis" or "requirement"
                    && item.TryGetProperty("data", out var data)
                    && ScientificStateCompletionPolicy.Text(data, "status") is "planned" or "active" or "unresolved" or "blocked" or "pending"
                    && ScientificStateCompletionPolicy.Text(item, "id") is { Length: > 0 } target)
                { progress = progress with { Target = target }; break; }

        if (accepted && call.Arguments.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array
            && changes.EnumerateArray().Any(static item => ScientificStateCompletionPolicy.Text(item, "kind") == "section")
            && request.Subagent is null)
            await _repository.EnsureScientificFirstSectionMetricAsync(runId, progress.Revision,
                progress.PublicationRevision, inputTokens, outputTokens, cancellationToken).ConfigureAwait(false);

        if (call.Name is "web.search" or "web.fetch" or CodingDeepResearchPipeline.ToolName)
        {
            progress = progress with { SourcesSinceProgress = progress.SourcesSinceProgress < int.MaxValue ? progress.SourcesSinceProgress + 1 : int.MaxValue };
            var identity = progress.Revision.ToString(CultureInfo.InvariantCulture) + ":" + progress.Target;
            if (progress.SourcesSinceProgress >= 3 && progress.LastStagnationHint != identity)
            {
                messages.Add(new("system", "[MISSUM_SCIENCE_PROGRESS_HINT:" + identity + "]\n"
                    + "Drei Quellenaktionen haben noch keine neue gespeicherte fachliche Erkenntnis oder tatsächliche Prüfung erzeugt. "
                    + "Bearbeite jetzt das offene Ziel '" + progress.Target + "': formuliere die belegte Hypothese, Grundlage oder konkrete nächste Prüfung "
                    + "und speichere den betroffenen Abschnitt oder Forschungszustand mit research.update. Bei einem echten Quellenhindernis "
                    + "halte Diagnose und offene Grenze fest. Werkzeuge und autonomes Weiterarbeiten bleiben verfügbar."));
                progress = progress with { LastStagnationHint = identity };
            }
        }
        if (!successful)
        {
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(call.Name + "\n" + call.Arguments.GetRawText())));
            var failures = progress.Failures?.ToList() ?? [];
            var index = failures.FindIndex(item => item.Fingerprint == fingerprint);
            var failure = index >= 0 ? failures[index] : new ScientificStateToolFailure(fingerprint, 0);
            var hint = fingerprint + ":" + progress.Revision.ToString(CultureInfo.InvariantCulture);
            failure = failure with { Count = Math.Min(2, failure.Count + 1) };
            if (failure.Count >= 2 && failure.LastHint != hint)
            {
                messages.Add(new("system", "[MISSUM_SCIENCE_TOOL_DIAGNOSIS:" + hint + "]\n"
                    + "Dasselbe Werkzeug wurde mit unveränderten Argumenten zweimal erfolglos ausgeführt. Verwende den vorhandenen "
                    + "Fehlerbeleg: korrigiere gezielt Argumente, Methode oder Umgebung beziehungsweise speichere die begründete offene Grenze. "
                    + "Wiederhole den unveränderten Aufruf nicht ohne neue Erkenntnis. Es gibt keine feste Forschungsquote oder Frist."));
                failure = failure with { LastHint = hint };
            }
            if (index >= 0) failures[index] = failure;
            else failures.Add(failure);
            progress = progress with { Failures = failures.TakeLast(32).ToArray() };
        }
        return progress;
    }
}
