using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Runs;

/// <summary>
/// Rejects repeated non-executable actions, not productive research. Its small
/// ledger is checkpointed independently of the model's compacted conversation.
/// </summary>
internal static class ScientificResearchProgressGuard
{
    internal const string RejectionCode = "science.repeated_unproductive_action";
    internal const string BlockedCode = "science.prerequisite_blocked";
    internal const int MaximumBlockedAttempts = 8;
    internal const int MaximumUnchangedUpdates = 6;
    private const string UnchangedUpdateCause = "research.update.no_change";
    private const string SnapshotQuotaCause = "research.execution.snapshot_quota";
    private static readonly string[] VerificationSections = ["scientificReview", "research", "publication", "simulation"];

    internal static bool TryBlock(RunRequest request, LmToolCall call, ScientificStateRunProgress progress,
        out ScientificResearchToolRejection? rejection)
    {
        rejection = null;
        if (!ScientificStateCompletionPolicy.Enabled(request)) return false;
        var arguments = ArgumentHash(call);
        var failed = progress.Failures?.LastOrDefault(failure => failure.Count >= 2
            && (failure.PrerequisiteBlocked && ExecutionTool(call.Name)
                || !failure.PrerequisiteBlocked && failure.Tool == call.Name && failure.ArgumentHash == arguments
                    && failure.EnvironmentRevision == progress.ExecutionEnvironmentRevision
                    // Reads remain repeatable; verification requires a fresh
                    // working-set change or a real new execution/visual receipt.
                    && call.Name != ClientToolNames.ResearchRead));
        if (failed is null && call.Name == ClientToolNames.ResearchDeliverablesVerify)
        {
            // The reducer keeps original arguments as evidence. Semantically
            // identical JSON with different spacing must not split a verify loop
            // into several one-attempt ledger entries and bypass the guard.
            var matching = progress.Failures?.Where(failure => !failure.PrerequisiteBlocked
                && failure.Tool == call.Name && failure.ArgumentHash == arguments
                && failure.EnvironmentRevision == progress.ExecutionEnvironmentRevision).ToArray() ?? [];
            if (matching.Sum(static failure => Math.Min(failure.Count, 2)) >= 2) failed = matching.LastOrDefault();
        }
        if (failed is null) return false;
        var prerequisite = failed.PrerequisiteBlocked;
        rejection = new(failed.Fingerprint, prerequisite,
            prerequisite
                ? "Dieser Rechenaufruf wurde nicht gestartet: Die eingefrorenen Eingaben konnten bereits zweimal "
                    + "wegen derselben Sandboxvoraussetzung nicht angelegt werden. Die Voraussetzung ist weiterhin "
                    + "offen; andere Skriptnamen, Parameter oder Teilchenzahlen lösen sie nicht. Vorhandener Fehler: "
                    + failed.LastError + " Repariere die Arbeitsumgebung oder arbeite formal mit Lese-, Schreib- und "
                    + "Mathematikwerkzeugen weiter. Für diesen verweigerten Rechenaufruf keine neue Ausführung, "
                    + "Messung oder numerische Verifikation behaupten."
                : failed.Cause == UnchangedUpdateCause
                ? "Dieser Aufruf hat den gespeicherten Forschungsstand bereits zweimal nicht geändert und wurde nicht erneut ausgeführt. "
                    + failed.LastError
                : "Dieser unveränderte Aufruf wurde bereits zweimal abgelehnt und wurde nicht erneut ausgeführt. "
                    + "Korrigiere die tatsächlichen Argumente, die betroffene Datei oder die Arbeitsumgebung anhand "
                    + "des vorhandenen Fehlerbelegs; ein neuer Titel, eine Ankündigung oder ein weiteres identisches "
                    + "Update ist kein Arbeitsschritt. Vorhandener Fehler: " + failed.LastError);
        return true;
    }

