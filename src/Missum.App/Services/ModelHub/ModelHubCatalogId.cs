using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Missum.App.Services.ModelHub;

/// <summary>
/// Where downloads go and which id the native catalog gives them. Mirrors
/// workers/coding/catalog.py: the catalog scans "&lt;root&gt;\hub" when that folder holds
/// models--* repositories, and names a model "coding/&lt;stem&gt;~&lt;sha256(path relative to hub)[:12]&gt;".
/// </summary>
public static partial class ModelHubCatalogId
{
    [GeneratedRegex(@"^(?<stem>.*)-(?<index>\d{5})-of-(?<total>\d{5})\.gguf$", RegexOptions.IgnoreCase)]
    private static partial Regex Shard();

    [GeneratedRegex(@"[^A-Za-z0-9_.-]")]
    private static partial Regex Unsafe();

    /// <summary>The Hugging Face cache folder ("hub") inside a chosen download folder.</summary>
    public static string HubRoot(string downloadFolder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(downloadFolder));
        return string.Equals(Path.GetFileName(full), "hub", StringComparison.OrdinalIgnoreCase) ? full : Path.Combine(full, "hub");
    }

    public static string RepositoryFolder(string downloadFolder, string repo) =>
        Path.Combine(HubRoot(downloadFolder), "models--" + repo.Replace("/", "--", StringComparison.Ordinal));

    public static string SnapshotFolder(string downloadFolder, string repo, string commit) =>
        Path.Combine(RepositoryFolder(downloadFolder, repo), "snapshots", commit);

    public static string FilePath(string downloadFolder, string repo, string commit, string repoPath) =>
        Path.Combine(SnapshotFolder(downloadFolder, repo, commit), repoPath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The id the gateway lists for a downloaded quantization.</summary>
    public static string For(string repo, string commit, string primaryRepoPath)
    {
        var relative = "models--" + repo.Replace("/", "--", StringComparison.Ordinal) + "/snapshots/" + commit + "/" + primaryRepoPath;
        var fileName = primaryRepoPath[(primaryRepoPath.LastIndexOf('/') + 1)..];
        var shard = Shard().Match(fileName);
        var name = shard.Success ? shard.Groups["stem"].Value : Path.GetFileNameWithoutExtension(fileName);
        var safe = Unsafe().Replace(name, "_");
        if (safe.Length > 160) safe = safe[..160];
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relative))).ToLowerInvariant()[..12];
        return $"coding/{safe}~{digest}";
    }

    /// <summary>True when the gateway's model folder scans this download folder.</summary>
    public static bool GatewaySees(string downloadFolder, string? modelFolder)
    {
        var model = string.IsNullOrWhiteSpace(modelFolder) ? DefaultFolder : modelFolder;
        try { return string.Equals(HubRoot(downloadFolder), HubRoot(model), StringComparison.OrdinalIgnoreCase); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "huggingface");
}
