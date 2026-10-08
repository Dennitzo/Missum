using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Research;

namespace Missum.Tests;

public sealed class ScientificPublicationDiagnosticsTests
{
    [Theory]
    [InlineData("Abschnitt \"3.3 Reduzierte Gleichungen\", Ausdruck: $$x=1$$", "### 3.3 Reduzierte Gleichungen\n\n$$x=1$$")]
    [InlineData("Abschnitt \"Andere Ansicht\", Ausdruck: $$x=1$$, Ursache: KaTeX parse error", "Eine Formel:\n\n$$x=1$$")]
    [InlineData("Abschnitt \"Andere Ansicht\", Ausdruck: $$x + y = z$$, Ursache: KaTeX parse error", "$$x +\n y = z$$")]
    public void FormulaDiagnosticLocatesTheOwningSectionInsteadOfListingEveryMathSection(string error, string content)
    {
        var state = State(
            Section("sec-model", "Reduziertes Modell", content),
            Section("sec-conclusion", "Zusammenfassung", "$$z=2$$"),
            Section("sec-withdrawn", "Andere Ansicht", content, status: "withdrawn"),
            Section("sec-child", "Andere Ansicht", content, owner: "subagent"));

        var diagnostic = ScientificPublicationService.SectionRepairDiagnostic(state, error);

        Assert.Contains("eingrenzen: sec-model.", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("sec-conclusion", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("sec-withdrawn", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("sec-child", diagnostic, StringComparison.Ordinal);
        Assert.StartsWith(error, diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void MultipleFormulaErrorsLocateEachAffectedSection()
    {
        var state = State(Section("sec-a", "Modell A", "$$x=1$$"),
            Section("sec-b", "Modell B", "$$y=2$$"), Section("sec-c", "Modell C", "$$z=3$$"));

        var diagnostic = ScientificPublicationService.SectionRepairDiagnostic(state,
            "Abschnitt \"Modell A\", Ausdruck: $$x=1$$, Ursache: Fehler A; Abschnitt \"Modell B\", Ausdruck: $$y=2$$, Ursache: Fehler B");

        Assert.Contains("eingrenzen: sec-a, sec-b.", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("sec-c", diagnostic, StringComparison.Ordinal);
    }

    private static ResearchWorkingState State(params ResearchWorkingItem[] items) =>
        new("project", 1, 1, "Forschungsstand", items, DateTimeOffset.UnixEpoch);

    private static ResearchWorkingItem Section(string id, string title, string content,
        string status = "completed", string? owner = null) => new(id, "section", 1, owner,
            JsonSerializer.SerializeToElement(new { title, contentMarkdown = content, status }), DateTimeOffset.UnixEpoch);
}
