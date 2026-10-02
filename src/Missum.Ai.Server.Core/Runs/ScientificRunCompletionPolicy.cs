using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Runs;

/// <summary>Only tool receipts can establish successful research deliverables.</summary>
internal static class ScientificRunCompletionPolicy
{
    internal const string VerifyTool = ClientToolNames.ResearchDeliverablesVerify;
    internal const string PublicationBegin = "<!-- MISSUM_PUBLICATION_BEGIN -->";
    internal const string PublicationEnd = "<!-- MISSUM_PUBLICATION_END -->";
    internal const string RecoveryMarker = "[MISSUM_SCIENCE_COMPLETION_RECOVERY]";
    private static readonly string[] RequiredSections = ["Kurzfassung", "Forschungsfrage", "Voraussetzungen", "Herleitungen", "Ergebnisse", "Diskussion", "Literatur"];
    private static readonly string[] PlotExtensions = [".png", ".jpg", ".jpeg"];

    internal static bool Applies(RunRequest request) => request.Mode is (RunMode.General or RunMode.Auto) && request.DeepResearch
        && request.ClientCapabilities is { } capabilities
        && (capabilities.Contains("research.deliverables", StringComparer.OrdinalIgnoreCase)
            || capabilities.Contains("research.sandbox", StringComparer.OrdinalIgnoreCase)
                && request.Messages.SelectMany(static message => message.Content).Any(static part =>
                part.Text?.Contains(RunProcessor.ScienceSessionContextStart, StringComparison.Ordinal) == true
                || part.Text?.Contains("CLAUDE SCIENCE – WISSENSCHAFTLICHE DARSTELLUNG", StringComparison.Ordinal) == true));

