using System.Globalization;
using System.Text.Json;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Missum.Tests;

public sealed class NativeModelPhasePresentationTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PrefillShowsMeasuredInputProgressWithoutClaimingReasoningOrGeneration()
    {
        var phase = new NativeModelPhasePresentation().Observe("promptProcessing", 70539, 80000, .88, Start);
        Assert.True(phase.IsProcessing);
        Assert.Contains("Kontext wird verarbeitet", phase.ProcessingLabel);
        Assert.Contains($"{70539:N0} Eingangstoken", phase.ProcessingLabel);
        Assert.Contains(.88.ToString("P0", CultureInfo.CurrentCulture), phase.ProcessingLabel);
        Assert.Contains($"{70539:N0} von {80000:N0} Eingangstoken", phase.ProcessingExplanation);
        Assert.Contains("Denkprozess erscheint", phase.ProcessingExplanation);
        Assert.DoesNotContain("Denke nach", phase.ProcessingLabel);
        Assert.DoesNotContain("generiert", phase.ProcessingLabel);
    }

    [Fact]
    public void MissingCountersStayUnavailableInsteadOfInventingZeroGeneratedTokens()
    {
        var phase = new NativeModelPhasePresentation().Observe("promptProcessing", null, null, null, Start);
        Assert.Equal("Kontext wird verarbeitet", phase.ProcessingLabel);
        Assert.DoesNotContain("Token", phase.ProcessingExplanation);
        Assert.Contains("Generierung beginnt", phase.ProcessingExplanation);
        Assert.Contains(.5.ToString("P0", CultureInfo.CurrentCulture), phase.Observe("promptProcessing", 100, 200, null, Start.AddSeconds(1)).ProcessingLabel);
    }

    [Theory]
    [InlineData("codingWaiting")]
    [InlineData("reasoningDelta")]
    [InlineData("contentDelta")]
    public void MetadataHeartbeatsPreserveActualProcessingCounters(string phaseName)
    {
        var phase = new NativeModelPhasePresentation().Observe("promptProcessing", 40, 100, .4, Start);
        Assert.Same(phase, phase.Observe(phaseName, null, null, null, Start.AddSeconds(1)));
        var continued = phase.Observe("promptProcessing", 60, null, null, Start.AddSeconds(2));
        Assert.Equal(60, continued.ProcessedPromptTokens);
        Assert.Equal(100, continued.TotalPromptTokens);
        Assert.Null(continued.PromptProgress);
        Assert.Contains(.6.ToString("P0", CultureInfo.CurrentCulture), continued.ProcessingLabel);
    }

    [Fact]
    public void FreshCountersRecalculateProgressWhileExplicitMeasurementsKeepPriority()
    {
        var phase = new NativeModelPhasePresentation().Observe("promptProcessing", 40, 100, .4, Start);
        var advanced = phase.Observe("promptProcessing", 60, null, null, Start.AddSeconds(1));
        Assert.Contains(.6.ToString("P0", CultureInfo.CurrentCulture), advanced.ProcessingLabel);
        var resized = advanced.Observe("promptProcessing", null, 200, null, Start.AddSeconds(2));
        Assert.Contains(.3.ToString("P0", CultureInfo.CurrentCulture), resized.ProcessingLabel);
        var measured = resized.Observe("promptProcessing", 70, 200, .28, Start.AddSeconds(3));
        Assert.Equal(.28, measured.PromptProgress);
        Assert.Contains(.28.ToString("P0", CultureInfo.CurrentCulture), measured.ProcessingLabel);
        Assert.Equal(.28, measured.Observe("promptProcessing", null, null, null, Start.AddSeconds(4)).PromptProgress);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("", "child", true)]
    [InlineData("child", "child", true)]
    [InlineData("child", "other", false)]
    [InlineData("child", null, false)]
    public void OwnSubagentToolsBlockThinkingWithoutBlockingTheParentOrOtherAgents(string? stepOwner, string? conversationOwner, bool ownsStep)
        => Assert.Equal(ownsStep, NativeThinkingIndicatorState.IsOwnedStep(stepOwner, conversationOwner));

    [Fact]
    public async Task CoordinatorProjectsFreshMeasuredCountersWithoutRetainingAnOlderFraction()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Kontextfortschritt", ChatMode.General);
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "", MessageStatus.Streaming);
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with { ActiveSessionId = session.Id });
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        var coordinator = new AssistantCoordinator(chats, environment.Get<IDocumentIngestor>(), environment.Get<IContextAssembler>(),
            environment.Get<IPromptTriggerRepository>(), environment.Get<IAssistantAttachmentRepository>(),
            environment.Get<IChatArtifactRepository>(), environment.Get<IConversationSnapshotRepository>(), null, settings, recent);
        await coordinator.EmitMissumAiUpdateAsync(new(MissumAiAssistantUpdateKind.Status, message,
            GenerationState: "promptProcessing", ProcessedPromptTokens: 40, TotalPromptTokens: 100, PromptProgress: .4),
            static (_, _, _) => Task.CompletedTask, "measured");
        await coordinator.EmitMissumAiUpdateAsync(new(MissumAiAssistantUpdateKind.Status, message,
            GenerationState: "promptProcessing", ProcessedPromptTokens: 60), static (_, _, _) => Task.CompletedTask, "advanced");
        var snapshot = JsonSerializer.SerializeToElement(await coordinator.BuildSnapshotAsync(), JsonSerializerOptions.Web);
        Assert.Equal(60, snapshot.GetProperty("processedPromptTokens").GetInt32());
        Assert.Equal(100, snapshot.GetProperty("totalPromptTokens").GetInt32());
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("promptProgress").ValueKind);
        await coordinator.EmitMissumAiUpdateAsync(new(MissumAiAssistantUpdateKind.Status, message,
            GenerationState: "promptProcessing", ProcessedPromptTokens: 70, PromptProgress: .68),
            static (_, _, _) => Task.CompletedTask, "explicit");
        snapshot = JsonSerializer.SerializeToElement(await coordinator.BuildSnapshotAsync(), JsonSerializerOptions.Web);
        Assert.Equal(.68, snapshot.GetProperty("promptProgress").GetDouble());
    }

    [Fact]
    public void OlderSnapshotsAndHistoricalReasoningCannotRollBackTheCurrentPhase()
    {
        var phase = new NativeModelPhasePresentation().Observe("promptProcessing", 50, 100, .5, Start.AddSeconds(2));
        Assert.Same(phase, phase.Observe("tokenProgress", null, null, null, Start));
        Assert.Same(phase, phase.ObserveReasoning(Start));
        var reasoning = phase.ObserveReasoning(Start.AddSeconds(3));
        Assert.False(reasoning.IsProcessing);
        Assert.Same(reasoning, reasoning.Observe("promptProcessing", 50, 100, .5, Start.AddSeconds(2)));
    }

    [Fact]
    public void RealReasoningAndVisibleAnswerEndProcessingWithoutRequiringTokenPulses()
    {
        var phase = new NativeModelPhasePresentation().Observe("promptProcessing", 100, 100, 1, Start);
        Assert.False(phase.ObserveReasoning(Start.AddSeconds(1)).IsProcessing);
        Assert.False(phase.ObserveContent(Start.AddSeconds(1)).IsProcessing);
        Assert.False(phase.Observe("tokenProgress", null, null, null, Start.AddSeconds(1)).IsProcessing);
        var restarted = phase.Observe("generationStarted", null, null, null, Start.AddSeconds(1));
        Assert.Null(restarted.ProcessedPromptTokens);
        Assert.Null(restarted.TotalPromptTokens);
        Assert.Null(restarted.PromptProgress);
    }

    [Fact]
    public void HistoricalBaselineAndWhitespaceAreNotNewVisibleModelOutput()
    {
        var state = new NativeThinkingIndicatorState();
        state.ObserveProgress("answer", "promptProcessing", 0, Start);
        Assert.False(state.ObserveContent("answer", "Bereits vorhandener Text", Start));
        Assert.False(state.ObserveContent("answer", "Bereits vorhandener Text\n\n", Start.AddSeconds(1)));
        Assert.True(state.ObserveContent("answer", "Bereits vorhandener Text\nNeues Ergebnis", Start.AddSeconds(2)));
    }

    [Fact]
    public void InvalidMeasurementsDoNotCreateInvalidPercentagesOrNegativeCounts()
    {
        var phase = new NativeModelPhasePresentation().Observe("promptProcessing", -1, -5, double.NaN, Start);
        Assert.Null(phase.ProcessedPromptTokens);
        Assert.Null(phase.TotalPromptTokens);
        Assert.Null(phase.PromptProgress);
        Assert.Equal("Kontext wird verarbeitet", phase.ProcessingLabel);
    }
}
