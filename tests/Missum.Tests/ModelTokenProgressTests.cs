using Missum.Ai.Contracts;
using Missum.App.Services;

namespace Missum.Tests;

public sealed class ModelTokenProgressTests
{
    [Fact]
    public void PartialPrefillDoesNotPromoteAnEstimateToMeasuredContext()
    {
        var counter = new MissumAiAssistantService.ModelTokenProgressState { SessionContextTokens = 5000 };
        _ = MissumAiAssistantService.UpdateSessionTokenProgress(new("promptProcessing", ProcessedPromptTokens: 100), counter);
        Assert.False(counter.HasMeasuredContext);
        _ = MissumAiAssistantService.UpdateSessionTokenProgress(new("promptProcessing", PromptTokens: 5100, ProcessedPromptTokens: 100), counter);
        Assert.True(counter.HasMeasuredContext);
        _ = MissumAiAssistantService.UpdateSessionTokenProgress(new("tokenProgress", GeneratedTokens: 20), counter);
        Assert.Equal(5120, counter.VisibleContextTokens);
        Assert.True(counter.HasMeasuredContext);
    }

    [Fact]
    public void SessionDisplayIncludesHistoryAndGenerationAcrossToolRoundsAndCompaction()
    {
        var counter = new MissumAiAssistantService.ModelTokenProgressState { SessionContextTokens = 5000 };
        Assert.Equal($"{5000:N0} Token", MissumAiAssistantService.UpdateSessionTokenProgress(new("generationStarted"), counter));
        Assert.Equal($"{5100:N0} Token", MissumAiAssistantService.UpdateSessionTokenProgress(new("tokenProgress", GeneratedTokens: 100), counter));
        Assert.Equal(5100, counter.VisibleContextTokens);
        Assert.Equal($"{5100:N0} Token", MissumAiAssistantService.UpdateSessionTokenProgress(new("generationStarted"), counter));
        Assert.Equal($"{5300:N0} Token", MissumAiAssistantService.UpdateSessionTokenProgress(new("promptProcessing", PromptTokens: 5300, ProcessedPromptTokens: 100), counter));
        Assert.Equal($"{5320:N0} Token", MissumAiAssistantService.UpdateSessionTokenProgress(new("tokenProgress", GeneratedTokens: 20), counter));
        _ = MissumAiAssistantService.UpdateSessionTokenProgress(new("generationStarted"), counter);
        Assert.Equal($"{2000:N0} Token", MissumAiAssistantService.UpdateSessionTokenProgress(new("promptProcessing", PromptTokens: 2000, ProcessedPromptTokens: 2000), counter));
    }

    [Fact]
    public void ContextMaximumUsesDeepSeekMetadataAndDiscoveredUnknownModels()
    {
        Assert.Equal(1048576, ModelContextProfiles.ResolveMaximum("coding/DeepSeek-V4-Flash-Vision-Exp-UD-IQ1_S~31b467a216d0", "general"));
        var id = "test/discovered-" + Guid.NewGuid();
        ModelContextProfiles.RegisterMaximum(id, 524288);
        Assert.Equal(524288, ModelContextProfiles.ResolveMaximum(id, "general"));
        ModelContextProfiles.RegisterMaximum(id, 0);
        Assert.Equal(524288, ModelContextProfiles.ResolveMaximum(id, "general"));
    }

    [Fact]
    public void RejectedToolArgumentsShowTheirCauseInsteadOfPreparedOrTokenProgress()
    {
        var counter = new MissumAiAssistantService.ModelTokenProgressState();
        var detail = MissumAiAssistantService.FormatModelTokenProgress(new("toolRejected",
            ToolName: "coding.write", FailureKind: "agent.invalid_tool_call",
            Message: "content exceeds its maximum length"), counter);
        Assert.Contains("coding.write abgewiesen", detail);
        Assert.Contains("content exceeds its maximum length", detail);
        Assert.Contains("Keine Ausführung", detail);
        Assert.DoesNotContain("vorbereitet", detail);
    }

