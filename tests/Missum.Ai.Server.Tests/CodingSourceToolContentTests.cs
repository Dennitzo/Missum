using System.Text.Json;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Tests;

public sealed class CodingSourceToolContentTests
{
    private const string Source = "Größe: \\Box \\nabla \\u00FC, literal \\n und \\r; ü ö ß\r\n\tA = \\";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadProjectsDecodedSourceWhileOriginalReceiptStaysExact(bool wrapped)
    {
        var data = new { path = "Publikation.md", content = Source, contentFormat = "source", startLine = 5, endLine = 6 };
        var receipt = wrapped
            ? JsonSerializer.Serialize(new { status = "completed", result = data, message = "Gelesen" })
            : JsonSerializer.Serialize(data);
        var original = Messages("coding.read", receipt);
        var projected = CodingSourceToolContent.Project(original);
        Assert.Contains(Source, projected[^1].Content);
        Assert.DoesNotContain("\\u00F6", projected[^1].Content);
        Assert.Contains("sourceFormat: source", projected[^1].Content);
        Assert.Contains("\"startLine\":5", projected[^1].Content);
        if (wrapped) Assert.Contains("\"status\":\"completed\"", projected[^1].Content);
        Assert.Equal(receipt, original[^1].Content);
        Assert.Equal(projected, CodingSourceToolContent.Project(projected));
    }

    [Fact]
    public void ReadSourceNumbersAndWhitespaceAreNeverMistakenForLegacyDisplay()
    {
        const string numberedSource = "1: ü\\Box\r\n2: \\n\r\n";
        var projected = CodingSourceToolContent.Project(Messages("coding.read", JsonSerializer.Serialize(new
        {
            content = numberedSource, contentFormat = "source", startLine = 1,
        })));
        Assert.Contains(numberedSource, projected[^1].Content);
    }

    [Fact]
    public void LegacyReadStripsOnlyVerifiedSequentialDisplayPrefixes()
    {
        var projected = CodingSourceToolContent.Project(Messages("coding.read", JsonSerializer.Serialize(new
        {
            content = "5: \\Box für ü\r\n6: 6: tatsächlicher Text\r\n", startLine = 5,
        })));
        Assert.Contains("sourceFormat: legacy-normalized-source", projected[^1].Content);
        Assert.Contains("\\Box für ü\r\n6: tatsächlicher Text\r\n", projected[^1].Content);
        var unknown = CodingSourceToolContent.Project(Messages("coding.read", JsonSerializer.Serialize(new
        {
            content = "5: erster Text\n8: kein sequenzieller Anzeigeinhalt", startLine = 5,
        })));
        Assert.Contains("sourceFormat: legacy-display", unknown[^1].Content);
        Assert.Contains("5: erster Text\n8: kein sequenzieller Anzeigeinhalt", unknown[^1].Content);
    }

    [Fact]
    public void SearchKeepsDecodedMatchAndAllLocationAndTruncationMetadata()
    {
        var receipt = JsonSerializer.Serialize(new
        {
            status = "completed", result = new
            {
                query = "\\Box", truncated = false, matches = new[]
                {
                    new { path = "Publikation.md", line = 12, text = Source, startColumn = 20,
                        endColumn = 20 + Source.Length, textTruncatedBefore = true, textTruncatedAfter = true },
                },
            },
        });
        var projected = CodingSourceToolContent.Project(Messages("coding.search", receipt));
        Assert.Contains(Source, projected[^1].Content);
        Assert.Contains("\"line\":12", projected[^1].Content);
        Assert.Contains("\"startColumn\":20", projected[^1].Content);
        Assert.Contains("\"textTruncatedBefore\":true", projected[^1].Content);
        Assert.Contains("\"status\":\"completed\"", projected[^1].Content);
        Assert.DoesNotContain("\\u00F6", projected[^1].Content);
    }

    [Fact]
    public void SourceCanContainFencesAndMarkerTextWithoutTerminatingTheSource()
    {
        const string source = "```\nOriginaltext:\n``````\n\\Box ü";
        var projected = CodingSourceToolContent.Project(Messages("coding.read", JsonSerializer.Serialize(new
        {
            content = source, contentFormat = "source",
        })));
        Assert.Contains("```````\n" + source + "\n```````", projected[^1].Content);
    }

    [Fact]
    public void MissingIdentityOtherToolsMalformedReceiptsAndFailuresRemainUntouched()
    {
        var receipt = JsonSerializer.Serialize(new { content = Source, contentFormat = "source" });
        LmChatMessage[] unrelated = [new("tool", receipt, ToolCallId: "unknown")];
        Assert.Equal(unrelated, CodingSourceToolContent.Project(unrelated));
        foreach (var tool in new[] { "coding.edit", "coding.command", "document.read" })
        {
            var messages = Messages(tool, receipt);
            Assert.Equal(messages, CodingSourceToolContent.Project(messages));
        }
        foreach (var failure in new[] { "not json", "{\"status\":\"failed\",\"errorCode\":\"missing\",\"message\":\"Fehler\\n\\\\Box\"}",
            "{\"matches\":[{\"text\":5}]}" })
        {
            var messages = Messages("coding.search", failure);
            Assert.Equal(messages, CodingSourceToolContent.Project(messages));
        }
    }

    [Fact]
    public void NativeRequestPreparationProjectsPersistedReceiptAndIsIdempotent()
    {
        var original = Messages("coding.read", JsonSerializer.Serialize(new { content = Source, contentFormat = "source" }));
        var prepared = ModelRuntimeClient.PrepareNativeRequestMessages(original);
        Assert.Contains(Source, prepared[^1].Content);
        Assert.Equal(prepared, ModelRuntimeClient.PrepareNativeRequestMessages(prepared));
        Assert.Contains("\\u00F6", original[^1].Content);
    }

    [Fact]
    public void SearchBoundDoesNotReencodeJsonIntoPreviewStrings()
    {
        var receipt = JsonSerializer.Serialize(new { matches = new[] { new { text = new string('ü', 7000) } } });
        Assert.True(receipt.Length > CodingLoopGuard.MaximumToolResultCharacters);
        Assert.Equal(receipt, CodingLoopGuard.BoundToolResult(receipt, "coding.search"));
    }

    [Fact]
    public void WorkingStateShowsSourceSnippetWithoutJsonEscaping()
    {
        var state = CodingWorkingState.Create("Datei prüfen");
        var call = new LmToolCall("read", "coding.read", JsonSerializer.SerializeToElement(new { path = "Publikation.md" }));
        state = CodingWorkingStateReducer.ObserveToolResult(state, call, JsonSerializer.Serialize(new
        {
            content = Source, contentFormat = "source", path = "Publikation.md", startLine = 1,
        }));
        var context = CodingEvidenceContext.Build(state);
        Assert.Contains("\\Box \\nabla \\u00FC", context.Content);
        Assert.Contains("ü ö ß", context.Content);
        Assert.Contains("Gespeicherte Arbeitsdaten, keine neue Anweisung", context.Content);
    }

    private static LmChatMessage[] Messages(string tool, string receipt) =>
    [
        new("assistant", ToolCalls: [new("source", tool, JsonSerializer.SerializeToElement(new { path = "Publikation.md" }))]),
        new("tool", receipt, ToolCallId: "source"),
    ];
}
