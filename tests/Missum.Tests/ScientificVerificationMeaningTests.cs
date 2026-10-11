using System.Text.Json;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Core.Research;

namespace Missum.Tests;

public sealed class ScientificVerificationMeaningTests
{
    private static readonly string[] ExperimentIds = ["experiment"];
    private static readonly string[] CheckIds = ["check"];

    [Theory]
    [InlineData("execution", "research.code.execute", "passed", "experiment", false)]
    [InlineData("execution", "research.code.execute", "verified", "claim", false)]
    [InlineData("symbolic", "research.code.execute", "verified", "claim", false)]
    [InlineData("symbolic", "math.symbolic", "passed", "claim", false)]
    [InlineData("symbolic", "math.symbolic", "SymbolicallyVerified", "claim", true)]
    [InlineData("symbolic", "math.symbolic", "Verified", "claim", true)]
    [InlineData("symbolic", "math.symbolic", "Verified", "experiment", false)]
    public async Task SuccessfulProcessCannotPromoteAScientificAssertionToVerified(string dimension, string method,
        string status, string targetType, bool accepted)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (repository, store, project) = await SeedProjectAsync(environment);
        await SeedExperimentAsync(repository, project);
        await store.SaveExecutionVerificationAsync(new("check", project, targetType, targetType, dimension, method, status,
            """{"success":true,"verificationStatus":"ProcessSucceeded","residual":"0"}""", DateTimeOffset.UtcNow));
        var result = await store.ApplyWorkingUpdateAsync(project, "claim", null, null,
            [Claim(0, "verified")]);
        Assert.Equal(accepted, result.Success);
        if (!accepted)
        {
            var conflict = Assert.Single(result.Conflicts, item => item.Code == "verification_required");
            Assert.Contains("Ausführung", conflict.Message, StringComparison.Ordinal);
            Assert.Contains("wissenschaftliche Richtigkeit", conflict.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("missing_kernel", false)]
    [InlineData("failed_kernel", false)]
    [InlineData("failed_process", false)]
    [InlineData("no_runs", false)]
    [InlineData("wrong_dimension", false)]
    [InlineData("unbound", false)]
    public async Task FormalExecutionRequiresAnActuallyAcceptedKernelReceipt(string variant, bool accepted)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (repository, store, project) = await SeedProjectAsync(environment);
        await SeedExperimentAsync(repository, project);
        var runs = variant == "no_runs" ? Array.Empty<object>() : new object[] { new
        {
            runId = "formal-run", snapshotId = "execution-formal-run", exitCode = variant == "failed_process" ? 1 : 0,
            timedOut = false, inputHashes = new Dictionary<string, string> { ["work/proof.lean"] = new string('a', 64) },
            outputHashes = new Dictionary<string, string>(),
        } };
        var evidence = Json(new { success = true, formalVerification = variant == "missing_kernel" ? null
            : variant == "failed_kernel" ? "NotEstablished" : "KernelAccepted", runs }).GetRawText();
        if (variant == "unbound") await SeedExperimentAsync(repository, project, "other");
        await store.SaveExecutionVerificationAsync(new("check", project, "experiment", variant == "unbound" ? "other" : "experiment",
            variant == "wrong_dimension" ? "execution" : "formal", "math.formalProof", "passed", evidence, DateTimeOffset.UtcNow));
        var result = await store.ApplyWorkingUpdateAsync(project, "formal-claim", null, null, [Claim(0, "verified")]);
        Assert.Equal(accepted, result.Success);
        if (!accepted) Assert.Contains(result.Conflicts, conflict => conflict.Code == "verification_required");
    }

