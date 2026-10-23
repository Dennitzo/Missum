using Missum.App.Services;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class SciencePublicationScaffoldTests
{
    [Theory]
    [InlineData(ChatMode.General)]
    [InlineData(ChatMode.Coding)]
    public async Task OtherModesDoNotResolvePackagesOrWriteProjectFiles(ChatMode mode)
    {
        using var fixture = new Fixture();
        await SciencePublicationScaffold.EnsureFromPackageAsync(mode, fixture.Project, Path.Combine(fixture.Root, "missing"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Project));
    }

    [Fact]
    public async Task ScienceInstallsStandaloneToolAndPreservesManuscriptAndExistingScripts()
    {
        using var fixture = new Fixture();
        var package = fixture.CreatePackage("v1");
        var script = Path.Combine(fixture.Project, "Publikation-erzeugen.ps1");
        var manuscript = Path.Combine(fixture.Project, "Publikation.md");
        await File.WriteAllTextAsync(script, "# My existing script");
        await File.WriteAllTextAsync(manuscript, "# My authored manuscript");

        await SciencePublicationScaffold.EnsureFromPackageAsync(ChatMode.ClaudeScience, fixture.Project, package);

        Assert.Equal("# My existing script", await File.ReadAllTextAsync(script));
        Assert.Equal("# My authored manuscript", await File.ReadAllTextAsync(manuscript));
        Assert.True(File.Exists(Path.Combine(fixture.Project, "PDF-ERZEUGEN.md")));
        Assert.Equal("v1", await File.ReadAllTextAsync(Path.Combine(fixture.Project, ".publication-renderer", "Missum.Publication.Cli.exe")));
        Assert.True(File.Exists(Path.Combine(fixture.Project, ".publication-renderer", "Assets", "Scripts", "export-document.ps1")));
    }

    [Fact]
    public async Task RepeatedInstallationKeepsExistingPackageUntouched()
    {
        using var fixture = new Fixture();
        var package = fixture.CreatePackage("v1");
        await SciencePublicationScaffold.EnsureFromPackageAsync(ChatMode.ClaudeScience, fixture.Project, package);
        var executable = Path.Combine(fixture.Project, ".publication-renderer", "Missum.Publication.Cli.exe");
        var originalTime = DateTime.UtcNow.AddDays(-1);
        File.SetLastWriteTimeUtc(executable, originalTime);

        await SciencePublicationScaffold.EnsureFromPackageAsync(ChatMode.ClaudeScience, fixture.Project, package);

        Assert.Equal(originalTime, File.GetLastWriteTimeUtc(executable));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Project, ".publication-renderer.install-*"));
    }

    [Fact]
    public async Task NewPackageReplacesOnlyOwnedPayloadWhileKeepingUserScript()
    {
        using var fixture = new Fixture();
        await SciencePublicationScaffold.EnsureFromPackageAsync(ChatMode.ClaudeScience, fixture.Project, fixture.CreatePackage("v1"));
        var script = Path.Combine(fixture.Project, "Publikation-erzeugen.ps1");
        await File.WriteAllTextAsync(script, "# Project-specific wrapper");

        await SciencePublicationScaffold.EnsureFromPackageAsync(ChatMode.ClaudeScience, fixture.Project, fixture.CreatePackage("v2"));

        Assert.Equal("v2", await File.ReadAllTextAsync(Path.Combine(fixture.Project, ".publication-renderer", "Missum.Publication.Cli.exe")));
        Assert.Equal("# Project-specific wrapper", await File.ReadAllTextAsync(script));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Project, ".publication-renderer.previous-*"));
    }

    [Fact]
    public async Task ModifiedOwnedPayloadIsPreservedWhenAnUpdateArrives()
    {
        using var fixture = new Fixture();
        await SciencePublicationScaffold.EnsureFromPackageAsync(ChatMode.ClaudeScience, fixture.Project, fixture.CreatePackage("v1"));
        var executable = Path.Combine(fixture.Project, ".publication-renderer", "Missum.Publication.Cli.exe");
        await File.WriteAllTextAsync(executable, "custom");

        var error = await Assert.ThrowsAsync<IOException>(() => SciencePublicationScaffold.EnsureFromPackageAsync(
            ChatMode.ClaudeScience, fixture.Project, fixture.CreatePackage("v2")));

        Assert.Contains("überschreibt", error.Message);
        Assert.Equal("custom", await File.ReadAllTextAsync(executable));
    }

    [Fact]
    public async Task UnownedDirectoryIsNeverReplaced()
    {
        using var fixture = new Fixture();
        var directory = Path.Combine(fixture.Project, ".publication-renderer");
        Directory.CreateDirectory(directory);
        var custom = Path.Combine(directory, "my-file.txt");
        await File.WriteAllTextAsync(custom, "keep");

        await Assert.ThrowsAsync<InvalidOperationException>(() => SciencePublicationScaffold.EnsureFromPackageAsync(
            ChatMode.ClaudeScience, fixture.Project, fixture.CreatePackage("v1")));

        Assert.Equal("keep", await File.ReadAllTextAsync(custom));
    }

    [Fact]
    public async Task BusyPublicationDoesNotFailOptionalRefresh()
    {
        using var fixture = new Fixture();
        await using var busy = File.Open(Path.Combine(fixture.Project, ".publication-build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var warning = await SciencePublicationScaffold.TryEnsureFromPackageAsync(ChatMode.ClaudeScience,
            fixture.Project, fixture.CreatePackage("v1"));

        Assert.IsType<IOException>(warning);
        Assert.False(Directory.Exists(Path.Combine(fixture.Project, ".publication-renderer")));
    }

    [Fact]
    public async Task ExistingStandaloneRendererDoesNotBlockNewSessionOrOptionalRefresh()
    {
        using var fixture = new Fixture();
        var directory = Path.Combine(fixture.Project, ".publication-renderer");
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "Missum.Publication.Cli.exe");
        await File.WriteAllTextAsync(executable, "My standalone single-file renderer");
        var script = Path.Combine(fixture.Project, "Publikation-erzeugen.ps1");
        await File.WriteAllTextAsync(script, "# My wrapper");
        var package = fixture.CreatePackage("v1");

        await SciencePublicationScaffold.EnsureForCreationFromPackageAsync(ChatMode.ClaudeScience, fixture.Project, package);
        var warning = await SciencePublicationScaffold.TryEnsureFromPackageAsync(ChatMode.ClaudeScience, fixture.Project, package);

        Assert.NotNull(warning);
        Assert.Equal("My standalone single-file renderer", await File.ReadAllTextAsync(executable));
        Assert.Equal("# My wrapper", await File.ReadAllTextAsync(script));
        Assert.False(File.Exists(Path.Combine(directory, "installation.json")));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "missum-publication-scaffold-" + Guid.NewGuid().ToString("N"));
        internal string Project { get; }

        internal Fixture()
        {
            Project = Path.Combine(Root, "project");
            Directory.CreateDirectory(Project);
        }

        internal string CreatePackage(string version)
        {
            var package = Path.Combine(Root, version);
            var scripts = Path.Combine(package, "Renderer", "Assets", "Scripts");
            var rendering = Path.Combine(package, "Renderer", "Assets", "DocumentRendering");
            Directory.CreateDirectory(scripts);
            Directory.CreateDirectory(rendering);
            File.WriteAllText(Path.Combine(package, "Publikation-erzeugen.ps1"), "# Packaged script " + version);
            File.WriteAllText(Path.Combine(package, "PDF-ERZEUGEN.md"), "# Usage " + version);
            File.WriteAllText(Path.Combine(package, "Renderer", "Missum.Publication.Cli.exe"), version);
            File.WriteAllText(Path.Combine(scripts, "export-document.ps1"), "# Shared exporter " + version);
            File.WriteAllText(Path.Combine(rendering, "styles.css"), "/* Shared styles */");
            File.WriteAllText(Path.Combine(rendering, "markdown.js"), "/* Shared markdown */");
            return package;
        }

        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
