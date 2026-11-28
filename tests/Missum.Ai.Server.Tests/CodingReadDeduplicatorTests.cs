using System.Text.Json;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Tests;

public sealed class CodingReadDeduplicatorTests
{
    private static readonly string Source = new('a', 3000);

    private static LmToolCall Read(string id, string path, int? start = null) =>
        new(id, "coding.read", JsonSerializer.SerializeToElement(start is null ? (object)new { path } : new { path, startLine = start }));

    private static string Result(string proposal, string content, string path = "src/a.cs") =>
        JsonSerializer.Serialize(new { proposalId = proposal, status = "completed",
            result = new { path, totalLines = 10, contentFormat = "source", content } });

    private static List<LmChatMessage> History(LmToolCall call, string result) =>
    [
        new("user", "Lies a.cs"),
        new("assistant", null, [call]),
        new("tool", result, ToolCallId: call.Id),
    ];

    [Fact]
    public void AnIdenticalRepeatBecomesAReferenceDespiteADifferentProposalId()
    {
        var messages = History(Read("c1", "src/a.cs"), Result("proposal-1", Source));
        var repeat = CodingReadDeduplicator.Apply(messages, Read("c2", "src/a.cs"), Result("proposal-2", Source));
        using var document = JsonDocument.Parse(repeat);
        Assert.Equal("unchanged", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("c1", document.RootElement.GetProperty("sameAsCallId").GetString());
        Assert.True(repeat.Length < 500);
    }

    [Fact]
    public void ChangedContentAnotherRangeOrAShortFileIsReturnedInFull()
    {
        var messages = History(Read("c1", "src/a.cs"), Result("proposal-1", Source));
        var changed = Result("proposal-2", Source + "b");
        Assert.Equal(changed, CodingReadDeduplicator.Apply(messages, Read("c2", "src/a.cs"), changed));
        var otherRange = Result("proposal-3", Source);
        Assert.Equal(otherRange, CodingReadDeduplicator.Apply(messages, Read("c3", "src/a.cs", start: 5), otherRange));
        var shortMessages = History(Read("c1", "src/a.cs"), Result("proposal-1", "kurz"));
        var shortRepeat = Result("proposal-4", "kurz");
        Assert.Equal(shortRepeat, CodingReadDeduplicator.Apply(shortMessages, Read("c4", "src/a.cs"), shortRepeat));
    }

    [Fact]
    public void AnEarlierCopyRewrittenByCompactionIsNotReferenced()
    {
        var messages = History(Read("c1", "src/a.cs"), "{\"status\":\"compacted\",\"summary\":\"a.cs gelesen\"}");
        var repeat = Result("proposal-2", Source);
        Assert.Equal(repeat, CodingReadDeduplicator.Apply(messages, Read("c2", "src/a.cs"), repeat));
    }
}