    [Fact]
    public void NativeProgressShowsCombinedTotalAndRetainsInternalCacheCounters()
    {
        var counter = new MissumAiAssistantService.ModelTokenProgressState();
        var first = MissumAiAssistantService.FormatModelTokenProgress(new("promptProcessing",
            PromptProgress: 0.94, PromptTokens: 9_262, ProcessedPromptTokens: 8_746,
            CachedPromptTokens: 8_622), counter);

        Assert.Contains($"{8_746:N0} Token", first);
        Assert.DoesNotContain("wiederverwendet", first);
        Assert.Equal(8_622, counter.CachedPromptTokens);
        Assert.DoesNotContain("Kontext wird verarbeitet", first);
        Assert.Equal(8_746, counter.ActiveTokens);
        var waiting = MissumAiAssistantService.FormatModelTokenProgress(new("codingWaiting",
            Attempt: 1, ElapsedSeconds: 2), counter);
        Assert.Contains($"{8_746:N0} Token", waiting);

        // The final usage frame is authoritative; generation keeps the known cache split.
        var final = MissumAiAssistantService.FormatModelTokenProgress(new("tokenProgress",
            PromptTokens: 9_262, ProcessedPromptTokens: 9_262, GeneratedTokens: 51,
            CurrentTokens: 9_313, CachedPromptTokens: 8_622), counter);
        Assert.Equal($"{9_313:N0} Token", final);
        Assert.Equal(9_313, counter.ActiveTokens);

        _ = MissumAiAssistantService.FormatModelTokenProgress(new("generationStarted"), counter);
        Assert.Null(counter.CachedPromptTokens);
    }

    [Fact]
    public void UnknownNativeCacheCountDoesNotClaimThatWholePromptWasReprocessed()
    {
        var counter = new MissumAiAssistantService.ModelTokenProgressState();
        var detail = MissumAiAssistantService.FormatModelTokenProgress(new("promptProcessing",
            PromptTokens: 9_262, ProcessedPromptTokens: 8_746), counter);
        Assert.Equal($"Kontext bereit · {8_746:N0} Token", detail);
        Assert.Null(counter.CachedPromptTokens);
        Assert.DoesNotContain("neu verarbeitet", detail);

        var cold = MissumAiAssistantService.FormatModelTokenProgress(new("promptProcessing",
            PromptTokens: 9_262, ProcessedPromptTokens: 8_746, CachedPromptTokens: 0), counter);
        Assert.Equal(0, counter.CachedPromptTokens);
        Assert.Contains($"{8_746:N0} Token", cold);
    }

    [Fact]
    public void EmptyResponseRecoveryIsExplainedWithoutRestartingTheCodingPrompt()
    {
        var counter = new MissumAiAssistantService.ModelTokenProgressState();
        var detail = MissumAiAssistantService.FormatModelTokenProgress(
            new("responseRecovery", Attempt: 1, FailureKind: "reasoning_only_response"), counter);
        Assert.Contains("Arbeitsstand", detail, StringComparison.Ordinal);
        Assert.True(MissumAiAssistantService.IsRetryableServerErrorCode("provider.empty_response"));
        Assert.False(MissumAiAssistantService.ShouldRetryCurrentPrompt(
            Missum.Core.Models.PromptTriggerAction.Coding,
            new MissumAiRunTerminalException("provider.empty_response", "Keine ausführbare Antwort", true)));
    }

    [Fact]
    public void NativeFragmentsAdvanceImmediatelyAfterLargePromptProcessing()
    {
        var counter = new MissumAiAssistantService.ModelTokenProgressState();
        _ = MissumAiAssistantService.FormatModelTokenProgress(new("generationStarted"), counter);
        _ = MissumAiAssistantService.FormatModelTokenProgress(new("promptProcessing",
            PromptProgress: 1, PromptTokens: 5_544, ProcessedPromptTokens: 5_544), counter);

        var first = MissumAiAssistantService.FormatModelTokenProgress(new("tokenProgress",
            GeneratedTokens: 8, CurrentTokens: 8), counter);
        Assert.Equal($"{5_552:N0} Token", first);
        Assert.Equal(5_552, counter.ActiveTokens);
        var later = MissumAiAssistantService.FormatModelTokenProgress(new("tokenProgress",
            GeneratedTokens: 2_300, CurrentTokens: 2_300), counter);
        Assert.Equal($"{7_844:N0} Token", later);
        Assert.Equal(7_844, counter.ActiveTokens);
        Assert.NotEqual(first, later);
        Assert.Contains(later!, MissumAiAssistantService.FormatModelTokenProgress(
            new("codingWaiting", Attempt: 2, ElapsedSeconds: 150), counter)!, StringComparison.Ordinal);

        // Final native usage includes the prompt; it must not be added a second time.
        var usage = MissumAiAssistantService.FormatModelTokenProgress(new("tokenProgress",
            PromptTokens: 5_544, ProcessedPromptTokens: 5_544, GeneratedTokens: 2_298, CurrentTokens: 7_842), counter);
        Assert.Equal($"{7_842:N0} Token", usage);
        Assert.Equal(7_842, counter.ActiveTokens);
    }