    internal static void ThrowIfStalled(ScientificStateRunProgress progress)
    {
        if (progress.NoChangeUpdateAttempts >= MaximumUnchangedUpdates)
            throw new ScientificResearchStalledException(progress.LastNoChangeUpdateFingerprint ?? "", false,
                "Die Forschung ist offen geblieben: Wiederholte research.update-Aufrufe haben trotz Diagnose keine "
                + "Änderung gespeichert. Eine erfolgreiche Werkzeugantwort ist kein fachlicher Fortschritt. "
                + "Der aktuelle Forschungsstand und alle Ergebnisse bleiben für eine korrigierte Fortsetzung erhalten. "
                + "Fehlende review-/quantitativeValidation-Objekte mit tatsächlichen Prüfungen ausfüllen; null entfernt nur Felder.");
        var blocked = progress.Failures?.FirstOrDefault(failure => failure.BlockedAttempts >= MaximumBlockedAttempts);
        if (blocked is not null)
            throw new ScientificResearchStalledException(blocked.Fingerprint, blocked.PrerequisiteBlocked,
                "Die Forschung ist offen geblieben: Das Modell hat trotz konkreter Diagnose wiederholt eine "
                + "unverändert blockierte Aktion angefordert. Weitere identische Aufrufe wurden nicht ausgeführt. "
                + "Der Forschungsstand, die Kriterien und das Laufjournal bleiben für die Fortsetzung erhalten. "
                + "Vor dem nächsten Rechenversuch die tatsächliche Ursache beheben oder eine fachlich begründete "
                + "andere Methode wählen. Letzter Fehler: " + blocked.LastError);
    }

    internal static ScientificStateRunProgress ObserveSuccess(LmToolCall call, JsonElement result,
        ScientificStateRunProgress progress)
    {
        // A successful idempotent update is not a repair of a rejected mutation.
        // This also covers replayed receipts, which must never erase the ledger.
        if (call.Name == ClientToolNames.ResearchUpdate && !AcceptedCanonicalUpdate(call, result)) return progress;
        var failures = progress.Failures?.ToList() ?? [];
        var argumentHash = ArgumentHash(call);
        failures.RemoveAll(failure => failure.PrerequisiteBlocked
            ? ExecutionTool(call.Name) && HasExecutedProcess(result)
            : failure.Tool == call.Name && (call.Name == ClientToolNames.ResearchUpdate || failure.ArgumentHash == argumentHash));
        if (AcceptedCanonicalUpdate(call, result)
            || ExecutionTool(call.Name) && HasExecutedProcess(result)
            || ActualVisualReview(call, result))
            failures.RemoveAll(static failure => !failure.PrerequisiteBlocked
                && failure.Tool == ClientToolNames.ResearchDeliverablesVerify);
        var environment = progress.ExecutionEnvironmentRevision;
        if (call.Name is ClientToolNames.ResearchCodeWrite or ClientToolNames.ResearchCodeRestore
            && result.ValueKind == JsonValueKind.Object
            && Text(result, "afterSha256") is { } after && ValidHash(after)
            && !string.Equals(after, Text(result, "beforeSha256"), StringComparison.OrdinalIgnoreCase)
            && !ScientificStateCompletionPolicy.Boolean(result, "replayed"))
        {
            // A measured source mutation permits a new ordinary execution attempt.
            // It cannot discharge a quota failure before execution has succeeded.
            environment = environment < long.MaxValue ? environment + 1 : long.MaxValue;
            failures.RemoveAll(static failure => !failure.PrerequisiteBlocked);
        }
        var newWork = AcceptedCanonicalUpdate(call, result) || environment != progress.ExecutionEnvironmentRevision
            || ExecutionTool(call.Name) && HasExecutedProcess(result) || ActualVisualReview(call, result);
        return progress with { Failures = failures, ExecutionEnvironmentRevision = environment,
            NoChangeUpdateAttempts = newWork ? 0 : progress.NoChangeUpdateAttempts,
            LastNoChangeUpdateFingerprint = newWork ? null : progress.LastNoChangeUpdateFingerprint };
    }

    internal static bool IsUnchangedUpdate(LmToolCall call, JsonElement result) =>
        call.Name == ClientToolNames.ResearchUpdate && result.ValueKind == JsonValueKind.Object
        && Text(result, "protocol") == ScientificStateCompletionPolicy.Protocol
        && Text(result, "projectId") is { Length: > 0 } project && project == Text(call.Arguments, "projectId")
        && ScientificStateCompletionPolicy.Boolean(result, "success")
        && !ScientificStateCompletionPolicy.Boolean(result, "replayed")
        && result.TryGetProperty("publicationChanged", out var publication) && publication.ValueKind == JsonValueKind.False
        && result.TryGetProperty("changedIds", out var changed) && changed.ValueKind == JsonValueKind.Array && changed.GetArrayLength() == 0;

