using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Missum.Core.Models;
using Missum.Core.Research;

namespace Missum.App.Services;

/// <summary>Explicit opt-in benchmark inputs; never used for ordinary conversations.</summary>
public sealed record NativeContextBenchmarkConfiguration(string Mode, string Variant, string Cache, int Repetition,
    string ModelId, string ReasoningEffort, string ServerUrl, string DataDirectory, string WorkspacePath,
    string ResultPath, string Stage = "complete", Guid? SessionId = null, int TimeoutMinutes = 20);

public sealed record NativeContextBenchmarkLeg(string RunId, string ModelId, string Phase, Guid AssistantMessageId,
    DateTimeOffset SubmittedAt, DateTimeOffset? FirstVisibleAnswerAt, DateTimeOffset VisibleCompletedAt,
    double Milliseconds, double? FirstVisibleAnswerMilliseconds, string Content,
    IReadOnlyList<AssistantToolStep> ToolSteps, IReadOnlyList<AssistantToolStep> ChildToolSteps);

public sealed record NativeContextBenchmarkResult(bool Passed, string? Error, Guid SessionId,
    IReadOnlyList<NativeContextBenchmarkLeg> Legs, IReadOnlyList<string> WarmupRunIds,
    string? FirstPdfPath, DateTimeOffset? FirstPdfAt, long? FirstPdfRevision,
    string? FinalPdfPath, string? ResearchStateHash, long? PublicationRevision,
    bool CanonicalStateRestored, bool RequiredSimulation, string Renderer = "WinUI3", ResearchWorkingState? ResearchState = null);

public static class ContextEfficiencyBenchmarkScenarios
{
    private static readonly string[] GeneralChecks = ["taskComplete", "permissionsPreserved", "endToEndTimingVerified", "followupComplete", "targetedSource"];
    private static readonly string[] CodingChecks = ["taskComplete", "permissionsPreserved", "endToEndTimingVerified", "readEditVerified"];
    private static readonly string[] ScienceChecks = ["taskComplete", "permissionsPreserved", "endToEndTimingVerified", "foundationsPdf", "scientificCheck", "restartContinuation"];
    public const string SourceUrl = "https://www.bipm.org/en/measurement-units";
    public const string Calculator = "def add(a, b):\n    return a - b\n";
    public const string CalculatorTest = "import unittest\nfrom calculator import add\nclass Tests(unittest.TestCase):\n    def test_positive(self): self.assertEqual(5, add(2, 3))\n    def test_negative(self): self.assertEqual(-5, add(-2, -3))\n    def test_zero(self): self.assertEqual(0, add(0, 0))\nif __name__ == '__main__': unittest.main()\n";

    public static string Prompt(string mode) => mode switch
    {
        "general" => "Lies die offizielle BIPM-Seite " + SourceUrl + ". Erkläre danach in höchstens 100 Wörtern "
            + "die SI-Einheit der Masse, nenne die gelesene Quelle und berechne mit dem Werkzeug math.evaluate "
            + "die kinetische Energie E=0.5*m*v*v für m=2 kg und v=3 m/s. Nenne Ergebnis und Einheit.",
        "coding" => "Lies calculator.py und test_calculator.py im ausgewählten Workspace. Behebe den konkreten Fehler "
            + "in add(a,b) durch Bearbeiten von calculator.py. Verändere die vorhandenen Tests nicht. Führe danach "
            + "die vorhandenen drei unittest-Tests tatsächlich aus. Berichte knapp über Änderung und Testresultat.",
        "science" => "Erstelle eine kurze wissenschaftliche Publikation mit dem Titel Kinetische Energie. "
            + "Ziel sind genau zwei vollständige Abschnitte: Grundlagen und Herleitung. Erkläre E=1/2*m*v^2, "
            + "die SI-Dimension kg*m^2/s^2=J, Symbolbedeutungen und die nichtrelativistischen Modellgrenzen. "
            + "Rechne m=2 kg und v=3 m/s mit dem Werkzeug math.evaluate aus und übernimm das gemessene Ergebnis. "
            + "Dokumentiere diese Ziele als erforderlich mit Methode derivation und den fertig bearbeiteten Abschnitten "
            + "im kanonischen Forschungszustand; etwa 180 bis 250 Wörter, eine abgesetzte Gleichung und eine Einheitenliste. "
            + "Eine numerische Simulation und Literaturrecherche sind für diesen rein theoretischen Auftrag nicht erforderlich. "
            + "Beende nach der aktuellen, tatsächlichen PDF und den erledigten Zielen.",
        _ => throw new ArgumentException("Unknown context benchmark mode.", nameof(mode)),
    };

    public static string Warmup(string mode) => mode switch
    {
        "general" => "Merke als Kontext für die folgende Aufgabe die SI-Symbole m für Masse und v für Geschwindigkeit. Antworte nur: Bereit.",
        "coding" => "Lies nur calculator.py und test_calculator.py im Workspace und nenne knapp die Funktions- und Testnamen. Ändere keine Datei und führe noch keine Tests aus.",
        "science" => "Erstelle für das Forschungsprojekt Kinetische Energie zunächst einen kurzen kanonischen Grundlagenabschnitt "
            + "über Masse und Geschwindigkeit mit SI-Symbolen. Dokumentiere genau dieses Ziel als required und Methode derivation, "
            + "und schließe es mit einer echten Grundlagen-PDF ab. Noch kein Zahlenbeispiel, keine Simulation, keine Quellenrecherche.",
        _ => throw new ArgumentException("Unknown context benchmark mode.", nameof(mode)),
    };

    public static string Followup(string mode) => mode switch
    {
        "general" => "Nutze die soeben ermittelte Masse und ändere ausschließlich die Geschwindigkeit auf 6 m/s. "
            + "Berechne die neue kinetische Energie mit math.evaluate und erkläre in einem Satz den Faktor gegenüber zuvor.",
        "science" => "Setze das gespeicherte Forschungsprojekt fort. Ergänze ausschließlich im bestehenden Grundlagenabschnitt "
            + "einen Satz zur Gültigkeitsgrenze v deutlich kleiner als Lichtgeschwindigkeit. Erhalte die übrigen Abschnitte, "
            + "die Einheiten und das berechnete Beispiel 9 J. Keine erneute Quellenrecherche oder Berechnung; "
            + "beende nach der aktualisierten echten PDF.",
        _ => throw new ArgumentException("This mode has no followup.", nameof(mode)),
    };

    public static IReadOnlyList<string> RequiredChecks(string mode) => mode switch
    {
        "general" => GeneralChecks,
        "coding" => CodingChecks,
        "science" => ScienceChecks,
        _ => throw new ArgumentException("Unknown context benchmark mode.", nameof(mode)),
    };

    public static string FixtureHash(string mode) => Hash(JsonSerializer.Serialize(new
    {
        protocol = "missum.context-efficiency.fixture.v1", mode, prompt = Prompt(mode), warmup = Warmup(mode),
        followup = mode == "coding" ? null : Followup(mode),
        files = mode == "coding" ? Calculator + CalculatorTest : null,
    }));

    public static string AcceptanceHash(string mode) => Hash(JsonSerializer.Serialize(new
    {
        protocol = "missum.context-efficiency.acceptance.v1", mode, checks = RequiredChecks(mode),
        expected = mode == "general" ? "fetched BIPM source; real math 9 then 36 J; complete visible answers"
            : mode == "coding" ? "changed add implementation; tests byte-identical; actual independent unittest 3/3"
            : "two canonical sections, SI units, real math 9 J, current non-placeholder PDF; persisted client restart continuation; no required simulation",
    }));

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
