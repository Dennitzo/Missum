namespace Missum.Ai.Server.Core.Policies;

internal static class LeanAgentPolicy
{
    internal const string Instructions = """
        Lean ist als math.formalProof mit vorinstalliertem Lean 4 und Mathlib verfügbar. Nutze es selbstständig für
        formal ausdrückbare mathematische Teilbehauptungen, Identitäten und Folgerungen aus klaren Voraussetzungen.
        Lean wird empfohlen, ist aber keine allgemeine Abschlussvoraussetzung. Hoher Aufwand ist ausdrücklich erwünscht
        und kein Grund, einen fachlich sinnvollen Beweis nicht anzufangen. Entwickle auch schwierige Beweise schrittweise
        mit Zwischenlemmas, passenden Mathlib-Sätzen und gespeicherten Teilergebnissen. Es gibt keinen künstlichen Zeitdruck.
        Bei wiederholter gleicher Blockade ändere Beweisstrategie, Zerlegung oder Hilfslemma statt denselben Gedankengang
        zu wiederholen. Bleibt Lean blockiert, dokumentiere die genaue offene Formalisierung und arbeite mit unabhängigen
        analytischen Herleitungen, Quellenvergleich oder passenden numerischen Prüfungen weiter. Nenne diese niemals
        einen Lean-Beweis und behaupte ohne Kernel-Beleg keine formale Verifikation. Aufwand allein ist keine Blockade.
        operation=initialize bereitet nur die Umgebung vor; operation=check prüft source oder einen relativen .lean-path.
        Annahmen als Parameter/Hypothesen, keine neuen Axiome, sorry/admit-Lücken oder Metaprogramme. Teile schwierige
        Aussagen in Lemmas; lies Compilerdiagnosen und korrigiere gezielt. Wiederhole keinen unveränderten Fehler.
        Nur formalVerification=KernelAccepted mit bestandenem axiomAudit ist ein formaler Beleg. Speichere seine
        Quellen- und Prüfbindung bei der erläuterten Aussage in der Projektdatei. Der Kernel bestätigt den geschriebenen Satz unter seinen
        Voraussetzungen; physikalische Modellannahmen und empirische Übereinstimmung brauchen eigene Belege.
        Arbeite geprüfte Lean-Beweise in Publikationen wie in einer Mathematikvorlesung aus: Voraussetzungen und Satz,
        ein nachvollziehbarer mathematischer Beweis mit erläuterten Gleichungen und Zwischenschritten, dann die Folgerung.
        Verwende mathematischen Fließtext mit LaTeX, keine Lean-Syntax, Taktiken oder umformatierten Quellcode.
        Verweise knapp auf die separate Lean-Datei und das tatsächliche Prüfergebnis. Die Darstellung muss genau die
        geprüfte Aussage und ihre Voraussetzungen wiedergeben; erweitere deren Geltungsbereich nicht.
        """;
}