    internal static ScientificCompletionAssessment Assess(RunRequest request, IReadOnlyList<LmChatMessage> messages,
        IReadOnlyList<AgentToolSpec> tools)
    {
        if (!Applies(request)) return new(true, false, "", "", [], 0);
        var projectId = request.ResearchOptions?.ProjectId;
        if (string.IsNullOrWhiteSpace(projectId) && !string.IsNullOrWhiteSpace(request.SessionId))
            projectId = "research-" + request.SessionId.Replace("-", "", StringComparison.Ordinal);
        projectId ??= "";
        var calls = new Dictionary<string, (LmToolCall Call, long Epoch)>(StringComparer.Ordinal);
        long epoch = 0;
        string? publication = null;
        var successfulPython = false;
        var successfulExperimentRecordId = "";
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> successfulExperimentWindows = [];
        var latestPythonError = "";
        var lastMutation = "";
        JsonElement? verification = null;
        long verifiedEpoch = -1;
        var verificationFailure = "";
        foreach (var message in messages)
        {
            if (message.Role == "assistant" && message.Content is { } content
                && TryLatestPublication(content, out var next))
            {
                if (!string.Equals(publication, next, StringComparison.Ordinal)) { publication = next; epoch++; }
            }
            foreach (var call in message.ToolCalls ?? [])
            {
                if (IsMutation(call.Name) && (SameProject(call.Arguments, projectId) || call.Name == "document.create")) epoch++;
                if (call.Name == "research.code.write" && SameProject(call.Arguments, projectId))
                {
                    successfulPython = false;
                    successfulExperimentRecordId = "";
                    successfulExperimentWindows = [];
                    latestPythonError = "Der zuletzt geschriebene Forschungsdateistand wurde noch nicht erfolgreich mit Python ausgeführt. Führe research.code.execute aus und kontrolliere Exitcode und Abbildungen.";
                }
                calls[call.Id] = (call, epoch);
            }
            if (message.Role != "tool" || message.ToolCallId is null || !calls.TryGetValue(message.ToolCallId, out var pending)
                || !SameProject(pending.Call.Arguments, projectId)) continue;
            if (!TryReadReceipt(message.Content, out var receipt, out var successful, out var failure)) continue;
            if (IsMutation(pending.Call.Name))
                lastMutation = pending.Call.Name + ":" + Hash(pending.Call.Arguments.GetRawText()) + ":" + successful + ":" + failure;
            if (pending.Call.Name is "research.code.execute" or "research.code.test" or "research.code.benchmark"
                && Path.GetFileName(Text(pending.Call.Arguments, "executable").Replace('\\', '/')).StartsWith("python", StringComparison.OrdinalIgnoreCase))
            {
                // A saved script or an assistant's claim is not a process result.
                var executable = Text(pending.Call.Arguments, "executable").Replace('\\', '/');
                var experimentId = Text(pending.Call.Arguments, "experimentId");
                var executed = Path.GetFileName(executable).StartsWith("python", StringComparison.OrdinalIgnoreCase)
                    && experimentId.Length > 0 && successful && ProcessSucceeded(receipt);
                successfulPython = executed;
                successfulExperimentRecordId = executed ? "experiment-" + Hash(projectId + "\nexperiment\n" + experimentId)[..24] : "";
                successfulExperimentWindows = executed ? ProcessWindows(receipt) : [];
                latestPythonError = executed ? "" : failure.Length > 0 ? failure : "Die letzte Python-Ausführung ist nicht erfolgreich belegt.";
            }
            if (pending.Call.Name != VerifyTool) continue;
            verification = receipt;
            verifiedEpoch = pending.Epoch;
            verificationFailure = successful ? "" : failure.Length > 0 ? failure : "Die Prüfung der Forschungsartefakte ist noch nicht erfolgreich.";
        }

        var missing = new List<string>();
        if (projectId.Length == 0) missing.Add("Die eindeutige Forschungsprojekt-ID fehlt.");
        if (!ValidPublication(publication)) missing.Add("Ein vollständiges fachliches Publikationsmanuskript mit Titel, Kurzfassung, Forschungsfrage, Voraussetzungen, Herleitungen, Ergebnissen, Diskussion/Grenzen und Literatur fehlt. Markiere die vollständige aktuelle Fassung mit MISSUM_PUBLICATION_BEGIN/END.");
        if (!successfulPython) missing.Add(latestPythonError.Length > 0 ? latestPythonError
            : "Das fachliche Python-Skript wurde noch nicht erfolgreich ausgeführt. research.code.write allein genügt nicht; führe research.code.execute aus und kontrolliere Exitcode und Abbildungen.");
        var verifierAvailable = tools.Any(static tool => tool.Name == VerifyTool);
        if (!verifierAvailable) missing.Add("Das Werkzeug research.deliverables.verify ist im verbundenen Client noch nicht verfügbar. Der Lauf bleibt offen; die PDF- und Simulationsprüfung darf nicht durch eine Behauptung ersetzt werden.");
        var currentVerification = verification is { } verified && verifiedEpoch == epoch;
        var ready = currentVerification && verificationFailure.Length == 0 && Ready(verification!.Value, projectId, successfulExperimentRecordId,
            successfulExperimentWindows);
        if (!ready && currentVerification)
        {
            missing.Add(verificationFailure.Length > 0 ? verificationFailure : VerificationDiagnosis(verification!.Value));
        }
        else if (!ready) missing.Add("Die aktuelle Publikation und Simulation wurden nach dem letzten Manuskript- oder Dateistand noch nicht erfolgreich geprüft.");
        // A failed current check must return to the model for an actual repair.
        // Automatically repeat verification only after manuscript/code progress.
        var needsVerification = verifierAvailable && projectId.Length > 0 && ValidPublication(publication) && successfulPython
            && !currentVerification;
        var verificationSignature = verification is { } value ? VerificationSignature(value) : "unverified";
        var fingerprint = Hash((publication ?? "") + "\n" + lastMutation + "\n" + successfulExperimentRecordId + "\n" + verificationSignature);
        var repeats = RecoveryAttempts(messages, fingerprint);
        return new(missing.Count == 0 && ready, needsVerification, projectId, fingerprint, missing, repeats);
    }

    internal static void UpsertRecoveryPrompt(List<LmChatMessage> messages, ScientificCompletionAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(messages);
        // Only our own top-level runtime messages are replaceable. Tool results,
        // quoted examples and scientific manuscript text remain authoritative.
        messages.RemoveAll(static message => RecoveryContent(message) is not null);
        messages.Add(new("system", RepairPrompt(assessment)));
    }

