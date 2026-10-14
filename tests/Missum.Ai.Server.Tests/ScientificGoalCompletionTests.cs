using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class ScientificGoalCompletionTests
{
    [Fact]
    public void PendingPlanCriteriaStillPermitCurrentNumericalVerification()
    {
        var pendingCheck = new ScientificCompletionAssessment(false, true, "science", "awaiting-check", [], 0);
        var state = CodingWorkingState.Create("Quantitative Lösung") with
        { AcceptanceCriteria = [new("a1", "Quantitative Abweichung prüfen", "in_progress", [])] };
        var assessment = ScientificStateCompletionPolicy.ApplyPlanRequirements(pendingCheck, state);
        Assert.False(assessment.Complete);
        Assert.True(assessment.NeedsVerification);
        Assert.Contains("a1", Assert.Single(assessment.Missing), StringComparison.Ordinal);
    }
    [Theory]
    [InlineData(null, false)]
    [InlineData("unresolved", false)]
    [InlineData("satisfied", true)]
    public void ReadyPublicationAndReviewCannotReplaceExplicitGoalAssessment(string? goalStatus, bool complete)
    {
        var request = new RunRequest(MissumAiProtocol.Version, RunMode.General,
            [new("user", [new("text", "Doppelspalt quantitativ prüfen")])],
            ClientCapabilities: ["research.deliverables"], ResearchOptions: new(ProtocolVersion: 2, ProjectId: "science"));
        var call = new LmToolCall("verify", ClientToolNames.ResearchDeliverablesVerify,
            JsonSerializer.SerializeToElement(new { projectId = "science" }));
        List<LmChatMessage> messages = [new("assistant", ToolCalls: [call]), new("tool", JsonSerializer.Serialize(new
        {
            status = "completed", result = new
            {
                success = true, projectId = "science",
                research = new { protocol = "section-delta-v1", revision = 4, publicationRevision = 4, ready = true, goalStatus },
                publication = new { ready = true, revision = 4, pdfPath = "Publikation.pdf", sourceSha256 = new string('a', 64) },
                scientificReview = new { protocol = "science-review-v1", projectId = "science", ready = true,
                    revision = 4, publicationRevision = 4, stateSha256 = new string('b', 64) },
                simulation = new { required = false, ready = true },
            },
        }), ToolCallId: call.Id)];
        var result = ScientificStateCompletionPolicy.Assess(request, messages, new AgentToolCatalog().GetAvailableTools(request));
        Assert.Equal(complete, result.Complete);
        if (!complete) Assert.Contains(result.Missing, reason => reason.Contains("Ziel", StringComparison.Ordinal));
    }

    [Fact]
    public void IncompletePlanCriteriaBlockCompletionAndChangedProgressUpdatesRecoveryFingerprint()
    {
        var ready = new ScientificCompletionAssessment(true, false, "science", "original", [], 0);
        var state = CodingWorkingState.Create("Quantitative Lösung") with
        {
            AcceptanceCriteria = [new("a1", "Fehler innerhalb der Schranke", "pending", [])],
        };
        var pending = ScientificStateCompletionPolicy.ApplyPlanRequirements(ready, state);
        Assert.False(pending.Complete);
        Assert.False(pending.NeedsVerification);
        Assert.Contains("a1", Assert.Single(pending.Missing), StringComparison.Ordinal);
        var active = ScientificStateCompletionPolicy.ApplyPlanRequirements(ready, state with
        {
            AcceptanceCriteria = [new("a1", "Fehler innerhalb der Schranke", "in_progress", [])],
        });
        Assert.NotEqual(pending.Fingerprint, active.Fingerprint);
        var done = ScientificStateCompletionPolicy.ApplyPlanRequirements(ready, state with
        {
            AcceptanceCriteria = [new("a1", "Fehler innerhalb der Schranke", "completed", ["measured-experiment"])],
        });
        Assert.True(done.Complete);
        Assert.Same(ready, ScientificStateCompletionPolicy.ApplyPlanRequirements(ready, null));
        var scientificFailure = ready with { Complete = false, Missing = ["Messfehler zu groß"] };
        Assert.False(ScientificStateCompletionPolicy.ApplyPlanRequirements(scientificFailure,
            state with { AcceptanceCriteria = [] }).Complete);
    }

    [Fact]
    public void ContinuationAdoptsQuantitativeContractWithoutChangingEarlierEvaluatedMessages()
    {
        List<LmChatMessage> messages = [new("system", "[MISSUM_SCIENCE_PUBLICATION_REVIEW:v2]"),
            new("assistant", "Alter gespeicherter Stand"), new("user", "Fortsetzen")];
        var prefix = messages.Take(2).ToArray();
        ScientificStateAgentPolicy.EnsurePublicationWorkflowAtNewRunBoundary(messages);
        Assert.Equal(prefix, messages.Take(2));
        Assert.Contains("quantitativeValidation", messages[^2].Content, StringComparison.Ordinal);
        Assert.Contains("Pearson-Korrelation", messages[^2].Content, StringComparison.Ordinal);
        Assert.Contains("openLimit", messages[^2].Content, StringComparison.Ordinal);
        Assert.Contains("erfüllt es nicht", messages[^2].Content, StringComparison.Ordinal);
        Assert.Equal("Fortsetzen", messages[^1].Content);
    }
}
