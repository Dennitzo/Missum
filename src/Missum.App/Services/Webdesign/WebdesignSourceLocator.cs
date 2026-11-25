namespace Missum.App.Services.Webdesign;

/// <summary>
/// Guesses where a marked element is written in the project, so a wish reads "vermutlich
/// index.html:42" instead of only a selector. It is a hint for the AI, which verifies it: the
/// search looks for the id, then the first classes, then the visible text, in markup files first.
/// </summary>
public static class WebdesignSourceLocator
{
    private static readonly string[] Ignored =
        [".git", ".missum", "node_modules", "dist", "build", ".next", ".nuxt", ".astro", ".svelte-kit", ".vite", ".cache", "bin", "obj"];
    private static readonly string[] MarkupExtensions = [".html", ".htm", ".jsx", ".tsx", ".vue", ".svelte", ".astro", ".php", ".njk", ".hbs", ".ejs"];
    private static readonly string[] StyleExtensions = [".css", ".scss", ".sass", ".less"];
    private const int MaximumFiles = 600;
    private const long MaximumFileBytes = 2 * 1024 * 1024;

    public static string? Find(string workspace, string root, string? id, IReadOnlyList<string> classes, string? text, string? tag)
    {
        try
        {
            var files = EnumerateFiles(workspace).ToArray();
            var markup = files.Where(file => MarkupExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                .OrderBy(file => InRoot(workspace, root, file) ? 0 : 1).ToArray();
            var probes = new List<string>();
            if (!string.IsNullOrWhiteSpace(id)) probes.AddRange([$"id=\"{id}\"", $"id='{id}'", $"id={id}"]);
            foreach (var name in classes.Take(2).Where(static name => name.Length > 1))
                probes.Add(name);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var snippet = text.Trim();
                if (snippet.Length > 40) snippet = snippet[..40];
                if (snippet.Length >= 4) probes.Add(snippet);
            }
            foreach (var probe in probes)
                if (Search(workspace, markup, probe) is { } hit) return hit;
            // A class that only appears in a stylesheet still points to where its look is defined.
            var styles = files.Where(file => StyleExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)).ToArray();
            foreach (var name in classes.Take(2).Where(static name => name.Length > 1))
                if (Search(workspace, styles, "." + name) is { } hit) return hit;
            if (!string.IsNullOrWhiteSpace(id) && Search(workspace, styles, "#" + id) is { } idHit) return idHit;
            _ = tag;
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool InRoot(string workspace, string root, string file)
    {
        if (root is "." or "") return true;
        var relative = Path.GetRelativePath(workspace, file).Replace('\\', '/');
        return relative.StartsWith(root.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Search(string workspace, IEnumerable<string> files, string probe)
    {
        foreach (var file in files)
        {
            var number = 0;
            foreach (var line in File.ReadLines(file))
            {
                number++;
                if (line.Contains(probe, StringComparison.Ordinal))
                    return Path.GetRelativePath(workspace, file).Replace('\\', '/') + ":" + number;
            }
        }
        return null;
    }

    private static IEnumerable<string> EnumerateFiles(string workspace)
    {
        var count = 0;
        var pending = new Stack<string>();
        pending.Push(workspace);
        while (pending.Count > 0 && count < MaximumFiles)
        {
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(directory); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    if (!Ignored.Contains(name, StringComparer.OrdinalIgnoreCase)
                        && !File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint)) pending.Push(entry);
                    continue;
                }
                var extension = Path.GetExtension(entry);
                if (!MarkupExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
                    && !StyleExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) continue;
                if (new FileInfo(entry).Length > MaximumFileBytes) continue;
                count++;
                yield return entry;
                if (count >= MaximumFiles) yield break;
            }
        }
    }
}