    private static int RecoveryAttempts(IReadOnlyList<LmChatMessage> messages, string fingerprint)
    {
        var attempts = 0;
        foreach (var message in messages)
        {
            var content = RecoveryContent(message);
            var prefix = RecoveryMarker + "\n" + fingerprint + "\n";
            if (content?.StartsWith(prefix, StringComparison.Ordinal) != true) continue;
            var remainder = content[prefix.Length..];
            var lineEnd = remainder.IndexOf('\n');
            var countLine = lineEnd < 0 ? remainder : remainder[..lineEnd];
            if (countLine.StartsWith("Attempts: ", StringComparison.Ordinal)
                && int.TryParse(countLine[10..], NumberStyles.None, CultureInfo.InvariantCulture, out var persisted) && persisted > 0)
                attempts = Math.Max(attempts, persisted);
            else if (attempts < int.MaxValue) attempts++; // Read-only migration of older append-per-attempt checkpoints.
        }
        return attempts;
    }

    private static string? RecoveryContent(LmChatMessage message)
    {
        if (message.Role is not ("system" or "user") || message.Content is not { } content) return null;
        content = content.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart();
        const string nativePrefix = "Missum-Laufanweisung:\n";
        if (message.Role == "user" && content.StartsWith(nativePrefix, StringComparison.Ordinal)) content = content[nativePrefix.Length..];
        return content.StartsWith(RecoveryMarker + "\n", StringComparison.Ordinal) ? content : null;
    }

    internal static TimeSpan RetryDelay(ScientificCompletionAssessment assessment) => assessment.RepeatedAttempts switch
    {
        < 1 => TimeSpan.Zero,
        1 => TimeSpan.FromSeconds(10),
        2 => TimeSpan.FromSeconds(20),
        _ => TimeSpan.FromSeconds(30),
    };

    internal static string RepairPrompt(ScientificCompletionAssessment assessment)
    {
        var alternative = (assessment.RepeatedAttempts % 3) switch
        {
            0 => "Bearbeite jetzt zuerst den konkret fehlenden Arbeitsschritt. Prüfe das echte Werkzeugergebnis und korrigiere Pfad, Argumente oder Code anhand der Diagnose.",
            1 => "Wechsle zu einem kleineren reproduzierbaren Referenzfall oder einer unterstützten alternativen Methode. Erhalte die wissenschaftlichen Voraussetzungen; erfinde keine Ergebnisse und ersetze keine fachliche Simulation durch eine Quellenstatistik.",
            _ => "Wiederhole keinen unveränderten erfolglosen Aufruf. Untersuche Abhängigkeiten und gespeicherte Ausgaben, korrigiere den konkreten Fehler und wähle gegebenenfalls eine andere belastbare Quelle oder einen nachvollziehbar begrenzten Rechenweg.",
        };
        var attempts = assessment.RepeatedAttempts < int.MaxValue ? assessment.RepeatedAttempts + 1 : int.MaxValue;
        return RecoveryMarker + "\n" + assessment.Fingerprint + "\nAttempts: " + attempts.ToString(CultureInfo.InvariantCulture) + "\n"
            + "Der Forschungsauftrag ist noch nicht erfolgreich abgeschlossen. Der bisherige Arbeitsstand bleibt erhalten.\n"
            + string.Join("\n", assessment.Missing.Select(static problem => "- " + problem)) + "\n"
            + alternative + "\nNutze ausschließlich die angebotenen strukturierten Werkzeuge. Wenn die Schritte erfolgreich sind, liefere die vollständige aktualisierte fachliche Publikation; Betriebsdiagnosen bleiben außerhalb des Publikationsblocks. "
            + "Führe anschließend research.deliverables.verify für das aktuelle projectId aus. Eine echte offene wissenschaftliche Frage darf ehrlich als offen dokumentiert werden; fehlende Ausführung, fehlende Abbildungen oder fehlendes PDF sind kein erfolgreicher Abschluss. "
            + "Die gespeicherte Arbeit wird bis zum tatsächlichen Abschluss oder manuellen Stop fortgesetzt.";
    }

    private static bool IsMutation(string tool) => tool is "research.code.write" or "research.code.execute" or "research.code.test" or "research.code.benchmark" or "document.create";
    private static bool SameProject(JsonElement arguments, string projectId) => Text(arguments, "projectId") == projectId;

