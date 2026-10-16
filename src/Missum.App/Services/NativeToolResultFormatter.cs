using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Missum.App.Services;

/// <summary>One linear, lazy presentation of tool receipts for parent and child conversations.</summary>
internal static partial class NativeToolResultFormatter
{
    internal sealed record Block(string Title, string Text, string? Language = null, string? Path = null);

    internal static IEnumerable<Block> Format(JsonElement data, string path, bool hidePatch, string? sourceLanguage = null)
    {
        if (data.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            if (Scalar(data) is { Length: > 0 } text) yield return new("Ergebnis", text);
            yield break;
        }
        foreach (var block in Walk(data, "", path, hidePatch, sourceLanguage, 0)) yield return block;
    }

    private static IEnumerable<Block> Walk(JsonElement data, string context, string path, bool hidePatch, string? sourceLanguage, int depth)
    {
        if (depth > 16)
        {
            // Deep machine structures remain available as a single code block,
            // never as a recursively nested tree of native controls.
            yield return new(context, JsonSerializer.Serialize(data, PrettyJson), "json");
            yield break;
        }
        if (data.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in data.EnumerateArray())
            {
                var title = context + " · " + (++index).ToString(CultureInfo.CurrentCulture);
                if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    foreach (var block in Walk(item, title, path, hidePatch, sourceLanguage, depth + 1)) yield return block;
                else if (Scalar(item) is { Length: > 0 } scalar) yield return new(title, scalar);
            }
            yield break;
        }
        var facts = new List<string>();
        foreach (var property in data.EnumerateObject())
        {
            var key = property.Name;
            var value = property.Value;
            if (hidePatch && key is "diff" or "stagedDiff" or "content" or "oldText" or "newText" or "edits") continue;
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                || value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())) continue;
            var label = Label(key);
            var title = context.Length == 0 ? label : context + " · " + label;
            if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0) continue;
            var codeLanguage = CodeLanguage(key, value, path, sourceLanguage);
            if (codeLanguage is not null || value.ValueKind == JsonValueKind.String && (IsProse(key) || value.GetString()!.Contains('\n'))
                || value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                if (facts.Count > 0) { yield return new(context, string.Join("\n\n", facts)); facts.Clear(); }
                if (codeLanguage is not null)
                {
                    yield return new(title, value.GetString() ?? "", codeLanguage, key is "content" or "code" or "source" ? path : null);
                }
                else if (value.ValueKind == JsonValueKind.String)
                {
                    yield return new(title, value.GetString()!);
                }
                else if (value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(item => item.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)))
                {
                    yield return new(title, string.Join(", ", value.EnumerateArray().Select(Scalar)));
                }
                else foreach (var block in Walk(value, title, path, hidePatch, sourceLanguage, depth + 1)) yield return block;
            }
            else
            {
                facts.Add(label + ": " + Scalar(value));
                if (facts.Count >= 8) { yield return new(context, string.Join("\n\n", facts)); facts.Clear(); }
            }
        }
        if (facts.Count > 0) yield return new(context, string.Join("\n\n", facts));
    }

    private static string? CodeLanguage(string key, JsonElement value, string path, string? sourceLanguage)
    {
        if (value.ValueKind != JsonValueKind.String) return null;
        if (key is "diff" or "stagedDiff") return "diff";
        if (key is "stdout" or "stderr" or "diagnostics" or "command") return "text";
        if (key == "source" && sourceLanguage is null) return null;
        if (key is not ("code" or "source" or "content" or "oldText" or "newText")) return null;
        if (sourceLanguage is not null) return sourceLanguage;
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return extension.Length > 0 ? extension : "text";
    }

    private static bool IsProse(string key) => key is "text" or "snippet" or "message" or "error" or "summary"
        or "description" or "explanation" or "statement" or "contentMarkdown" or "result" or "nextCheck";
    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => "Ja", JsonValueKind.False => "Nein", JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.String => value.GetString() ?? "", _ => value.ToString(),
    };
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };
    private static string Label(string key) => key switch
    {
        "path" or "file" => "Datei", "content" or "text" or "contentMarkdown" => "Inhalt", "code" => "Code", "oldText" => "Vorher", "newText" => "Nachher",
        "stdout" => "Ausgabe", "stderr" => "Fehlerausgabe", "diagnostics" => "Prüfmeldungen", "diff" or "stagedDiff" => "Änderungen", "exitCode" => "Exitcode",
        "title" => "Titel", "query" => "Suchbegriff", "queries" => "Suchbegriffe", "url" => "Adresse", "source" => "Quelle", "snippet" => "Auszug",
        "description" => "Beschreibung", "explanation" => "Erläuterung", "statement" => "Aussage", "summary" => "Zusammenfassung", "message" => "Meldung", "error" => "Fehler",
        "success" => "Erfolgreich", "status" or "toolStatus" => "Status", "operation" => "Vorgang", "result" => "Ergebnis", "results" => "Ergebnisse", "entries" => "Einträge",
        "sources" or "citations" => "Quellen", "matches" => "Treffer", "arguments" => "Argumente", "executable" => "Programm", "workingDirectory" => "Arbeitsordner",
        "startLine" => "Ab Zeile", "totalLines" => "Zeilen", "maximumLines" => "Zeilenlimit", "nextLine" => "Nächste Zeile", "truncated" or "diffTruncated" => "Ausgabe gekürzt",
        "timedOut" => "Zeitlimit überschritten", "elapsedMilliseconds" => "Laufzeit in Millisekunden", "elapsedSeconds" => "Laufzeit in Sekunden",
        "proofs" => "Beweise", "theorems" => "Sätze", "axioms" => "Axiome", "axiomAudit" => "Axiomenprüfung", "formalKernelAccepted" or "kernelAccepted" => "Vom Lean-Kernel bestätigt",
        "verification" => "Prüfung", "valid" => "Gültig", "passed" => "Bestanden", "failures" => "Fehler", "warnings" => "Hinweise", "runs" => "Ausführungen",
        "quantitativeValidation" => "Quantitative Prüfung", "review" => "Fachliche Prüfung", "nextCheck" => "Nächste Prüfung", "expected" => "Erwartet", "actual" => "Gemessen",
        "requirements" => "Anforderungen", "hypotheses" => "Hypothesen", "claims" => "Aussagen", "sections" => "Abschnitte", "changes" => "Ergänzungen", "data" => "Daten",
        "calculationAssessment" => "Berechnungsprüfung", "sourceAssessment" => "Quellenprüfung", "contradictionAssessment" => "Widerspruchsprüfung", "scope" => "Gültigkeitsbereich",
        _ => Regex.Replace(key, "([a-zäöüß])([A-ZÄÖÜ])", "$1 $2", RegexOptions.CultureInvariant).Replace('_', ' '),
    };
}
