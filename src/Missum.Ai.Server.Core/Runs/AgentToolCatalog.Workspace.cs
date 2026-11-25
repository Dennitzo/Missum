using Missum.Ai.Contracts;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class AgentToolCatalog
{
    private static IEnumerable<AgentToolSpec> WorkspaceToolSpecs() =>
    [
        Server("speech.synthesize", "Vorlesen oder Audiodatei erstellen: Synthetisiere den angegebenen Text als Audioartefakt. Verwende dies selbstständig, wenn die Aufgabe eine gesprochene Ausgabe verlangt.",
            ToolRiskClass.ReadOnly, Parse("""
            {"type":"object","properties":{"text":{"type":"string","minLength":1,"maxLength":20000}},"required":["text"],"additionalProperties":false}
            """)),
        Client(WorkspaceTools.Open,
            "Öffne eine im ausgewählten Workspace erstellte Anwendung: relative HTML/PDF-Datei oder eine kompilierte EXE. "
                + "Prüfe im Ergebnis opened/displayed. image.input windows/capture und media.analyze belegen nur den tatsächlich sichtbaren Fensterinhalt; "
                + "ein Missum-Chat-Screenshot ist kein Beleg für eine Simulation. Vorhandene PNG-Plots kannst du mit image.input operation=file analysieren, "
                + "ohne daraus ungeprüfte Eigenschaften einer HTML-Animation abzuleiten.",
            ToolRiskClass.Process, Parse("""
            {"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}
            """)),
        Client(WorkspaceTools.ImageInput,
            "Lade ein lokales Workspace-Bild (operation=file, path), auch einen vorhandenen PNG-Plot, oder erfasse das zur Aufgabe gehörende Anwendungsfenster. "
                + "windows liefert aktuelle windowIds; capture benötigt eine solche windowId. Die Erfassung wechselt keine Missum-Ansicht. "
                + "Anschließend media.analyze mit der erhaltenen uploadId aufrufen und nur dessen tatsächlich sichtbaren Bildinhalt als Beleg verwenden.",
            ToolRiskClass.ReadOnly, Parse("""
            {"type":"object","properties":{"operation":{"type":"string","enum":["file","windows","capture"]},"path":{"type":"string"},"windowId":{"type":"string"}},"required":["operation"],"additionalProperties":false}
            """)),
        Client(WorkspaceTools.WebdesignServe,
            "Lokaler Webserver des Webdesign-Projekts mit garantiert freiem Port. status zeigt URL, Port, Art und Bereitschaft; "
                + "start/stop/restart steuern ihn; log liefert die letzte Ausgabe eines Projekt-Dev-Servers; configure setzt root (bedienter Ordner), "
                + "startPage, kind (auto|static|command) und command (eigener Dev-Befehl mit {port} und {host}) und startet neu. "
                + "Starte Server nie mit coding.command: dessen Prozesse enden mit dem Aufruf.",
            ToolRiskClass.Process, Parse("""
            {"type":"object","properties":{"operation":{"type":"string","enum":["status","start","stop","restart","log","configure"]},"root":{"type":"string"},"startPage":{"type":"string"},"command":{"type":"string"},"kind":{"type":"string","enum":["auto","static","command"]}},"required":["operation"],"additionalProperties":false}
            """)),
        Client(WorkspaceTools.WebdesignPreview,
            "Tab Webseite des Nutzers. open zeigt eine Projektseite (path relativ zum Projekt) im Tab; capture nimmt die gerenderte Seite "
                + "optional in einem Gerät (smartphone|tablet|laptop) auf und liefert eine uploadId für media.analyze; console liefert "
                + "Konsolenfehler und fehlgeschlagene Anfragen; inspect(selector) liefert Box, berechnete Stile und Text; annotations liefert "
                + "die offenen Markierungen, Kommentare, Rahmen und Feintuning-Wünsche des Nutzers mit Nummer; resolve(ids, note) meldet "
                + "umgesetzte Nummern als erledigt.",
            ToolRiskClass.ReadOnly, Parse("""
            {"type":"object","properties":{"operation":{"type":"string","enum":["open","capture","console","inspect","annotations","resolve"]},"path":{"type":"string"},"device":{"type":"string","enum":["smartphone","tablet","laptop","free"]},"selector":{"type":"string"},"ids":{"type":"array","items":{"type":"integer","minimum":1}},"note":{"type":"string"}},"required":["operation"],"additionalProperties":false}
            """)),
    ];
}
