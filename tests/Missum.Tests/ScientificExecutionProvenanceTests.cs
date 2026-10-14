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
    private static readonly string[] MeasuredReadPaths = ["work/helper.py", "inputs/input.csv", "artifacts/reference.json"];
    private static readonly string[] MeasuredSourcePaths = ["work/calculate.py", "work/helper.py"];

    [Fact]
    public async Task SingleProjectTreeBindsInputsAndOutputsWithoutCreatingFileRevisions()
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
        await File.WriteAllTextAsync(Path.Combine(layout.WorkPath, "parent.txt"), "independent");
        Assert.Equal(layout.WorkPath, snapshot.ExecutionWorkPath);
        Assert.StartsWith("current-", snapshot.SnapshotId, StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(snapshot.ExecutionWorkPath, "counter.txt"), "2");
        await File.WriteAllTextAsync(Path.Combine(snapshot.ExecutionWorkPath, "child.txt"), "child result");
        var outputs = await ResearchSandboxService.CompleteExecutionSnapshotAsync(layout, snapshot);
        Assert.Equal(Hash("1"), snapshot.InputHashes["work/counter.txt"]);
        Assert.Equal(Hash("2"), outputs["work/counter.txt"]);
        Assert.Equal(Hash("child result"), outputs["work/child.txt"]);
        Assert.Equal("2", await File.ReadAllTextAsync(Path.Combine(layout.WorkPath, "counter.txt")));
        Assert.Equal("independent", await File.ReadAllTextAsync(Path.Combine(layout.WorkPath, "parent.txt")));
        Assert.Equal("print('before')", await File.ReadAllTextAsync(Path.Combine(layout.WorkPath, "calculate.py")));
        Assert.DoesNotContain("artifacts/old.png", outputs.Keys);
        Assert.Empty(Directory.EnumerateFileSystemEntries(layout.SnapshotsPath));
    }

    [Fact]
    public async Task SourceMutationDuringExecutionInvalidatesTheReceiptWithoutOverwritingUserFiles()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var sandbox = Sandbox(environment);
        var layout = await sandbox.EnsureProjectAsync("conflict");
        await sandbox.WriteTextAsync(layout.ProjectId, "calculate.py", "print(1)");
        await sandbox.WriteTextAsync(layout.ProjectId, "data.txt", "original");
        var snapshot = await ResearchSandboxService.CaptureExecutionSnapshotAsync(layout, Guid.NewGuid().ToString("N"), Path.Combine(layout.WorkPath, "calculate.py"));
        await File.WriteAllTextAsync(Path.Combine(layout.WorkPath, "calculate.py"), "print('changed during execution')");
        await File.WriteAllTextAsync(Path.Combine(layout.WorkPath, "data.txt"), "parent");
        await Assert.ThrowsAsync<IOException>(() => ResearchSandboxService.CompleteExecutionSnapshotAsync(layout, snapshot));
        Assert.Equal("parent", await File.ReadAllTextAsync(Path.Combine(layout.WorkPath, "data.txt")));
        Assert.Equal("print('changed during execution')", await File.ReadAllTextAsync(Path.Combine(layout.WorkPath, "calculate.py")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(layout.SnapshotsPath));
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
        Assert.DoesNotContain(Directory.EnumerateFiles(layout.RunsPath),
            path => !Path.GetFileName(path).StartsWith("edit-", StringComparison.Ordinal));
        Assert.DoesNotContain(Directory.EnumerateDirectories(layout.SnapshotsPath),
            path => Path.GetFileName(path).StartsWith("execution-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HistoricalDuplicateSnapshotsDoNotExhaustTheActiveExecutionQuota()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var sandbox = Sandbox(environment);
        var layout = await sandbox.EnsureProjectAsync("legacy-archive-quota");
        await sandbox.WriteTextAsync(layout.ProjectId, "calculate.py", "print(1)");
        var historical = Path.Combine(layout.SnapshotsPath, "execution-legacy", "execution-work");
        Directory.CreateDirectory(historical);
        var scientificEvidence = Path.Combine(historical, "preserved-result.bin");
        await File.WriteAllBytesAsync(scientificEvidence, new byte[16 * 1024]);
        var before = Hash(await File.ReadAllBytesAsync(scientificEvidence));

        var snapshot = await ResearchSandboxService.CaptureExecutionSnapshotAsync(layout,
            Guid.NewGuid().ToString("N"), Path.Combine(layout.WorkPath, "calculate.py"), maximumBytes: 4096);

        Assert.Equal(Hash("print(1)"), snapshot.ScriptSha256);
        Assert.True(await ResearchSandboxService.ActiveProjectBytesAsync(layout) < 4096);
        Assert.Equal(before, Hash(await File.ReadAllBytesAsync(scientificEvidence)));
    }

    [Fact]
    public async Task ActiveQuotaFailureLeavesExistingScientificFilesAndCreatesNoExecutionDirectory()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var sandbox = Sandbox(environment);
        var layout = await sandbox.EnsureProjectAsync("active-quota");
        await sandbox.WriteTextAsync(layout.ProjectId, "calculate.py", "print(1)");
        var data = Path.Combine(layout.InputsPath, "too-large.bin");
        await File.WriteAllBytesAsync(data, new byte[8192]);

        var error = await Assert.ThrowsAsync<IOException>(() => ResearchSandboxService.CaptureExecutionSnapshotAsync(layout,
            Guid.NewGuid().ToString("N"), Path.Combine(layout.WorkPath, "calculate.py"), maximumBytes: 4096));

        Assert.Contains("Projektbaums", error.Message, StringComparison.Ordinal);
        Assert.Equal(8192, new FileInfo(data).Length);
        Assert.DoesNotContain(Directory.EnumerateDirectories(layout.SnapshotsPath),
            path => Path.GetFileName(path).StartsWith("execution-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApplicationWritesWaitForExecutionOnTheSameAuthoritativeTree()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var sandbox = Sandbox(environment);
        var layout = await sandbox.EnsureProjectAsync("project-execution-lock");
        await sandbox.WriteTextAsync(layout.ProjectId, "calculate.py", "print(1)");
        using var execution = await ResearchSandboxService.AcquireProjectExecutionAsync(layout);
        var pending = sandbox.WriteTextAsync(layout.ProjectId, "calculate.py", "print(2)");
        Assert.False(pending.IsCompleted);
        Assert.Equal("print(1)", await File.ReadAllTextAsync(Path.Combine(layout.WorkPath, "calculate.py")));
        execution.Dispose();
        await pending;
        Assert.Equal("print(2)", await File.ReadAllTextAsync(Path.Combine(layout.WorkPath, "calculate.py")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(layout.SnapshotsPath));
    }

    [Fact]
    public async Task ProjectExecutionLocksAllowOtherProjectsAndCancelWaitingRequests()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var sandbox = Sandbox(environment);
        var first = await sandbox.EnsureProjectAsync("locked-project");
        var other = await sandbox.EnsureProjectAsync("independent-project");
        using var active = await ResearchSandboxService.AcquireProjectExecutionAsync(first);
        using var otherLease = await ResearchSandboxService.AcquireProjectExecutionAsync(other);
        using var cancellation = new CancellationTokenSource();
        var waiting = ResearchSandboxService.AcquireProjectExecutionAsync(first, cancellation.Token);

        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        active.Dispose();
        using var resumed = await ResearchSandboxService.AcquireProjectExecutionAsync(first);
    }

    [Fact]
    public async Task NewExecutableSourceDuringExecutionCannotProduceAValidReceipt()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var sandbox = Sandbox(environment);
        var layout = await sandbox.EnsureProjectAsync("source-created-during-run");
        await sandbox.WriteTextAsync(layout.ProjectId, "calculate.py", "print(1)");
        var measured = await ResearchSandboxService.CaptureExecutionSnapshotAsync(layout,
            Guid.NewGuid().ToString("N"), Path.Combine(layout.WorkPath, "calculate.py"));
        await File.WriteAllTextAsync(Path.Combine(layout.WorkPath, "unmeasured_dependency.py"), "VALUE = 2");

        var error = await Assert.ThrowsAsync<IOException>(() => ResearchSandboxService.CompleteExecutionSnapshotAsync(layout, measured));

        Assert.Contains("neue ausführbare Quelldatei", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(layout.SnapshotsPath));
    }

    [Fact]
    public async Task CapacityWatchdogRejectsGrowingOutputsAndCanBeCancelled()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var sandbox = Sandbox(environment);
        var layout = await sandbox.EnsureProjectAsync("watched-capacity");
        using var cancellation = new CancellationTokenSource();
        var waiting = ResearchSandboxService.WaitForCapacityViolationAsync(layout,
            maximumBytes: 4096, pollingInterval: TimeSpan.FromMilliseconds(1), token: cancellation.Token);
        await File.WriteAllBytesAsync(Path.Combine(layout.WorkPath, "growing-output.bin"), new byte[1024 * 1024]);
        var reason = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("Sandbox-Speicherlimits", reason, StringComparison.Ordinal);
        File.Delete(Path.Combine(layout.WorkPath, "growing-output.bin"));
        var cancelled = ResearchSandboxService.WaitForCapacityViolationAsync(layout,
            maximumBytes: 4096, pollingInterval: TimeSpan.FromSeconds(5), token: cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
    }

    [Fact]
    public async Task ActualReadTraceBindsHelpersAndDataButExcludesUnusedScripts()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var sandbox = Sandbox(environment);
        var layout = await sandbox.EnsureProjectAsync("actual-read-bindings");
        await sandbox.WriteTextAsync(layout.ProjectId, "calculate.py", "import helper");
        await sandbox.WriteTextAsync(layout.ProjectId, "helper.py", "VALUE = 1");
        await sandbox.WriteTextAsync(layout.ProjectId, "unused.py", "VALUE = 99");
        await File.WriteAllTextAsync(Path.Combine(layout.InputsPath, "input.csv"), "1");
        await File.WriteAllTextAsync(Path.Combine(layout.ArtifactsPath, "reference.json"), "{\"value\":1}");
        var measured = await ResearchSandboxService.CaptureExecutionSnapshotAsync(layout,
            Guid.NewGuid().ToString("N"), Path.Combine(layout.WorkPath, "calculate.py"));
        var traceText = "numeric result\n" + ResearchSandboxService.ReadTraceMarker + JsonSerializer.Serialize(new
        {
            readPaths = MeasuredReadPaths,
            writtenPaths = Array.Empty<string>(), sourcePaths = MeasuredSourcePaths,
        }, Json);
        var parsed = ResearchSandboxService.ParseExecutionReadTrace(traceText);
        Assert.Equal("numeric result", parsed.StandardOutput);
        Assert.NotNull(parsed.Trace);
        var bound = ResearchSandboxService.BindExecutionReadTrace(measured, parsed.Trace);
        Assert.Equal(4, bound.InputHashes.Count);
        Assert.Equal(4, bound.SourceHashes.Count); // Executable sources and read-only data dependencies.
        Assert.DoesNotContain("work/unused.py", bound.InputHashes.Keys);
        Assert.Equal(Hash("{\"value\":1}"), bound.InputHashes["artifacts/reference.json"]);
        await ResearchSandboxService.CompleteExecutionSnapshotAsync(layout, bound);
        await File.WriteAllTextAsync(Path.Combine(layout.WorkPath, "unused.py"), "VALUE = 100");
        await ResearchSandboxService.CompleteExecutionSnapshotAsync(layout, bound);
        await File.WriteAllTextAsync(Path.Combine(layout.ArtifactsPath, "reference.json"), "{\"value\":2}");
        var changedData = await Assert.ThrowsAsync<IOException>(() => ResearchSandboxService.CompleteExecutionSnapshotAsync(layout, bound));
        Assert.Contains("artifacts/reference.json", changedData.Message, StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(layout.ArtifactsPath, "reference.json"), "{\"value\":1}");
        await File.WriteAllTextAsync(Path.Combine(layout.WorkPath, "helper.py"), "VALUE = 2");
        var error = await Assert.ThrowsAsync<IOException>(() => ResearchSandboxService.CompleteExecutionSnapshotAsync(layout, bound));
        Assert.Contains("work/helper.py", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeGeneratedDataCanBeReadButUnmeasuredExecutableSourceIsRejected()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var sandbox = Sandbox(environment);
        var layout = await sandbox.EnsureProjectAsync("generated-runtime-data");
        await sandbox.WriteTextAsync(layout.ProjectId, "calculate.py", "print(1)");
        var measured = await ResearchSandboxService.CaptureExecutionSnapshotAsync(layout,
            Guid.NewGuid().ToString("N"), Path.Combine(layout.WorkPath, "calculate.py"));
        var data = new ResearchSandboxService.ExecutionReadTrace(new HashSet<string>(StringComparer.Ordinal) { "work/derived.csv" },
            new HashSet<string>(StringComparer.Ordinal) { "work/derived.csv" }, new HashSet<string>(StringComparer.Ordinal) { "work/calculate.py" });
        var bound = ResearchSandboxService.BindExecutionReadTrace(measured, data);
        Assert.Single(bound.InputHashes);
        await File.WriteAllTextAsync(Path.Combine(layout.WorkPath, "derived.csv"), "1");
        var outputs = await ResearchSandboxService.CompleteExecutionSnapshotAsync(layout, bound);
        Assert.Equal(Hash("1"), outputs["work/derived.csv"]);
        var source = new ResearchSandboxService.ExecutionReadTrace(new HashSet<string>(StringComparer.Ordinal) { "work/new-helper.py" },
            new HashSet<string>(StringComparer.Ordinal) { "work/new-helper.py" }, new HashSet<string>(StringComparer.Ordinal) { "work/new-helper.py" });
        var error = Assert.Throws<IOException>(() => ResearchSandboxService.BindExecutionReadTrace(measured, source));
        Assert.Contains("keinen vorab gemessenen Ausgangsstand", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"readPaths\":[\"work/../outside.py\"],\"writtenPaths\":[],\"sourcePaths\":[]}")]
    [InlineData("{\"readPaths\":[\"C:/outside.py\"],\"writtenPaths\":[],\"sourcePaths\":[]}")]
    [InlineData("{\"readPaths\":[]}")]
    public void InvalidReadTraceCannotSilentlyCreateAValidReceipt(string invalid)
        => Assert.Throws<IOException>(() => ResearchSandboxService.ParseExecutionReadTrace(ResearchSandboxService.ReadTraceMarker + invalid));

    [Fact]
    public async Task RepeatedRunsUseTheSameTreeAndNeverCopyHistoricalResults()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var sandbox = Sandbox(environment);
        var layout = await sandbox.EnsureProjectAsync("deduplicated-runs");
        await sandbox.WriteTextAsync(layout.ProjectId, "calculate.py", "import dependency");
        await sandbox.WriteTextAsync(layout.ProjectId, "dependency.py", "VALUE = 1");
        var payload = new byte[128 * 1024];
        for (var index = 0; index < payload.Length; index++) payload[index] = (byte)(index % 251);
        await File.WriteAllBytesAsync(Path.Combine(layout.WorkPath, "input.bin"), payload);
        var script = Path.Combine(layout.WorkPath, "calculate.py");
        var first = await ResearchSandboxService.CaptureExecutionSnapshotAsync(layout, Guid.NewGuid().ToString("N"), script);
        await ResearchSandboxService.CompleteExecutionSnapshotAsync(layout, first);
        var second = await ResearchSandboxService.CaptureExecutionSnapshotAsync(layout, Guid.NewGuid().ToString("N"), script);
        await ResearchSandboxService.CompleteExecutionSnapshotAsync(layout, second);

        Assert.Equal(layout.WorkPath, first.ExecutionWorkPath);
        Assert.Equal(layout.WorkPath, second.ExecutionWorkPath);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(second.ExecutionWorkPath, "input.bin")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(layout.SnapshotsPath));
        Assert.Equal(3, Directory.EnumerateFiles(layout.WorkPath).Count());
        await File.WriteAllTextAsync(Path.Combine(layout.WorkPath, "dependency.py"), "VALUE = 2");
        Assert.Equal(Hash("VALUE = 1"), first.InputHashes["work/dependency.py"]);
        Assert.Equal(Hash("VALUE = 1"), first.SourceHashes["work/dependency.py"]);
    }

    [Fact]
    public async Task SingleTreeOutputHashesDetectLaterEditsWithoutKeepingACopy()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var sandbox = Sandbox(environment);
        var layout = await sandbox.EnsureProjectAsync("sealed-child-output");
        await sandbox.WriteTextAsync(layout.ProjectId, "calculate.py", "print(1)");
        var snapshot = await ResearchSandboxService.CaptureExecutionSnapshotAsync(layout,
            Guid.NewGuid().ToString("N"), Path.Combine(layout.WorkPath, "calculate.py"));
        await File.WriteAllTextAsync(Path.Combine(snapshot.ExecutionWorkPath, "new-result.txt"), "measured result");
        var outputs = await ResearchSandboxService.CompleteExecutionSnapshotAsync(layout, snapshot);
        await File.WriteAllTextAsync(Path.Combine(layout.WorkPath, "new-result.txt"), "later edit");

        Assert.Equal(Hash("measured result"), outputs["work/new-result.txt"]);
        Assert.Equal("later edit", await File.ReadAllTextAsync(Path.Combine(snapshot.ExecutionWorkPath, "new-result.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(layout.SnapshotsPath));
    }

    [Fact]
    public async Task ReadOnlyInputMutationDuringExecutionRejectsTheReceipt()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var sandbox = Sandbox(environment);
        var layout = await sandbox.EnsureProjectAsync("changed-readonly-input");
        await sandbox.WriteTextAsync(layout.ProjectId, "calculate.py", "print(1)");
        var script = Path.Combine(layout.WorkPath, "calculate.py");
        var input = Path.Combine(layout.InputsPath, "input.csv");
        await File.WriteAllTextAsync(input, "1");
        var first = await ResearchSandboxService.CaptureExecutionSnapshotAsync(layout, Guid.NewGuid().ToString("N"), script);
        await File.WriteAllTextAsync(input, "2");

        var error = await Assert.ThrowsAsync<IOException>(() => ResearchSandboxService.CompleteExecutionSnapshotAsync(layout, first));

        Assert.Contains("Eingabedatei", error.Message, StringComparison.Ordinal);
        Assert.Equal("print(1)", await File.ReadAllTextAsync(script));
        Assert.Equal("2", await File.ReadAllTextAsync(input));
        Assert.Empty(Directory.EnumerateFileSystemEntries(layout.SnapshotsPath));
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
    [InlineData("dependency")]
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
        await sandbox.WriteTextAsync(projectId, "dependency.py", "VALUE = 1");
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
            _ => Path.Combine(layout.WorkPath, "dependency.py"),
        };
        await File.AppendAllTextAsync(altered, "changed");
        var rejected = await ScientificDeliverablesVerifier.VerifyAsync(projectId, presentation);
        Assert.False(rejected.GetProperty("success").GetBoolean());
        Assert.Empty(rejected.GetProperty("simulation").GetProperty("artifacts").EnumerateArray());
    }

    private static ResearchSandboxService Sandbox(TestEnvironment environment) => new(AssistantRuntimeProfile.Resolve() with
        { DataDirectory = environment.Directory, NativeStateDirectory = Path.Combine(environment.Directory, "native") });
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
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
