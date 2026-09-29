using Missum.App.Services;

namespace Missum.Tests;

public sealed class MediaCaptureLifecycleTests
{
    [Fact]
    public async Task SystemAudioLimitRunsFinalizerAtExactBoundaryOnly()
    {
        var completionCount = 0;

        var belowLimit = await SystemAudioAnalysisCaptureService.CompleteWhenLimitReachedAsync(
            elapsedSeconds: 599,
            maximumSeconds: 600,
            () =>
            {
                completionCount++;
                return Task.CompletedTask;
            });
        var atLimit = await SystemAudioAnalysisCaptureService.CompleteWhenLimitReachedAsync(
            elapsedSeconds: 600,
            maximumSeconds: 600,
            () =>
            {
                completionCount++;
                return Task.CompletedTask;
            });

        Assert.False(belowLimit);
        Assert.True(atLimit);
        Assert.Equal(1, completionCount);
    }

    [Fact]
    public async Task RetiredScreenClipCannotOverwriteNewCaptureState()
    {
        var generations = new ScreenClipCaptureService.CaptureGenerationState();
        var oldGeneration = generations.Begin();
        var state = "initial";
        var allowOldUpdate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldUpdate = Task.Run(async () =>
        {
            await allowOldUpdate.Task;
            return generations.TryApply(oldGeneration, () => state = "alt");
        });

        Assert.True(generations.Retire(oldGeneration));
        var newGeneration = generations.Begin();
        Assert.True(generations.TryApply(newGeneration, () => state = "neu"));

        allowOldUpdate.SetResult(true);

        Assert.False(await oldUpdate);
        Assert.Equal("neu", state);
    }
}