    [Theory]
    [InlineData("generationStarted")]
    [InlineData("generationRetry")]
    public void ANewModelAttemptResetsBothPromptAndGeneratedCounts(string state)
    {
        var counter = new MissumAiAssistantService.ModelTokenProgressState();
        _ = MissumAiAssistantService.FormatModelTokenProgress(new("tokenProgress",
            ProcessedPromptTokens: 5_544, GeneratedTokens: 2_300, CurrentTokens: 7_844), counter);
        Assert.Equal("0 Token", MissumAiAssistantService.FormatModelTokenProgress(new(state), counter));
        Assert.Equal(0, counter.ProcessedPromptTokens);
        Assert.Equal(0, counter.GeneratedTokens);
        Assert.Equal(0, counter.ActiveTokens);

        _ = MissumAiAssistantService.FormatModelTokenProgress(new("promptProcessing",
            ProcessedPromptTokens: 300), counter);
        var detail = MissumAiAssistantService.FormatModelTokenProgress(new("tokenProgress",
            GeneratedTokens: 8, CurrentTokens: 8), counter);
        Assert.Equal("308 Token", detail);
        Assert.Equal(308, counter.ActiveTokens);
    }

    [Fact]
    public void ToolCallCompletionWithoutUsageRetainsTheKnownPromptCount()
    {
        var counter = new MissumAiAssistantService.ModelTokenProgressState();
        _ = MissumAiAssistantService.FormatModelTokenProgress(new("promptProcessing",
            PromptTokens: 5_544, ProcessedPromptTokens: 5_544), counter);
        _ = MissumAiAssistantService.FormatModelTokenProgress(new("tokenProgress",
            GeneratedTokens: 2_300, CurrentTokens: 2_300), counter);

        var detail = MissumAiAssistantService.FormatModelTokenProgress(new("tokenProgress",
            PromptTokens: 0, ProcessedPromptTokens: 0, GeneratedTokens: 2_308, CurrentTokens: 2_308), counter);
        Assert.Equal($"{7_852:N0} Token", detail);
        Assert.Equal(5_544, counter.ProcessedPromptTokens);
        Assert.Equal(7_852, counter.ActiveTokens);
    }

    [Fact]
    public void GeneratedOnlyAndLegacyAggregateEventsDoNotInventPromptCounts()
    {
        var counter = new MissumAiAssistantService.ModelTokenProgressState();
        Assert.Null(MissumAiAssistantService.FormatModelTokenProgress(new("toolSelected"), counter));
        var generated = MissumAiAssistantService.FormatModelTokenProgress(new("tokenProgress",
            GeneratedTokens: 12, CurrentTokens: 12), counter);
        Assert.Equal("12 Token", generated);
        Assert.Equal(0, counter.ProcessedPromptTokens);

        _ = MissumAiAssistantService.FormatModelTokenProgress(new("generationStarted"), counter);
        _ = MissumAiAssistantService.FormatModelTokenProgress(new("promptProcessing", ProcessedPromptTokens: 100), counter);
        var legacy = MissumAiAssistantService.FormatModelTokenProgress(new ModelGenerationEvent("tokenProgress",
            CurrentTokens: 112), counter);
        Assert.Equal("112 Token", legacy);
        Assert.Equal(112, counter.ActiveTokens);
    }
}
