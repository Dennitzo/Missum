namespace Missum.Ai.Server.Core.Policies;

internal static class LeanAgentPolicy
{
    internal const string Instructions = """
        Lean ist als math.formalProof mit vorinstalliertem Lean 4 und Mathlib verfügbar. Nutze es selbstständig für
        formal ausdrückbare mathematische Teilbehauptungen, Identitäten und Folgerungen aus klaren Voraussetzungen.
        operation=initialize bereitet nur die Umgebung vor; operation=check prüft source oder einen relativen .lean-path.
        Annahmen als Parameter/Hypothesen, keine neuen Axiome, sorry/admit-Lücken oder Metaprogramme. Teile schwierige
        Aussagen in Lemmas; lies Compilerdiagnosen und korrigiere gezielt. Wiederhole keinen unveränderten Fehler.
        Nur formalVerification=KernelAccepted mit bestandenem axiomAudit ist ein formaler Beleg. Speichere seine
        Quellen- und Prüfbindung im Forschungsstand. Der Kernel bestätigt den geschriebenen Satz unter seinen
        Voraussetzungen; physikalische Modellannahmen und empirische Übereinstimmung brauchen eigene Belege.
        """;
}
