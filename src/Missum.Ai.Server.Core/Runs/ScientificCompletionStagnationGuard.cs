using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Runs;

/// <summary>Stop unsupported final-answer repetition without limiting genuine scientific work.</summary>
internal static class ScientificCompletionStagnationGuard
{
    internal const string Marker = "[MISSUM_SCIENCE_COMPLETION_STAGNATION]";
    internal const int MaximumUnsupportedAttempts = 6;
    private const int MaximumEvidenceFingerprints = 16;

    internal static ScientificStateRunProgress Observe(ScientificStateRunProgress progress,
        ScientificCompletionAssessment assessment, List<LmChatMessage> messages)
    {
        if (assessment.Complete) return Reset(progress);
        // Verification is an actual next operation; its own tool loop guard
        // handles repeated failed receipts. Do not count that handoff as a claim.
        if (assessment.NeedsVerification) return progress;
        var same = progress.PrematureCompletionFingerprint == assessment.Fingerprint;
        var attempts = Math.Min(MaximumUnsupportedAttempts, (same ? progress.PrematureCompletionAttempts : 0) + 1);
        var hinted = same && progress.CompletionMethodChangeHinted;
        if (attempts >= 2 && !hinted)
        {
            var prefix = Marker + "\n" + assessment.Fingerprint + "\n";
            if (!messages.Any(message => message.Role == "system" && message.Content?.StartsWith(prefix, StringComparison.Ordinal) == true
                || message.Role == "user" && message.Content?.StartsWith("Missum-Laufanweisung:\n" + prefix, StringComparison.Ordinal) == true))
                messages.Add(new("system", prefix
                    + "Zwei Abschlussversuche haben dieselben offenen fachlichen Ziele nicht erfüllt. Andere Abschlussformulierungen "
                    + "oder weitere Behauptungen sind keine neue Erkenntnis. Bearbeite jetzt eine konkrete offene Diagnose. "
                    + "Vergleiche eine analytische Herleitung, einen Grenzfall, eine begründete Abschätzung oder ein Gedankenexperiment "
                    + "mit einer tatsächlich anderen numerischen Methode. Speichere die überprüfbare Hypothese und nextCheck; "
                    + "erst danach den kleinen unterscheidenden Versuch durchführen. Keine Toleranzen lockern und keine "
                    + "abweichende Observable als dieselbe ausgeben. Offene Prüfdiagnosen (Daten): "
                    + JsonSerializer.Serialize(assessment.Missing.Take(3).Select(static item => item[..Math.Min(item.Length, 800)]))));
            hinted = true;
        }
        return progress with { PrematureCompletionFingerprint = assessment.Fingerprint,
            PrematureCompletionAttempts = attempts, CompletionMethodChangeHinted = hinted };
    }

    internal static void ThrowIfStalled(ScientificStateRunProgress progress)
    {
        if (progress.PrematureCompletionAttempts >= MaximumUnsupportedAttempts)
            throw new ScientificResearchStalledException("completion:" + progress.PrematureCompletionFingerprint, false,
                "Die Forschung bleibt offen: Das Modell hat sechsmal bei unveränderten offenen Prüfdiagnosen einen "
                + "unbelegten Abschluss versucht, ohne neue gespeicherte Erkenntnis oder ein neues gemessenes Ergebnis. "
                + "Der Arbeitsstand und das Laufjournal bleiben für eine gezielte Fortsetzung erhalten. Vor einem weiteren "
                + "Abschlussversuch eine begründete andere Methode und deren konkrete Prüfung bearbeiten; keine Ergebnisse "
                + "oder erfüllten Kriterien erfinden.");
    }

    internal static ScientificStateRunProgress ResetOnProductiveReceipt(ScientificStateRunProgress progress,
        LmToolCall call, JsonElement result, bool acceptedCanonicalUpdate)
    {
        if (acceptedCanonicalUpdate) return Reset(progress);
        if (call.Name is not (ClientToolNames.ResearchCodeExecute or ClientToolNames.ResearchCodeTest
            or ClientToolNames.ResearchCodeBenchmark or ClientToolNames.MathFormalProof)
            || call.Name == ClientToolNames.MathFormalProof && Text(result, "formalVerification") != "KernelAccepted"
            || !TryMeasuredEvidence(call, result, out var signature)) return progress;
        var seen = progress.CompletionEvidenceFingerprints ?? [];
        if (seen.Contains(signature, StringComparer.Ordinal)) return progress;
        return Reset(progress) with { CompletionEvidenceFingerprints = seen.Append(signature)
            .TakeLast(MaximumEvidenceFingerprints).ToArray() };
    }

    private static ScientificStateRunProgress Reset(ScientificStateRunProgress progress) => progress with
    { PrematureCompletionFingerprint = null, PrematureCompletionAttempts = 0, CompletionMethodChangeHinted = false };

    private static bool TryMeasuredEvidence(LmToolCall call, JsonElement result, out string signature)
    {
        signature = "";
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("runs", out var runs)
            || runs.ValueKind != JsonValueKind.Array || runs.GetArrayLength() == 0) return false;
        var measured = new List<object>();
        foreach (var run in runs.EnumerateArray())
        {
            var script = Text(run, "executedScriptPath");
            var scriptHash = Text(run, "scriptSha256");
            if (run.ValueKind != JsonValueKind.Object || Text(run, "runId").Length == 0
                || !run.TryGetProperty("exitCode", out var code) || !code.TryGetInt32(out var exit) || exit != 0
                || ScientificStateCompletionPolicy.Boolean(run, "timedOut") || script.Length == 0 || !ValidHash(scriptHash)
                || !run.TryGetProperty("inputHashes", out var inputs) || !ValidHashes(inputs)
                || !inputs.TryGetProperty(script, out var frozen) || frozen.ValueKind != JsonValueKind.String
                || !string.Equals(frozen.GetString(), scriptHash, StringComparison.OrdinalIgnoreCase)
                || !run.TryGetProperty("outputHashes", out var outputs) || !ValidHashes(outputs)) return false;
            measured.Add(new { script, scriptHash, inputs, outputs,
                // Stdout matters for symbolic/console-only calculations. Runtime
                // IDs/timestamps are deliberately excluded from scientific novelty.
                stdout = outputs.EnumerateObject().Any() ? null : Text(run, "standardOutput") });
        }
        var configuration = call.Arguments.ValueKind == JsonValueKind.Object
            ? call.Arguments.EnumerateObject().Where(static field => field.Name is not ("experimentId" or "timeoutSeconds"))
                .ToDictionary(static field => field.Name, static field => field.Value.Clone(), StringComparer.Ordinal)
            : null;
        signature = CodingWorkingStateReducer.ArgumentHash(JsonSerializer.SerializeToElement(new { call.Name, configuration, measured }));
        return true;
    }

    private static bool ValidHashes(JsonElement value) => value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Take(4097).Count() <= 4096
        && value.EnumerateObject().All(static item => item.Value.ValueKind == JsonValueKind.String && ValidHash(item.Value.GetString()!));
    private static bool ValidHash(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
    private static string Text(JsonElement value, string field) => ScientificStateCompletionPolicy.Text(value, field);
}
