using System.Text.RegularExpressions;

namespace Missum.App.Services.ModelHub;

/// <summary>
/// Groups a repository's files into downloadable quantizations, the way unsloth lays them out:
/// single files at the root ("Model-Q4_K_M.gguf"), one folder per large quantization
/// ("UD-Q2_K_XL/Model-UD-Q2_K_XL-00001-of-00004.gguf") and companions next to them
/// (mmproj for vision, mtp drafts). Everything else (imatrix, *.gguf_file, support folders) is left out.
/// </summary>
public static partial class ModelHubQuantizations
{
    [GeneratedRegex(@"^(?<stem>.*)-(?<index>\d{5})-of-(?<total>\d{5})\.gguf$", RegexOptions.IgnoreCase)]
    private static partial Regex SplitPattern();

    [GeneratedRegex(@"(?:^|[-_.])(?<label>(?:UD-)?(?:I?Q\d[A-Z0-9_]*|BF16|F16|F32|MXFP4[A-Z0-9_]*))$", RegexOptions.IgnoreCase)]
    private static partial Regex LabelPattern();

    public static IReadOnlyList<HubQuantization> Group(IEnumerable<HubFile> files)
    {
        var ggufs = files.Where(f => f.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) && !IsExcluded(f.Path)).ToList();
        var projectors = ggufs.Where(f => FileName(f.Path).StartsWith("mmproj", StringComparison.OrdinalIgnoreCase)).ToList();
        var models = ggufs.Where(f => !IsCompanion(f.Path)).ToList();

        var groups = new Dictionary<string, List<HubFile>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in models)
        {
            var split = SplitPattern().Match(file.Path);
            var key = split.Success ? split.Groups["stem"].Value + "|" + split.Groups["total"].Value : file.Path;
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add(file);
        }

        var result = new List<HubQuantization>();
        foreach (var (key, parts) in groups)
        {
            var ordered = parts.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToList();
            var split = SplitPattern().Match(ordered[0].Path);
            // A split model is only offered when every part is in the repository.
            if (split.Success && ordered.Count != int.Parse(split.Groups["total"].Value, System.Globalization.CultureInfo.InvariantCulture)) continue;
            var stem = split.Success ? FileName(split.Groups["stem"].Value) : Path.GetFileNameWithoutExtension(FileName(ordered[0].Path));
            var folder = Folder(ordered[0].Path);
            result.Add(new HubQuantization(Label(stem, folder), ordered, Projector(projectors, folder)));
            _ = key;
        }
        return result.OrderBy(q => q.ModelBytes).ToList();
    }

    /// <summary>"Qwen3.8-Flash-Next-UD-Q2_K_XL" → "UD-Q2_K_XL"; a quantization folder name wins.</summary>
    internal static string Label(string stem, string folder)
    {
        var folderName = FileName(folder);
        if (folderName.Length > 0 && LabelPattern().Match(folderName) is { Success: true } byFolder && byFolder.Value.TrimStart('-', '_', '.').Length == folderName.Length)
            return folderName;
        var match = LabelPattern().Match(stem);
        return match.Success ? match.Groups["label"].Value : stem;
    }

    /// <summary>The vision projector catalog.py would pair: nearest mmproj walking up from the model's folder.</summary>
    private static List<HubFile> Projector(List<HubFile> projectors, string folder)
    {
        for (var current = folder; ; current = Folder(current))
        {
            var here = projectors.Where(p => string.Equals(Folder(p.Path), current, StringComparison.OrdinalIgnoreCase)).ToList();
            if (here.Count > 0)
            {
                // Prefer the 16-bit projector, the usual choice; never download all variants.
                var pick = here.OrderBy(p => FileName(p.Path).Contains("F16", StringComparison.OrdinalIgnoreCase) && !FileName(p.Path).Contains("BF16", StringComparison.OrdinalIgnoreCase) ? 0
                    : FileName(p.Path).Contains("BF16", StringComparison.OrdinalIgnoreCase) ? 1 : 2).First();
                return [pick];
            }
            if (current.Length == 0) return [];
        }
    }

    // Speculative-decoding drafts (mtp, dspark, eagle, draft) belong to a model; they are no model of their own.
    private static readonly string[] DraftPrefixes = ["mtp", "dspark", "eagle", "draft"];

    private static bool IsCompanion(string path)
    {
        var name = FileName(path);
        return name.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase)
            || DraftPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || path.Split('/').SkipLast(1).Any(part => DraftPrefixes.Any(prefix => part.Equals(prefix, StringComparison.OrdinalIgnoreCase)))
            || name.Contains("imatrix", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExcluded(string path) =>
        path.Contains("Mainline_Support", StringComparison.OrdinalIgnoreCase)
        || FileName(path).Contains("imatrix", StringComparison.OrdinalIgnoreCase);

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static string Folder(string path) => path.LastIndexOf('/') is var index and >= 0 ? path[..index] : "";
}
