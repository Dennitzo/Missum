using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Missum.Core.Coding;

/// <summary>Excludes only Missum-owned publication copies and sandbox receipts, never authored research files.</summary>
public sealed class CodingGeneratedArtifactFilter
{
    private readonly string[] _excluded;
    private static readonly StringComparison Comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private CodingGeneratedArtifactFilter(string[] excluded) => _excluded = excluded;
    public static CodingGeneratedArtifactFilter Empty { get; } = new([]);
    public IReadOnlyList<string> ExcludedPaths => _excluded;

    public bool IsExcluded(string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        if (Path.IsPathRooted(path) || path.Split('/').Any(part => part is ".." or ".")) return false;
        return _excluded.Any(excluded => string.Equals(path, excluded, Comparison)
            || path.StartsWith(excluded + "/", Comparison));
    }

    public static CodingGeneratedArtifactFilter Discover(string workspace, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(workspace);
        var excluded = new List<string>();
        var science = Path.Combine(root, "Science");
        if (!SafeDirectory(science)) return new([]);
        try
        {
            foreach (var project in Directory.EnumerateDirectories(science))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!SafeDirectory(project)) continue;
                var projectId = Path.GetFileName(project);
                if (!projectId.StartsWith("research-", StringComparison.Ordinal)
                    || !Guid.TryParseExact(projectId[9..], "N", out _)) continue;
                using var sandbox = ReadMarker(Path.Combine(project, "sandbox.json"));
                if (sandbox is null || !Identity(sandbox.RootElement, projectId, 1)
                    || Text(sandbox.RootElement, "runtime") != "docker-linux-isolated") continue;
                var prefix = Path.GetRelativePath(root, project).Replace('\\', '/');
                var publicationKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(projectId)))[..24];
                // The exporter owns exactly this hash folder, including its .pending staging directories.
                excluded.Add(prefix + "/publications/" + publicationKey);
                var snapshots = Path.Combine(project, "snapshots");
                if (SafeDirectory(snapshots))
                    foreach (var snapshot in Directory.EnumerateDirectories(snapshots))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!SafeDirectory(snapshot)) continue;
                        var name = Path.GetFileName(snapshot);
                        using var marker = ReadMarker(Path.Combine(snapshot, "manifest.json"));
                        if (marker is null || !Identity(marker.RootElement, projectId, 1)) continue;
                        var data = marker.RootElement;
                        var ownedChange = Guid.TryParseExact(name, "N", out _)
                            && Text(data, "changeSetId") == name && !string.IsNullOrEmpty(Text(data, "relativePath"));
                        var ownedExecution = name.StartsWith("execution-", StringComparison.Ordinal)
                            && Guid.TryParseExact(name[10..], "N", out _)
                            && Text(data, "snapshotId") == name && Text(data, "runId") == name[10..];
                        if (ownedChange || ownedExecution) excluded.Add(prefix + "/snapshots/" + name);
                    }
                var runs = Path.Combine(project, "runs");
                if (SafeDirectory(runs))
                    foreach (var receipt in Directory.EnumerateFiles(runs, "*.json"))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var name = Path.GetFileNameWithoutExtension(receipt);
                        if (!Guid.TryParseExact(name, "N", out _)) continue;
                        using var marker = ReadMarker(receipt);
                        if (marker is not null && Identity(marker.RootElement, projectId, 2)
                            && Text(marker.RootElement, "runId") == name
                            && Text(marker.RootElement, "snapshotId") == "execution-" + name)
                            excluded.Add(prefix + "/runs/" + name + ".json");
                    }
            }
        }
        catch (IOException) { /* Unreadable ownership is never a reason to hide authored files. */ }
        catch (UnauthorizedAccessException) { }
        return new(excluded.ToArray());
    }

    private static bool SafeDirectory(string path)
    {
        try { return Directory.Exists(path) && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static JsonDocument? ReadMarker(string path)
    {
        try
        {
            if (!File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)
                || new FileInfo(path).Length > 2 * 1024 * 1024) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonDocument.Parse(stream);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    private static bool Identity(JsonElement marker, string projectId, int schema) => marker.ValueKind == JsonValueKind.Object
        && Text(marker, "projectId") == projectId && marker.TryGetProperty("schemaVersion", out var version)
        && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var value) && value == schema;
    private static string? Text(JsonElement marker, string property) => marker.ValueKind == JsonValueKind.Object
        && marker.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
