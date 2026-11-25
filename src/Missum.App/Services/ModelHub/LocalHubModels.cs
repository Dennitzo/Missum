namespace Missum.App.Services.ModelHub;

/// <summary>A file already on disk: its path inside the repository, size and the snapshot folder that holds it.</summary>
public sealed record LocalHubFile(string RepoPath, long Size, string SnapshotFolder)
{
    /// <summary>Stands in for another file of the same role (e.g. mmproj-BF16 for mmproj-F16).</summary>
    public bool Alternative { get; init; }
}

/// <summary>An unsloth repository found in the download folder.</summary>
public sealed record LocalHubRepo(string Id, DateTimeOffset LastWrite, IReadOnlyList<LocalHubFile> Files)
{
    public string Name => new HubModel(Id, LastWrite, null, false).Name;

    /// <summary>True when every file of the quantization is present (same path and size) in one snapshot.</summary>
    public bool Has(HubQuantization quantization) => FolderOf(quantization) is not null;

    /// <summary>The snapshot folder that holds the whole quantization, or null.</summary>
    public string? FolderOf(HubQuantization quantization) =>
        Files.GroupBy(f => f.SnapshotFolder, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(snapshot => quantization.AllFiles.All(file =>
                snapshot.Any(local => string.Equals(local.RepoPath, file.Path, StringComparison.OrdinalIgnoreCase) && local.Size == file.Size)))
            ?.Key;
}

/// <summary>
/// Finds models that are already in a download folder – downloaded by the Model Hub, by an
/// earlier tool or copied by hand – in the Hugging Face cache layout the native catalog scans:
/// &lt;folder&gt;\hub\models--unsloth--X\snapshots\&lt;commit&gt;\&lt;path&gt;.gguf.
/// </summary>
public static class LocalHubModels
{
    public static IReadOnlyList<LocalHubRepo> Scan(string downloadFolder)
    {
        var hub = ModelHubCatalogId.HubRoot(downloadFolder);
        var result = new List<LocalHubRepo>();
        try
        {
            if (!Directory.Exists(hub)) return result;
            foreach (var repository in Directory.EnumerateDirectories(hub, "models--*--*"))
            {
                var parts = Path.GetFileName(repository)["models--".Length..].Split("--", 2);
                if (parts.Length != 2 || !parts[0].Equals(HuggingFaceHubClient.Author, StringComparison.OrdinalIgnoreCase)) continue;
                var snapshots = Path.Combine(repository, "snapshots");
                if (!Directory.Exists(snapshots)) continue;
                var files = new List<LocalHubFile>();
                var lastWrite = DateTimeOffset.MinValue;
                foreach (var snapshot in Directory.EnumerateDirectories(snapshots))
                {
                    foreach (var file in Directory.EnumerateFiles(snapshot, "*.gguf", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(snapshot, file).Replace(Path.DirectorySeparatorChar, '/');
                        if (relative.StartsWith(".download/", StringComparison.Ordinal)) continue;
                        var info = new FileInfo(file);
                        files.Add(new LocalHubFile(relative, info.Length, snapshot));
                        if (info.LastWriteTimeUtc > lastWrite) lastWrite = info.LastWriteTimeUtc;
                    }
                }
                if (files.Count > 0) result.Add(new LocalHubRepo(HuggingFaceHubClient.Author + "/" + parts[1], lastWrite, files));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { _ = exception; }
        return result.OrderByDescending(r => r.LastWrite).ToList();
    }
}
