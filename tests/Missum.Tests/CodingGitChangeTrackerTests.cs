using Missum.Core.Coding;
using System.Diagnostics;

namespace Missum.Tests;

public sealed class CodingGitChangeTrackerTests
{
    [Fact]
    public async Task ScienceWorkspaceTracksPublicationBinaryAndPythonSourceInTheSameOverview()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "science-workspace")).FullName;
        await CodingWorkspaceGit.EnsureRepositoryAsync(workspace);
        using var tracker = new CodingGitChangeTracker(workspace, Path.Combine(environment.Directory, "receipt"));
        Assert.Empty((await tracker.InitializeAsync()).Files);
        var science = Directory.CreateDirectory(Path.Combine(workspace, "Science", "research-test")).FullName;
        await File.WriteAllTextAsync(Path.Combine(science, "simulation.py"), "import math\nprint(math.pi)\n");
        await File.WriteAllBytesAsync(Path.Combine(science, "Publikation.pdf"), [37, 80, 68, 70, 45, 0, 255]);
        var snapshot = await tracker.RefreshAsync();
        Assert.False(snapshot.IsPartial);
        var python = Assert.Single(snapshot.Files, file => file.Path.EndsWith("simulation.py", StringComparison.Ordinal));
        Assert.Contains("+import math", python.Diff, StringComparison.Ordinal); Assert.Equal(2, python.AddedLines);
        var pdf = Assert.Single(snapshot.Files, file => file.Path.EndsWith("Publikation.pdf", StringComparison.Ordinal));
        Assert.True(pdf.IsBinary); Assert.Contains("Binary files", pdf.Diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitDiffTracksNewEditedDeletedAndRevertedFilesWithoutChangingUserIndex()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "workspace")).FullName;
        await File.WriteAllTextAsync(Path.Combine(workspace, "source.txt"), "before\n");
        await File.WriteAllTextAsync(Path.Combine(workspace, "deleted.txt"), "deleted\n");
        await CodingWorkspaceGit.EnsureRepositoryAsync(workspace);
        await GitAsync(workspace, ["add", "source.txt"]);
        var index = await File.ReadAllBytesAsync(Path.Combine(workspace, ".git", "index"));
        using var tracker = new CodingGitChangeTracker(workspace, Path.Combine(environment.Directory, "run"));
        Assert.Empty((await tracker.InitializeAsync()).Files);
        await File.WriteAllTextAsync(Path.Combine(workspace, "source.txt"), "after\n");
        await File.WriteAllTextAsync(Path.Combine(workspace, "new file.txt"), "new\n");
        File.Delete(Path.Combine(workspace, "deleted.txt"));
        var changes = await tracker.RefreshAsync();
        Assert.False(changes.IsPartial);
        Assert.Equal(3, changes.Files.Count);
        var edit = Assert.Single(changes.Files, file => file.Path == "source.txt");
        Assert.Equal(1, edit.AddedLines);
        Assert.Equal(1, edit.RemovedLines);
        Assert.Contains("diff --git", edit.Diff);
        Assert.Contains("-before", edit.Diff);
        Assert.Contains("+after", edit.Diff);
        Assert.Equal(index, await File.ReadAllBytesAsync(Path.Combine(workspace, ".git", "index")));
        await File.WriteAllTextAsync(Path.Combine(workspace, "source.txt"), "before\n");
        await File.WriteAllTextAsync(Path.Combine(workspace, "deleted.txt"), "deleted\n");
        File.Delete(Path.Combine(workspace, "new file.txt"));
        Assert.Empty((await tracker.RefreshAsync()).Files);
    }

    [Fact]
    public async Task RestartPreservesRunStartAndNextRunStartsEmpty()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "workspace")).FullName;
        var storage = Path.Combine(environment.Directory, "run");
        var path = Path.Combine(workspace, "source.txt");
        await File.WriteAllTextAsync(path, "original\n");
        using (var initial = new CodingGitChangeTracker(workspace, storage)) await initial.InitializeAsync();
        await File.WriteAllTextAsync(path, "changed\n");
        using (var resumed = new CodingGitChangeTracker(workspace, storage))
            Assert.Contains("-original", Assert.Single((await resumed.LoadExistingAsync()).Files).Diff);
        using var next = new CodingGitChangeTracker(workspace, Path.Combine(environment.Directory, "next"));
        Assert.Empty((await next.InitializeAsync()).Files);
    }

    [Fact]
    public async Task LargeWorkspaceAndTextFilesHaveNoOldSnapshotLimits()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "workspace")).FullName;
        await using (var large = File.Create(Path.Combine(workspace, "data.bin"))) large.SetLength(65L * 1024 * 1024);
        var path = Path.Combine(workspace, "large.txt");
        await File.WriteAllTextAsync(path, string.Concat(Enumerable.Repeat("content line\n", 200_000)));
        using var tracker = new CodingGitChangeTracker(workspace, Path.Combine(environment.Directory, "run"));
        Assert.False((await tracker.InitializeAsync()).IsPartial);
        await File.AppendAllTextAsync(path, "new final line\n");
        var changes = await tracker.RefreshAsync();
        Assert.False(changes.IsPartial);
        var file = Assert.Single(changes.Files);
        Assert.Equal(1, file.AddedLines);
        Assert.Contains("+new final line", file.Diff);
    }

    [Fact]
    public async Task ConcurrentTrackerInstancesKeepOneBaselineAndUsePrivateIndicesWithoutDeletingAnExistingLock()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "project")).FullName;
        var storage = Path.Combine(environment.Directory, "receipt");
        var source = Path.Combine(workspace, "source.txt");
        await File.WriteAllTextAsync(source, "original\n");
        var trackers = Enumerable.Range(0, 8).Select(_ => new CodingGitChangeTracker(workspace, storage)).ToArray();
        try
        {
            var initial = await Task.WhenAll(trackers.Select(tracker => tracker.InitializeAsync()));
            Assert.All(initial, snapshot => Assert.Empty(snapshot.Files));
            var manifest = await File.ReadAllBytesAsync(Path.Combine(storage, "git-baseline.json"));
            var stale = Path.Combine(storage, "git-comparison", "index.lock");
            await File.WriteAllTextAsync(stale, "existing lock must stay intact");
            await File.WriteAllTextAsync(source, "changed\n");
            var results = await Task.WhenAll(trackers.Select(tracker => tracker.RefreshAsync()));
            Assert.All(results, snapshot =>
            {
                Assert.False(snapshot.IsPartial);
                var change = Assert.Single(snapshot.Files);
                Assert.Contains("-original", change.Diff, StringComparison.Ordinal);
                Assert.Contains("+changed", change.Diff, StringComparison.Ordinal);
            });
            Assert.Equal(manifest, await File.ReadAllBytesAsync(Path.Combine(storage, "git-baseline.json")));
            Assert.Equal("existing lock must stay intact", await File.ReadAllTextAsync(stale));
            Assert.Empty(Directory.EnumerateFiles(storage, ".git-index-*"));
        }
        finally { foreach (var tracker in trackers) tracker.Dispose(); }
    }

    [Fact]
    public async Task RestartIgnoresTheOldComparisonIndexLockAndRetainsTheOriginalTree()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "project")).FullName;
        var storage = Path.Combine(environment.Directory, "receipt");
        var source = Path.Combine(workspace, "source.txt");
        await File.WriteAllTextAsync(source, "original\n");
        using (var initial = new CodingGitChangeTracker(workspace, storage)) await initial.InitializeAsync();
        var manifest = await File.ReadAllBytesAsync(Path.Combine(storage, "git-baseline.json"));
        var stale = Path.Combine(storage, "git-comparison", "index.lock");
        await File.WriteAllBytesAsync(stale, []);
        await File.WriteAllTextAsync(source, "after restart\n");
        using var resumed = new CodingGitChangeTracker(workspace, storage);
        var snapshot = await resumed.LoadExistingAsync();
        Assert.Contains("-original", Assert.Single(snapshot.Files).Diff, StringComparison.Ordinal);
        Assert.Contains("+after restart", snapshot.Files[0].Diff, StringComparison.Ordinal);
        Assert.True(File.Exists(stale));
        Assert.Equal(manifest, await File.ReadAllBytesAsync(Path.Combine(storage, "git-baseline.json")));
        Assert.Empty(Directory.EnumerateFiles(storage, ".git-index-*"));
    }

    [Fact]
    public async Task CancellingAnActiveGitFilterWaitsForItsExitAndCleansOnlyItsPrivateIndex()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "project")).FullName;
        var storage = Path.Combine(environment.Directory, "receipt");
        var source = Path.Combine(workspace, "source.txt");
        await File.WriteAllTextAsync(source, "original\n");
        using var tracker = new CodingGitChangeTracker(workspace, storage);
        await tracker.InitializeAsync();
        var manifest = await File.ReadAllBytesAsync(Path.Combine(storage, "git-baseline.json"));
        var filterReady = Path.Combine(storage, "filter-ready");
        // Signal only after the slow filter child has been spawned. An index
        // lock alone precedes the filter and can race a later process fork.
        await File.AppendAllTextAsync(Path.Combine(storage, "git-comparison", "config"),
            "\n[filter \"missum-cancel-test\"]\n clean = \"sleep 20 & child=$!; printf %s $child > ../receipt/filter-ready; wait $child; cat\"\n required = true\n");
        var attributes = Path.Combine(workspace, ".gitattributes");
        await File.WriteAllTextAsync(attributes, "*.txt filter=missum-cancel-test\n");
        await File.WriteAllTextAsync(source, "cancelled capture\n");
        using var cancellation = new CancellationTokenSource();
        var refresh = tracker.RefreshAsync(cancellation.Token);
        try
        {
            var deadline = Stopwatch.StartNew();
            while ((!File.Exists(filterReady) || new FileInfo(filterReady).Length == 0
                    || !Directory.EnumerateFiles(storage, ".git-index-*.lock").Any())
                   && deadline.Elapsed < TimeSpan.FromSeconds(30))
                await Task.Delay(20);
            Assert.True(File.Exists(filterReady), "The filter must spawn its slow child before cancellation.");
            Assert.Matches("^[0-9]+$", await File.ReadAllTextAsync(filterReady));
            Assert.NotEmpty(Directory.EnumerateFiles(storage, ".git-index-*.lock"));
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await refresh.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
        }
        Assert.Empty(Directory.EnumerateFiles(storage, ".git-index-*"));
        Assert.Equal(manifest, await File.ReadAllBytesAsync(Path.Combine(storage, "git-baseline.json")));
        File.Delete(filterReady);
        File.Delete(attributes);
        var recovered = await tracker.RefreshAsync();
        Assert.Contains("-original", Assert.Single(recovered.Files).Diff, StringComparison.Ordinal);
        Assert.Contains("+cancelled capture", recovered.Files[0].Diff, StringComparison.Ordinal);
    }

    private static async Task GitAsync(string workspace, string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }
}
