using System.Security.Cryptography;
using System.Text;

namespace Missum.Core.Coding;

public sealed record ProjectRepositoryScaffoldResult(
    bool ReadmeCreated,
    bool GitIgnoreUpdated,
    IReadOnlyList<string> DetectedTechnologies)
{
    public bool Changed => ReadmeCreated || GitIgnoreUpdated;
}

/// <summary>Maintains only Missum's ignore rules and creates a missing GitHub README without replacing authored content.</summary>
public static class ProjectRepositoryScaffold
{
    internal const string BeginMarker = "# >>> Missum managed local files >>>";
    internal const string EndMarker = "# <<< Missum managed local files <<<";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf32LittleEndianBom = [0xFF, 0xFE, 0x00, 0x00];
    private static readonly byte[] Utf32BigEndianBom = [0x00, 0x00, 0xFE, 0xFF];
    private static readonly byte[] Utf16LittleEndianBom = [0xFF, 0xFE];
    private static readonly byte[] Utf16BigEndianBom = [0xFE, 0xFF];
    private static readonly char[] MarkdownHeadingEscapes = ['\\', '`', '*', '_', '[', ']', '<', '>', '#'];
    private static readonly string[] LocalPatterns =
    [
        "/.publication-renderer/",
        "/.publication-renderer.install-*/",
        "/.publication-renderer.previous-*/",
        "/Publikation-erzeugen.ps1",
        "/PDF-ERZEUGEN.md",
        "/publication-build.json",
        "/publication-build.json.*.tmp",
        "/publication-build.log",
        "/.publication-build.lock",
        "/publication.json",
        "/publication.json.*.tmp",
        "/.missum-publication-*/",
        "/.publication-*.tmp.pdf",
        "/.publication-build-*.tmp.pdf",
        "/.missum-repository-*.tmp",
    ];
    private static readonly HashSet<string> RetainedCachePatterns = new(StringComparer.Ordinal)
    {
        "/node_modules/", "/.npm/", "/.pnpm-store/",
        "__pycache__/", "*.pyc", "/.venv/", "/.pytest_cache/", "/.mypy_cache/", "/.ruff_cache/",
        "bin/", "obj/", "/.vs/", "*.suo", "*.user", "/target/", "/.lake/", "/.gradle/", "/CMakeFiles/", "/CMakeCache.txt",
    };

