using System.Text.Json;
using Missum.App.Services;

namespace Missum.Tests;

public sealed class NativeToolResultFormatterTests
{
    [Fact]
    public void ReadReceiptShowsOnlyExactSourceWithFileAndLines()
    {
        var input = JsonSerializer.SerializeToElement(new { path = "work/check.py" });
        var output = JsonSerializer.SerializeToElement(new { startLine = 7, content = "7: x = 1\n8: print(x)\n", sha256 = "secret-metadata", totalLines = 100 });
        var block = Assert.Single(NativeToolResultFormatter.Receipt("coding.read", input, output, "completed"));
        Assert.Equal("x = 1\nprint(x)\n", block.Text);
        Assert.Equal("work/check.py", block.Path);
        Assert.Contains("7–8", block.Title);
        Assert.Equal("py", block.Language);
    }

    [Fact]
    public void RawReadReceiptDoesNotStripRealNumberedSourceOrChangeNewlines()
    {
        const string source = "7: Prüfung \\Box\r\n8: tatsächlicher Inhalt\r\n";
        var output = JsonSerializer.SerializeToElement(new { startLine = 7, contentFormat = "source", content = source });
        var block = Assert.Single(NativeToolResultFormatter.Receipt("coding.read", default, output, "completed"));
        Assert.Equal(source, block.Text);
    }

    [Fact]
    public void EditReceiptShowsOneDiffWithoutMetadataOrDuplicateInput()
    {
        const string diff = "@@ -4 +4 @@\n-old\n+new";
        var input = JsonSerializer.SerializeToElement(new { path = "a.cs", oldText = "old", newText = "new" });
        var output = JsonSerializer.SerializeToElement(new { diff = new { stdout = diff }, sha256 = "hash" });
        var block = Assert.Single(NativeToolResultFormatter.Receipt("coding.edit", input, output, "completed"));
        Assert.Equal(diff, block.Text);
        Assert.Equal("diff", block.Language);
        Assert.Contains("@@ -4 +4 @@", block.Title);
    }

    [Fact]
    public void ResearchReceiptShowsChangedTextAndListShowsOnlyNames()
    {
        var input = JsonSerializer.SerializeToElement(new { changes = new[] { new { id = "sec-1", data = new { title = "Ergebnis", contentMarkdown = "Neue Gleichung: $x=1$", review = new { itemRevision = 7 } } } } });
        var block = Assert.Single(NativeToolResultFormatter.Receipt("research.update", input, default, "completed"));
        Assert.Equal("Neue Gleichung: $x=1$", block.Text);
        Assert.Equal("markdown", block.Language);
        var listing = JsonSerializer.SerializeToElement(new { entries = new[] { new { path = "a.py", bytes = 100 }, new { path = "sub/b.py", bytes = 200 } }, environment = "hidden" });
        Assert.Equal("a.py\nsub/b.py", Assert.Single(NativeToolResultFormatter.Receipt("coding.list", default, listing, "completed")).Text);
    }

    [Fact]
    public void LeanReceiptPreservesProofAndFailureDiagnostics()
    {
        const string source = "theorem test : True := by\n  trivial";
        var input = JsonSerializer.SerializeToElement(new { source });
        var output = JsonSerializer.SerializeToElement(new { success = false, lean = new { diagnostics = LeanDiagnostics, source = "lean/Proof.lean", elapsedMilliseconds = 45 } });
        var blocks = NativeToolResultFormatter.Receipt("math.formalProof", input, output, "failed").ToArray();
        Assert.Equal(source, blocks[0].Text);
        Assert.Equal("lean", blocks[0].Language);
        Assert.Contains("Fehlgeschlagen", blocks[0].Title);
        Assert.Contains(blocks, b => b.Text == "Proof.lean:2: error: unknown tactic");
        Assert.DoesNotContain(blocks, b => b.Text.Contains("elapsedMilliseconds"));
    }

    [Fact]
    public void LeanSuccessOmitsMachineAuditButShowsCapturedProof()
    {
        var output = JsonSerializer.SerializeToElement(new { success = true, formalVerification = "KernelAccepted",
            lean = new { sourceText = "example : True := by trivial", source = "lean/a.lean", axiomAudit = "passed", stdout = "INTERNAL AUDIT" } });
        var block = Assert.Single(NativeToolResultFormatter.Receipt("math.formalProof", default, output, "completed"));
        Assert.Contains("Erfolgreich", block.Title);
        Assert.Equal("example : True := by trivial", block.Text);
    }

