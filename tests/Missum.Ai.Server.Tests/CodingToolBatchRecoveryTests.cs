using System.Text.Json;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class CodingToolBatchRecoveryTests
{
    [Fact]
    public void IdenticalInvalidCallsHaveOneRepresentativeAndOriginalJournalReceipts()
    {
        var calls = Enumerable.Range(0, 111).Select(index =>
            new LmToolCall("invalid-" + index, "coding.edit", JsonSerializer.SerializeToElement(new { }))).ToArray();
        var batch = CodingToolBatchRecovery.Prepare(calls, new(), CodingToolCatalog.CreateTools());
        Assert.True(batch.AllInvalid);
        Assert.Same(calls[0], Assert.Single(batch.Calls));
        Assert.Equal(110, batch.RejectedDuplicates.Count);
        Assert.Equal(calls.Skip(1), batch.RejectedDuplicates.Select(item => item.Call));
        Assert.All(batch.RejectedDuplicates, item =>
        {
            Assert.Equal("invalid-0", item.RepresentativeId);
            Assert.Contains("path", item.Error, StringComparison.Ordinal);
            Assert.Equal("{}", item.Call.Arguments.GetRawText());
        });
    }

    [Fact]
    public void ValidIdenticalOperationsAndDistinctInvalidOperationsRemainInOriginalOrder()
    {
        var valid = JsonSerializer.SerializeToElement(new
        {
            path = "a.txt", expectedSha256 = new string('a', 64), oldText = "old", newText = "new",
        });
        LmToolCall[] calls = [new("first", "coding.edit", valid), new("second", "coding.edit", valid),
            new("missing-all", "coding.edit", JsonSerializer.SerializeToElement(new { })),
            new("missing-hash", "coding.edit", JsonSerializer.SerializeToElement(new { path = "a.txt" }))];
        var batch = CodingToolBatchRecovery.Prepare(calls, new AgentToolCatalog(), CodingToolCatalog.CreateTools());
        Assert.False(batch.AllInvalid);
        Assert.Empty(batch.RejectedDuplicates);
        Assert.Equal(calls, batch.Calls);
    }
}
