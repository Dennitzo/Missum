using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Core.Research;

namespace Missum.Tests;

public sealed class ScientificResearchReviewTests
{
    private static readonly string[] SectionIds = ["section"];
    private static readonly string[] SourceIds = ["source"];
    private static readonly string[] EvidenceIds = ["evidence"];
    private static readonly string[] ClaimIds = ["claim"];
    private static readonly string[] HypothesisIds = ["hypothesis"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void MissingReviewAndMissingStatusAreReportedTogetherBeforeAnyRewrite()
    {
        var report = ScientificResearchReview.Assess(State(new { title = "Abschnitt", contentMarkdown = "Gespeicherter vollständiger Text." }), [], [], [], []);
        Assert.False(report.Ready);
        Assert.Contains(report.Issues, issue => issue.Code == "review_missing" && issue.Field == "data.review");
        var status = Assert.Single(report.Issues, issue => issue.Code == "status_missing");
        Assert.Equal("section", status.Id);
        Assert.Equal(1, status.Revision);
        Assert.Equal("<missing>", status.CurrentValue);
        Assert.Contains("completed", status.AllowedValues);
        Assert.Contains("changes[].patch", status.NextAction, StringComparison.Ordinal);
        Assert.Contains("data.status", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AReviewWithDraftStatusReturnsAnExactMetadataRepairInsteadOfRequestingFullText()
    {
        var report = ScientificResearchReview.Assess(State(new { title = "Abschnitt", contentMarkdown = "Unveränderter Text.",
            status = "draft", review = Review(1), classification = "hypothesis", assumptions = "Eingeschränktes hypothetisches Modell." }), [], [], [], []);
        var issue = Assert.Single(report.Issues);
        Assert.False(report.Ready);
        Assert.Equal("status_open", issue.Code);
        Assert.Equal("data.status", issue.Field);
        Assert.Equal("draft", issue.CurrentValue);
        Assert.Contains("vorhandenen Text", issue.NextAction, StringComparison.Ordinal);
    }
    [Fact]
    public void CompletedStatusAndModelAuthoredVerifiedScalarDoNotCertifyManuscript()
    {
        var state = State(new { title = "Aussage", contentMarkdown = "Empirische Behauptung.", status = "verified" });
        var report = ScientificResearchReview.Assess(state, [], [], [], []);
        Assert.False(report.Ready);
        Assert.Contains(report.Missing, message => message.Contains("data.review", StringComparison.Ordinal));
        Assert.False(ScientificResearchReview.HasCurrentReview(state, []));
    }

    [Fact]
    public void ReviewWithoutActualEvidenceDoesNotApproveEmpiricalClaim()
    {
        var state = State(new { title = "Aussage", contentMarkdown = "Empirische Behauptung.", status = "supported", review = Review(1) });
        var report = ScientificResearchReview.Assess(state, [], [], [], []);
        Assert.False(report.Ready);
        Assert.Contains(report.Missing, message => message.Contains("Es fehlen", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitHypotheticalScopeCanBeReviewedWithoutClaimingEmpiricalTruth()
    {
        var state = HypotheticalState();
        var report = ScientificResearchReview.Assess(state, [], [], [], []);
        Assert.True(report.Ready);
        Assert.Contains("keine allgemeine Wahrheitsgarantie", report.Scope, StringComparison.Ordinal);
        Assert.Equal(SectionIds, report.ReviewedItemIds);
    }

    [Fact]
    public void OldReviewAndChangedContentOrRevisionCannotRemainApproved()
    {
        var state = HypotheticalState();
        var receipt = Receipt(state);
        Assert.True(ScientificResearchReview.HasCurrentReview(state, [receipt]));
        Assert.False(ScientificResearchReview.HasCurrentReview(state with { Revision = 2 }, [receipt]));
        Assert.False(ScientificResearchReview.HasCurrentReview(state with { PublicationRevision = 2 }, [receipt]));
        Assert.False(ScientificResearchReview.HasCurrentReview(state with { Title = "Anderer fachlicher Stand" }, [receipt]));
        var changed = state with { Items = [state.Items[0] with { Revision = 2 }] };
        Assert.False(ScientificResearchReview.Assess(changed, [], [], [], []).Ready);
        Assert.False(ScientificResearchReview.HasCurrentReview(changed, [receipt]));
    }

    [Fact]
    public void ARealReadExcerptCanSupportAScopedDocumentedSourceReview()
    {
        var state = State(new { title = "Quellenabgleich", contentMarkdown = "Die Quelle berichtet den Beobachtungswert.",
            status = "supported", sourceIds = SourceIds, evidenceIds = EvidenceIds, review = Review(1) });
        var now = DateTimeOffset.UtcNow;
        ResearchLiteratureEntry[] sources = [new("source", state.ProjectId, "Originalarbeit", "https://example.org/original", "published", "{}", "included", "fullTextExcerpt", now)];
        ResearchEvidenceRecord[] evidence = [new("evidence", state.ProjectId, "source", "Originaler Beobachtungswert", "Beobachtung",
            new string('a', 64), "fullTextExcerpt", "{}", now)];
        Assert.True(ScientificResearchReview.Assess(state, sources, evidence, [], []).Ready);
        Assert.False(ScientificResearchReview.Assess(state, sources, [], [], []).Ready);
        Assert.False(ScientificResearchReview.Assess(state, sources, [evidence[0] with { ProjectId = "other" }], [], []).Ready);
    }

    [Fact]
    public async Task ReviewIsAdditiveAndPersistsThroughProtectedExecutionReceiptOnly()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("Prüfung", ChatMode.ClaudeScience);
        var repository = environment.Get<IScientificResearchRepository>();
        var store = Assert.IsAssignableFrom<IScientificResearchStateRepository>(repository);
        var now = DateTimeOffset.UtcNow;
        var projectId = "research-" + session.Id.ToString("N");
        await repository.UpsertProjectAsync(new(projectId, session.Id, "scientificEvidence", "Prüfe den Stand", "Prüfung",
            "codingWorkspaceResearch", "multiPath", "active", 1, 2, now, now, environment.Directory));
        var draft = HypotheticalState(projectId).Items[0];
        var stored = await store.ApplyWorkingUpdateAsync(projectId, "reviewed-section", null, "Begrenztes Modell",
            [new(draft.Id, draft.Kind, 0, draft.Data)]);
        Assert.True(stored.Success);
        var receipt = Receipt(stored.State);
        await repository.SaveResultSnapshotAsync(projectId, new([], [], [receipt], []));
        Assert.Empty(await store.LoadExecutionVerificationsAsync(projectId)); // Model/raw result writes cannot forge protected evidence.
        await store.SaveExecutionVerificationAsync(receipt);
        Assert.True(ScientificResearchReview.HasCurrentReview(stored.State, await store.LoadExecutionVerificationsAsync(projectId)));
        var loaded = await store.LoadWorkingStateAsync(projectId);
        Assert.Equal(1, loaded.Items[0].Data.GetProperty("review").GetProperty("itemRevision").GetInt64());
        var invalid = await store.ApplyWorkingUpdateAsync(projectId, "stale-review-write", null, null,
            [new(draft.Id, draft.Kind, 1, draft.Data)]);
        Assert.False(invalid.Success);
        Assert.Contains(invalid.Conflicts, conflict => conflict.Code == "invalid_review");
        Assert.Equal(loaded.Revision, invalid.State.Revision);
    }

    [Fact]
    public void OnlySuccessfulCurrentWholeDeliverableVerificationPersistsAReview()
    {
        var state = HypotheticalState();
        var review = ScientificResearchReview.Assess(state, [], [], [], []);
        var report = JsonSerializer.SerializeToElement(new { success = true, scientificReview = review }, JsonOptions);
        Assert.NotNull(ScientificResearchReviewPersistence.Create(state, report));
        Assert.Null(ScientificResearchReviewPersistence.Create(state with { Revision = 2 }, report));
        Assert.Null(ScientificResearchReviewPersistence.Create(state,
            JsonSerializer.SerializeToElement(new { success = false, scientificReview = review }, JsonOptions)));
    }

    [Fact]
    public void ReferencedClaimCannotHideAnUnreviewedUnderlyingHypothesis()
    {
        var state = State(new { title = "Abhängigkeit", contentMarkdown = "Die Aussage verwendet einen Modellansatz.",
            status = "completed", claimIds = ClaimIds, review = Review(1) });
        var now = DateTimeOffset.UtcNow;
        state = state with { Items = [state.Items[0],
            new("claim", "claim", 1, null, JsonSerializer.SerializeToElement(new { statement = "Begrenzte Aussage",
                status = "openLimit", classification = "hypothesis", reason = "Nur Modellannahme",
                hypothesisIds = HypothesisIds, review = Review(1) }), now),
            new("hypothesis", "hypothesis", 1, null, JsonSerializer.SerializeToElement(new { statement = "Ungeprüfte Voraussetzung",
                status = "unresolved" }), now)] };
        var report = ScientificResearchReview.Assess(state, [], [], [], []);
        Assert.False(report.Ready);
        Assert.Contains("hypothesis", report.ReviewedItemIds);
        Assert.Contains(report.Missing, item => item.Contains("hypothesis", StringComparison.Ordinal) && item.Contains("data.review", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("state")]
    [InlineData("presentation")]
    public void ConcurrentChangesRejectTheWholeVerificationAndNeverCreateAReceipt(string change)
    {
        var state = HypotheticalState();
        var presentation = new ScientificPresentationSnapshot(2, null, null, null, null);
        Assert.True(ScientificResearchReviewPersistence.IsCurrent(state, presentation, state, presentation));
        var current = change == "state" ? state with { Revision = 2 } : state;
        var latest = change == "presentation" ? presentation with { Version = 3 } : presentation;
        Assert.False(ScientificResearchReviewPersistence.IsCurrent(state, presentation, current, latest));
        var review = ScientificResearchReview.Assess(state, [], [], [], []);
        var verified = JsonSerializer.SerializeToElement(new { success = true, scientificReview = review,
            research = new { ready = true, missing = Array.Empty<string>() } }, JsonOptions);
        var rejected = ScientificResearchReviewPersistence.RejectStale(verified, current);
        Assert.False(rejected.GetProperty("success").GetBoolean());
        Assert.False(rejected.GetProperty("scientificReview").GetProperty("ready").GetBoolean());
        Assert.Contains("während der Prüfung geändert", rejected.GetProperty("research").GetProperty("missing")[0].GetString(), StringComparison.Ordinal);
        Assert.Null(ScientificResearchReviewPersistence.Create(state, rejected));
    }

    private static ResearchWorkingState State(object data, string projectId = "research-review") => new(projectId, 1, 1,
        "Begrenztes Modell", [new("section", "section", 1, null, JsonSerializer.SerializeToElement(data), DateTimeOffset.UtcNow)], DateTimeOffset.UtcNow);
    private static ResearchWorkingState HypotheticalState(string projectId = "research-review") => State(new { title = "Illustration",
        contentMarkdown = "Dies ist ein hypothetisches Modell, keine bestätigte empirische Aussage.", status = "openLimit",
        classification = "hypothesis", reason = "Es liegt keine empirische Bestätigung vor.", review = Review(1) }, projectId);
    private static object Review(long revision) => new { itemRevision = revision,
        sourceAssessment = "Quellenbedarf und vorhandene Originalbelege wurden im angegebenen Umfang geprüft.",
        calculationAssessment = "Der Abschnitt behauptet keine ausgeführten Rechnungen.",
        contradictionAssessment = "Keine fachlich weitergehende Schlussfolgerung als der begrenzte Modellansatz.",
        scope = "Nur der dokumentierte Abschnitt; keine allgemeine Bestätigung." };
    private static ResearchVerification Receipt(ResearchWorkingState state)
    {
        var report = ScientificResearchReview.Assess(state, [], [], [], []);
        return new("review-" + report.StateSha256, state.ProjectId, "publication-review", state.ProjectId,
            "SourcesCalculationsContradictions", ScientificResearchReview.Method, "ReviewedWithEvidence",
            JsonSerializer.Serialize(report, JsonOptions), DateTimeOffset.UtcNow);
    }
}
