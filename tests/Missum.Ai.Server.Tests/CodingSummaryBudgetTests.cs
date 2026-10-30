using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class CodingSummaryBudgetTests
{
    private static CodingCompactionPlan CreatePlan(int contextLength = 32768, string? goal = null)
    {
        LmChatMessage[] messages = [new("system", "Trusted preserved main policy."), new("user", "Earlier task"),
            new("assistant", "ARCHIVE_BEGIN\n" + new string('x', 300_000) + "\nARCHIVE_END"),
            new("user", goal ?? "Original exact scientific goal: verify rho = 0 without weakening assumptions."),
            new("assistant", "Most recent answer stays in the model context.")];
        var state = CodingWorkingState.Create("Exact persisted goal and outstanding checks.") with { NextStep = "Do the outstanding check." };
        return CodingContextCompactor.Plan(messages, contextLength, state, force: true)!;
    }

    [Fact]
    public async Task ExactNativeCountReboundsOnlySummaryArchiveAndReservesOutputBeforeGeneration()
    {
        var plan = CreatePlan();
        var original = plan.SummaryRequest.ToArray();
        var calls = new List<IReadOnlyList<LmChatMessage>>();
        var generated = 0;
        var result = await CodingContextCompactor.CompleteSummaryAsync(plan, 8192, 4096,
            (request, output, _) =>
            {
                calls.Add(request);
                var exactInput = ContextPlanner.EstimateTokens(request) * 2;
                CodingContextCompactor.ValidateSummaryTokenBudget(new("tokenCounting", PromptTokens: exactInput), 8192, output);
                Assert.True(exactInput + output + 1 <= 8192);
                generated++;
                return Task.FromResult(new LmChatResult("Summary from the accepted request.", [], exactInput, 30));
            }, CancellationToken.None);

        Assert.Equal("Summary from the accepted request.", result.Content);
        Assert.InRange(calls.Count, 2, CodingContextCompactor.MaximumSummaryBudgetRetries + 1);
        Assert.Equal(1, generated);
        Assert.All(calls, request =>
        {
            Assert.Same(original[0], request[0]);
            Assert.Contains(plan.CurrentRequest.Content!, request[1].Content, StringComparison.Ordinal);
            Assert.Contains("ARCHIVE_BEGIN", request[1].Content, StringComparison.Ordinal);
            Assert.Contains("ARCHIVE_END", request[1].Content, StringComparison.Ordinal);
            Assert.Contains("gekürzt", request[1].Content, StringComparison.Ordinal);
            Assert.Same(original[2], request[2]);
        });
        Assert.True(calls[^1][1].Content!.Length < calls[0][1].Content!.Length);
        Assert.Equal(original, plan.SummaryRequest);
        var completed = CodingContextCompactor.Complete(plan, result.Content);
        Assert.Contains(plan.CurrentRequest, completed);
        Assert.Equal(plan.RecentMessages[^1], completed[^1]);
    }

    [Fact]
    public async Task UncompressibleGoalIsNotShortenedOrSentBeyondTheLoadedContext()
    {
        var goal = new string('g', 90_000);
        var plan = CreatePlan(goal: goal);
        var called = false;
        await Assert.ThrowsAsync<ContextBudgetException>(() => CodingContextCompactor.CompleteSummaryAsync(plan, 8192, 4096,
            (_, _, _) => { called = true; return Task.FromResult(new LmChatResult("Wrong.", [], 0, 0)); }, CancellationToken.None));
        Assert.False(called);
        Assert.Equal(goal, plan.CurrentRequest.Content);
        Assert.Contains(goal, plan.SummaryRequest[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationStopsCalibratedRetryAndTransportFailuresDoNotRepeatInference()
    {
        var plan = CreatePlan();
        using var stop = new CancellationTokenSource();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CodingContextCompactor.CompleteSummaryAsync(plan, 8192, 4096,
            (_, _, _) =>
            {
                calls++; stop.Cancel();
                throw new NativeContextBudgetException(9000, 8192);
            }, stop.Token));
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<HttpRequestException>(() => CodingContextCompactor.CompleteSummaryAsync(plan, 8192, 4096,
            (_, _, _) => { calls++; throw new HttpRequestException("Native transport unavailable."); }, CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void SummaryTokenGuardUsesOnlyExactPreflightAndKeepsSpaceForTheRequestedSummary()
    {
        CodingContextCompactor.ValidateSummaryTokenBudget(new("generationStarted"), 8192, 4096);
        CodingContextCompactor.ValidateSummaryTokenBudget(new("tokenProgress", PromptTokens: 8000), 8192, 4096);
        CodingContextCompactor.ValidateSummaryTokenBudget(new("tokenCounting", PromptTokens: 4095), 8192, 4096);
        var error = Assert.Throws<NativeContextBudgetException>(() =>
            CodingContextCompactor.ValidateSummaryTokenBudget(new("tokenCounting", PromptTokens: 4096), 8192, 4096));
        Assert.Equal(4095, error.BudgetTokens);
    }
}
