using System.Text;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Research;

namespace Missum.Tests;

public sealed class ResearchReadProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly string[] SectionIds = ["section-1"];
    private static readonly string[] ExperimentIds = ["experiment-1"];
    private static readonly string[] SourceIds = ["source-1"];
    private static readonly string[] EvidenceIds = ["evidence-1"];

    [Fact]
    public void OverviewDistinguishesResearchWorkPathsFromWorkspacePathsAndTracksWorkspaceChanges()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "science-workspace");
        var root = Path.Combine(workspace, "Science", "research-test");
        var layout = new ResearchSandboxLayout("research-test", root,
            Path.Combine(root, "inputs"), Path.Combine(root, "work"), Path.Combine(root, "artifacts"),
            Path.Combine(root, "notebooks"), Path.Combine(root, "manuscripts"), Path.Combine(root, "env"),
            Path.Combine(root, "runs"), Path.Combine(root, "snapshots"), Now, "ready");
        var paths = ResearchReadPathScope.From(workspace, layout);
        var snapshot = Snapshot();
        var overview = ResearchReadProjection.Create(snapshot, Args(new { view = "overview" }), paths);
        var scope = overview.GetProperty("paths");
        Assert.Equal(layout.WorkPath, scope.GetProperty("workRoot").GetString());
        Assert.Equal("Science/research-test/work/", scope.GetProperty("codingWorkPrefix").GetString());
        Assert.Contains("ohne work/-Präfix", scope.GetProperty("researchCodePaths").GetString(), StringComparison.Ordinal);
        var objects = ResearchReadProjection.Create(snapshot, Args(new { view = "objects" }), paths);
        Assert.Equal(overview.GetProperty("stateStamp").GetString(), objects.GetProperty("stateStamp").GetString());
        Assert.Equal(JsonValueKind.Null, objects.GetProperty("paths").ValueKind);
        var known = Args(new { view = "overview", knownStateStamp = overview.GetProperty("stateStamp").GetString() });
        Assert.True(ResearchReadProjection.Create(snapshot, known, paths).GetProperty("unchanged").GetBoolean());
        Assert.False(ResearchReadProjection.Create(snapshot, known, paths with { WorkRoot = Path.Combine(root, "new-work") })
            .GetProperty("unchanged").GetBoolean());
    }

    [Fact]
    public void OverviewOmitsManuscriptAndReceiptsButLeavesAllDetailsAddressable()
    {
        var snapshot = Snapshot();
        var overview = ResearchReadProjection.Create(snapshot, Args(new { view = "overview" }));
        Assert.True(overview.GetProperty("success").GetBoolean());
        Assert.False(overview.GetProperty("unchanged").GetBoolean());
        Assert.Equal(1, overview.GetProperty("counts").GetProperty("sources").GetInt32());
        Assert.False(overview.TryGetProperty("sources", out _));
        Assert.DoesNotContain("FULL MANUSCRIPT", overview.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("ACTUAL EXECUTION RECEIPT", overview.GetRawText(), StringComparison.Ordinal);
        var section = ResearchReadProjection.Create(snapshot, Args(new { view = "objects", ids = SectionIds }));
        Assert.Equal("FULL MANUSCRIPT", section.GetProperty("items")[0].GetProperty("data").GetProperty("contentMarkdown").GetString());
        var check = ResearchReadProjection.Create(snapshot, Args(new { view = "checks", ids = ExperimentIds }));
        Assert.Equal("ACTUAL EXECUTION RECEIPT", check.GetProperty("items")[0].GetProperty("receipt").GetString());
        var unchanged = ResearchReadProjection.Create(snapshot, Args(new
        {
            view = "overview", knownStateStamp = overview.GetProperty("stateStamp").GetString(),
        }));
        Assert.True(unchanged.GetProperty("unchanged").GetBoolean());
        Assert.False(unchanged.TryGetProperty("items", out _));
        Assert.False(unchanged.TryGetProperty("task", out _));
    }

    [Fact]
    public void TargetedReadsExposeAllPersistedProvenanceWithoutExpandingLists()
    {
        var initial = Snapshot();
        var snapshot = initial with
        {
            Sources = [initial.Sources[0] with { MetadataJson = "{\"publisher\":\"Primary source\"}", Doi = "10.1234/example", VersionKind = "preprint" }],
            Evidence = [initial.Evidence[0] with { Page = "17", Section = "Method", TableOrFigure = "Figure 2", LocatorJson = "{\"paragraph\":3}" }],
            Experiments = [initial.Experiments[0] with
            {
                EnvironmentLock = "python=3.13", SourceFilesJson = "[\"model.py\"]", RandomSeedsJson = "[42]",
                InputHashesJson = "{\"model.py\":\"actual-input-hash\"}", ResourceLimitsJson = "{\"memoryMiB\":4096}",
                StderrEvidence = "convergence warning", ResultArtifactsJson = "[\"result.png\"]", HypothesisId = "hypothesis-1",
                CommandText = new string('c', 600),
            }],
        };
        var source = ResearchReadProjection.Create(snapshot, Args(new { view = "sources", ids = SourceIds }))
            .GetProperty("items")[0].GetProperty("details");
        Assert.Equal(snapshot.Sources[0].MetadataJson, source.GetProperty("metadataJson").GetString());
        Assert.Equal(snapshot.Sources[0].Doi, source.GetProperty("doi").GetString());
        Assert.Equal("preprint", source.GetProperty("versionKind").GetString());
        var evidence = ResearchReadProjection.Create(snapshot, Args(new { view = "sources", ids = EvidenceIds }))
            .GetProperty("items")[0];
        Assert.Equal("verifiedExcerpt", evidence.GetProperty("evidenceLevel").GetString());
        Assert.Equal(Now, evidence.GetProperty("retrievedAt").GetDateTimeOffset());
        Assert.Equal("Figure 2", evidence.GetProperty("tableOrFigure").GetString());
        Assert.Equal(snapshot.Evidence[0].LocatorJson, evidence.GetProperty("locator").GetString());
        var experiment = ResearchReadProjection.Create(snapshot, Args(new { view = "experiments", ids = ExperimentIds }))
            .GetProperty("items")[0].GetProperty("details");
        var restored = experiment.Deserialize<ResearchExperiment>(WebJson);
        Assert.Equal(snapshot.Experiments[0], restored);
        var sourceList = ResearchReadProjection.Create(snapshot, Args(new { view = "sources" }));
        var experimentList = ResearchReadProjection.Create(snapshot, Args(new { view = "experiments" }));
        Assert.Equal(JsonValueKind.Null, sourceList.GetProperty("items")[0].GetProperty("details").ValueKind);
        Assert.Equal(JsonValueKind.Null, experimentList.GetProperty("items")[0].GetProperty("details").ValueKind);
        Assert.DoesNotContain("actual-input-hash", experimentList.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void StampDetectsSourceExecutionAndOriginalTaskChangesWithoutWorkingRevisionChange()
    {
        var snapshot = Snapshot();
        var stamp = ResearchReadProjection.Stamp(snapshot);
        ResearchWorkingReadSnapshot[] changed =
        [
            snapshot with { Project = snapshot.Project with { OriginalQuestion = "Additional binding requirement" } },
            snapshot with { Sources = [snapshot.Sources[0] with { CanonicalUrl = "https://example.org/correction" }] },
            snapshot with { Evidence = [snapshot.Evidence[0] with { ContentHash = "new-hash" }] },
            snapshot with { Experiments = [snapshot.Experiments[0] with { StdoutEvidence = "new run output" }] },
            snapshot with { Checks = [snapshot.Checks[0] with { EvidenceJson = "new verification receipt" }] },
        ];
        foreach (var next in changed)
        {
            Assert.Equal(snapshot.State.Revision, next.State.Revision);
            Assert.NotEqual(stamp, ResearchReadProjection.Stamp(next));
            Assert.False(ResearchReadProjection.Create(next, Args(new { view = "overview", knownStateStamp = stamp }))
                .GetProperty("unchanged").GetBoolean());
        }
    }

    [Fact]
    public void OriginalTaskPaginationPreservesEveryCharacterIncludingSurrogateAtBoundary()
    {
        var original = new string('a', 7999) + "🌍" + new string('b', 8000) + " Important middle constraint " + new string('c', 4000);
        var snapshot = Snapshot(original);
        var overview = ResearchReadProjection.Create(snapshot, Args(new { view = "overview" }));
        Assert.False(overview.GetProperty("task").GetProperty("complete").GetBoolean());
        var read = new StringBuilder();
        string? cursor = null;
        do
        {
            var arguments = cursor is null ? Args(new { view = "task" }) : Args(new { view = "task", cursor });
            var page = ResearchReadProjection.Create(snapshot, arguments);
            Assert.Equal(read.Length, page.GetProperty("characterOffset").GetInt32());
            var content = page.GetProperty("originalQuestion").GetString()!;
            Assert.False(char.IsLowSurrogate(content[0]));
            Assert.False(char.IsHighSurrogate(content[^1]));
            read.Append(content);
            cursor = page.GetProperty("nextCursor").GetString();
        } while (cursor is not null);
        Assert.Equal(original, read.ToString());
    }

    [Fact]
    public void CursorRejectsAnotherProjectViewOrFilterAndReportsChangedSnapshot()
    {
        var snapshot = Snapshot();
        var first = ResearchReadProjection.Create(snapshot, Args(new { view = "objects", limit = 1 }));
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);
        Assert.Throws<InvalidDataException>(() => ResearchReadProjection.Create(snapshot, Args(new { view = "sources", cursor })));
        Assert.Throws<InvalidDataException>(() => ResearchReadProjection.Create(snapshot, Args(new { view = "objects", ids = SectionIds, cursor })));
        Assert.Throws<InvalidDataException>(() => ResearchReadProjection.Create(snapshot with
        {
            Project = snapshot.Project with { Id = "another-project" },
        }, Args(new { view = "objects", cursor })));
        var stale = ResearchReadProjection.Create(snapshot with
        {
            Evidence = [snapshot.Evidence[0] with { ContentHash = "updated" }],
        }, Args(new { view = "objects", cursor }));
        Assert.False(stale.GetProperty("success").GetBoolean());
        Assert.Equal("research.cursor_stale", stale.GetProperty("errorCode").GetString());
        Assert.True(stale.GetProperty("restartRequired").GetBoolean());
    }

    [Theory]
    [InlineData("overview")]
    [InlineData("objects")]
    [InlineData("sources")]
    [InlineData("experiments")]
    [InlineData("checks")]
    public void EachCollectionHasIndependentLosslessPagination(string view)
    {
        var initial = Snapshot();
        var snapshot = initial with
        {
            State = initial.State with { Items = Enumerable.Range(0, 5).Select(index => initial.State.Items[0] with { Id = "item-" + index }).ToArray() },
            Sources = Enumerable.Range(0, 5).Select(index => initial.Sources[0] with { WorkId = "source-" + index }).ToArray(),
            Experiments = Enumerable.Range(0, 5).Select(index => initial.Experiments[0] with { Id = "experiment-" + index }).ToArray(),
            Checks = Enumerable.Range(0, 5).Select(index => initial.Checks[0] with { Id = "check-" + index }).ToArray(),
        };
        var read = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var arguments = cursor is null ? Args(new { view, limit = 2 }) : Args(new { view, limit = 2, cursor });
            var page = ResearchReadProjection.Create(snapshot, arguments);
            Assert.Equal(5, page.GetProperty("totalItems").GetInt32());
            foreach (var item in page.GetProperty("items").EnumerateArray()) Assert.True(read.Add(item.GetProperty("id").GetString()!));
            cursor = page.GetProperty("nextCursor").GetString();
        } while (cursor is not null);
        Assert.Equal(5, read.Count);
    }

    [Theory]
    [InlineData("\"offset\":\"wrong\"")]
    [InlineData("\"limit\":false")]
    [InlineData("\"cursor\":\"cursor\"")]
    [InlineData("\"view\":\"task\",\"limit\":1")]
    [InlineData("\"view\":\"overview\",\"knownStateStamp\":\"short\"")]
    public void InvalidReadArgumentsAreControlledValidationFailures(string properties)
    {
        var arguments = JsonSerializer.Deserialize<JsonElement>("{\"projectId\":\"research-test\"," + properties + "}");
        var proposal = new ToolProposal("proposal", "run", ClientToolNames.ResearchRead, arguments,
            ToolRiskClass.ReadOnly, "Read research state", DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(proposal));
    }

    private static JsonElement Args<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static ResearchWorkingReadSnapshot Snapshot(string originalQuestion = "Explain the scientific question completely.")
    {
        const string projectId = "research-test";
        var project = new ScientificResearchProject(projectId, Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
            "mathematicalInvestigation", originalQuestion, originalQuestion, "sandboxResearch", "standard", "active", 2, 1, Now, Now);
        var state = new ResearchWorkingState(projectId, 4, 2, "Scientific title",
        [
            new("requirement-1", "requirement", 1, null, Args(new { statement = "Explain the fundamentals", required = true, status = "active" }), Now),
            new("section-1", "section", 1, null, Args(new { title = "Fundamentals", contentMarkdown = "FULL MANUSCRIPT", status = "draft" }), Now),
        ], Now);
        return new(project, state,
            [new("source-1", projectId, "Source title", "https://example.org/paper", "published", "{}", "included", "fullTextExcerpt", Now)],
            [new("evidence-1", projectId, "source-1", "Precise source excerpt", "Statement", "hash", "verifiedExcerpt", "{}", Now)],
            [new("experiment-1", projectId, "environment", "[]", "[]", "{}", "python model.py", "{}", "runtime output", "", "[]", "verified", Now, Now)],
            [new("check-1", projectId, "experiment", "experiment-1", "mathematics", "python", "verified", "ACTUAL EXECUTION RECEIPT", Now)]);
    }
}
