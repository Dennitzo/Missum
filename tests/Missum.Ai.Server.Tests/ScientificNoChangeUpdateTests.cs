using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class ScientificNoChangeUpdateTests
{
    private static readonly string[] SectionIds = ["section"];
    [Fact]
    public void OvernightNullRemovalLoopIsBlockedEvenThoughRepositorySucceeded()
    {
        var messages = new List<LmChatMessage>();
        var call = Update();
        var progress = Observe(new(), call, Unchanged(), messages);
        Assert.Empty(messages);
        progress = Observe(progress, call with { Id = "next-call" }, Unchanged(), messages);
        Assert.Equal(2, progress.NoChangeUpdateAttempts);
        Assert.Single(messages);
        Assert.True(ScientificResearchProgressGuard.TryBlock(Request(), call, progress, out var rejected));
        Assert.Contains("nicht geändert", rejected!.Message);
        Assert.Contains("patch.review", rejected.Message);
        Assert.Contains("Keine Prüfung erfinden", rejected.Message);
        for (var attempt = 0; attempt < ScientificResearchProgressGuard.MaximumBlockedAttempts; attempt++)
        {
            Assert.True(ScientificResearchProgressGuard.TryBlock(Request(), call, progress, out rejected));
            progress = Observe(progress, call, JsonSerializer.Serialize(new { status = "not_executed",
                errorCode = ScientificResearchProgressGuard.RejectionCode, scienceFailureFingerprint = rejected!.Fingerprint,
                message = rejected.Message }), messages, false);
            progress = JsonSerializer.Deserialize<ScientificStateRunProgress>(JsonSerializer.Serialize(progress))!;
        }
        Assert.Throws<ScientificResearchStalledException>(() => ScientificResearchProgressGuard.ThrowIfStalled(progress));
        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public void CosmeticArgumentChangesAndReadsCannotEvadeUnchangedUpdateLimit()
    {
        var messages = new List<LmChatMessage>();
        var progress = new ScientificStateRunProgress();
        for (var attempt = 0; attempt < ScientificResearchProgressGuard.MaximumUnchangedUpdates; attempt++)
        {
            progress = Observe(progress, Update("section-" + attempt), Unchanged(), messages);
            progress = Observe(progress, new("read", ClientToolNames.ResearchRead,
                JsonSerializer.SerializeToElement(new { projectId = "project" })),
                JsonSerializer.Serialize(new { status = "completed", result = new { success = true,
                    protocol = "section-delta-v1", projectId = "project", revision = 31 } }), messages);
            progress = JsonSerializer.Deserialize<ScientificStateRunProgress>(JsonSerializer.Serialize(progress))!;
        }
        Assert.Throws<ScientificResearchStalledException>(() => ScientificResearchProgressGuard.ThrowIfStalled(progress));
    }

    [Fact]
    public void ActualCorrectionCanProceedAndClearsNoChangeGuardWhileReplayDoesNot()
    {
        var messages = new List<LmChatMessage>();
        var progress = Observe(new(), Update(), Unchanged(), messages);
        progress = Observe(progress, Update(), Unchanged(), messages);
        var correction = Update() with { Arguments = JsonSerializer.SerializeToElement(new { projectId = "project",
            changes = new[] { new { id = "section", kind = "section", expectedRevision = 8, patch = new { status = "unresolved", reason = "Needs measured convergence." } } } }) };
        Assert.False(ScientificResearchProgressGuard.TryBlock(Request(), correction, progress, out _));
        progress = Observe(progress, correction, Changed(replayed: true), messages);
        Assert.Equal(2, progress.NoChangeUpdateAttempts);
        Assert.True(ScientificResearchProgressGuard.TryBlock(Request(), Update(), progress, out _));
        progress = Observe(progress, correction, Changed(replayed: false), messages);
        Assert.Equal(0, progress.NoChangeUpdateAttempts);
        Assert.Empty(progress.Failures!);
    }

    [Fact]
    public void UnchangedSuccessCannotEraseEarlierValidationFailure()
    {
        var messages = new List<LmChatMessage>();
        var invalid = JsonSerializer.Serialize(new { status = "failed", errorCode = "agent.invalid_tool_call", message = "Missing status." });
        var progress = Observe(new(), Update(), invalid, messages, false);
        progress = Observe(progress, Update(), invalid, messages, false);
        progress = Observe(progress, Update(), Unchanged(), messages);
        Assert.Contains(progress.Failures!, failure => failure.Cause == "agent.invalid_tool_call" && failure.Count == 2);
    }

    private static ScientificStateRunProgress Observe(ScientificStateRunProgress progress, LmToolCall call,
        string result, List<LmChatMessage> messages, bool succeeded = true) =>
        RunProcessor.ReduceScientificStateReceipt(Request(), call, result, succeeded, progress, messages).Progress;

    private static LmToolCall Update(string id = "section") => new(Guid.NewGuid().ToString("N"), ClientToolNames.ResearchUpdate,
        JsonSerializer.SerializeToElement(new { projectId = "project", changes = new[] { new { id, kind = "section",
            expectedRevision = 8, patch = new { review = (object?)null, quantitativeValidation = (object?)null } } } }));

    private static string Unchanged() => JsonSerializer.Serialize(new { status = "completed", result = new {
        success = true, protocol = "section-delta-v1", projectId = "project", revision = 31,
        publicationRevision = 19, changedIds = Array.Empty<string>(), publicationChanged = false, replayed = false, stored = true } });

    private static string Changed(bool replayed) => JsonSerializer.Serialize(new { status = "completed", result = new {
        success = true, protocol = "section-delta-v1", projectId = "project", revision = 32,
        publicationRevision = 20, changedIds = SectionIds, publicationChanged = true, replayed } });

    private static RunRequest Request() => new(MissumAiProtocol.Version, RunMode.General,
        [new("user", [new("text", "Prüfe quantitativ.")])], DeepResearch: true, ClientCapabilities: ["research.deliverables"],
        ResearchOptions: new(ProtocolVersion: 2, ProjectId: "project", Profile: DeepResearchProfile.ScientificEvidence,
            AutonomyLevel: ResearchAutonomyLevel.SandboxResearch));
}
