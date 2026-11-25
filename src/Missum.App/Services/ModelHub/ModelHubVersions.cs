namespace Missum.App.Services.ModelHub;

public enum LocalQuantizationState
{
    /// <summary>No file of this quantization is in the folder.</summary>
    Missing,
    /// <summary>The folder holds this quantization exactly as it is on Hugging Face now.</summary>
    Current,
    /// <summary>The folder holds an older or incomplete copy; Hugging Face has a newer version.</summary>
    Outdated,
}

/// <summary>
/// The comparison result. For an update, <see cref="Reusable"/> maps the repository paths whose
/// content did not change (same SHA-256) to the local file that can be reused instead of a download.
/// </summary>
public sealed record LocalQuantizationStatus(LocalQuantizationState State, string? Folder, IReadOnlyDictionary<string, string> Reusable);

/// <summary>
/// Compares a quantization on Hugging Face with the copies in the download folder. A copy in the
/// snapshot of the current commit is current when every file is there with its size. A copy in an
/// older snapshot is current only if Hugging Face lists the same SHA-256 for every file at that
/// older commit as now; otherwise the model was changed and it is offered as an update. When the old
/// file list is unknown (no network, a folder that is no commit), equal sizes count as unchanged.
/// </summary>
public static class ModelHubVersions
{
    public static LocalQuantizationStatus Compare(
        HubQuantization quantization,
        string currentCommit,
        LocalHubRepo? local,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, HubFile>?> filesAtCommit)
    {
        var none = new Dictionary<string, string>(StringComparer.Ordinal);
        if (local is null) return new(LocalQuantizationState.Missing, null, none);
        var wanted = quantization.AllFiles.ToList();
        var anyPresent = false;
        string? bestFolder = null;
        var reusable = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var snapshot in local.Files.GroupBy(f => f.SnapshotFolder, StringComparer.OrdinalIgnoreCase))
        {
            var commit = Path.GetFileName(snapshot.Key);
            var byPath = snapshot.ToDictionary(f => f.RepoPath, StringComparer.OrdinalIgnoreCase);
            // Any vision projector in the same folder serves (F16, BF16 or F32 alike): treat it as the wanted one.
            foreach (var projector in quantization.Companions.Where(c => IsProjector(c.Path) && !byPath.ContainsKey(c.Path)))
            {
                var folder = projector.Path.Contains('/', StringComparison.Ordinal) ? projector.Path[..(projector.Path.LastIndexOf('/') + 1)] : "";
                var alternative = snapshot.FirstOrDefault(f => IsProjector(f.RepoPath)
                    && string.Equals(f.RepoPath.Contains('/', StringComparison.Ordinal) ? f.RepoPath[..(f.RepoPath.LastIndexOf('/') + 1)] : "", folder, StringComparison.OrdinalIgnoreCase));
                if (alternative is not null) byPath[projector.Path] = alternative with { Size = projector.Size, RepoPath = projector.Path, Alternative = true };
            }
            var present = wanted.Where(file => byPath.ContainsKey(file.Path)).ToList();
            // Shared companions (the vision projector) alone do not make a quantization present.
            if (!quantization.Parts.Any(part => byPath.ContainsKey(part.Path))) continue;
            anyPresent = true;
            bestFolder ??= snapshot.Key;
            filesAtCommit.TryGetValue(commit, out var old);
            var complete = present.Count == wanted.Count;
            var unchanged = new List<HubFile>();
            foreach (var file in present)
            {
                var local_ = byPath[file.Path];
                if (local_.Alternative) { unchanged.Add(file); continue; }
                if (local_.Size != file.Size) continue;
                var same = string.Equals(commit, currentCommit, StringComparison.OrdinalIgnoreCase)
                    || (old is not null && old.TryGetValue(file.Path, out var before)
                        ? before.Sha256 is not null && file.Sha256 is not null
                            ? string.Equals(before.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase)
                            : before.Size == file.Size
                        : old is null);
                if (!same) continue;
                unchanged.Add(file);
                reusable.TryAdd(file.Path, Path.Combine(snapshot.Key, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            }
            if (complete && unchanged.Count == wanted.Count)
                return new(LocalQuantizationState.Current, snapshot.Key, none);
        }
        return anyPresent
            ? new(LocalQuantizationState.Outdated, bestFolder, reusable)
            : new(LocalQuantizationState.Missing, null, none);
    }

    private static bool IsProjector(string path) =>
        path[(path.LastIndexOf('/') + 1)..].StartsWith("mmproj", StringComparison.OrdinalIgnoreCase);
}
