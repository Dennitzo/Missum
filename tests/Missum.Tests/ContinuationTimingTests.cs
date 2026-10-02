using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class ContinuationTimingTests
{
    private static readonly Guid SessionId = Guid.NewGuid();
    private static readonly Guid MessageId = Guid.NewGuid();
    private static readonly DateTimeOffset Created = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StoppedInitialAttemptUsesEarlierPersistedRunEnd()
    {
        var message = Message(Created.AddMinutes(2));
        Assert.Equal(90_000, MissumAiAssistantService.ContinuationActiveMilliseconds(message, Run(Created.AddSeconds(90))));
        Assert.Equal(120_000, MissumAiAssistantService.ContinuationActiveMilliseconds(message, null));
    }

    [Fact]
    public void ContinuedAttemptRetainsPriorWorkAndExcludesOneHourPause()
    {
        var start = Created.AddHours(1);
        var message = Message(start.AddSeconds(30), Receipt(start, 60_000));
        Assert.Equal(90_000, MissumAiAssistantService.ContinuationActiveMilliseconds(message, null));
        Assert.Equal(80_000, MissumAiAssistantService.ContinuationActiveMilliseconds(message, Run(start.AddSeconds(20))));
    }

    [Fact]
    public void LatestValidReceiptWinsRegardlessOfStoredOrder()
    {
        var first = Receipt(Created.AddHours(1), 60_000);
        var latest = Receipt(Created.AddHours(2), 125_000);
        var message = Message(Created.AddHours(2).AddSeconds(20), latest, first);
        Assert.Equal(145_000, MissumAiAssistantService.ContinuationActiveMilliseconds(message, null));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"priorActiveMilliseconds\":-1}")]
    [InlineData("{\"sessionId\":1,\"messageId\":true}")]
    public void MalformedReceiptsCannotResetOrBreakDuration(string output)
    {
        var bad = Receipt(Created.AddHours(1), 1) with { OutputJson = output };
        Assert.Equal(120_000, MissumAiAssistantService.ContinuationActiveMilliseconds(Message(Created.AddMinutes(2), bad), null));
    }

    [Fact]
    public void ForeignReceiptAndRunCannotChangeOwningMessageDuration()
    {
        var foreign = Receipt(Created.AddSeconds(80), 1, Guid.NewGuid());
        var message = Message(Created.AddMinutes(2), foreign);
        var run = Run(Created.AddSeconds(10)) with { SessionId = Guid.NewGuid() };
        Assert.Equal(120_000, MissumAiAssistantService.ContinuationActiveMilliseconds(message, run));
    }

    [Fact]
    public void PreparationReceiptsKeepPauseExcludedAfterCreateFailure()
    {
        var preparation = Receipt(Created.AddHours(1), 60_000) with { Status = "running" };
        var message = Message(Created.AddHours(1).AddSeconds(5), preparation) with { Error = "create failed" };
        Assert.Equal(65_000, MissumAiAssistantService.ContinuationActiveMilliseconds(message, null));
    }

    [Fact]
    public void ReattachmentKeepsExistingAttemptTimer()
    {
        var receipt = Receipt(Created.AddHours(1), 60_000);
        var message = Message(Created.AddHours(1).AddMinutes(2), receipt) with { Status = MessageStatus.Streaming };
        Assert.Equal(180_000, MissumAiAssistantService.ContinuationActiveMilliseconds(message, null));
    }

    [Fact]
    public void NegativeIntervalsClampToZeroAndAccumulationCannotOverflow()
    {
        Assert.Equal(0, MissumAiAssistantService.ContinuationActiveMilliseconds(Message(Created.AddSeconds(-1)), null));
        var maximum = TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond;
        var message = Message(Created.AddSeconds(5), Receipt(Created, maximum));
        Assert.Equal(maximum, MissumAiAssistantService.ContinuationActiveMilliseconds(message, null));
        var invalid = Receipt(Created, long.MaxValue);
        Assert.Equal(5_000, MissumAiAssistantService.ContinuationActiveMilliseconds(Message(Created.AddSeconds(5), invalid), null));
    }

    [Fact]
    public void OlderOrUnacceptedReceiptsAreIgnored()
    {
        var older = Receipt(Created.AddSeconds(-5), 80_000);
        var cancelled = Receipt(Created.AddSeconds(60), 80_000) with { Status = "cancelled" };
        Assert.Equal(120_000, MissumAiAssistantService.ContinuationActiveMilliseconds(Message(Created.AddMinutes(2), older, cancelled), null));
    }

    private static ChatMessage Message(DateTimeOffset updated, params AssistantToolStep[] steps) =>
        new(MessageId, SessionId, ChatRole.Assistant, "Teilantwort", MessageStatus.Cancelled, Created, updated, ToolSteps: steps);

    private static MissumAiRunRecord Run(DateTimeOffset updated) =>
        new(Guid.NewGuid(), SessionId, MessageId, null, "timing", "run-timing", 1, "cancelled", null, null, Created, updated);

    private static AssistantToolStep Receipt(DateTimeOffset started, long prior, Guid? owner = null) =>
        new("continuation:timing", "assistant.continuation", "completed", OutputJson: JsonSerializer.Serialize(new
        {
            sessionId = owner ?? SessionId, messageId = MessageId, localRunId = Guid.NewGuid(), priorActiveMilliseconds = prior,
        }), StartedAt: started, UpdatedAt: started);
}