    private static bool ValidPublication(string? body)
    {
        if (body is null || body.Length < 200) return false;
        var lines = OutsideCodeFences(body).ToArray();
        var title = lines.FirstOrDefault(static line => line.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Contains("MISSUM_", StringComparison.OrdinalIgnoreCase)
            || title.StartsWith("Publikation", StringComparison.OrdinalIgnoreCase)) return false;
        return RequiredSections.All(section =>
        {
            var heading = Array.FindIndex(lines, line => line.StartsWith("## ", StringComparison.Ordinal)
                && (line[3..].Trim().Equals(section, StringComparison.OrdinalIgnoreCase)
                    || line[3..].Trim().StartsWith(section + " ", StringComparison.OrdinalIgnoreCase)));
            return heading >= 0 && string.Join(" ", lines.Skip(heading + 1)
                .TakeWhile(static line => !line.StartsWith("## ", StringComparison.Ordinal)))
                .Trim().Length >= 10;
        });
    }

    private static bool TryLatestPublication(string content, out string publication)
    {
        publication = "";
        var found = false;
        StringBuilder? current = null;
        char fence = '\0';
        var fenceLength = 0;
        foreach (var line in UnwrapAssistantMessage(content).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.Trim();
            if (fence == '\0' && trimmed == PublicationBegin)
            {
                found = true;
                publication = ""; // An unfinished newest manuscript cannot borrow an older complete one.
                current = new StringBuilder();
                continue;
            }
            if (fence == '\0' && trimmed == PublicationEnd && current is not null)
            {
                publication = current.ToString().Trim();
                current = null;
                continue;
            }
            UpdateFence(trimmed, ref fence, ref fenceLength);
            current?.AppendLine(line);
        }
        return found;
    }

    private static string UnwrapAssistantMessage(string content)
    {
        var candidate = content.Trim();
        if (candidate.StartsWith("```", StringComparison.Ordinal))
        {
            var newline = candidate.IndexOf('\n');
            var end = candidate.LastIndexOf("```", StringComparison.Ordinal);
            if (newline >= 0 && end > newline) candidate = candidate[(newline + 1)..end].Trim();
        }
        if (!candidate.StartsWith('{')) return content;
        try
        {
            using var document = JsonDocument.Parse(candidate);
            var root = document.RootElement;
            if (Text(root, "schema") is "assistant.agent.response.v1" or "go.ai.agent.response.v1"
                && Text(root, "type") == "message" && root.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String) return message.GetString() ?? "";
        }
        catch (JsonException) { }
        return content;
    }

