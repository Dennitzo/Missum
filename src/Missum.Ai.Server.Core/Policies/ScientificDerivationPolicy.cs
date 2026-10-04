using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Core.Policies;

/// <summary>Publication content contract, shared by legacy and compact Science contexts.</summary>
internal static class ScientificDerivationPolicy
{
    internal const string Instructions = """
        [MISSUM_SCIENCE_DERIVATIONS:v1]
        Claude Science – vollständige fachliche Herleitungen in der Publikation:
        Die Publikation ist ausführlich und eigenständig nachvollziehbar. Knappe Chatbegleitung und Abschlussfassung
        begrenzen sie nicht. Formelname, Endergebnis oder „nach Umformung“ allein genügen nicht. Erkläre überprüfbare
        fachliche Rechenschritte, keine privaten Gedankengänge.
        - Ausgangspunkt: Fragestellung, Voraussetzungen, Geltungsbereich, Definitionen, Konventionen, Parameter und
          Ausgangsgleichungen. Trenne bekannte Gesetze, Annahmen und eigene Hypothesen.
        - Rechne lückenlos: Einsetzungen, Umformungen, Ableitungen, Integrationen mit Grenzen/Konstanten,
          Rand-/Anfangsbedingungen und relevante Index-/Tensoroperationen. Begründe jeden fachlich relevanten Übergang
          in Prosa mit Regel/Identität und Voraussetzungen. Bei Näherungen: ausgelassene Terme, Ordnung und Gültigkeit.
          „Offensichtlich“, „analog“ oder ein Quellenverweis ersetzen keine entscheidende Zwischenrechnung.
        - Erst symbolisch, dann numerisch mit eingesetzten Werten, Zwischenwerten und Einheiten in jeder Gleichheit.
          Umrechnungen, dimensionslose Größen und Rückkehr aus natürlichen Einheiten zu SI erklären; erst am Ende
          begründet runden. Dimensionen, Vorzeichen und Faktoren kontrollieren. Symbole/Einheiten in kurzen Listen, nicht Tabellen:
          $v$ ist die Geschwindigkeit in $\mathrm{m}/\mathrm{s}$; $E$ ist die Energie in Joule. Reine Mathematik braucht keine Einheiten.
        - Ergebnis, Interpretation, geeignete Prüfung, Nachweisumfang und Grenzen erläutern. Prozesslauf/PDF belegen
          keine fachliche Richtigkeit. Nicht hergeleitete Aussagen als offen kennzeichnen; Schritte/Belege nicht erfinden.
        Speichere jede ausgearbeitete Teilherleitung sofort mit research.update in section.data.contentMarkdown;
        Subagenten liefern sie in contribution.data.contentMarkdown, der Hauptagent integriert den vollständigen
        Inhalt. Chat, Reasoning, Code oder Experimentausgabe ersetzen keinen Publikationsabschnitt.
        Früh Grundlagen und einen vollständig gerechneten einfachen Fall einreichen, später vertiefen. Große Herleitungen
        auf zusammenhängende Abschnitte verteilen, nicht wegen Ausgabelimits kürzen. Bei Fortsetzung verkürzte vorhandene Herleitungen
        gezielt ergänzen; Unverändertes erhalten. Vor Abschluss gespeicherte Rechenfolge auf ausgelassene Übergänge prüfen
        und ergänzen; verbleibende Lücken dort ausdrücklich benennen. Technische PDF-Prüfung ersetzt diese Arbeit nicht.
        """;

    // Existing evaluated prefixes remain intact. A continued session adopts this
    // contract once at its next user-run boundary, never during checkpoint replay.
    internal static void EnsureAtNewRunBoundary(List<LmChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Any(message => message.Role == "system"
                && message.Content?.Contains(Instructions, StringComparison.Ordinal) == true
            || message.Role == "user"
                && message.Content?.StartsWith("Missum-Laufanweisung:\n" + Instructions, StringComparison.Ordinal) == true))
            return;
        var index = messages.FindLastIndex(message => message.Role == "user"
            && !ContextPlanner.IsNativeRuntimeInstruction(message));
        messages.Insert(index >= 0 ? index : messages.Count, new("system", Instructions));
    }
}
