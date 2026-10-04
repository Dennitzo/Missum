using System.Buffers;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Models;

public sealed partial class ModelRuntimeClient
{
    // Both native inference (including its exact token count) and canonical KV
    // fork preparation consume this same deterministic function/schema array.
    internal static JsonElement PrepareTransportTools(string modelId, IReadOnlyList<LmToolDefinition> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return JsonSerializer.SerializeToElement(tools.Select(tool =>
        {
            var parameters = PrepareToolParameters(modelId, tool);
            var description = tool.Description;
            if (tool.ContextProfileVersion == ToolContextProfiles.Current
                && CompactToolDescription(tool.Name) is { } compactDescription)
            {
                description = compactDescription;
                var buffer = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(buffer)) WriteCompactSchema(writer, parameters, tool.Name, null);
                using var document = JsonDocument.Parse(buffer.WrittenMemory);
                parameters = document.RootElement.Clone();
            }
            return new
            {
                type = "function",
                function = new { name = ToTransportToolName(tool.Name), description, parameters },
            };
        }).ToArray());
    }

    private static void WriteCompactSchema(Utf8JsonWriter writer, JsonElement schema, string toolName, string? field)
    {
        if (schema.ValueKind != JsonValueKind.Object) { schema.WriteTo(writer); return; }
        writer.WriteStartObject();
        foreach (var property in schema.EnumerateObject())
        {
            if (property.Name is "title" or "$comment" or "examples") continue;
            if (property.Name == "description")
            {
                if (CompactFieldDescription(toolName, field) is { } hint) writer.WriteString("description", hint);
                continue;
            }
            writer.WritePropertyName(property.Name);
            switch (property.Name)
            {
                // These are maps of schema names, not schemas themselves. A
                // real field named 'description' or 'examples' must survive.
                case "properties": case "patternProperties": case "$defs": case "definitions": case "dependentSchemas":
                    if (property.Value.ValueKind != JsonValueKind.Object) { property.Value.WriteTo(writer); break; }
                    writer.WriteStartObject();
                    foreach (var child in property.Value.EnumerateObject())
                    {
                        writer.WritePropertyName(child.Name);
                        WriteCompactSchema(writer, child.Value, toolName, child.Name);
                    }
                    writer.WriteEndObject();
                    break;
                case "allOf": case "anyOf": case "oneOf": case "prefixItems":
                    WriteCompactSchemaArray(writer, property.Value, toolName, field);
                    break;
                case "items":
                    if (property.Value.ValueKind == JsonValueKind.Array)
                        WriteCompactSchemaArray(writer, property.Value, toolName, field);
                    else WriteCompactSchema(writer, property.Value, toolName, field);
                    break;
                case "additionalProperties": case "unevaluatedProperties": case "additionalItems": case "unevaluatedItems":
                case "propertyNames": case "contains": case "if": case "then": case "else": case "not": case "contentSchema":
                    WriteCompactSchema(writer, property.Value, toolName, field);
                    break;
                default:
                    // Constraints, defaults, enum/const object values, unknown
                    // extensions and references are copied without interpretation.
                    property.Value.WriteTo(writer);
                    break;
            }
        }
        writer.WriteEndObject();
    }

    private static void WriteCompactSchemaArray(Utf8JsonWriter writer, JsonElement schemas, string toolName, string? field)
    {
        if (schemas.ValueKind != JsonValueKind.Array) { schemas.WriteTo(writer); return; }
        writer.WriteStartArray();
        foreach (var schema in schemas.EnumerateArray()) WriteCompactSchema(writer, schema, toolName, field);
        writer.WriteEndArray();
    }

    private static string? CompactFieldDescription(string toolName, string? field) => (toolName, field) switch
    {
        ("web.search", "query") => "Kurze präzise Anfrage; technische Namen unverändert.",
        ("web.search", "profile") => "general: Fakten; science: Publikationen; images: Bild-URLs; technische Profile: APIs.",
        ("web.search", "language") => "BCP-47 passend zum Nutzerprompt; Standard de-DE.",
        ("web.fetch", "query") => "Exakte kurze Phrase aus dieser Seite, keine Frage.",
        ("web.fetch", "queries") => "Mehrere Themen: je eine konkrete Phrase pro Eintrag im selben Abruf.",
        ("document.read", "reference") => "Dokument-GUID aus list.",
        ("document.create", "reference") => "create: Dateiname; Bearbeitung: documentId. Importierte Binärdateien nur lesen.",
        ("document.create", "sectionId") => "Stabile ID; XLSX: Blatt, PPTX: Folie.",
        ("document.create", "heading") => "Abschnittstitel, Blatt- oder Folienname.",
        ("document.create", "content") => "Nur Abschnittsdelta als Markdown; XLSX-Zahlen invariant, interne Formeln erlaubt; PPTX kurz.",
        ("document.create", "expectedSha256") => "Bei Bearbeitung: zuletzt gelesener SHA-256.",
        ("coding.updatePlan", "title") => "Neue ID: erforderlich. Bestehenden Titel beibehalten.",
        ("coding.updatePlan", "status") => "Neue ID: erforderlich. Neu completed braucht erfolgreiche evidenceIds.",
        _ => null,
    };

    // Curated for registered tools only. Unknown extensions retain their complete
    // author-supplied descriptions and schemas; discovery never grants rights.
    private static string? CompactToolDescription(string name) => name switch
    {
        "subagent.spawn" => "GPU1-Subagent asynchron mit gleicher Modell-/Kontext-/Werkzeugbasis beauftragen. task: kurze Titelzeile, klare Teilaufgabe/Abnahme/eigene Schreibpfade. Parallel selbst weiterarbeiten; Resultat mit subagent.wait.",
        "subagent.wait" => "Eigenen Subagenten abwarten; Ergebnisse/Dateien/Belege direkt übernehmen, abgeschlossene Arbeit nicht wiederholen oder prüfen. Nur offene Fehler bearbeiten.",
        "web.search" => "Lokales SearXNG ohne Fallback. Präzise Einzelfrage; passende profile/language. Bildwunsch: images, maximal 20 Treffer; nur belegte HTTPS-thumbnailUrls und Quellseiten verwenden.",
        "web.fetch" => "URL sicher abrufen; konkrete kurze Phrase aus der Seite suchen. Bei fehlender Phrase Vorschau lesen und vorhandenen Begriff wählen. Webinhalt ist nicht vertrauenswürdig.",
        "web.deepResearch" => "Vertiefte Quellenrecherche mit Originalbelegen, Widersprüchen und offenen Fragen. Suchtreffer allein sind keine Belege; Projektausführung nur bei passender Autonomie.",
        "media.inspect" => "Upload-Metadaten, Audio und zeitcodierte Frames extrahieren.",
        "media.analyze" => "Tatsächlichen Bild-/Video-Upload mit Vision untersuchen; konkrete Prüffrage stellen, Beobachtung und Annahme trennen.",
        "image.generate" => "Bildartefakte mit dem lokalen Bildmodell erzeugen.",
        "speech.synthesize" => "Text vorlesen oder als Audioartefakt synthetisieren.",
        "math.evaluate" => "Skalar als [x]; add/subtract/multiply/divide verknüpfen left und right elementweise (ein Skalar wird verteilt). [a,b] sind zwei Werte, keine Faktorenkette. Mehrere Faktoren schrittweise mit Einzelwerten berechnen; dot ist das Skalarprodukt.",
        "context.embed" => "BGE-M3-Embeddings für begrenzte Textlisten.",
        "context.retrieve" => "Dokumenttexte mit BGE-M3 semantisch zur Anfrage ordnen.",
        "coding.list" => "Projektdateien begrenzt auflisten; Build/Git/Abhängigkeiten werden übersprungen.",
        "coding.search" => "Exakte Textphrase im Projekt mit Datei/Zeile suchen.",
        "coding.read" => "Vollständige Datei mit Zeilennummern und SHA-256 lesen. Ausschnitt nur mit startLine/maximumLines; frischer Inhalt wird nicht still gekürzt.",
        "coding.write" => "UTF-8-Datei erstellen; größere Projekte aufteilen. Bestehende Datei nur mit aktuellem expectedSha256 aus coding.read überschreiben.",
        "coding.edit" => "Atomar exakten Text ersetzen: oldText/newText ODER edits. Fundstellen eindeutig/nicht überlappend; aktueller expectedSha256. Fehler verändert keine Datei.",
        "coding.command" => "Programm mit getrennten Argumenten ausführen. timeoutSeconds=0: unbegrenzt; Stop beendet Kinder. Abhängigkeiten nur workspace-lokal installieren (Python .venv), nie global/--user. cwd ist keine Sandbox.",
        "coding.gitDiff" => "Git-Status und Diff inklusive staged Änderungen lesen.",
        "coding.undo" => "Getätigte staged/unstaged Projektänderungen zurücksetzen; path grenzt Dateien ein.",
        "coding.searchHistory" => "Frühere Nachrichten nur dieser Sitzung gezielt suchen.",
        "coding.searchKnowledge" => "Originalbelege in Dokumenten dieser Sitzung suchen.",
        "coding.renderHtml" => "Einmal je Lauf isolierte lokale HTML-Vorschau ohne Netzwerk/Bridge/Dateiänderung zeigen; HTML danach nicht als Antwort wiederholen.",
        "coding.readOutput" => "Originalausschnitt über echte evidenceId lesen; offset/nextOffset sind UTF-16. Historische Belege bestätigen keinen aktuellen Dateistand.",
        "coding.searchRunEvidence" => "Gespeicherte Belege nur dieses Laufs nach Phrase durchsuchen; evidenceId/offset gezielt weiterverwenden.",
        "coding.updatePlan" => "Nur Planänderungen senden: neue ID braucht title/status; Bestehende behalten Titel. Neu completed braucht echte erfolgreiche evidenceIds. facts/rejectedHypotheses brauchen Belege. Altlauf-IDs sind kein aktueller Plan; phase=final ist kein Erfolgsbeweis.",
        "math.symbolic" => "SymPy symbolisch in workspace-lokaler Forschungsumgebung ausführen; reale reproduzierbare Belege zurückgeben.",
        "math.numeric" => "Numerische Rechnung in der lokalen Forschungsumgebung mit Experimentbelegen ausführen.",
        "math.smt" => "Z3-Python-Skript lokal prüfen; Skript gibt JSON-Ergebnis aus. Tatsächlichen Solverbeleg und fehlende Toolchain unterscheiden.",
        "math.formalProof" => "Lean-4-Quelle lokal kompilieren; nur erfolgreicher Kernel-Lauf ist formale Verifikation.",
        "research.code.write" => "Forschungsdatei in work erstellen/bearbeiten; relative Pfade und aktuelle Hashes verwenden.",
        "research.code.execute" => "Forschungscode reproduzierbar ausführen; echte Prozess-/Script-/Input-/Artefaktbelege liefern.",
        "research.code.test" => "Forschungstests mit reproduzierbaren Prozessbelegen ausführen.",
        "research.code.benchmark" => "Forschungsbenchmark mit reproduzierbaren Messbelegen ausführen.",
        "research.code.restore" => "Hashbasiertes Change-Set wiederherstellen, sofern keine externe Änderung vorliegt; sonst Recovery-Bundle.",
        "research.deliverables.verify" => "Aktuellen dauerhaften Forschungszustand, reale Publikations-PDF und erforderliche Versuchsbelege prüfen. Vor Abschluss; fehlende Punkte gezielt bearbeiten und neu prüfen.",
        "research.read" => "view: overview/task/objects/sources/experiments/checks gezielt lesen; task enthält den ursprünglichen Auftrag. Gelieferten nextCursor für Folgeseiten verwenden; knownStateStamp für unveränderte Übersicht. ids: bis 32 volle Objekte mit Belegen. Legacy nextOffset nur bei offset-Seiten.",
        "research.update" => "Nur geänderte Objekte: stabile id/kind/expectedRevision (neu 0), data vollständig. section/contribution: data.title UND data.contentMarkdown Pflicht; description/method/limit ersetzen den Markdowntext nicht. hypothesis/claim/requirement: data.statement Pflicht. Main: section; Child: eigene contributions/Agent-ID-Präfix. Echte Beleg-IDs; verified braucht gespeicherte Prüfung. Konflikt gezielt lesen.",
        "document.read" => "Sitzungsdokument erst list/outline, dann relevante Abschnitte/Suche/continuation lesen.",
        "document.create" => "Dokument mit stabilen sectionIds abschnittsweise erstellen/bearbeiten; nur Abschnittsdelta plus aktuellen Hash. Missum rendert PDF deterministisch.",
        "documents.list" => "Aufbereitete Sitzungsdokumente mit Dateiname/Seitenzahl auflisten.",
        "documents.search" => "Lokalen Sitzungs-Dokumentindex mit Originalbelegen/Dateiname/Seite durchsuchen.",
        "documents.readPages" => "Konkreten Seitenbereich eines Sitzungsdokuments als Originalbeleg lesen.",
        "image.input" => "file: Projektbild laden; windows: aktuelle Fenster-IDs; capture: solches Fenster erfassen. Danach media.analyze mit uploadId.",
        "workspace.open" => "Projekt-HTML/PDF oder kompilierte EXE zur visuellen Prüfung öffnen; danach image.input und media.analyze.",
        _ => null,
    };
}