    [Fact]
    public void NestedCommandResultsDoNotLeakJsonOrRuntimeMetadata()
    {
        var output = JsonSerializer.SerializeToElement(new { runs = new[] { new { standardOutput = "[{\"message\":\"Berechnung fertig\",\"sha256\":\"hidden\"}]", snapshotId = "hidden" } }, resourceLimits = new { cpuCores = 16 } });
        var block = Assert.Single(NativeToolResultFormatter.Receipt("research.code.execute", default, output, "completed"));
        Assert.Equal("Berechnung fertig", block.Text);
        Assert.Equal("text", block.Language);
    }

    private static readonly string[] LeanDiagnostics = ["Proof.lean:2: error: unknown tactic"];
    private static readonly string[] LeanAxioms = ["propext", "Classical.choice"];
    private static readonly string[] ScriptArguments = ["--check", "work/model.py"];
    [Fact]
    public void NestedResearchResultsBecomeFlatReadableParagraphsWithoutDiscardingFailures()
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            success = false,
            verification = new
            {
                quantitativeValidation = new { expected = 1.0, actual = 1.14, passed = false },
                failures = new[] { new { message = "Die Abweichung liegt außerhalb der begründeten Genauigkeit." } },
            },
        });
        var blocks = NativeToolResultFormatter.Format(data, "", false).ToArray();
        Assert.Contains(blocks, block => block.Text == "Erfolgreich: Nein");
        Assert.Contains(blocks, block => block.Title == "Prüfung · Quantitative Prüfung"
            && block.Text.Contains("Erwartet: 1") && block.Text.Contains("Gemessen: 1.14") && block.Text.Contains("Bestanden: Nein"));
        Assert.Contains(blocks, block => block.Text == "Die Abweichung liegt außerhalb der begründeten Genauigkeit.");
        Assert.All(blocks, block => Assert.Null(block.Language));
        Assert.DoesNotContain(blocks, block => block.Text.Contains("\"quantitativeValidation\""));
    }

    [Fact]
    public void LeanSourceAndCompilerDiagnosticsPreserveExactCodeAndLineBreaks()
    {
        const string source = "import Mathlib\r\ntheorem identity (x : ℝ) : x = x := by\r\n  rfl";
        const string diagnostics = "Proof.lean:2:20: error\n  unexpected token";
        var data = JsonSerializer.SerializeToElement(new
        {
            source,
            diagnostics,
            verification = new { kernelAccepted = false, axioms = LeanAxioms },
        });
        var blocks = NativeToolResultFormatter.Format(data, "work/lean/Proof.lean", false, "lean").ToArray();
        Assert.Contains(blocks, block => block.Language == "lean" && block.Text == source && block.Path == "work/lean/Proof.lean");
        Assert.Contains(blocks, block => block.Language == "text" && block.Text == diagnostics);
        Assert.Contains(blocks, block => block.Text == "Vom Lean-Kernel bestätigt: Nein");
        Assert.Contains(blocks, block => block.Text == "propext, Classical.choice");
    }

    [Fact]
    public void ExistingDedicatedDiffIsNotRepeatedButErrorRemainsVisible()
    {
        var data = JsonSerializer.SerializeToElement(new { diff = "@@ -1 +1 @@\n-a\n+b", content = "b", error = "Speichern fehlgeschlagen" });
        var blocks = NativeToolResultFormatter.Format(data, "a.txt", true).ToArray();
        Assert.Single(blocks);
        Assert.Equal("Fehler", blocks[0].Title);
        Assert.Equal("Speichern fehlgeschlagen", blocks[0].Text);
    }

    [Fact]
    public void LargeResultSetsAreEnumerableInOrderForIncrementalDisplay()
    {
        var data = JsonSerializer.SerializeToElement(new { results = Enumerable.Range(1, 1000).Select(number => new { title = "Fund " + number, snippet = "Absatz " + number }) });
        var first = NativeToolResultFormatter.Format(data, "", false).Take(4).ToArray();
        Assert.Equal(4, first.Length);
        Assert.Equal("Ergebnisse · 1", first[0].Title);
        Assert.Equal("Titel: Fund 1", first[0].Text);
        Assert.Equal("Absatz 1", first[1].Text);
        Assert.Equal("Ergebnisse · 2", first[2].Title);
        Assert.Equal("Absatz 2", first[3].Text);
    }

    [Fact]
    public void TextPayloadKeepsMarkdownAndMathAndPrimitiveArgumentsAreReadable()
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            arguments = ScriptArguments,
            summary = "Der Zusammenhang lautet $F=ma$.\n\nDie Messung bleibt offen.",
        });
        var blocks = NativeToolResultFormatter.Format(data, "", false).ToArray();
        Assert.Contains(blocks, block => block.Title == "Argumente" && block.Text == "--check, work/model.py");
        Assert.Contains(blocks, block => block.Text == "Der Zusammenhang lautet $F=ma$.\n\nDie Messung bleibt offen.");
    }
}