    internal static ScientificStateRunProgress ObserveUnchangedUpdate(LmToolCall call, JsonElement result,
        ScientificStateRunProgress progress, List<LmChatMessage> messages)
    {
        var targets = call.Arguments.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array
            ? string.Join(", ", changes.EnumerateArray().Select(item => Text(item, "id")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            : "Forschungsstand";
        var identity = UnchangedUpdateCause + "\n" + Text(result, "projectId") + "\n" + progress.Revision + "\n" + targets;
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var failures = progress.Failures?.ToList() ?? [];
        var index = failures.FindIndex(item => item.Fingerprint == fingerprint);
        var failure = index >= 0 ? failures[index] : new ScientificStateToolFailure(fingerprint, 0);
        var error = "Betroffene Objekte: " + targets[..Math.Min(targets.Length, 500)]
            + ". changedIds ist leer, die Revision unverändert. null löscht optionale Felder; "
            + "ein bereits fehlendes Feld wird dadurch weder geprüft noch angelegt. "
            + "Lies bei Unsicherheit gezielt diese Objekte mit research.read. Für eine fachliche Prüfung "
            + "sende patch.status und ein vollständiges patch.review mit itemRevision=expectedRevision+1; "
            + "quantitativeValidation benötigt echte Messgröße, Referenz, Ergebnisdatei und Kriterien. "
            + "Keine Prüfung erfinden und keine offenen Ziele löschen. Wenn keine Änderung nötig ist, mit der eigentlichen Forschung fortfahren.";
        failure = failure with { Count = Math.Min(2, failure.Count + 1), Tool = call.Name,
            ArgumentHash = ArgumentHash(call), Cause = UnchangedUpdateCause, LastError = error,
            EnvironmentRevision = progress.ExecutionEnvironmentRevision };
        if (failure.Count >= 2 && failure.FeedbackCount == 0)
        {
            messages.Add(new("system", "[MISSUM_SCIENCE_TOOL_DIAGNOSIS:" + fingerprint + "]\n"
                + "Zwei bestätigte research.update-Aufrufe haben nichts geändert. " + error));
            failure = failure with { FeedbackCount = 1 };
        }
        if (index >= 0) failures[index] = failure;
        else failures.Add(failure);
        return progress with { Failures = failures.OrderBy(static item => item.PrerequisiteBlocked ? 1 : 0).TakeLast(32).ToArray(),
            NoChangeUpdateAttempts = Math.Min(MaximumUnchangedUpdates, progress.NoChangeUpdateAttempts + 1),
            LastNoChangeUpdateFingerprint = fingerprint };
    }

    private static bool AcceptedCanonicalUpdate(LmToolCall call, JsonElement result) =>
        call.Name == ClientToolNames.ResearchUpdate && result.ValueKind == JsonValueKind.Object
        && Text(result, "protocol") == ScientificStateCompletionPolicy.Protocol
        && Text(result, "projectId") is { Length: > 0 } project && project == Text(call.Arguments, "projectId")
        && !ScientificStateCompletionPolicy.Boolean(result, "replayed")
        && (ScientificStateCompletionPolicy.Boolean(result, "publicationChanged")
            || result.TryGetProperty("changedIds", out var changed) && changed.ValueKind == JsonValueKind.Array
                && changed.GetArrayLength() > 0);

    private static bool ActualVisualReview(LmToolCall call, JsonElement result)
    {
        if (call.Name != "media.analyze" || result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("visualReview", out var review) || review.ValueKind != JsonValueKind.Object
            || Text(review, "protocol") != AgentToolExecutor.VisualReviewProtocol || Text(review, "kind") != "image"
            || Text(review, "status") != "analyzed" || !ScientificStateCompletionPolicy.Boolean(review, "exactIdentity")
            || Text(review, "sourceUploadId") is not { Length: > 0 } upload || upload != Text(call.Arguments, "uploadId")
            || !ValidHash(Text(review, "sourceSha256")) || !ValidHash(Text(review, "analysisSha256"))
            || Text(review, "modelId").Length == 0 || Text(review, "analysis") is not { Length: > 0 } analysis) return false;
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(analysis)));
        return string.Equals(actualHash, Text(review, "analysisSha256"), StringComparison.OrdinalIgnoreCase);
    }

    private static bool ValidHash(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);

    private static bool HasExecutedProcess(JsonElement result) => result.ValueKind == JsonValueKind.Object
        && result.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array && runs.GetArrayLength() > 0
        && runs.EnumerateArray().All(static run => run.ValueKind == JsonValueKind.Object
            && Text(run, "runId").Length > 0 && run.TryGetProperty("exitCode", out var code)
            && code.TryGetInt32(out var exit) && exit == 0 && !ScientificStateCompletionPolicy.Boolean(run, "timedOut"));

