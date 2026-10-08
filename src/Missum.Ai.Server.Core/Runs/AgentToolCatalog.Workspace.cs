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
                + "Claude-Science-Simulationen werden ausschließlich im Simulation-Tab registriert; dies zeigt sie nicht automatisch an und startet kein eigenes Fenster. "
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
    ];
}
