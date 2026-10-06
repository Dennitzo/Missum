namespace Missum.App.Services;

public sealed record ServerWorkspaceEntry(string Name, string Path);
public sealed record ServerWorkspaceListing(string ServerName, string? Path, string? ParentPath,
    IReadOnlyList<ServerWorkspaceEntry> Breadcrumbs, IReadOnlyList<ServerWorkspaceEntry> Roots,
    IReadOnlyList<ServerWorkspaceEntry> Directories, int Offset, int TotalDirectories, bool HasMore);

/// <summary>Lists the PC's folders for browser clients, independently of the AI gateway.</summary>
public static class ServerWorkspaceBrowser
{
    public const int PageSize = 200;

    public static Task<ServerWorkspaceListing> BrowseAsync(string? path, string? filter = null, int offset = 0,
        CancellationToken cancellationToken = default) => Task.Run(() => Browse(path, filter, offset, cancellationToken), cancellationToken);

    private static ServerWorkspaceListing Browse(string? requestedPath, string? filter, int offset, CancellationToken token)
    {
        if (offset is < 0 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(offset));
        if (filter?.Length > 256) throw new ArgumentException("Der Ordnerfilter ist zu lang.", nameof(filter));
        token.ThrowIfCancellationRequested();
        var roots = new List<ServerWorkspaceEntry>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!drive.IsReady) continue;
                var label = drive.VolumeLabel;
                roots.Add(new(string.IsNullOrWhiteSpace(label) ? drive.Name : $"{drive.Name} · {label}", drive.RootDirectory.FullName));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        var path = requestedPath?.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return new(Environment.MachineName, null, null, [], roots, [], 0, 0, false);
        if (path.Length > 4096 || !System.IO.Path.IsPathFullyQualified(path))
            throw new ArgumentException("Gib einen vollständigen Ordnerpfad auf dem Server-PC an.", nameof(requestedPath));
        path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        // Enumerating rather than Directory.Exists preserves meaningful permission and I/O failures.
        var directories = new List<ServerWorkspaceEntry>();
        foreach (var child in Directory.EnumerateDirectories(path))
        {
            token.ThrowIfCancellationRequested();
            var name = System.IO.Path.GetFileName(child);
            if (string.IsNullOrEmpty(filter) || name.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
                directories.Add(new(name, child));
        }
        directories.Sort((left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
        var breadcrumbs = new List<ServerWorkspaceEntry>();
        for (string? current = path; current is not null; current = Directory.GetParent(current)?.FullName)
            breadcrumbs.Add(new(System.IO.Path.GetFileName(current) is { Length: > 0 } name ? name : current, current));
        breadcrumbs.Reverse();
        return new(Environment.MachineName, path, Directory.GetParent(path)?.FullName, breadcrumbs, roots,
            directories.Skip(offset).Take(PageSize).ToArray(), offset, directories.Count, offset + PageSize < directories.Count);
    }
}
