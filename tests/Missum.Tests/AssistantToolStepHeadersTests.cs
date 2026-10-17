using System.Text.Json;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class AssistantToolStepHeadersTests
{
    private static readonly string[] Entries = ["private-entry-a", "private-entry-b"];
    private static readonly string[] Matches = ["private-match"];
    private static readonly string[] Results = ["one", "two", "three"];
    [Fact]
    public void PartialReadShowsReturnedLinesAndTruncationWithoutRetainingCode()
    {
        var content = string.Join("\r\n", Enumerable.Range(1, 25).Select(index => $"{index}: secret body {index}")) + "\r\n";
        var step = new AssistantToolStep("read", "coding.read", "completed",
            InputJson: JsonSerializer.Serialize(new { path = "work\\consolidation_numerics.py", startLine = 100, endLine = 124 }),
            OutputJson: JsonSerializer.Serialize(new
            {
                path = "work\\consolidation_numerics.py", startLine = 100, totalLines = 100000, lineCount = 100000,
                content, truncated = true,
            }));

        var header = AssistantToolStepHeaders.Create(step);

        Assert.Equal(AssistantToolStepHeaders.CurrentVersion, header.Version);
        Assert.Equal("work\\consolidation_numerics.py", header.FilePath);
        Assert.Contains("work/consolidation_numerics.py", header.Summary);
        Assert.Contains("25 Zeilen gelesen (100–124)", header.Summary);
        Assert.Contains("Ausgabe gekürzt", header.Summary);
        Assert.DoesNotContain("100000", header.Summary);
        Assert.DoesNotContain("secret body", JsonSerializer.Serialize(header));
        Assert.Null(header.AddedLines);
        Assert.Null(header.RemovedLines);
        using var originalOutput = JsonDocument.Parse(step.OutputJson!);
        Assert.Equal(content, originalOutput.RootElement.GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("", "0 Zeilen gelesen")]
    [InlineData("a", "1 Zeilen gelesen (1–1)")]
    [InlineData("a\n", "1 Zeilen gelesen (1–1)")]
    [InlineData("a\r\nb\r\n", "2 Zeilen gelesen (1–2)")]
    [InlineData("a\rb\n", "2 Zeilen gelesen (1–2)")]
    public void ReadLineCountHandlesEmptyAndTrailingNewlines(string content, string expected)
    {
        var step = new AssistantToolStep("read", "coding.read", "completed",
            OutputJson: JsonSerializer.Serialize(new { content, startLine = 1 }));
        Assert.Equal(expected, AssistantToolStepHeaders.Create(step).Summary);
    }

    [Fact]
    public void ReadUsesRequestedStartLineWhenOutputOmitsIt()
    {
        var step = new AssistantToolStep("read", "coding.read", "completed",
            InputJson: "{\"path\":\"requested.py\",\"startLine\":37}",
            OutputJson: "{\"content\":\"one\\ntwo\\n\"}");
        Assert.Equal("requested.py · 2 Zeilen gelesen (37–38)", AssistantToolStepHeaders.Create(step).Summary);
    }

    [Theory]
    [InlineData("assistant.reasoning")]
    [InlineData("assistant.narration")]
    [InlineData("assistant.progress")]
    public void ReasoningAndNarrationDoNotInspectTheirPotentiallyLargeBodies(string tool)
    {
        var step = new AssistantToolStep("thinking", tool, "running", "Datei: not-a-tool.py\n" + new string('r', 100000),
            InputJson: "{\"path\":\"also-not-a-tool.py\"}", OutputJson: "{\"error\":\"Not a tool result\"}");
        Assert.Equal(new ToolStepDisplayHeader(), AssistantToolStepHeaders.Create(step));
    }

    [Fact]
    public void ArrayCountsDoNotCopyEntriesOrSourceBodies()
    {
        var step = new AssistantToolStep("search", "coding.search", "completed",
            InputJson: "{\"query\":\"convergence\"}", OutputJson: JsonSerializer.Serialize(new
            {
                entries = Entries, matches = Matches,
                results = Results, sources = new[] { new { content = new string('x', 100000) } },
            }));

        var header = AssistantToolStepHeaders.Create(step);

        Assert.Equal("convergence · 2 Einträge · 1 Treffer · 3 Treffer · 1 Quellen", header.Summary);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(header));
        Assert.True(JsonSerializer.Serialize(header).Length < 500);
    }

    [Theory]
    [InlineData("coding.command")]
    [InlineData("research.code.execute")]
    [InlineData("research.code.test")]
    [InlineData("research.code.benchmark")]
    public void CommandHeadersShowExecutableArgumentsAndExitStatus(string tool)
    {
        var step = new AssistantToolStep("command", tool, "completed",
            InputJson: "{\"executable\":\"python\",\"arguments\":[\"check.py\",\"--mode\",\"exact\"]}",
            OutputJson: "{\"exitCode\":0,\"stdout\":\"Large output stays deferred\"}");
        Assert.Equal("python check.py --mode exact · Exitcode 0", AssistantToolStepHeaders.Create(step).Summary);
    }

    [Fact]
    public void SingleCommandStringAndErrorCodeRemainVisible()
    {
        var step = new AssistantToolStep("command", "coding.command", "failed",
            InputJson: "{\"command\":\"python verify.py\"}",
            OutputJson: "{\"exitCode\":2,\"errorCode\":\"process.failed\",\"truncated\":true}");
        Assert.Equal("python verify.py · Exitcode 2 · process.failed · Ausgabe gekürzt", AssistantToolStepHeaders.Create(step).Summary);
    }

    [Fact]
    public void CompleteUnifiedDiffProvidesPathAndMeasuredCounts()
    {
        var diff = "--- a/src/value.cs\n+++ b/src/value.cs\n@@ -10,2 +10,3 @@\n-old\n+new\n+extra\n same\n";
        var step = new AssistantToolStep("edit", "coding.edit", "completed",
            OutputJson: JsonSerializer.Serialize(new { diff }));

        var header = AssistantToolStepHeaders.Create(step);

        Assert.Equal("src/value.cs", header.FilePath);
        Assert.Equal("src/value.cs", header.Summary);
        Assert.Equal(2L, header.AddedLines);
        Assert.Equal(1L, header.RemovedLines);
        Assert.DoesNotContain("old", JsonSerializer.Serialize(header));
        Assert.DoesNotContain("extra", JsonSerializer.Serialize(header));
    }

    [Theory]
    [InlineData("--- a/a.py\n+++ b/a.py\n@@ -1,2 +1,2 @@\n-old\n+new\n", false, false)]
    [InlineData("-old\n+new\n", false, false)]
    [InlineData("--- a/a.py\n+++ b/a.py\n@@ -1 +1 @@\n-old\n+new\n", true, false)]
    [InlineData("--- a/a.py\n+++ b/a.py\n@@ -1 +1 @@\n-old\n+new\n", false, true)]
    [InlineData("Binary files a/a.png and b/a.png differ\n", false, false)]
    public void IncompleteUnstructuredTruncatedAndBinaryDiffsDoNotInventCounts(string diff, bool diffTruncated, bool isBinary)
    {
        var header = AssistantToolStepHeaders.Create(new("edit", "coding.write", "completed",
            OutputJson: JsonSerializer.Serialize(new { diff, diffTruncated, isBinary })));
        Assert.Null(header.AddedLines);
        Assert.Null(header.RemovedLines);
    }

    [Fact]
    public void LastDetailDiffAndExplicitDetailPathKeepExistingDisplayPrecedence()
    {
        var step = new AssistantToolStep("edit", "coding.edit", "completed",
            Detail: "Datei: src/actual.py\n```diff\n@@ -1 +1 @@\n-a\n+b\n```\n```diff\n@@ -0,0 +1,2 @@\n+one\n+two\n```",
            InputJson: "{\"path\":\"input.py\"}", OutputJson: "{\"path\":\"output.py\"}");
        var header = AssistantToolStepHeaders.Create(step);
        Assert.Equal("src/actual.py", header.FilePath);
        Assert.Equal(2L, header.AddedLines);
        Assert.Equal(0L, header.RemovedLines);
    }

    [Fact]
    public void ResearchAndSubagentTitlesMatchTheirSpecializedSemantics()
    {
        var single = new AssistantToolStep("research", "research.update", "completed",
            InputJson: "{\"changes\":[{\"id\":\"sec-method\",\"data\":{\"title\":\"Konvergenzprüfung\"}}]}");
        Assert.Equal("Konvergenzprüfung", AssistantToolStepHeaders.Create(single).Summary);
        Assert.Equal("2 Forschungsobjekte", AssistantToolStepHeaders.Create(single with
        {
            InputJson = "{\"changes\":[{\"id\":\"one\"},{\"id\":\"two\"}]}",
        }).Summary);
        Assert.Equal("Hypothesen, offene Prüfungen und Publikationsstand", AssistantToolStepHeaders.Create(single with
        {
            Tool = "research.read",
        }).Summary);
        Assert.Equal("Tatsächliche Aufgabe", AssistantToolStepHeaders.Create(single with
        {
            Tool = "subagent.spawn", InputJson = "{\"title\":\"Angefragte Aufgabe\"}",
            OutputJson = "{\"title\":\"Tatsächliche Aufgabe\"}",
        }).Summary);
    }

    [Theory]
    [InlineData("{", "[1,2]")]
    [InlineData("null", "{\"path\":null,\"error\":{\"unbounded\":\"body\"}}")]
    [InlineData("{\"changes\":[null,3,{}, {\"data\":[]} ]}", "not json")]
    public void MalformedMetadataIsConservativeAndKeepsAvailableDetailPath(string input, string output)
    {
        var header = AssistantToolStepHeaders.Create(new("broken", "research.update", "failed",
            Detail: "Datei: valid.py\n", InputJson: input, OutputJson: output));
        Assert.Equal("valid.py", header.FilePath);
        Assert.Null(header.AddedLines);
        Assert.DoesNotContain("unbounded", header.Summary);
    }

    [Fact]
    public void HeaderSizeIsBoundedAndDoesNotContainMultilinePayloads()
    {
        var path = "src/" + new string('a', 10000) + ".py";
        var header = AssistantToolStepHeaders.Create(new("long", "coding.read", "failed",
            InputJson: JsonSerializer.Serialize(new { path }), OutputJson: JsonSerializer.Serialize(new
            {
                path, content = "body must not appear", startLine = 1, error = new string('e', 10000), truncated = true,
            })));
        Assert.InRange(header.Summary.Length, 1, AssistantToolStepHeaders.MaximumSummaryCharacters);
        Assert.InRange(header.FilePath.Length, 1, AssistantToolStepHeaders.MaximumFilePathCharacters);
        Assert.DoesNotContain("body must not appear", JsonSerializer.Serialize(header));
        Assert.DoesNotContain('\n', header.Summary);
    }

    [Fact]
    public void ProjectionCacheUsesRecordIdentityAndCompleteUpdatesReplaceStaleHeaders()
    {
        var original = new AssistantToolStep("read", "coding.read", "completed",
            OutputJson: "{\"path\":\"old.py\",\"content\":\"one\"}");
        var first = AssistantToolStepHeaders.Create(original);
        Assert.Same(first, AssistantToolStepHeaders.Create(original));
        Assert.NotSame(first, AssistantToolStepHeaders.Create(original with { }));

        var deferred = original with { DetailsDeferred = true, DisplayHeader = first, InputJson = null, OutputJson = null };
        Assert.Same(first, AssistantToolStepHeaders.Create(deferred));
        var changed = original with
        {
            DisplayHeader = first, OutputJson = "{\"path\":\"new.py\",\"content\":\"one\\ntwo\"}",
        };
        var latest = AssistantToolStepHeaders.Create(changed);
        Assert.NotSame(first, latest);
        Assert.Equal("new.py", latest.FilePath);
        Assert.Contains("2 Zeilen gelesen", latest.Summary);
        Assert.Equal("old.py", first.FilePath);
    }

    [Fact]
    public void DisplayHeaderSerializesAdditivelyAndAbsentMetadataIsOmitted()
    {
        var step = new AssistantToolStep("read", "coding.read", "completed");
        using var oldShape = JsonDocument.Parse(JsonSerializer.Serialize(step, JsonSerializerOptions.Web));
        Assert.False(oldShape.RootElement.TryGetProperty("displayHeader", out _));
        var withHeader = step with { DisplayHeader = new(Summary: "file.py", FilePath: "file.py"), DetailsDeferred = true };
        var serialized = JsonSerializer.Serialize(withHeader, JsonSerializerOptions.Web);
        var restored = JsonSerializer.Deserialize<AssistantToolStep>(serialized, JsonSerializerOptions.Web);
        Assert.Equal(withHeader, restored);
    }
}
