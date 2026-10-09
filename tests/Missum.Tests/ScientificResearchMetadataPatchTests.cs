using System.Text.Json;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Core.Research;

namespace Missum.Tests;

public sealed class ScientificResearchMetadataPatchTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] ClaimIds = ["claim"];
    private static readonly string[] InventedSourceIds = ["invented-source"];

    [Fact]
    public async Task ReviewPatchPreservesScientificTextAndReferencesWithoutReexportingUnchangedManuscript()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (store, project, initial) = await SeedAsync(environment);
        var result = await store.ApplyWorkingUpdateAsync(project, "review", null, null,
            [Patch(1, new { status = "draft", review = Review(2) })]);
        Assert.True(result.Success);
        Assert.Equal(initial.Revision + 1, result.State.Revision);
        Assert.Equal(initial.PublicationRevision, result.State.PublicationRevision);
        var section = Assert.Single(result.State.Items, item => item.Id == "section");
        Assert.Equal("Die Herleitung bleibt unverändert: $x=1$.", section.Data.GetProperty("contentMarkdown").GetString());
        Assert.Equal("claim", section.Data.GetProperty("claimIds")[0].GetString());
        Assert.Equal("x", section.Data.GetProperty("units")[0].GetProperty("symbol").GetString());
        Assert.Equal(2, section.Revision);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SuppliedReviewWithoutExplicitStatusFailsBeforeItCreatesAnotherOpenReview(bool patch)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (store, project, initial) = await SeedAsync(environment);
        var data = Json(new { title = "Grundlage", contentMarkdown = "Unverändert", review = Review(2) });
        var change = patch ? Patch(1, new { review = Review(2) }) : new ResearchWorkingChange("section", "section", 1, data);
        var result = await store.ApplyWorkingUpdateAsync(project, "missing-status", null, null, [change]);
        Assert.False(result.Success);
        var failure = Assert.Single(result.Conflicts, conflict => conflict.Code == "review_status_required");
        Assert.Contains("draft", failure.Message, StringComparison.Ordinal);
        Assert.Contains("completed", failure.Message, StringComparison.Ordinal);
        Assert.Equal(initial.Revision, result.State.Revision);
    }

    [Fact]
    public async Task StatusOrTextPatchStillUpdatesTheDisplayedPublicationAndClearsOldReview()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (store, project, initial) = await SeedAsync(environment);
        var reviewed = await store.ApplyWorkingUpdateAsync(project, "review", null, null,
            [Patch(1, new { status = "draft", review = Review(2) })]);
        Assert.True(reviewed.Success);
        var completed = await store.ApplyWorkingUpdateAsync(project, "status", null, null,
            [Patch(2, new { status = "completed" })]);
        Assert.True(completed.Success);
        Assert.Equal(initial.PublicationRevision + 1, completed.State.PublicationRevision);
        Assert.False(completed.State.Items.Single(item => item.Id == "section").Data.TryGetProperty("review", out _));
        var changed = await store.ApplyWorkingUpdateAsync(project, "text", null, null,
            [Patch(3, new { contentMarkdown = "Eine fachlich geänderte Herleitung." })]);
        Assert.True(changed.Success);
        Assert.Equal(completed.State.PublicationRevision + 1, changed.State.PublicationRevision);
    }

    [Fact]
    public async Task NoOpPatchAndReplayPreserveExistingRevisionAndReviewWhileChangedReplayIsRejected()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (store, project, _) = await SeedAsync(environment);
        var reviewed = await store.ApplyWorkingUpdateAsync(project, "review", null, null,
            [Patch(1, new { status = "draft", review = Review(2) })]);
        var noop = await store.ApplyWorkingUpdateAsync(project, "noop", null, null, [Patch(2, new { status = "draft" })]);
        Assert.True(noop.Success);
        Assert.Empty(noop.ChangedIds);
        Assert.Equal(reviewed.State.Revision, noop.State.Revision);
        Assert.True(noop.State.Items.Single(item => item.Id == "section").Data.TryGetProperty("review", out _));
        var replay = await store.ApplyWorkingUpdateAsync(project, "review", null, null,
            [Patch(1, new { status = "draft", review = Review(2) })]);
        Assert.True(replay.Replayed);
        Assert.True(replay.Success);
        var reused = await store.ApplyWorkingUpdateAsync(project, "review", null, null, [Patch(1, new { status = "completed" })]);
        Assert.False(reused.Success);
        Assert.Contains(reused.Conflicts, conflict => conflict.Code == "operation_reused");
    }

    [Fact]
    public async Task PatchCannotBypassRevisionOwnershipOrExclusivePayloadValidation()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (store, project, initial) = await SeedAsync(environment);
        var stale = await store.ApplyWorkingUpdateAsync(project, "stale", null, null, [Patch(3, new { status = "completed" })]);
        Assert.Contains(stale.Conflicts, conflict => conflict.Code == "revision_conflict");
        var foreign = await store.ApplyWorkingUpdateAsync(project, "foreign", "other-agent", null, [Patch(1, new { status = "completed" })]);
        Assert.Contains(foreign.Conflicts, conflict => conflict.Code == "owner_required");
        var ambiguous = await store.ApplyWorkingUpdateAsync(project, "ambiguous", null, null,
            [new("section", "section", 1, Json(new { title = "Titel", contentMarkdown = "Text" }), Json(new { status = "completed" }))]);
        Assert.Contains(ambiguous.Conflicts, conflict => conflict.Code == "ambiguous_update");
        var newPatch = await store.ApplyWorkingUpdateAsync(project, "new-patch", null, null,
            [new("new", "section", 0, Patch: Json(new { status = "draft" }))]);
        Assert.Contains(newPatch.Conflicts, conflict => conflict.Code == "patch_requires_existing");
        Assert.Equal(initial.Revision, (await store.LoadWorkingStateAsync(project)).Revision);
    }

    [Fact]
    public async Task NullRemovesOptionalMetadataButCannotRemoveRequiredContentOrIntroduceUnknownReferences()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (store, project, initial) = await SeedAsync(environment);
        var cleared = await store.ApplyWorkingUpdateAsync(project, "clear", null, null, [Patch(1, new { reason = (string?)null })]);
        Assert.True(cleared.Success);
        Assert.Equal(initial.PublicationRevision, cleared.State.PublicationRevision);
        Assert.False(cleared.State.Items.Single(item => item.Id == "section").Data.TryGetProperty("reason", out _));
        var missingText = await store.ApplyWorkingUpdateAsync(project, "remove-text", null, null,
            [Patch(2, new { contentMarkdown = (string?)null })]);
        Assert.Contains(missingText.Conflicts, conflict => conflict.Code == "missing_content");
        var invented = await store.ApplyWorkingUpdateAsync(project, "invented-source", null, null,
            [Patch(2, new { sourceIds = InventedSourceIds })]);
        Assert.Contains(invented.Conflicts, conflict => conflict.Code == "unknown_reference");
        Assert.Equal(cleared.State.Revision, (await store.LoadWorkingStateAsync(project)).Revision);
    }

    [Fact]
    public async Task LinkedClaimReviewDoesNotChangePublicationButItsDisplayedStatementDoes()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (store, project, initial) = await SeedAsync(environment);
        var reviewed = await store.ApplyWorkingUpdateAsync(project, "claim-review", null, null,
            [new("claim", "claim", 1, Patch: Json(new { status = "draft", review = Review(2) }))]);
        Assert.True(reviewed.Success);
        Assert.Equal(initial.PublicationRevision, reviewed.State.PublicationRevision);
        var statement = await store.ApplyWorkingUpdateAsync(project, "claim-statement", null, null,
            [new("claim", "claim", 2, Patch: Json(new { statement = "Eine geänderte Aussage." }))]);
        Assert.True(statement.Success);
        Assert.Equal(initial.PublicationRevision + 1, statement.State.PublicationRevision);
    }

    private static ResearchWorkingChange Patch(long revision, object patch) => new("section", "section", revision, Patch: Json(patch));
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, JsonOptions);
    private static object Review(long revision) => new { itemRevision = revision, sourceAssessment = "Quellenabgleich dokumentiert.",
        calculationAssessment = "Die Rechnung im Geltungsbereich geprüft.", contradictionAssessment = "Grenzen sind offen dokumentiert.", scope = "Illustrativer Grenzfall." };

    private static async Task<(IScientificResearchStateRepository Store, string Project, ResearchWorkingState Initial)> SeedAsync(TestEnvironment environment)
    {
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("Prüfung", ChatMode.ClaudeScience);
        var repository = environment.Get<IScientificResearchRepository>();
        var store = Assert.IsAssignableFrom<IScientificResearchStateRepository>(repository);
        var project = "research-" + session.Id.ToString("N");
        var now = DateTimeOffset.UtcNow;
        await repository.UpsertProjectAsync(new(project, session.Id, "scientificEvidence", "Prüfe den Stand", "Prüfung",
            "codingWorkspaceResearch", "multiPath", "active", 1, 2, now, now, environment.Directory));
        var initial = await store.ApplyWorkingUpdateAsync(project, "seed", null, "Illustrativer Grenzfall",
            [new("claim", "claim", 0, Json(new { statement = "Die Aussage benötigt einen Nachweis.", status = "draft" })),
             new("section", "section", 0, Json(new { title = "Grundlage", contentMarkdown = "Die Herleitung bleibt unverändert: $x=1$.",
                 status = "draft", order = 1, reason = "Die Nachweise werden noch geprüft.", claimIds = ClaimIds,
                 units = new[] { new { symbol = "x", meaning = "Illustrativer Parameter", unit = "1" } } }))]);
        Assert.True(initial.Success);
        return (store, project, initial.State);
    }
}
