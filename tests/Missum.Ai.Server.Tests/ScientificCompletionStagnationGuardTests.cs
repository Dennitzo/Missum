using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using System.Globalization;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class ScientificCompletionStagnationGuardTests
{
    private static readonly string[] MissingGoals = ["Der Vergleich am Detektor erfüllt die festgelegte Fehlerschranke nicht."];
    private static readonly string[] ChangedSectionIds = ["detector-result"];

    [Fact]
    public void DifferentFinalWordingCannotResetAnUnchangedUnsupportedCompletion()
    {
        var messages = new List<LmChatMessage> { new("system", "Stable prefix."), new("user", "Quantitativ prüfen.") };
        var progress = new ScientificStateRunProgress();
        for (var attempt = 1; attempt <= ScientificCompletionStagnationGuard.MaximumUnsupportedAttempts; attempt++)
        {
            messages.Add(new("assistant", "Andere Abschlussbehauptung " + attempt.ToString(CultureInfo.InvariantCulture)));
            progress = ScientificCompletionStagnationGuard.Observe(progress, Assessment(), messages);
            Assert.Equal(attempt, progress.PrematureCompletionAttempts);
            if (attempt < ScientificCompletionStagnationGuard.MaximumUnsupportedAttempts)
                ScientificCompletionStagnationGuard.ThrowIfStalled(progress);
        }
        Assert.Single(messages, message => message.Content?.StartsWith(ScientificCompletionStagnationGuard.Marker, StringComparison.Ordinal) == true);
        var blocked = Assert.Throws<ScientificResearchStalledException>(() =>
            ScientificCompletionStagnationGuard.ThrowIfStalled(progress));
        Assert.False(blocked.PrerequisiteBlocked);
        Assert.Contains("offen", blocked.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckpointAndNativeNormalizationPreserveCounterWithoutDuplicatingTheMethodChangeHint()
    {
        var messages = new List<LmChatMessage> { new("system", "Stable prefix."), new("user", "Forschungsauftrag.") };
        var progress = ScientificCompletionStagnationGuard.Observe(new(), Assessment(), messages);
        progress = ScientificCompletionStagnationGuard.Observe(progress, Assessment(), messages);
        var checkpoint = new AgentRunCheckpoint(messages, 50, 100, 10000, 3000, ScientificStateProgress: progress);
        var restored = JsonSerializer.Deserialize<AgentRunCheckpoint>(JsonSerializer.Serialize(checkpoint))!;
        var normalized = ModelRuntimeClient.NormalizeMessageOrderForNativeRuntime(restored.Messages).ToList();
        progress = ScientificCompletionStagnationGuard.Observe(restored.ScientificStateProgress!, Assessment(), normalized);
        Assert.Equal(3, progress.PrematureCompletionAttempts);
        Assert.True(progress.CompletionMethodChangeHinted);
        var guide = Assert.Single(normalized, message => message.Content?.Contains(ScientificCompletionStagnationGuard.Marker, StringComparison.Ordinal) == true);
        Assert.Equal("user", guide.Role);
        Assert.Contains("Gedankenexperiment", guide.Content, StringComparison.Ordinal);
        Assert.Contains("nextCheck", guide.Content, StringComparison.Ordinal);
        Assert.Contains("Keine Toleranzen lockern", guide.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredCurrentVerificationDoesNotConsumeACompletionAttempt()
    {
        var progress = FiveAttempts();
        var messages = new List<LmChatMessage>();
        var next = ScientificCompletionStagnationGuard.Observe(progress, Assessment() with { NeedsVerification = true }, messages);
        Assert.Same(progress, next);
        Assert.Equal(5, next.PrematureCompletionAttempts);
        Assert.Empty(messages);
    }

    [Fact]
    public void AChangedScientificDiagnosisStartsItsOwnStreak()
    {
        var progress = ScientificCompletionStagnationGuard.Observe(FiveAttempts(),
            Assessment() with { Fingerprint = new string('b', 64) }, []);
        Assert.Equal(1, progress.PrematureCompletionAttempts);
        Assert.False(progress.CompletionMethodChangeHinted);
    }

    [Fact]
    public void AnAcceptedCanonicalStateChangeResetsTheStreak()
    {
        var result = Receipt(new { success = true, protocol = "section-delta-v1", projectId = "science-project",
            revision = 4, publicationRevision = 3, changedIds = ChangedSectionIds, publicationChanged = true });
        var call = Call(ClientToolNames.ResearchUpdate);
        var progress = RunProcessor.ReduceScientificStateReceipt(Request(), call, result, true, FiveAttempts(), []).Progress;
        Assert.Equal(0, progress.PrematureCompletionAttempts);
        Assert.Null(progress.PrematureCompletionFingerprint);
        Assert.False(progress.CompletionMethodChangeHinted);
    }

    [Theory]
    [InlineData(ClientToolNames.ResearchRead)]
    [InlineData(ClientToolNames.MathSymbolic)]
    [InlineData(ClientToolNames.MathFormalProof)]
    [InlineData(ClientToolNames.ResearchCodeExecute)]
    public void MereToolSuccessWithoutScientificStateChangeOrMeasuredProofDoesNotReset(string tool)
    {
        var progress = RunProcessor.ReduceScientificStateReceipt(Request(), Call(tool),
            Receipt(new { success = true }), true, FiveAttempts(), []).Progress;
        Assert.Equal(5, progress.PrematureCompletionAttempts);
    }

    [Fact]
    public void NoOpAndReplayedStateUpdatesDoNotResetTheStreak()
    {
        var progress = RunProcessor.ReduceScientificStateReceipt(Request(), Call(ClientToolNames.ResearchUpdate),
            Receipt(new { success = true, protocol = "section-delta-v1", projectId = "science-project",
                changedIds = Array.Empty<string>(), publicationChanged = false }), true, FiveAttempts(), []).Progress;
        progress = RunProcessor.ReduceScientificStateReceipt(Request(), Call(ClientToolNames.ResearchUpdate),
            Receipt(new { success = true, protocol = "section-delta-v1", projectId = "science-project",
                changedIds = ChangedSectionIds, publicationChanged = true, replayed = true }), true, progress, []).Progress;
        Assert.Equal(5, progress.PrematureCompletionAttempts);
    }

    [Fact]
    public void NewMeasuredOutputResetsButReplayedExecutionIdsAndExperimentNamesDoNotManufactureNovelty()
    {
        var call = Call(ClientToolNames.ResearchCodeExecute);
        var first = ScientificCompletionStagnationGuard.ResetOnProductiveReceipt(FiveAttempts(), call, Execution("first-run", 'b'), false);
        Assert.Equal(0, first.PrematureCompletionAttempts);
        var oldWork = first with { PrematureCompletionFingerprint = new string('a', 64), PrematureCompletionAttempts = 5 };
        var renamed = call with { Id = "new-call", Arguments = JsonSerializer.SerializeToElement(new
            { projectId = "science-project", experimentId = "renamed-experiment", timeoutSeconds = 200 }) };
        var repeated = ScientificCompletionStagnationGuard.ResetOnProductiveReceipt(oldWork, renamed, Execution("second-run", 'b'), false);
        Assert.Equal(5, repeated.PrematureCompletionAttempts);
        var restored = JsonSerializer.Deserialize<ScientificStateRunProgress>(JsonSerializer.Serialize(repeated))!;
        var next = ScientificCompletionStagnationGuard.ResetOnProductiveReceipt(restored, call, Execution("third-run", 'c'), false);
        Assert.Equal(0, next.PrematureCompletionAttempts);
        Assert.Equal(2, next.CompletionEvidenceFingerprints!.Count);
    }

    [Fact]
    public void FormalProofRequiresActualKernelAcceptanceAndFrozenExecutionEvidence()
    {
        var call = Call(ClientToolNames.MathFormalProof);
        var missingKernel = ScientificCompletionStagnationGuard.ResetOnProductiveReceipt(FiveAttempts(), call, Execution("no-kernel", 'b'), false);
        Assert.Equal(5, missingKernel.PrematureCompletionAttempts);
        var accepted = ScientificCompletionStagnationGuard.ResetOnProductiveReceipt(FiveAttempts(), call, Execution("kernel", 'b', true), false);
        Assert.Equal(0, accepted.PrematureCompletionAttempts);
        Assert.Single(accepted.CompletionEvidenceFingerprints!);
    }

    [Fact]
    public void CompletedAssessmentClearsThePendingClaimCounter()
    {
        var progress = ScientificCompletionStagnationGuard.Observe(FiveAttempts(), Assessment() with { Complete = true }, []);
        Assert.Equal(0, progress.PrematureCompletionAttempts);
        Assert.Null(progress.PrematureCompletionFingerprint);
    }

    [Fact]
    public void LegacyCheckpointWithoutNewGuardFieldsStartsWithNoPrematureCompletionCount()
    {
        var legacy = JsonSerializer.Deserialize<ScientificStateRunProgress>("{\"Revision\":3,\"PublicationRevision\":2}")!;
        Assert.Equal(0, legacy.PrematureCompletionAttempts);
        Assert.Null(legacy.CompletionEvidenceFingerprints);
        Assert.Equal(1, ScientificCompletionStagnationGuard.Observe(legacy, Assessment(), []).PrematureCompletionAttempts);
    }

    private static ScientificStateRunProgress FiveAttempts() => new(PrematureCompletionFingerprint: new string('a', 64),
        PrematureCompletionAttempts: 5, CompletionMethodChangeHinted: true);
    private static ScientificCompletionAssessment Assessment() => new(false, false, "science-project", new string('a', 64), MissingGoals, 0);
    private static LmToolCall Call(string name) => new("actual-call", name, JsonSerializer.SerializeToElement(new { projectId = "science-project" }));
    private static string Receipt(object result) => JsonSerializer.Serialize(new { status = "completed", result });
    private static JsonElement Execution(string runId, char outputHash, bool kernel = false) => JsonSerializer.SerializeToElement(new
    {
        success = true, formalVerification = kernel ? "KernelAccepted" : null,
        runs = new[] { new { runId, exitCode = 0, timedOut = false, executedScriptPath = "work/pilot.py", scriptSha256 = new string('a', 64),
            inputHashes = new Dictionary<string, string> { ["work/pilot.py"] = new string('a', 64) },
            outputHashes = new Dictionary<string, string> { ["artifacts/metrics.json"] = new string(outputHash, 64) } } },
    });
    private static RunRequest Request() => new(MissumAiProtocol.Version, RunMode.General,
        [new("user", [new("text", "Quantitativ prüfen.")])], ClientCapabilities: ["research.deliverables"],
        ResearchOptions: new(ProjectId: "science-project", ProtocolVersion: 2));
}