    public static async Task<ProjectRepositoryScaffoldResult> EnsureAsync(
        string workspacePath,
        string? projectTitle = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Der Projektordner existiert nicht: " + root);
        RejectReparseAncestors(root);
        await using var projectLock = await AcquireProjectLockAsync(root, cancellationToken).ConfigureAwait(false);
        RejectReparseAncestors(root);

        // Only directory entry names are inspected. Never recurse through research data, generated runtimes or source trees.
        var names = Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName)
            .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var technologies = new List<string>();
        var patterns = new List<string>(LocalPatterns);
        AddLanguagePatterns(names, technologies, patterns);
        var ignoreUpdated = await UpdateIgnoreAsync(root, patterns, cancellationToken).ConfigureAwait(false);
        var readmeCreated = false;
        if (!names.Contains("README.md"))
        {
            var title = string.IsNullOrWhiteSpace(projectTitle) ? Path.GetFileName(root) : projectTitle;
            var readme = BuildReadme(title);
            readmeCreated = await CreateReadmeAsync(root, readme, cancellationToken).ConfigureAwait(false);
        }
        return new(readmeCreated, ignoreUpdated, technologies.AsReadOnly());
    }

    private static void AddLanguagePatterns(HashSet<string> names, List<string> technologies, List<string> patterns)
    {
        if (names.Contains("package.json"))
        {
            technologies.Add("Node.js");
            patterns.AddRange(["/node_modules/", "/.npm/", "/.pnpm-store/"]);
        }
        if (names.Overlaps(["pyproject.toml", "setup.py", "setup.cfg", "requirements.txt", "Pipfile"]))
        {
            technologies.Add("Python");
            patterns.AddRange(["__pycache__/", "*.pyc", "/.venv/", "/.pytest_cache/", "/.mypy_cache/", "/.ruff_cache/"]);
        }
        if (names.Any(name => name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
        {
            technologies.Add(".NET");
            patterns.AddRange(["bin/", "obj/", "/.vs/", "*.suo", "*.user"]);
        }
        if (names.Contains("Cargo.toml"))
        {
            technologies.Add("Rust");
            patterns.Add("/target/");
        }
        if (names.Overlaps(["lakefile.lean", "lakefile.toml", "lean-toolchain"]))
        {
            technologies.Add("Lean");
            patterns.Add("/.lake/");
        }
        if (names.Contains("pom.xml"))
        {
            technologies.Add("Maven");
            patterns.Add("/target/");
        }
        if (names.Overlaps(["build.gradle", "build.gradle.kts"]))
        {
            technologies.Add("Gradle");
            patterns.Add("/.gradle/");
        }
        if (names.Contains("CMakeLists.txt"))
        {
            technologies.Add("CMake");
            patterns.AddRange(["/CMakeFiles/", "/CMakeCache.txt"]);
        }
    }

    private static string BuildReadme(string? title)
    {
        var heading = new string((title ?? "Projekt").Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
        if (heading.Length == 0) heading = "Projekt";
        foreach (var character in MarkdownHeadingEscapes)
            heading = heading.Replace(character.ToString(), "\\" + character, StringComparison.Ordinal);
        return "# " + heading + "\n\n"
            + "## Überblick\n\nProjektbeschreibung, Ziel und aktuellen Stand hier ergänzen.\n\n"
            + "## Verwendung\n\nVoraussetzungen und überprüfte Schritte zur Einrichtung und Verwendung hier dokumentieren.\n\n"
            + "## Projektstruktur\n\nDie relevanten Quelldateien, Daten und Dokumente mit ihrem Zweck hier beschreiben.\n";
    }

    private static async Task<bool> UpdateIgnoreAsync(string root, List<string> patterns, CancellationToken token)
    {
        var path = Path.Combine(root, ".gitignore");
        RejectReparsePoint(path);
        if (Directory.Exists(path)) throw new IOException(".gitignore ist ein Ordner und wird nicht ersetzt.");
        var original = File.Exists(path) ? await File.ReadAllBytesAsync(path, token).ConfigureAwait(false) : null;
        var (encoding, preambleLength) = DetectEncoding(original);
        var content = original is null ? string.Empty : encoding.GetString(original, preambleLength, original.Length - preambleLength);
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var begin = content.IndexOf(BeginMarker, StringComparison.Ordinal);
        var end = content.IndexOf(EndMarker, StringComparison.Ordinal);
        if (begin >= 0 || end >= 0)
        {
            if (begin < 0 || end < begin || !IsWholeLine(content, begin, BeginMarker.Length)
                || !IsWholeLine(content, end, EndMarker.Length)
                || content.IndexOf(BeginMarker, begin + BeginMarker.Length, StringComparison.Ordinal) >= 0
                || content.IndexOf(EndMarker, end + EndMarker.Length, StringComparison.Ordinal) >= 0)
                throw new InvalidDataException("Der verwaltete Missum-Bereich in .gitignore ist unvollständig oder mehrdeutig. Vorhandene Regeln bleiben unverändert.");
            // Manifest edits are often multi-step operations. Previously detected caches must not suddenly become publishable.
            foreach (var previousLine in content[(begin + BeginMarker.Length)..end].Split('\n'))
            {
                var previousPattern = previousLine.TrimEnd('\r');
                if (RetainedCachePatterns.Contains(previousPattern)) patterns.Add(previousPattern);
            }
        }
        var block = BeginMarker + newline + "# Keep project-specific rules outside this automatically maintained block." + newline
            + string.Join(newline, patterns.Distinct(StringComparer.Ordinal)) + newline + EndMarker;
        var updated = begin < 0
            ? content + (content.Length == 0 ? string.Empty : content.EndsWith('\n') ? newline : newline + newline) + block + newline
            : string.Concat(content.AsSpan(0, begin), block, content.AsSpan(end + EndMarker.Length));
        if (updated == content) return false;
        var body = encoding.GetBytes(updated);
        var bytes = preambleLength == 0 ? body : original![..preambleLength].Concat(body).ToArray();
        var temporary = Path.Combine(root, ".missum-repository-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
            RejectReparseAncestors(root);
            RejectReparsePoint(path);
            if (original is not null)
            {
                var latest = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
                if (!latest.AsSpan().SequenceEqual(original)) throw new IOException(".gitignore wurde während der Aktualisierung geändert. Die Änderung wird nicht überschrieben.");
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: original is not null);
            return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool IsWholeLine(string content, int start, int length) =>
        (start == 0 || content[start - 1] == '\n') && (start + length == content.Length || content[start + length] is '\r' or '\n');

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[]? bytes)
    {
        if (bytes is null) return (Utf8, 0);
        if (bytes.AsSpan().StartsWith(Utf8Bom)) return (Utf8, 3);
        if (bytes.AsSpan().StartsWith(Utf32LittleEndianBom)) return (new UTF32Encoding(false, true, true), 4);
        if (bytes.AsSpan().StartsWith(Utf32BigEndianBom)) return (new UTF32Encoding(true, true, true), 4);
        if (bytes.AsSpan().StartsWith(Utf16LittleEndianBom)) return (new UnicodeEncoding(false, true, true), 2);
        if (bytes.AsSpan().StartsWith(Utf16BigEndianBom)) return (new UnicodeEncoding(true, true, true), 2);
        return (Utf8, 0);
    }

    private static async Task<bool> CreateReadmeAsync(string root, string content, CancellationToken token)
    {
        var path = Path.Combine(root, "README.md");
        var temporary = Path.Combine(root, ".missum-repository-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, content, Utf8, token).ConfigureAwait(false);
            RejectReparseAncestors(root);
            // Recheck after preparation: an authored README can have appeared in the meantime, with any casing.
            if (Directory.EnumerateFileSystemEntries(root).Any(entry => string.Equals(Path.GetFileName(entry), "README.md", StringComparison.OrdinalIgnoreCase))) return false;
            token.ThrowIfCancellationRequested();
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path)) { return false; }
            return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void RejectReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Verknüpfte Projekt- oder Konfigurationspfade werden nicht automatisch verändert: " + path);
        }
        catch (FileNotFoundException) { }
    }

    private static void RejectReparseAncestors(string root)
    {
        for (string? current = root; current is not null; current = Path.GetDirectoryName(current))
            RejectReparsePoint(current);
    }

    private static async Task<FileStream> AcquireProjectLockAsync(string root, CancellationToken token)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Missum-RepositoryScaffold");
        Directory.CreateDirectory(directory);
        var identity = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
        var path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(identity))) + ".lock");
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            RejectReparsePoint(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException exception) when (attempt < 100 && (exception.HResult & 0xFFFF) is 11 or 32 or 33)
            { await Task.Delay(50, token).ConfigureAwait(false); }
        }
    }
}
