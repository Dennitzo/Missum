using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Research;

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
    public async Task FormalProfileExcludesUnusedLakeTreeButBindsProofAndRejectsUnmeasuredDependencyReads()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var sandbox = Sandbox(environment);
        var layout = await sandbox.EnsureProjectAsync("lean-large-dependencies");
        await sandbox.WriteTextAsync(layout.ProjectId, "check.py", "print('check')");
        var lean = Path.Combine(layout.WorkPath, "lean");
        var dependencies = Path.Combine(lean, ".lake", "packages", "mathlib", "Mathlib");
        Directory.CreateDirectory(dependencies);
        for (var index = 0; index < 4097; index++)
            await File.WriteAllTextAsync(Path.Combine(dependencies, index + ".lean"), "-- unused installed dependency");
        const string proof = "example : 1 = 1 := rfl";
        await File.WriteAllTextAsync(Path.Combine(lean, "Proof.lean"), proof);
        await File.WriteAllTextAsync(Path.Combine(lean, "MissumAudit.lean"), proof);
        await File.WriteAllTextAsync(Path.Combine(lean, "lean-toolchain"), "leanprover/lean4:v4.30.0");
        await File.WriteAllTextAsync(Path.Combine(lean, "lakefile.toml"), "name = \"missum_proofs\"");
        var script = Path.Combine(layout.WorkPath, "check.py");

        // The same large tree still exceeds the complete generic Python baseline.
        await Assert.ThrowsAsync<IOException>(() => ResearchSandboxService.CaptureExecutionSnapshotAsync(
            layout, Guid.NewGuid().ToString("N"), script));
        var snapshot = await ResearchSandboxService.CaptureExecutionSnapshotAsync(layout, Guid.NewGuid().ToString("N"),
            script, executionProfile: ResearchExecutionProfile.LeanProof);
        Assert.Equal(5, snapshot.InputHashes.Count);
        Assert.Equal(ResearchExecutionProfile.LeanProof, snapshot.ExecutionProfile);
        Assert.Equal(Hash(proof), snapshot.InputHashes["work/lean/Proof.lean"]);
        Assert.Contains("work/lean/MissumAudit.lean", snapshot.InputHashes.Keys);
        Assert.Contains("work/lean/lean-toolchain", snapshot.InputHashes.Keys);
        Assert.Contains("work/lean/lakefile.toml", snapshot.InputHashes.Keys);
        var measured = ResearchSandboxService.BindExecutionReadTrace(snapshot, new(
            new HashSet<string>(StringComparer.Ordinal) { "work/lean/Proof.lean", "work/lean/MissumAudit.lean" },
            new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal) { "work/check.py" }));
        Assert.Empty(await ResearchSandboxService.CompleteExecutionSnapshotAsync(layout, measured));
        await File.WriteAllTextAsync(Path.Combine(lean, "receipt.json"), "{\"success\":true}");
        var outputs = await ResearchSandboxService.CompleteExecutionSnapshotAsync(layout, measured);
        Assert.Contains("work/lean/receipt.json", outputs.Keys);
        Assert.DoesNotContain(outputs.Keys, path => path.Contains("/.lake/", StringComparison.Ordinal));

        var unmeasuredRead = new ResearchSandboxService.ExecutionReadTrace(
            new HashSet<string>(StringComparer.Ordinal) { "work/lean/.lake/packages/mathlib/Mathlib/0.lean" },
            new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
        Assert.Throws<IOException>(() => ResearchSandboxService.BindExecutionReadTrace(snapshot, unmeasuredRead));
        await File.WriteAllTextAsync(Path.Combine(lean, "Proof.lean"), "example : 2 = 2 := rfl");
        var error = await Assert.ThrowsAsync<IOException>(() => ResearchSandboxService.CompleteExecutionSnapshotAsync(layout, measured));
        Assert.Contains("work/lean/Proof.lean", error.Message, StringComparison.Ordinal);
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

    private static ResearchSandboxService Sandbox(TestEnvironment environment) => new(AssistantRuntimeProfile.Resolve() with
        { DataDirectory = environment.Directory, NativeStateDirectory = Path.Combine(environment.Directory, "native") });
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
