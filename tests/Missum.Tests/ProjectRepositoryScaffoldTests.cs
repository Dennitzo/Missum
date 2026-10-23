using System.Diagnostics;
using System.Text;
using Missum.Core.Coding;

namespace Missum.Tests;

public sealed class ProjectRepositoryScaffoldTests
{
    private static readonly byte[] Utf8Preamble = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] InvalidUtf8 = [0xFF, 0x00, 0x80];
    [Fact]
    public async Task MissingFilesReceiveGitHubReadmeAndNarrowLocalRulesWithoutInitializingGit()
    {
        using var fixture = new Fixture();

        var result = await ProjectRepositoryScaffold.EnsureAsync(fixture.Project, "Bohm [Forschung]");

        Assert.True(result.ReadmeCreated);
        Assert.True(result.GitIgnoreUpdated);
        Assert.True(result.Changed);
        Assert.Empty(result.DetectedTechnologies);
        var readme = await File.ReadAllTextAsync(Path.Combine(fixture.Project, "README.md"));
        Assert.StartsWith("# Bohm \\[Forschung\\]\n", readme, StringComparison.Ordinal);
        Assert.Contains("## Verwendung\n", readme, StringComparison.Ordinal);
        var ignore = await fixture.ReadIgnoreAsync();
        Assert.Contains("/.publication-renderer/\n", ignore, StringComparison.Ordinal);
        Assert.Contains("/Publikation-erzeugen.ps1\n", ignore, StringComparison.Ordinal);
        Assert.Contains("/PDF-ERZEUGEN.md\n", ignore, StringComparison.Ordinal);
        Assert.Contains("/publication.json\n", ignore, StringComparison.Ordinal);
        Assert.DoesNotContain("*.pdf", ignore, StringComparison.Ordinal);
        Assert.DoesNotContain("*.md", ignore, StringComparison.Ordinal);
        Assert.DoesNotContain("Quellen", ignore, StringComparison.Ordinal);
        Assert.DoesNotContain("plots", ignore, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(fixture.Project, ".git")));
        Assert.Empty(Directory.EnumerateFiles(fixture.Project, ".missum-repository-*.tmp"));
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("readme.md")]
    [InlineData("ReadMe.Md")]
    public async Task AuthoredReadmeIsNeverReplacedOrRenamed(string fileName)
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Project, fileName);
        var source = "# Mein Projekt\r\n\r\nMit ![Bild](Missum.png) und eigenen Anweisungen.\r\n";
        await File.WriteAllTextAsync(path, source, Encoding.Unicode);
        var before = await File.ReadAllBytesAsync(path);
        var time = DateTime.UtcNow.AddDays(-1);
        File.SetLastWriteTimeUtc(path, time);

        var result = await ProjectRepositoryScaffold.EnsureAsync(fixture.Project, "Another title");

        Assert.False(result.ReadmeCreated);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal(time, File.GetLastWriteTimeUtc(path));
        Assert.Single(Directory.EnumerateFiles(fixture.Project), file => string.Equals(Path.GetFileName(file), "README.md", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IgnoreAppendAndOwnedUpdatesPreserveAuthoredRulesEncodingAndLineEndings()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Project, ".gitignore");
        const string before = "# Eigene Regeln für größe Daten\r\nsecret.local\r\n!keep.local\r\n";
        await File.WriteAllTextAsync(path, before, new UTF8Encoding(true));
        await ProjectRepositoryScaffold.EnsureAsync(fixture.Project);
        const string after = "\r\n# Absichtlich veröffentlichte Forschung\r\n!Publikation.pdf\r\n";
        await File.AppendAllTextAsync(path, after);
        await File.WriteAllTextAsync(Path.Combine(fixture.Project, "pyproject.toml"), "[project]\nname = 'research'\n");

        var result = await ProjectRepositoryScaffold.EnsureAsync(fixture.Project);

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.True(bytes.AsSpan().StartsWith(Utf8Preamble));
        var content = await fixture.ReadIgnoreAsync();
        Assert.StartsWith(before, content, StringComparison.Ordinal);
        Assert.EndsWith(after, content, StringComparison.Ordinal);
        Assert.Contains("__pycache__/\r\n", content, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', content.Replace("\r\n", string.Empty, StringComparison.Ordinal));
        Assert.Contains("Python", result.DetectedTechnologies);
        Assert.Equal(1, Count(content, ProjectRepositoryScaffold.BeginMarker));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingUtf16IgnoreIsNotRecoded(bool bigEndian)
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Project, ".gitignore");
        var encoding = new UnicodeEncoding(bigEndian, true, true);
        const string original = "# Größe\r\nmeine-datei\r\n";
        await File.WriteAllTextAsync(path, original, encoding);

        await ProjectRepositoryScaffold.EnsureAsync(fixture.Project);

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.True(bytes.AsSpan().StartsWith(encoding.GetPreamble()));
        Assert.StartsWith(original, await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedCallsAreIdempotentAndDiscoverNewTopLevelManifests()
    {
        using var fixture = new Fixture();
        await ProjectRepositoryScaffold.EnsureAsync(fixture.Project);
        var ignore = Path.Combine(fixture.Project, ".gitignore");
        var readme = Path.Combine(fixture.Project, "README.md");
        var timestamp = DateTime.UtcNow.AddDays(-1);
        File.SetLastWriteTimeUtc(ignore, timestamp);
        File.SetLastWriteTimeUtc(readme, timestamp);

        var unchanged = await ProjectRepositoryScaffold.EnsureAsync(fixture.Project);

        Assert.False(unchanged.Changed);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(ignore));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(readme));
        await File.WriteAllTextAsync(Path.Combine(fixture.Project, "package.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(fixture.Project, "lakefile.lean"), "import Lake");
        var changed = await ProjectRepositoryScaffold.EnsureAsync(fixture.Project);
        Assert.True(changed.GitIgnoreUpdated);
        Assert.False(changed.ReadmeCreated);
        Assert.Contains("/node_modules/\n", await fixture.ReadIgnoreAsync(), StringComparison.Ordinal);
        Assert.Contains("/.lake/\n", await fixture.ReadIgnoreAsync(), StringComparison.Ordinal);
        Assert.Contains("Node.js", changed.DetectedTechnologies);
        Assert.Contains("Lean", changed.DetectedTechnologies);
    }

    [Theory]
    [InlineData("Research.sln", "bin/", ".NET")]
    [InlineData("Cargo.toml", "/target/", "Rust")]
    [InlineData("pom.xml", "/target/", "Maven")]
    [InlineData("build.gradle.kts", "/.gradle/", "Gradle")]
    [InlineData("CMakeLists.txt", "/CMakeFiles/", "CMake")]
    public async Task ManifestRulesAreSpecificAndDoNotIgnoreResearchInputs(string manifest, string expectedPattern, string technology)
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Project, manifest), string.Empty);

        var result = await ProjectRepositoryScaffold.EnsureAsync(fixture.Project);

        Assert.Contains(technology, result.DetectedTechnologies);
        var ignore = await fixture.ReadIgnoreAsync();
        Assert.Contains(expectedPattern + "\n", ignore, StringComparison.Ordinal);
        Assert.DoesNotContain("/work/", ignore, StringComparison.Ordinal);
        Assert.DoesNotContain("/data/", ignore, StringComparison.Ordinal);
        Assert.DoesNotContain("/build/", ignore, StringComparison.Ordinal);
        Assert.DoesNotContain("/dist/", ignore, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedSourcesAndGeneratedRuntimeAreNotTraversedForManifestDetection()
    {
        using var fixture = new Fixture();
        var nested = Path.Combine(fixture.Project, ".publication-renderer", "sources");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "package.json"), "{}");

        var result = await ProjectRepositoryScaffold.EnsureAsync(fixture.Project);

        Assert.Empty(result.DetectedTechnologies);
        Assert.DoesNotContain("node_modules", await fixture.ReadIgnoreAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TemporarilyMissingManifestDoesNotExposePreviouslyDetectedCaches()
    {
        using var fixture = new Fixture();
        var manifest = Path.Combine(fixture.Project, "package.json");
        await File.WriteAllTextAsync(manifest, "{}");
        await ProjectRepositoryScaffold.EnsureAsync(fixture.Project);
        var previous = await fixture.ReadIgnoreAsync();
        File.Delete(manifest);

        var result = await ProjectRepositoryScaffold.EnsureAsync(fixture.Project);

        Assert.False(result.GitIgnoreUpdated);
        Assert.Equal(previous, await fixture.ReadIgnoreAsync());
        Assert.Contains("/node_modules/\n", previous, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("# >>> Missum managed local files >>>\ncustom\n")]
    [InlineData("# <<< Missum managed local files <<<\n")]
    [InlineData("# >>> Missum managed local files >>>\n# >>> Missum managed local files >>>\n# <<< Missum managed local files <<<\n")]
    public async Task MalformedOwnedBlockCannotDestroyUserRules(string original)
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Project, ".gitignore"), original);

        await Assert.ThrowsAsync<InvalidDataException>(() => ProjectRepositoryScaffold.EnsureAsync(fixture.Project));

        Assert.Equal(original, await fixture.ReadIgnoreAsync());
        Assert.False(File.Exists(Path.Combine(fixture.Project, "README.md")));
    }

    [Fact]
    public async Task ConcurrentCallersProduceExactlyOneManagedBlockAndOneReadme()
    {
        using var fixture = new Fixture();

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => ProjectRepositoryScaffold.EnsureAsync(fixture.Project)));

        Assert.Equal(1, results.Count(result => result.ReadmeCreated));
        Assert.Equal(1, results.Count(result => result.GitIgnoreUpdated));
        Assert.Equal(1, Count(await fixture.ReadIgnoreAsync(), ProjectRepositoryScaffold.BeginMarker));
        Assert.Empty(Directory.EnumerateFiles(fixture.Project, ".missum-repository-*.tmp"));
    }

    [Fact]
    public async Task CancellationAndMissingDirectoryDoNotCreateProjectFiles()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectRepositoryScaffold.EnsureAsync(fixture.Project, cancellationToken: cancellation.Token));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => ProjectRepositoryScaffold.EnsureAsync(Path.Combine(fixture.Root, "missing")));

        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Project));
    }

    [Fact]
    public async Task InvalidTextAndDirectoryAtIgnorePathAreNeverOverwritten()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Project, ".gitignore");
        await File.WriteAllBytesAsync(path, InvalidUtf8);

        await Assert.ThrowsAsync<DecoderFallbackException>(() => ProjectRepositoryScaffold.EnsureAsync(fixture.Project));
        Assert.Equal(InvalidUtf8, await File.ReadAllBytesAsync(path));
        File.Delete(path);
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "keep.txt"), "authored");

        await Assert.ThrowsAsync<IOException>(() => ProjectRepositoryScaffold.EnsureAsync(fixture.Project));
        Assert.Equal("authored", await File.ReadAllTextAsync(Path.Combine(path, "keep.txt")));
    }

    [Fact]
    public async Task JunctionWorkspaceDoesNotWriteThroughToAnotherDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var target = Path.Combine(fixture.Root, "outside");
        var link = Path.Combine(fixture.Root, "linked-project");
        Directory.CreateDirectory(target);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => ProjectRepositoryScaffold.EnsureAsync(link));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
            var nested = Path.Combine(target, "nested-project");
            Directory.CreateDirectory(nested);
            await Assert.ThrowsAsync<IOException>(() => ProjectRepositoryScaffold.EnsureAsync(Path.Combine(link, "nested-project")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(nested));
        }
        finally { Directory.Delete(link); }
    }

    private static int Count(string value, string text) => (value.Length - value.Replace(text, string.Empty, StringComparison.Ordinal).Length) / text.Length;

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "missum-repository-scaffold-" + Guid.NewGuid().ToString("N"));
        internal string Project { get; }

        internal Fixture()
        {
            Project = Path.Combine(Root, "Research Project");
            Directory.CreateDirectory(Project);
        }

        internal Task<string> ReadIgnoreAsync() => File.ReadAllTextAsync(Path.Combine(Project, ".gitignore"));

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
