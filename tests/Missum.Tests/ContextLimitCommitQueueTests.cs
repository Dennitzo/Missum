using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class ContextLimitCommitQueueTests
{
    [Theory]
    [InlineData(1_048_576, 262_144)]
    [InlineData(262_144, 1_048_576)]
    public async Task PromptWaitsForOrderedContextWritesBeforeFreezingGatewayLimit(int first, int final)
    {
        const string model = "coding/Qwen3.8-Flash-Next~test";
        var queue = new ContextLimitCommitQueue();
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = new AppSettings { ContextLimitsByModel = new() { [model] = final } };
        _ = queue.Enqueue(async () =>
        {
            await releaseFirst.Task;
            settings = settings with { ContextLimitsByModel = new() { [model] = first } };
            return true;
        });
        _ = queue.Enqueue(() =>
        {
            settings = settings with { ContextLimitsByModel = new() { [model] = final } };
            return Task.FromResult(true);
        });
        async Task<RunLimits> FreezeLimitsAsync()
        {
            Assert.True(await queue.Pending);
            using var frozen = AssistantClientExecutionScope.EnterFrozen(settings);
            var submitted = AssistantClientExecutionScope.Resolve(new AppSettings());
            var selected = ModelContextSelection.Resolve(submitted, model, 1_048_576);
            return MissumAiAssistantService.CreateChatRunLimits(selected);
        }
        var capture = FreezeLimitsAsync();
        Assert.False(capture.IsCompleted);
        releaseFirst.SetResult();
        var limits = await capture;
        Assert.Equal(final, limits.MaximumContextTokens);
        Assert.Equal(final, settings.ContextLimitsByModel[model]);
    }

    [Fact]
    public async Task NewCommitCanRecoverFromAnEarlierFailedWrite()
    {
        var queue = new ContextLimitCommitQueue();
        Assert.False(await queue.Enqueue(() => Task.FromResult(false)));
        Assert.True(await queue.Enqueue(() => Task.FromResult(true)));
        Assert.True(await queue.Pending);
    }
}
