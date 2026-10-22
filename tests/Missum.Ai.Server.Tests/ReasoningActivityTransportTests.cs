using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class ReasoningActivityTransportTests
{
    [Fact]
    public void ThousandsOfPrivateFragmentsProduceOnlyOnePhaseStartAndNoTextPayload()
    {
        var transport = new ReasoningActivityTransport();
        var first = transport.Observe(new("reasoningDelta", ReasoningDelta: "Private thought"));
        Assert.Equal("reasoningStarted", first!.State);
        for (var index = 0; index < 4096; index++)
            Assert.Null(transport.Observe(new("reasoningDelta", ReasoningDelta: "more thought")));
        Assert.Null(transport.Observe(new("tokenProgress", GeneratedTokens: 4097)));
        Assert.Equal("reasoningCompleted", transport.Complete()!.State);
        Assert.Null(transport.Complete());
        Assert.Null(first.Message);
    }

    [Theory]
    [InlineData("contentDelta")]
    [InlineData("toolCallGeneration")]
    [InlineData("generationRetry")]
    public void ANewReasoningPhaseAfterVisibleOutputOrRetryIsReportedAgain(string state)
    {
        var transport = new ReasoningActivityTransport();
        Assert.Equal("reasoningStarted", transport.Observe(new("reasoningDelta", ReasoningDelta: "first"))!.State);
        Assert.Equal("reasoningCompleted", transport.Observe(new(state, ContentDelta: state == "contentDelta" ? "Visible." : null))!.State);
        Assert.Equal("reasoningStarted", transport.Observe(new("reasoningDelta", ReasoningDelta: "second"))!.State);
        Assert.Equal("steered", transport.Complete("steered")!.FailureKind);
    }

    [Fact]
    public void EmptyFragmentsAndHeartbeatDoNotInventAnActivityPhase()
    {
        var transport = new ReasoningActivityTransport();
        Assert.Null(transport.Observe(new("reasoningDelta", ReasoningDelta: "")));
        Assert.Null(transport.Observe(new("tokenProgress", GeneratedTokens: 1)));
        Assert.Null(transport.Complete());
    }
}
