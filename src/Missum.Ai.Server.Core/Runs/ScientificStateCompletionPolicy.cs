using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Runs;

/// <summary>Accepts the canonical working set only through actual current client verification receipts.</summary>
internal static class ScientificStateCompletionPolicy
{
    internal const string Protocol = "section-delta-v1";
    internal const string RecoveryMarker = "[MISSUM_SCIENCE_STATE_RECOVERY]";

    internal static bool Enabled(RunRequest request) => request.ResearchOptions?.ProtocolVersion >= 2
        && request.ConversationProfile != ConversationProfile.ContextPreparation
        && request.ClientCapabilities?.Contains("research.deliverables", StringComparer.OrdinalIgnoreCase) == true;

    internal static string ProjectId(RunRequest request) => request.ResearchOptions?.ProjectId
        ?? (request.SessionId is { Length: > 0 } session ? "research-" + session.Replace("-", "", StringComparison.Ordinal) : "");

    internal static ScientificCompletionAssessment Assess(RunRequest request, IReadOnlyList<LmChatMessage> messages,
        IReadOnlyList<AgentToolSpec> tools)
    {
        var projectId = ProjectId(request);
        var pending = new Dictionary<string, (LmToolCall Call, long Epoch)>(StringComparer.Ordinal);
        long epoch = 0, revision = -1, publicationRevision = -1, verifiedEpoch = -1;
        JsonElement? verification = null;
        var verificationFailed = false;
        foreach (var message in messages)
        {
            foreach (var call in message.ToolCalls ?? [])
            {
                if (Mutation(call.Name)) epoch++;
                pending[call.Id] = (call, epoch);
            }
            if (message.Role != "tool" || message.ToolCallId is not { } id || !pending.Remove(id, out var operation)
                || !TryReceipt(message.Content, out var receipt, out var completed)
                || Text(receipt, "projectId") != projectId) continue;
            if (operation.Call.Name is ClientToolNames.ResearchRead or ClientToolNames.ResearchUpdate)
            {
                if (Text(receipt, "protocol") != Protocol) continue;
                revision = Math.Max(revision, Number(receipt, "revision"));
                publicationRevision = Math.Max(publicationRevision, Number(receipt, "publicationRevision"));
                continue;
            }
            if (operation.Call.Name != ClientToolNames.ResearchDeliverablesVerify) continue;
            verification = receipt;
            verifiedEpoch = operation.Epoch;
            verificationFailed = !completed;
        }

        var missing = new List<string>();
        if (projectId.Length == 0) missing.Add("Die aktuelle Forschungsprojekt-ID fehlt.");
        var available = tools.Any(static tool => tool.Name == ClientToolNames.ResearchDeliverablesVerify);
        if (!available) missing.Add("Die kanonische Ergebnisprüfung ist im verbundenen Client nicht verfügbar.");
        var research = verification is { } result && result.TryGetProperty("research", out var state) ? state : default;
        var publication = verification is { } checkedResult && checkedResult.TryGetProperty("publication", out var pub) ? pub : default;
        var simulation = verification is { } checkedSimulation && checkedSimulation.TryGetProperty("simulation", out var sim) ? sim : default;
        var current = verification is not null && verifiedEpoch == epoch
            && Number(research, "revision") >= revision && Number(research, "publicationRevision") >= publicationRevision;
        var validStateReceipt = Text(research, "protocol") == Protocol && Number(research, "revision") >= 0
            && Number(research, "publicationRevision") >= 0;
        var pdfPath = Text(publication, "pdfPath");
        var hash = Text(publication, "sourceSha256");
        var validPublication = Boolean(publication, "ready") && pdfPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            && hash.Length == 64 && hash.All(Uri.IsHexDigit)
            && Number(publication, "revision") == Number(research, "publicationRevision")
            && Number(publication, "revision") >= 0;
        var declaredSimulation = simulation.ValueKind == JsonValueKind.Object
            && simulation.TryGetProperty("required", out var required)
            && required.ValueKind is JsonValueKind.True or JsonValueKind.False;
        var simulationReady = declaredSimulation && (!Boolean(simulation, "required")
            || Boolean(simulation, "ready") && ValidSimulationReceipt(simulation, projectId));
        var ready = current && !verificationFailed && verification is { } verified && Boolean(verified, "success")
            && Text(research, "protocol") == Protocol && Boolean(research, "ready")
            && validPublication && simulationReady;
        if (!ready)
        {
            if (!current && (verification is null || validStateReceipt)) missing.Add("Prüfe den aktuellen kanonischen Forschungsstand nach den letzten Objekt- oder Dateiänderungen mit research.deliverables.verify.");
            else
            {
                if (research.ValueKind == JsonValueKind.Object && research.TryGetProperty("missing", out var targets)
                    && targets.ValueKind == JsonValueKind.Array)
                    foreach (var target in targets.EnumerateArray().Take(16))
                        if (target.ValueKind == JsonValueKind.String && target.GetString() is { Length: > 0 } diagnosis)
                            missing.Add(diagnosis[..Math.Min(diagnosis.Length, 2000)]);
                if (Text(research, "protocol") != Protocol) missing.Add("Der Client hat keinen gültigen section-delta-v1-Prüfbeleg zurückgegeben.");
                if (!validPublication) missing.Add("Die aktuelle Publikationsrevision enthält noch keinen bestätigten gerenderten PDF-Beleg. Bearbeite nur die fehlenden kanonischen Abschnitte.");
                if (!simulationReady) missing.Add("Eine ausdrücklich erforderliche Auswertung oder interaktive Simulation besitzt noch keine gültigen aktuellen, zum jeweiligen Ergebnistyp passenden Belege.");
                if (missing.Count == 0) missing.Add("Bearbeite die offenen Anforderungen und Prüfdiagnosen im kanonischen Forschungszustand; ein Prozesslauf oder Modellstatus allein belegt keine wissenschaftliche Aussage.");
            }
        }
        var signature = epoch.ToString(CultureInfo.InvariantCulture) + ":" + revision.ToString(CultureInfo.InvariantCulture)
            + ":" + publicationRevision.ToString(CultureInfo.InvariantCulture) + ":" + (verification?.GetRawText() ?? "unverified");
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(signature)));
        return new(ready && missing.Count == 0, available && projectId.Length > 0 && !current && (verification is null || validStateReceipt),
            projectId, fingerprint, missing, 0);
    }

    internal static bool UpsertRecoveryPrompt(List<LmChatMessage> messages, ScientificCompletionAssessment assessment)
    {
        var prefix = RecoveryMarker + "\n" + assessment.Fingerprint + "\n";
        // Native templates keep chronological system guidance as a controlled user
        // turn. Recognize that exact form on recovery without rewriting the prefix.
        if (messages.Any(message => message.Role == "system" && message.Content?.StartsWith(prefix, StringComparison.Ordinal) == true
            || message.Role == "user" && message.Content?.StartsWith("Missum-Laufanweisung:\n" + prefix, StringComparison.Ordinal) == true)) return false;
        messages.Add(new("system", prefix
            + "Der kanonische Forschungsauftrag ist noch offen. Nutze research.read gezielt für die betroffenen IDs; "
            + "ändere nur fehlende oder fachlich neue Objekte mit research.update. Bewahre verworfene Hypothesen, Gründe und echte Belege. "
            + "Schreibe kein vollständiges Chatmanuskript erneut und wiederhole keine bereits abgeschlossene delegierte Arbeit.\n"
            + string.Join("\n", assessment.Missing.Take(8).Select(static item => "- " + item))));
        return true;
    }

    internal static bool Mutation(string name) => name is ClientToolNames.ResearchUpdate
        or ClientToolNames.ResearchCodeWrite or ClientToolNames.ResearchCodeRestore or ClientToolNames.ResearchCodeExecute
        or ClientToolNames.ResearchCodeTest or ClientToolNames.ResearchCodeBenchmark
        or ClientToolNames.MathSymbolic or ClientToolNames.MathNumeric or ClientToolNames.MathSmt or ClientToolNames.MathFormalProof;

    private static bool ValidSimulationReceipt(JsonElement simulation, string projectId)
    {
        // Older clients supply only the executed-figure receipt. The additive
        // source-artifact branch applies solely to explicit interactive delivery.
        if (!DeclaredBoolean(simulation, "executionRequired") || !DeclaredBoolean(simulation, "interactiveRequired"))
            return Boolean(simulation, "executed");
        var executionRequired = Boolean(simulation, "executionRequired");
        var interactiveRequired = Boolean(simulation, "interactiveRequired");
        if (!executionRequired && !interactiveRequired || executionRequired && !Boolean(simulation, "executed")) return false;
        if (!interactiveRequired) return true;
        if (!Boolean(simulation, "interactiveReady") || !simulation.TryGetProperty("interactiveArtifacts", out var artifacts)
            || artifacts.ValueKind != JsonValueKind.Array || artifacts.GetArrayLength() is < 1 or > 48) return false;
        return artifacts.EnumerateArray().Any(artifact => Text(artifact, "kind") == "interactive"
            && Text(artifact, "projectId") == projectId && Boolean(artifact, "sourceArtifact")
            && DeclaredBoolean(artifact, "executed") && !Boolean(artifact, "executed")
            && Text(artifact, "contentType") == "text/html" && ValidInteractivePath(Text(artifact, "artifactPath"))
            && Text(artifact, "sha256") is { Length: 64 } hash && hash.All(Uri.IsHexDigit));
    }

    private static bool DeclaredBoolean(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool ValidInteractivePath(string path)
    {
        if (path.Length is < 1 or > 1024 || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl)) return false;
        var parts = path.Split('/');
        return parts.Length > 1 && (parts[0] is "work" or "artifacts")
            && parts.All(part => part.Length > 0 && part is not ("." or ".."))
            && !parts.Any(part => part.Equals("publication", StringComparison.OrdinalIgnoreCase))
            && (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool TryReceipt(string? content, out JsonElement result, out bool completed)
    {
        result = default;
        completed = false;
        if (string.IsNullOrWhiteSpace(content)) return false;
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("result", out var payload)
                || payload.ValueKind != JsonValueKind.Object) return false;
            result = payload.Clone();
            completed = Text(root, "status") == "completed" && Boolean(payload, "success");
            return true;
        }
        catch (JsonException) { return false; }
    }

    internal static long Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt64(out var number) ? number : -1;
    internal static bool Boolean(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
    internal static string Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";
}