    internal static string? PrerequisiteCause(LmToolCall call, string content)
    {
        if (!ExecutionTool(call.Name)) return null;
        var error = ErrorMessage(content);
        // Match the actual host prerequisite, not arbitrary physical values,
        // process stderr or a generic HTTP failure. No inputs/process exist yet.
        if (error.Contains("eingefrorene", StringComparison.OrdinalIgnoreCase)
            && error.Contains("Sandboxkontingent", StringComparison.OrdinalIgnoreCase)
            && error.Contains("keine Eingaben kopiert", StringComparison.OrdinalIgnoreCase)) return SnapshotQuotaCause;
        // The content-addressed archive reports its independent physical budgets
        // explicitly. These are still prerequisites before any Python execution.
        if (error.StartsWith("Das 100-GiB-Kontingent der aktiven Sandboxdateien ist ausgeschöpft.", StringComparison.Ordinal))
            return "research.execution.active_project_quota";
        if (error.StartsWith("Das 100-GiB-Kontingent für neue reproduzierbare Ausführungsstände ist ausgeschöpft.", StringComparison.Ordinal)
            || error.StartsWith("Das Kontingent für neue reproduzierbare Ausführungsstände ist ausgeschöpft.", StringComparison.Ordinal))
            return "research.execution.archive_quota";
        if (error.StartsWith("Auf dem Projektlaufwerk reicht der freie Speicher für den unabhängigen Ausführungsstand", StringComparison.Ordinal)
            && error.Contains("keine Eingaben kopiert", StringComparison.OrdinalIgnoreCase))
            return "research.execution.insufficient_free_space";
        if (error.StartsWith("Der vollständige Ausführungsstand besitzt mehr als 4096 Eingabedateien;", StringComparison.Ordinal)
            && error.Contains("keine Eingaben kopiert", StringComparison.OrdinalIgnoreCase))
            return "research.execution.input_file_quota";
        return null;
    }

    internal static string? RejectionFingerprint(string content)
    {
        if (!TryRoot(content, out var root)
            || Text(root, "errorCode") != RejectionCode) return null;
        var value = Text(root, "scienceFailureFingerprint");
        return value.Length == 64 && value.All(char.IsAsciiHexDigit) ? value : null;
    }

    internal static string ErrorMessage(string content)
    {
        if (!TryRoot(content, out var root)) return "Werkzeugergebnis enthält keine lesbare Fehlerdiagnose.";
        var inner = root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object ? result : root;
        var error = Text(root, "message");
        if (error.Length == 0) error = Text(inner, "message");
        if (error.Length == 0) error = Text(inner, "error");
        if (error.Length == 0) error = Text(inner, "stderr");
        if (error.Length == 0)
        {
            var diagnoses = new List<string>();
            foreach (var section in VerificationSections)
            {
                if (!inner.TryGetProperty(section, out var details) || details.ValueKind != JsonValueKind.Object) continue;
                if (Text(details, "error") is { Length: > 0 } problem) diagnoses.Add(problem);
                if (details.TryGetProperty("missing", out var missing) && missing.ValueKind == JsonValueKind.Array)
                    diagnoses.AddRange(missing.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.String)
                        .Take(3).Select(static item => item.GetString()!).Where(static item => item.Length > 0));
            }
            error = string.Join(" ", diagnoses);
        }
        if (error.Length == 0) error = Text(root, "errorCode");
        return error[..Math.Min(error.Length, 1200)];
    }

    internal static string ArgumentHash(LmToolCall call) => CodingWorkingStateReducer.ArgumentHash(call.Arguments);

    private static bool ExecutionTool(string tool) => tool is ClientToolNames.ResearchCodeExecute
        or ClientToolNames.ResearchCodeTest or ClientToolNames.ResearchCodeBenchmark;

    private static string Text(JsonElement value, string field) => ScientificStateCompletionPolicy.Text(value, field);

    private static bool TryRoot(string content, out JsonElement root)
    {
        root = default;
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException) { return false; }
    }
}

internal sealed record ScientificResearchToolRejection(string Fingerprint, bool PrerequisiteBlocked, string Message);

internal sealed class ScientificResearchStalledException(string fingerprint, bool prerequisiteBlocked, string message)
    : Exception(message)
{
    internal string Fingerprint { get; } = fingerprint;
    internal bool PrerequisiteBlocked { get; } = prerequisiteBlocked;
}
