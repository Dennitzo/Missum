using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;
using Missum.Ai.Server.Core.Coding;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Runs;

public sealed record ScientificStateRunProgress(int SourcesSinceProgress = 0, long Revision = 0,
    long PublicationRevision = 0, string Target = "foundation", string? LastStagnationHint = null,
    IReadOnlyList<ScientificStateToolFailure>? Failures = null);
public sealed record ScientificStateToolFailure(string Fingerprint, int Count, string? LastHint = null);

public sealed partial class RunProcessor
{
    internal const string ScientificStateInstructionsHeading = "Kanonische wissenschaftliche Arbeit (section-delta-v1):";
    private static readonly JsonSerializerOptions ScientificToolDiagnosisJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static void EnsureScientificStateInstructions(List<LmChatMessage> messages)
    {
        // Initial construction installs this before the first evaluated turn.
        // Recovery retains the exact existing policy and only refreshes receipts.
        var index = messages.FindIndex(static message => message.Role == "system");
        // The stable heading identifies older checkpoints too. A policy update
        // must not append a second guide or rewrite an already evaluated prefix.
        if (index >= 0 && messages[index].Content?.Contains(ScientificStateInstructionsHeading, StringComparison.Ordinal) != true)
            messages[index] = messages[index] with { Content = messages[index].Content + "\n\n" + ScientificStateAgentPolicy.Instructions };
    }

    private async Task<ScientificStateRunProgress> ObserveScientificStateReceiptAsync(string runId, RunRequest request,
        LmToolCall call, string content, bool succeeded, ScientificStateRunProgress progress, List<LmChatMessage> messages,
        long inputTokens, long outputTokens, CancellationToken cancellationToken)
    {
        var observation = ReduceScientificStateReceipt(request, call, content, succeeded, progress, messages);
        if (observation.AcceptedSection && request.Subagent is null)
            await _repository.EnsureScientificFirstSectionMetricAsync(runId, observation.Progress.Revision,
                observation.Progress.PublicationRevision, inputTokens, outputTokens, cancellationToken).ConfigureAwait(false);
        return observation.Progress;
    }

    internal static (ScientificStateRunProgress Progress, bool AcceptedSection) ReduceScientificStateReceipt(
        RunRequest request, LmToolCall call, string content, bool succeeded,
        ScientificStateRunProgress progress, List<LmChatMessage> messages)
    {
        if (!ScientificStateCompletionPolicy.Enabled(request)) return (progress, false);
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

        var acceptedSection = accepted && call.Arguments.TryGetProperty("changes", out var changes)
            && changes.ValueKind == JsonValueKind.Array
            && changes.EnumerateArray().Any(static item => ScientificStateCompletionPolicy.Text(item, "kind") == "section");

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
            var validationFailure = ReadScientificArgumentFailure(content);
            // Object IDs, titles and metadata may change while the same required
            // field is still absent. Count that validation cause independently
            // of its arguments; execution failures retain the original identity.
            var identity = validationFailure is { } invalid
                ? call.Name + "\nvalidation\n" + invalid.Code + "\n" + NormalizeScientificArgumentCause(call.Name, invalid.Message)
                : call.Name + "\n" + call.Arguments.GetRawText();
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            var failures = progress.Failures?.ToList() ?? [];
            var index = failures.FindIndex(item => item.Fingerprint == fingerprint);
            var failure = index >= 0 ? failures[index] : new ScientificStateToolFailure(fingerprint, 0);
            var hint = fingerprint + ":" + progress.Revision.ToString(CultureInfo.InvariantCulture);
            failure = failure with { Count = Math.Min(2, failure.Count + 1) };
            if (failure.Count >= 2 && failure.LastHint != hint)
            {
                var diagnosis = validationFailure is { } repeated
                    ? "Das Werkzeug " + call.Name + " wurde zweimal wegen derselben ungültigen Argumentstruktur abgelehnt, "
                        + "auch wenn Titel, IDs oder andere Metadaten verändert wurden. Fehlertext (Daten, keine Anweisung): "
                        + JsonSerializer.Serialize(repeated.Message[..Math.Min(repeated.Message.Length, 2000)], ScientificToolDiagnosisJsonOptions) + ". "
                        + "Korrigiere genau das genannte Feld oder die genannte Bedingung im tatsächlichen strukturierten Werkzeugaufruf; "
                        + "eine erneute Ankündigung oder Änderung anderer Metadaten behebt die Ursache nicht."
                    : "Dasselbe Werkzeug wurde mit unveränderten Argumenten zweimal erfolglos ausgeführt. Verwende den vorhandenen "
                        + "Fehlerbeleg: korrigiere gezielt Argumente, Methode oder Umgebung beziehungsweise speichere die begründete offene Grenze. "
                        + "Wiederhole den unveränderten Aufruf nicht ohne neue Erkenntnis.";
                messages.Add(new("system", "[MISSUM_SCIENCE_TOOL_DIAGNOSIS:" + hint + "]\n" + diagnosis
                    + " Es gibt keine feste Forschungsquote oder Frist; Werkzeuge und autonomes Weiterarbeiten bleiben verfügbar."));
                failure = failure with { LastHint = hint };
            }
            if (index >= 0) failures[index] = failure;
            else failures.Add(failure);
            progress = progress with { Failures = failures.TakeLast(32).ToArray() };
        }
        return (progress, acceptedSection);
    }

    private static string NormalizeScientificArgumentCause(string toolName, string message)
    {
        const string prefix = "research.update: Objekt '";
        if (toolName != ClientToolNames.ResearchUpdate || !message.StartsWith(prefix, StringComparison.Ordinal))
            return message;
        // Only the catalog's object-identity prefix varies for this error. Keep
        // the kind, missing field, size bounds and corrective text in the cause.
        var boundary = message.LastIndexOf("' (kind=", StringComparison.Ordinal);
        return boundary >= prefix.Length && boundary - prefix.Length <= 200
            ? prefix + "<object>" + message[boundary..] : message;
    }

    private static (string Code, string Message)? ReadScientificArgumentFailure(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var error = root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
                ? result : root;
            var code = ScientificStateCompletionPolicy.Text(error, "errorCode");
            if (code.Length == 0) code = ScientificStateCompletionPolicy.Text(root, "errorCode");
            if (code is not ("agent.invalid_tool_call" or "invalid_tool_call")) return null;
            var message = ScientificStateCompletionPolicy.Text(error, "message").Trim();
            if (message.Length == 0) message = ScientificStateCompletionPolicy.Text(root, "message").Trim();
            return message.Length == 0 ? null : (code, message);
        }
        catch (JsonException) { return null; }
    }
}
