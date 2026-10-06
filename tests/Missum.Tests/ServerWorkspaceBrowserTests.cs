using Missum.App.Services;

namespace Missum.Tests;

public sealed class ServerWorkspaceBrowserTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Missum-folder-browser-" + Guid.NewGuid().ToString("N"));
    public ServerWorkspaceBrowserTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task ListsPcRootsWithoutInventingASelectedDirectory()
    {
        var result = await ServerWorkspaceBrowser.BrowseAsync(null);
        Assert.Null(result.Path);
        Assert.Empty(result.Directories);
        Assert.NotEmpty(result.Roots);
        Assert.All(result.Roots, root => Assert.True(Path.IsPathFullyQualified(root.Path)));
        Assert.Equal(Environment.MachineName, result.ServerName);
    }

    [Fact]
    public async Task NormalizesAbsolutePathsAndPreservesUnicodeSpacesAndEmptyFolders()
    {
        var child = Directory.CreateDirectory(Path.Combine(_root, "Überprüfung mit Leerzeichen")).FullName;
        Directory.CreateDirectory(Path.Combine(_root, "Zweiter Ordner"));
        File.WriteAllText(Path.Combine(_root, "Keine Datei auswählen.txt"), "test");
        var result = await ServerWorkspaceBrowser.BrowseAsync(Path.Combine(child, ".."));
        Assert.Equal(_root, result.Path);
        Assert.Equal(2, result.TotalDirectories);
        Assert.Contains(result.Directories, item => item.Path == child);
        Assert.Equal(_root, result.Breadcrumbs[^1].Path);
        var empty = await ServerWorkspaceBrowser.BrowseAsync(child);
        Assert.Equal(_root, empty.ParentPath);
        Assert.Empty(empty.Directories);
        Assert.Equal(child, empty.Path);
    }

    [Fact]
    public async Task PagesLargeDirectoriesAndSearchesTheWholeDirectory()
    {
        for (var index = 0; index < ServerWorkspaceBrowser.PageSize + 3; index++)
            Directory.CreateDirectory(Path.Combine(_root, $"Ordner-{index:D4}"));
        var first = await ServerWorkspaceBrowser.BrowseAsync(_root);
        var next = await ServerWorkspaceBrowser.BrowseAsync(_root, offset: ServerWorkspaceBrowser.PageSize);
        Assert.True(first.HasMore);
        Assert.Equal(ServerWorkspaceBrowser.PageSize, first.Directories.Count);
        Assert.False(next.HasMore);
        Assert.Equal(3, next.Directories.Count);
        Assert.Empty(first.Directories.Intersect(next.Directories));
        var filtered = await ServerWorkspaceBrowser.BrowseAsync(_root, "0202");
        Assert.Single(filtered.Directories);
        Assert.Equal("Ordner-0202", filtered.Directories[0].Name);
    }

    [Fact]
    public async Task RejectsRelativeFilesMissingDirectoriesAndInvalidPaging()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => ServerWorkspaceBrowser.BrowseAsync("relative-folder"));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => ServerWorkspaceBrowser.BrowseAsync(Path.Combine(_root, "missing")));
        var file = Path.Combine(_root, "file.txt");
        File.WriteAllText(file, "test");
        await Assert.ThrowsAnyAsync<IOException>(() => ServerWorkspaceBrowser.BrowseAsync(file));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ServerWorkspaceBrowser.BrowseAsync(_root, offset: -1));
    }

    [Fact]
    public async Task HonorsCancelledBrowserRequests()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ServerWorkspaceBrowser.BrowseAsync(_root, cancellationToken: cancellation.Token));
    }
}