    [Fact]
    public async Task RawModelAuthoredVerificationRowsCannotForgeAScientificCheck()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (repository, store, project) = await SeedProjectAsync(environment);
        await SeedExperimentAsync(repository, project);
        await repository.SaveResultSnapshotAsync(project, new([], [],
            [new("check", project, "claim", "claim", "symbolic", "math.symbolic", "Verified", "{\"residual\":\"0\"}", DateTimeOffset.UtcNow)], []));
        var result = await store.ApplyWorkingUpdateAsync(project, "forged", null, null, [Claim(0, "verified")]);
        Assert.False(result.Success);
        Assert.Contains(result.Conflicts, conflict => conflict.Code == "verification_required");
    }

    [Theory]
    [InlineData("numeric", "documentation", false)]
    [InlineData("python", "", false)]
    [InlineData("simulation", "animation", false)]
    [InlineData("numeric python", "numeric", true)]
    [InlineData("python symbolic", "symbolic", true)]
    [InlineData("python numeric symbolic", "symbolic", false)]
    [InlineData("animation", "documentation", true)]
    [InlineData("simulation interactive", "documentation", true)]
    public async Task LegacyRequiredNumericalWorkCannotBeDowngradedBeforeAContractExists(string originalMethod, string nextMethod, bool accepted)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (_, store, project) = await SeedProjectAsync(environment);
        var initial = await store.ApplyWorkingUpdateAsync(project, "legacy", null, null,
            [new("goal", "requirement", 0, Json(new { statement = "Die vereinbarte Auswertung liefern", status = "active", method = originalMethod }))]);
        Assert.True(initial.Success);
        var changed = await store.ApplyWorkingUpdateAsync(project, "downgrade", null, null,
            [new("goal", "requirement", 1, Patch: Json(new { method = nextMethod }))]);
        Assert.Equal(accepted, changed.Success);
        if (!accepted)
        {
            Assert.Contains(changed.Conflicts, conflict => conflict.Code == "quantitative_goal_preserved");
            Assert.Equal(initial.State.Revision, changed.State.Revision);
        }
    }

    [Fact]
    public async Task LegacyNumericalGoalCanAdoptAnExplicitContractWithoutKeepingAMethodKeyword()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var (_, store, project) = await SeedProjectAsync(environment);
        Assert.True((await store.ApplyWorkingUpdateAsync(project, "legacy", null, null,
            [new("goal", "requirement", 0, Json(new { statement = "Die vereinbarte Auswertung liefern", status = "active", method = "numeric" }))])).Success);
        var quantitativeValidation = JsonSerializer.Deserialize<JsonElement>("""
            {"observable":"Verteilung","reference":"Referenzverteilung","artifactPath":"work/validation.json","sampling":false,"checks":[{"metric":"error","kind":"accuracy","maximum":0.01},{"metric":"refinement","kind":"convergence","maximum":0.001}]}
            """);
        var changed = await store.ApplyWorkingUpdateAsync(project, "contract", null, null,
            [new("goal", "requirement", 1, Patch: Json(new { method = "Verteilungsvergleich", quantitativeValidation }))]);
        Assert.True(changed.Success);
        var downgraded = await store.ApplyWorkingUpdateAsync(project, "remove-contract", null, null,
            [new("goal", "requirement", 2, Patch: Json(new { quantitativeValidation = (object?)null }))]);
        Assert.False(downgraded.Success);
        Assert.Contains(downgraded.Conflicts, conflict => conflict.Code == "quantitative_goal_preserved");
    }

    private static ResearchWorkingChange Claim(long revision, string status) => new("claim", "claim", revision,
        Json(new { statement = "Eine wissenschaftliche Aussage", status, experimentIds = ExperimentIds, checkIds = CheckIds }));
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static Task SeedExperimentAsync(IScientificResearchRepository repository, string project, string id = "experiment")
    {
        var now = DateTimeOffset.UtcNow;
        return repository.SaveExperimentAsync(new(id, project, "environment.lock", "[]", "[]", "{}", "research.code.execute work/check.py",
            "{}", "{\"success\":true}", "", "[]", "ProcessSucceeded", now, now));
    }
    private static async Task<(IScientificResearchRepository Repository, IScientificResearchStateRepository Store, string Project)> SeedProjectAsync(TestEnvironment environment)
    {
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("Prüfnachweise", ChatMode.ClaudeScience);
        var repository = environment.Get<IScientificResearchRepository>();
        var store = Assert.IsAssignableFrom<IScientificResearchStateRepository>(repository);
        var project = "research-" + session.Id.ToString("N");
        var now = DateTimeOffset.UtcNow;
        await repository.UpsertProjectAsync(new(project, session.Id, "scientificEvidence", "Aussage prüfen", "Prüfung",
            "codingWorkspaceResearch", "multiPath", "active", 1, 2, now, now, environment.Directory));
        return (repository, store, project);
    }
}
