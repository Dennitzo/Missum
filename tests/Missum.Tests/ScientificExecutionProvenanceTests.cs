using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Core.Research;
using Microsoft.Extensions.Logging.Abstractions;

namespace Missum.Tests;

public sealed class ScientificExecutionProvenanceTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lZsAAAAASUVORK5CYII=");

    [Fact]
    public async Task FrozenInputsExcludeArtifactsAndIndependentWorkMergesWithoutOverwritingParentFiles()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var sandbox = Sandbox(environment);
        var layout = await sandbox.EnsureProjectAsync("provenance");
        await sandbox.WriteTextAsync(layout.ProjectId, "calculate.py", "print('before')");
        await sandbox.WriteTextAsync(layout.ProjectId, "counter.txt", "1");
        await File.WriteAllTextAsync(Path.Combine(layout.InputsPath, "input.csv"), "value\n17");
        await File.WriteAllTextAsync(Path.Combine(layout.EnvironmentPath, "lock.txt"), "numpy=2");
        await File.WriteAllBytesAsync(Path.Combine(layout.ArtifactsPath, "old.png"), Png);
        var snapshot = await ResearchSandboxService.CaptureExecutionSnapshotAsync(layout, Guid.NewGuid().ToString("N"), Path.Combine(layout.WorkPath, "calculate.py"));
        Assert.Equal(4, snapshot.InputHashes.Count);
        Assert.DoesNotContain(snapshot.InputHashes.Keys, key => key.StartsWith("artifacts/", StringComparison.Ordinal));
        Assert.Equal(Hash("print('before')"), snapshot.ScriptSha256);
        Assert.Equal("work/calculate.py", snapshot.ExecutedScriptPath);
        await File.WriteAllTextAsync(Path.Combine(layout.WorkPath, "calculate.py"), "print('parent edit')");
        await File.WriteAllTextAsync(Path.Combine(layout.WorkPath, "parent.txt"), "independent");
        Assert.Equal("print('before')", await File.ReadAllTextAsync(Path.Combine(snapshot.ExecutionWorkPath, "calculate.py")));
        Assert.Equal("print('before')", await File.ReadAllTextAsync(Path.Combine(snapshot.FrozenPath, "work", "calculate.py")));
        await File.WriteAllTextAsync(Path.Combine(snapshot.ExecutionWorkPath, "counter.txt"), "2");
        await File.WriteAllTextAsync(Path.Combine(snapshot.ExecutionWorkPath, "child.txt"), "child result");
        var outputs = await ResearchSandboxService.CompleteExecutionSnapshotAsync(layout, snapshot);
        Assert.Equal(Hash("1"), snapshot.InputHashes["work/counter.txt"]);
        Assert.Equal(Hash("2"), outputs["work/counter.txt"]);
        Assert.Equal(Hash("child result"), outputs["work/child.txt"]);
        Assert.Equal("2", await File.ReadAllTextAsync(Path.Combine(layout.WorkPath, "counter.txt")));
        Assert.Equal("independent", await File.ReadAllTextAsync(Path.Combine(layout.WorkPath, "parent.txt")));
        Assert.Equal("print('parent edit')", await File.ReadAllTextAsync(Path.Combine(layout.WorkPath, "calculate.py")));
        Assert.DoesNotContain("artifacts/old.png", outputs.Keys);
    }

    [Fact]
    public async Task ConcurrentEditsKeepBothParentFileAndIndependentExecutionCopy()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var sandbox = Sandbox(environment);
        var layout = await sandbox.EnsureProjectAsync("conflict");
        await sandbox.WriteTextAsync(layout.ProjectId, "calculate.py", "print(1)");
        await sandbox.WriteTextAsync(layout.ProjectId, "data.txt", "original");
        var snapshot = await ResearchSandboxService.CaptureExecutionSnapshotAsync(layout, Guid.NewGuid().ToString("N"), Path.Combine(layout.WorkPath, "calculate.py"));
        await File.WriteAllTextAsync(Path.Combine(snapshot.ExecutionWorkPath, "data.txt"), "child");
        await File.WriteAllTextAsync(Path.Combine(layout.WorkPath, "data.txt"), "parent");
        await Assert.ThrowsAsync<IOException>(() => ResearchSandboxService.CompleteExecutionSnapshotAsync(layout, snapshot));
        Assert.Equal("parent", await File.ReadAllTextAsync(Path.Combine(layout.WorkPath, "data.txt")));
        Assert.Equal("child", await File.ReadAllTextAsync(Path.Combine(snapshot.ExecutionWorkPath, "data.txt")));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(snapshot.FrozenPath, "work", "data.txt")));
    }

    [Fact]
    public async Task SuccessfulRetryRetainsIdenticalByteOutputsActuallyRewrittenDuringExecution()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var sandbox = Sandbox(environment);
        var layout = await sandbox.EnsureProjectAsync("identical-retry");
        await sandbox.WriteTextAsync(layout.ProjectId, "calculate.py", "print(1)");
        var image = Path.Combine(layout.ArtifactsPath, "plot.png");
        await File.WriteAllBytesAsync(image, Png);
        File.SetLastWriteTimeUtc(image, DateTime.UtcNow.AddHours(-1));
        var snapshot = await ResearchSandboxService.CaptureExecutionSnapshotAsync(layout, Guid.NewGuid().ToString("N"), Path.Combine(layout.WorkPath, "calculate.py"));
        await File.WriteAllBytesAsync(image, Png);
        var outputs = await ResearchSandboxService.CompleteExecutionSnapshotAsync(layout, snapshot);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Png)), outputs["artifacts/plot.png"]);
        Assert.Equal(snapshot.BeforeOutputHashes["artifacts/plot.png"], outputs["artifacts/plot.png"]);
    }

    [Fact]
    public async Task OversizedCandidateSetFailsWithoutAPartialSuccessfulReceipt()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var sandbox = Sandbox(environment);
        var layout = await sandbox.EnsureProjectAsync("too-many-inputs");
        await sandbox.WriteTextAsync(layout.ProjectId, "calculate.py", "print(1)");
        for (var index = 0; index < 4096; index++)
            await File.WriteAllTextAsync(Path.Combine(layout.InputsPath, index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt"), "1");
        var error = await Assert.ThrowsAsync<IOException>(() => ResearchSandboxService.CaptureExecutionSnapshotAsync(layout,
            Guid.NewGuid().ToString("N"), Path.Combine(layout.WorkPath, "calculate.py")));
        Assert.Contains("4096", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(layout.RunsPath));
    }

    [Fact]
    public async Task RepeatedRunsBindAnIdenticalRewrittenFigureToTheLatestCompletedProcess()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var research = environment.Get<IScientificResearchRepository>();
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("Repeated execution", ChatMode.ClaudeScience);
        var projectId = "research-" + session.Id.ToString("N");
        var now = DateTimeOffset.UtcNow;
        await research.UpsertProjectAsync(new(projectId, session.Id, "mathematicalInvestigation", "Repeated runs", "Repeated runs",
            "sandboxResearch", "multiPath", "active", 1, 1, now, now));
        using var sandbox = Sandbox(environment);
        var layout = await sandbox.EnsureProjectAsync(projectId);
        await sandbox.WriteTextAsync(projectId, "calculate.py", "print('repeatable numeric result')");
        var first = await CreateMeasuredRunAsync(layout, "calculate.py", writeFigure: true);
        // Preserve deterministic bytes while making the next overwrite observable.
        File.SetLastWriteTimeUtc(Path.Combine(layout.ArtifactsPath, "plot.png"), now.UtcDateTime.AddHours(-1));
        var last = await CreateMeasuredRunAsync(layout, "calculate.py", writeFigure: true);
        Assert.Equal(first.OutputHashes!["artifacts/plot.png"], last.OutputHashes!["artifacts/plot.png"]);
        Assert.True(last.CompletedAt > first.CompletedAt);
        var recordId = RecordId(projectId, "repeated-numerical");
        await research.SaveExperimentAsync(new(recordId, projectId, ResearchSandboxService.RunnerImage,
            "[\"work/calculate.py\"]", "[]", JsonSerializer.Serialize(first.InputHashes, Json),
            "research.code.execute {}", "{}", JsonSerializer.Serialize(new { runs = new[] { first, last } }, Json),
            "", "[\"artifacts/plot.png\"]", "ProcessSucceeded", first.StartedAt, last.CompletedAt));
        using var simulations = new ScientificSimulationService(research, sandbox);
        var simulation = await simulations.RefreshAsync(projectId);
        var figure = Assert.Single(simulation.Artifacts);
        Assert.NotNull(figure.Execution);
        Assert.Equal(last.RunId, figure.Execution.RunId);
        Assert.Equal(recordId, figure.Execution.ExperimentRecordId);
        var publication = await PublicationAsync(layout, projectId);
        var verified = await ScientificDeliverablesVerifier.VerifyAsync(projectId,
            new(1, publication, simulation, null, null));
        Assert.True(verified.GetProperty("success").GetBoolean(), verified.GetRawText());
        Assert.Equal(last.RunId, verified.GetProperty("simulation").GetProperty("artifacts")[0].GetProperty("runId").GetString());
    }

    [Theory]
    [InlineData("input")]
    [InlineData("script")]
    [InlineData("secondary-output")]
    [InlineData("frozen-input")]
    public async Task LaterHelperCannotReplaceNumericProofAndAnyBoundFileMutationInvalidatesIt(string mutation)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var research = environment.Get<IScientificResearchRepository>();
        var session = await chats.CreateSessionAsync("Measured experiment", ChatMode.ClaudeScience);
        var projectId = "research-" + session.Id.ToString("N");
        var now = DateTimeOffset.UtcNow;
        await research.UpsertProjectAsync(new(projectId, session.Id, "mathematicalInvestigation", "Oscillator", "Oscillator",
            "sandboxResearch", "multiPath", "active", 1, 1, now, now));
        using var sandbox = Sandbox(environment);
        var layout = await sandbox.EnsureProjectAsync(projectId);
        await sandbox.WriteTextAsync(projectId, "calculate.py", "print('numeric result')");
        await File.WriteAllTextAsync(Path.Combine(layout.InputsPath, "input.csv"), "m,c,k\n1,.4,4");
        var numeric = await CreateMeasuredRunAsync(layout, "calculate.py", writeFigure: true);
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance);
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        using var service = new MissumAiAssistantService(connection, chats, environment.Get<IAssistantAttachmentRepository>(),
            environment.Get<IChatArtifactRepository>(), environment.Get<IMissumAiRunRepository>(), environment.Get<IClientToolExecutionRepository>(),
            environment.Get<IBinaryObjectStore>(), environment.Get<IDocumentIngestor>(), null!, null!, null!, null!, null!, settings,
            recent, NullLogger<MissumAiAssistantService>.Instance, scientificResearch: research);
        await PersistAsync(service, session.Id, projectId, "numerical", numeric);
        await sandbox.WriteTextAsync(projectId, "verify.py", "print('files exist')");
        var helper = await CreateMeasuredRunAsync(layout, "verify.py", writeFigure: false);
        await PersistAsync(service, session.Id, projectId, "helper", helper);
        var experiments = (await research.LoadResultSnapshotAsync(projectId)).Experiments;
        Assert.Equal(2, experiments.Count);
        var numericExperiment = Assert.Single(experiments, item => item.Id == RecordId(projectId, "numerical"));
        Assert.Equal(numeric.InputHashes!["inputs/input.csv"], JsonSerializer.Deserialize<Dictionary<string, string>>(numericExperiment.InputHashesJson)!["inputs/input.csv"]);
        Assert.Contains("work/calculate.py", numericExperiment.SourceFilesJson, StringComparison.Ordinal);
        Assert.Contains("artifacts/plot.png", numericExperiment.ResultArtifactsJson, StringComparison.Ordinal);
        using var simulations = new ScientificSimulationService(research, sandbox);
        var simulation = await simulations.RefreshAsync(projectId);
        var figure = Assert.Single(simulation.Artifacts);
        Assert.NotNull(figure.Execution);
        Assert.Equal(numeric.RunId, figure.Execution.RunId);
        Assert.Equal(numeric.ScriptSha256, figure.Execution.ScriptSha256);
        Assert.Equal(RecordId(projectId, "numerical"), figure.Execution.ExperimentRecordId);
        var publication = await PublicationAsync(layout, projectId);
        var presentation = new ScientificPresentationSnapshot(1, publication, simulation, null, null);
        var verified = await ScientificDeliverablesVerifier.VerifyAsync(projectId, presentation);
        Assert.True(verified.GetProperty("success").GetBoolean(), verified.GetRawText());
        var artifact = Assert.Single(verified.GetProperty("simulation").GetProperty("artifacts").EnumerateArray());
        Assert.Equal(numeric.RunId, artifact.GetProperty("runId").GetString());
        Assert.Equal("artifacts/plot.png", artifact.GetProperty("artifactPath").GetString());
        Assert.Equal(numeric.InputHashes.Count, artifact.GetProperty("inputHashes").EnumerateObject().Count());
        Assert.Equal(numeric.OutputHashes!.Count, artifact.GetProperty("outputHashes").EnumerateObject().Count());
        var altered = mutation switch
        {
            "input" => Path.Combine(layout.InputsPath, "input.csv"),
            "script" => Path.Combine(layout.WorkPath, "calculate.py"),
            "secondary-output" => Path.Combine(layout.ArtifactsPath, "result.json"),
            _ => Path.Combine(layout.SnapshotsPath, numeric.SnapshotId!, "frozen", "inputs", "input.csv"),
        };
        await File.AppendAllTextAsync(altered, "changed");
        var rejected = await ScientificDeliverablesVerifier.VerifyAsync(projectId, presentation);
        Assert.False(rejected.GetProperty("success").GetBoolean());
        Assert.Empty(rejected.GetProperty("simulation").GetProperty("artifacts").EnumerateArray());
    }

    private static ResearchSandboxService Sandbox(TestEnvironment environment) => new(AssistantRuntimeProfile.Resolve() with
        { DataDirectory = environment.Directory, NativeStateDirectory = Path.Combine(environment.Directory, "native") });
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string RecordId(string projectId, string experimentId) => "experiment-" + Hash(projectId + "\nexperiment\n" + experimentId)[..24];
    private static async Task<ResearchSandboxRunResult> CreateMeasuredRunAsync(ResearchSandboxLayout layout, string script, bool writeFigure)
    {
        var runId = Guid.NewGuid().ToString("N");
        var snapshot = await ResearchSandboxService.CaptureExecutionSnapshotAsync(layout, runId, Path.Combine(layout.WorkPath, script));
        var started = DateTimeOffset.UtcNow;
        if (writeFigure)
        {
            await File.WriteAllBytesAsync(Path.Combine(layout.ArtifactsPath, "plot.png"), Png);
            await File.WriteAllTextAsync(Path.Combine(layout.ArtifactsPath, "result.json"), "{\"max_abs_error\":1e-9}");
        }
        var completed = DateTimeOffset.UtcNow;
        var outputs = await ResearchSandboxService.CompleteExecutionSnapshotAsync(layout, snapshot);
        return new(runId, 0, false, "filesystem projection fixture", "", started, completed, "docker run python3 -I /sandbox/work/" + script,
            snapshot.ExecutedScriptPath, snapshot.ScriptSha256, snapshot.SnapshotId, snapshot.InputHashes, outputs);
    }
    private static Task PersistAsync(MissumAiAssistantService service, Guid session, string projectId, string experimentId, ResearchSandboxRunResult run)
    {
        var proposal = new ToolProposal(Guid.NewGuid().ToString("N"), "run-parent", ClientToolNames.ResearchCodeExecute,
            JsonSerializer.SerializeToElement(new { projectId, experimentId, executable = "python", arguments = new[] { run.ExecutedScriptPath![5..] } }, Json),
            ToolRiskClass.Process, "execute", DateTimeOffset.UtcNow.AddMinutes(1));
        var receipt = new ClientToolResult(proposal.ProposalId, "completed", JsonSerializer.SerializeToElement(new
        {
            success = true, projectId, experimentId, experimentRecordId = RecordId(projectId, experimentId),
            sourceFiles = new[] { run.ExecutedScriptPath }, inputHashes = run.InputHashes,
            verificationStatus = "ProcessSucceeded", runs = new[] { run },
        }, Json));
        return service.PersistScientificToolResultAsync(session, proposal, receipt, CancellationToken.None);
    }
    private static async Task<ScientificPublicationArtifact> PublicationAsync(ResearchSandboxLayout layout, string projectId)
    {
        var markdown = Path.Combine(layout.ManuscriptsPath, "publication.md");
        var pdf = Path.Combine(layout.ManuscriptsPath, "publication.pdf");
        await File.WriteAllTextAsync(markdown, "# Oscillator\nNumerical validation.");
        await File.WriteAllTextAsync(pdf, "%PDF-1.7\n" + new string(' ', 1200));
        return new(projectId, 1, markdown, pdf, true, DateTimeOffset.UtcNow, Hash("# Oscillator\nNumerical validation."));
    }
}
