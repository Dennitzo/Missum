using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Coding;

namespace Missum.Tests;

public sealed class CodingGeneratedArtifactFilterTests
{
    private const string ProjectId = "research-671e99452fa643ee81c6a08ceaa74e1a";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static string ProjectPrefix => "Science/" + ProjectId;
    private static string PublicationPrefix => ProjectPrefix + "/publications/"
        + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ProjectId)))[..24];

    [Fact]
    public async Task OnlyOwnedGeneratedCopiesAreExcludedWhileRealScienceSourceAndOutputsRemainTracked()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = await CreateWorkspaceAsync(environment.Directory);
        var snapshotId = Guid.NewGuid().ToString("N");
        var snapshot = Directory.CreateDirectory(Path.Combine(workspace, ProjectPrefix, "snapshots", snapshotId)).FullName;
        await File.WriteAllTextAsync(Path.Combine(snapshot, "manifest.json"), JsonSerializer.Serialize(new
        { schemaVersion = 1, projectId = ProjectId, changeSetId = snapshotId, relativePath = "plot.py" }));
        var filter = CodingGeneratedArtifactFilter.Discover(workspace);
        Assert.True(filter.IsExcluded(PublicationPrefix + "/r35-012345678901234567890123/Publikation.md"));
        Assert.True(filter.IsExcluded(PublicationPrefix + "/.pending-0123456789/Publikation.pdf"));
        Assert.True(filter.IsExcluded(ProjectPrefix + "/snapshots/" + snapshotId + "/before.bin"));
        Assert.False(filter.IsExcluded(ProjectPrefix + "/work/plot.py"));
        Assert.False(filter.IsExcluded(ProjectPrefix + "/work/animation.html"));
        Assert.False(filter.IsExcluded(ProjectPrefix + "/artifacts/measured.png"));
        Assert.False(filter.IsExcluded(ProjectPrefix + "/publications/authored/paper.md"));
        Assert.False(filter.IsExcluded(ProjectPrefix + "/snapshots/unowned/notes.md"));
        Assert.False(filter.IsExcluded(PublicationPrefix + "/../authored.md"));
    }

    [Theory]
    [InlineData("foreign-project", "docker-linux-isolated")]
    [InlineData(ProjectId, "other-runtime")]
    public async Task SimilarDirectoriesWithForeignOrUntrustedMarkersRemainVisible(string projectId, string runtime)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = await CreateWorkspaceAsync(environment.Directory);
        await File.WriteAllTextAsync(Path.Combine(workspace, ProjectPrefix, "sandbox.json"),
            JsonSerializer.Serialize(new { schemaVersion = 1, projectId, runtime }));
        Assert.False(CodingGeneratedArtifactFilter.Discover(workspace).IsExcluded(PublicationPrefix + "/r1-key/Publikation.md"));
    }

    [Fact]
    public async Task ReadingOldHugeReceiptFiltersTheProjectionWithoutRewritingTheJournal()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = await CreateWorkspaceAsync(environment.Directory);
        var receipt = Directory.CreateDirectory(Path.Combine(environment.Directory, "receipt")).FullName;
        var summary = new CodingChangesSummary(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), workspace, 42, DateTimeOffset.UtcNow,
            [new(PublicationPrefix + "/r35-key/Publikation.md", 82_000, 0, "+generated\n", false, "added"),
                new(ProjectPrefix + "/work/check.py", 56, 0, "+print('check')\n", false, "added")], false, null, "git-v1");
        var path = Path.Combine(receipt, "latest.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(summary, JsonOptions); await File.WriteAllBytesAsync(path, bytes);
        var projected = await CodingChangesMonitor.ReadLatestAsync(receipt);
        Assert.NotNull(projected); Assert.Equal(42, projected.Revision);
        Assert.Equal(ProjectPrefix + "/work/check.py", Assert.Single(projected.Files).Path);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task NewOwnershipExcludesOldGeneratedBaselineWithoutInventingDeletedSourceOrChangingUserIndex()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "workspace")).FullName;
        var publication = Directory.CreateDirectory(Path.Combine(workspace, PublicationPrefix, "r1-key")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(workspace, ProjectPrefix, "work")).FullName;
        await File.WriteAllTextAsync(Path.Combine(publication, "Publikation.md"), "old generated\n");
        await File.WriteAllTextAsync(Path.Combine(source, "check.py"), "original\n");
        var storage = Path.Combine(environment.Directory, "receipt");
        using (var baseline = new CodingGitChangeTracker(workspace, storage)) await baseline.InitializeAsync();
        var manifest = await File.ReadAllBytesAsync(Path.Combine(storage, "git-baseline.json"));
        await File.WriteAllTextAsync(Path.Combine(workspace, ProjectPrefix, "sandbox.json"), JsonSerializer.Serialize(new
            { schemaVersion = 1, projectId = ProjectId, runtime = "docker-linux-isolated" }));
        await File.WriteAllTextAsync(Path.Combine(publication, "Publikation.md"), "new generated\n");
        await File.WriteAllTextAsync(Path.Combine(source, "check.py"), "changed\n");
        using var resumed = new CodingGitChangeTracker(workspace, storage);
        var snapshot = await resumed.LoadExistingAsync();
        Assert.DoesNotContain(snapshot.Files, file => file.Path.StartsWith(PublicationPrefix, StringComparison.Ordinal));
        Assert.Contains("-original", Assert.Single(snapshot.Files, file => file.Path.EndsWith("check.py", StringComparison.Ordinal)).Diff, StringComparison.Ordinal);
        Assert.Equal(manifest, await File.ReadAllBytesAsync(Path.Combine(storage, "git-baseline.json")));
    }

    private static async Task<string> CreateWorkspaceAsync(string parent)
    {
        var workspace = Directory.CreateDirectory(Path.Combine(parent, "workspace")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(workspace, ProjectPrefix)).FullName;
        await File.WriteAllTextAsync(Path.Combine(project, "sandbox.json"), JsonSerializer.Serialize(new
            { schemaVersion = 1, projectId = ProjectId, runtime = "docker-linux-isolated" }));
        return workspace;
    }
}