    private static IEnumerable<string> OutsideCodeFences(string content)
    {
        char fence = '\0';
        var fenceLength = 0;
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            var wasFenced = fence != '\0';
            var delimiter = UpdateFence(trimmed, ref fence, ref fenceLength);
            if (!wasFenced && fence == '\0' && !delimiter) yield return trimmed;
        }
    }

    private static bool UpdateFence(string line, ref char fence, ref int fenceLength)
    {
        if (!line.StartsWith("```", StringComparison.Ordinal) && !line.StartsWith("~~~", StringComparison.Ordinal)) return false;
        if (fence == '\0')
        {
            fence = line[0];
            fenceLength = line.TakeWhile(character => character == line[0]).Count();
        }
        else if (line[0] == fence && line.Length >= fenceLength && line.All(character => character == line[0])) fence = '\0';
        return true;
    }

    private static bool TryReadReceipt(string? content, out JsonElement result, out bool succeeded, out string failure)
    {
        result = default;
        succeeded = false;
        failure = "";
        if (string.IsNullOrWhiteSpace(content)) return false;
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var wrapped = root.TryGetProperty("result", out var inner) && inner.ValueKind == JsonValueKind.Object;
            result = (wrapped ? inner : root).Clone();
            succeeded = (!root.TryGetProperty("status", out var status) || status.ValueKind == JsonValueKind.String && status.GetString() == "completed")
                && Boolean(result, "success");
            failure = Text(root, "message");
            if (failure.Length == 0) failure = Text(result, "error");
            if (failure.Length == 0) failure = Text(result, "message");
            if (failure.Length > 1200) failure = failure[..1200];
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool ProcessSucceeded(JsonElement result)
    {
        if (result.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
            return runs.GetArrayLength() > 0 && runs.EnumerateArray().All(static run =>
                run.ValueKind == JsonValueKind.Object && run.TryGetProperty("exitCode", out var code) && code.TryGetInt32(out var exit)
                && exit == 0 && !Boolean(run, "timedOut"));
        return result.TryGetProperty("exitCode", out var value) && value.TryGetInt32(out var resultExit)
            && resultExit == 0 && !Boolean(result, "timedOut");
    }

    private static bool Ready(JsonElement result, string projectId, string experimentRecordId,
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> experimentWindows) => Boolean(result, "success") && Text(result, "projectId") == projectId
        && result.TryGetProperty("publication", out var publication) && Boolean(publication, "ready")
        && Text(publication, "pdfPath").EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && ValidHash(Text(publication, "sourceSha256"))
        && result.TryGetProperty("simulation", out var simulation) && Boolean(simulation, "ready") && Boolean(simulation, "executed")
        && simulation.TryGetProperty("artifacts", out var artifacts) && artifacts.ValueKind == JsonValueKind.Array
        && experimentRecordId.Length > 0 && artifacts.EnumerateArray().Any(artifact => ValidHash(Text(artifact, "sha256"))
            && Text(artifact, "experimentRecordId") == experimentRecordId
            && Text(artifact, "scriptPath").Length > 0 && ValidHash(Text(artifact, "scriptSha256"))
            && (experimentWindows.Count == 0 || Date(artifact, "lastModifiedAt") is { } modified
                && experimentWindows.Any(window => modified >= window.Start.AddSeconds(-2) && modified <= window.End.AddSeconds(2)))
            && PlotExtensions.Any(extension => Text(artifact, "path").EndsWith(extension, StringComparison.OrdinalIgnoreCase)));

    private static List<(DateTimeOffset Start, DateTimeOffset End)> ProcessWindows(JsonElement result)
    {
        var windows = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        if (Date(result, "startedAt") is { } start && Date(result, "completedAt") is { } end && end >= start)
            windows.Add((start, end));
        if (result.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
            foreach (var run in runs.EnumerateArray())
                if (Date(run, "startedAt") is { } runStart && Date(run, "completedAt") is { } runEnd && runEnd >= runStart)
                    windows.Add((runStart, runEnd));
        return windows;
    }

    private static string VerificationDiagnosis(JsonElement result)
    {
        var problems = new List<string>();
        if (!result.TryGetProperty("publication", out var publication) || !Boolean(publication, "ready"))
            problems.Add("Publikation/PDF nicht bereit: " + Text(publication, "error"));
        if (!result.TryGetProperty("simulation", out var simulation) || !Boolean(simulation, "ready") || !Boolean(simulation, "executed"))
            problems.Add("Fachliche Simulation/Abbildungen nicht erfolgreich bereit: " + Text(simulation, "error"));
        return problems.Count > 0 ? string.Join(" ", problems) : "Die Artefaktprüfung enthält keine gültigen aktuellen PDF-/Abbildungsbelege mit SHA-256. Prüfe Projektzuordnung und tatsächliche Dateien.";
    }

    private static string VerificationSignature(JsonElement result)
    {
        var publication = result.TryGetProperty("publication", out var pub) ? pub : default;
        var simulation = result.TryGetProperty("simulation", out var sim) ? sim : default;
        var artifacts = simulation.ValueKind == JsonValueKind.Object && simulation.TryGetProperty("artifacts", out var files)
            && files.ValueKind == JsonValueKind.Array
            ? string.Join("|", files.EnumerateArray().Select(static file => Text(file, "path") + ":" + Text(file, "sha256")
                + ":" + Text(file, "experimentRecordId") + ":" + Text(file, "scriptSha256")).Order(StringComparer.Ordinal)) : "";
        return Boolean(result, "success") + ":" + Text(result, "projectId") + ":" + Boolean(publication, "ready") + ":"
            + Text(publication, "sourceSha256") + ":" + Text(publication, "error") + ":" + Boolean(simulation, "ready") + ":"
            + Boolean(simulation, "executed") + ":" + Text(simulation, "error") + ":" + artifacts;
    }

    private static bool Boolean(JsonElement owner, string name) => owner.ValueKind == JsonValueKind.Object
        && owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static string Text(JsonElement owner, string name) => owner.ValueKind == JsonValueKind.Object
        && owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static DateTimeOffset? Date(JsonElement owner, string name) => owner.ValueKind == JsonValueKind.Object
        && owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && value.TryGetDateTimeOffset(out var timestamp) ? timestamp : null;
    private static bool ValidHash(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
    private static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}

internal sealed record ScientificCompletionAssessment(bool Complete, bool NeedsVerification, string ProjectId,
    string Fingerprint, IReadOnlyList<string> Missing, int RepeatedAttempts);
