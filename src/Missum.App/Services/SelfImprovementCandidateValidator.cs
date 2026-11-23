using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Missum.App.Services;

/// <summary>
/// Re-checks a verified self-improvement candidate immediately before it may be applied.
/// The build report alone is never trusted: source state, portable files and image digests
/// are compared against what the broker recorded when the mandatory checks passed.
/// </summary>
internal static class SelfImprovementCandidateValidator
{
    internal static readonly string[] RequiredSteps = ["portable", "app-tests", "ai-stack", "portable-smoke", "gateway-smoke"];

    internal sealed record Problem(string Code, string Detail, IReadOnlyList<string>? Paths = null);

    internal static async Task<IReadOnlyList<Problem>> ValidateAsync(
        string candidateId, string candidateDirectory, string workspace, JsonElement report,
        Func<string, CancellationToken, Task<string?>>? imageDigest, CancellationToken cancellationToken)
    {
        var problems = new List<Problem>();
        if (S(report, "candidateId") != candidateId || S(report, "status") != "verified" || S(report, "phase") != "complete")
        {
            problems.Add(new("selfImprovement.candidate_not_verified",
                $"Der Kandidat ist nicht vollständig geprüft (Status '{S(report, "status")}', Phase '{S(report, "phase")}')."));
            return problems;
        }

        var steps = report.TryGetProperty("steps", out var stepArray) && stepArray.ValueKind == JsonValueKind.Array
            ? stepArray.EnumerateArray().ToArray() : [];
        foreach (var required in RequiredSteps)
        {
            var step = steps.FirstOrDefault(item => S(item, "name") == required);
            if (step.ValueKind != JsonValueKind.Object)
                problems.Add(new("selfImprovement.check_missing", $"Die Pflichtprüfung '{required}' fehlt im Bericht."));
            else if (!step.TryGetProperty("exitCode", out var exit) || !exit.TryGetInt32(out var code) || code != 0)
                problems.Add(new("selfImprovement.check_failed", $"Die Pflichtprüfung '{required}' ist nicht bestanden."));
        }
        if (problems.Count > 0) return problems;

        if (!report.TryGetProperty("artifacts", out var artifacts) || artifacts.ValueKind != JsonValueKind.Object)
            return [new("selfImprovement.artifacts_missing", "Der Bericht enthält keinen Artefaktsatz.")];
        string? Inside(string name)
        {
            var value = S(artifacts, name);
            if (value.Length == 0) return null;
            var full = Path.GetFullPath(value);
            return full.StartsWith(Path.GetFullPath(candidateDirectory).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        var sourceManifest = Inside("sourceManifest");
        var portableFiles = Inside("portableFiles");
        var portableDirectory = Inside("portableDirectory");
        var portableManifest = Inside("portableManifest");
        if (sourceManifest is null || portableFiles is null || portableDirectory is null || portableManifest is null)
            return [new("selfImprovement.artifacts_invalid", "Der Artefaktsatz ist unvollständig oder verweist außerhalb des Kandidaten.")];

        // Source: the recorded snapshot must still equal the workspace on disk.
        var current = await ListWorkspaceFilesAsync(workspace, cancellationToken).ConfigureAwait(false);
        var recorded = await ReadEntriesAsync(sourceManifest, cancellationToken).ConfigureAwait(false);
        if (recorded is null)
            problems.Add(new("selfImprovement.source_manifest_invalid", "Das Quellmanifest des Kandidaten ist nicht lesbar."));
        else
        {
            var changed = new List<string>();
            foreach (var (path, sha) in recorded)
            {
                var file = Path.Combine(workspace, path);
                if (!current.Contains(path) || !File.Exists(file) || !string.Equals(await HashAsync(file, cancellationToken).ConfigureAwait(false), sha, StringComparison.OrdinalIgnoreCase))
                    changed.Add(path);
            }
            changed.AddRange(current.Where(path => !recorded.ContainsKey(path) && File.Exists(Path.Combine(workspace, path))));
            if (changed.Count > 0)
                problems.Add(DescribeSourceDrift(changed));
        }

        // Portable: every file of the verified output, nothing more, nothing less.
        var portable = await ReadEntriesAsync(portableFiles, cancellationToken).ConfigureAwait(false);
        if (portable is null || portable.Count == 0 || !Directory.Exists(portableDirectory))
            problems.Add(new("selfImprovement.portable_missing", "Die geprüften Portable-Dateien fehlen."));
        else
        {
            var changed = new List<string>();
            foreach (var (path, sha) in portable)
            {
                var file = Path.Combine(portableDirectory, path);
                if (!File.Exists(file) || !string.Equals(await HashAsync(file, cancellationToken).ConfigureAwait(false), sha, StringComparison.OrdinalIgnoreCase))
                    changed.Add(path);
            }
            var root = Path.GetFullPath(portableDirectory).TrimEnd('\\') + "\\";
            changed.AddRange(Directory.EnumerateFiles(portableDirectory, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetFullPath(file)[root.Length..]).Where(path => !portable.ContainsKey(path)));
            if (changed.Count > 0)
                problems.Add(new("selfImprovement.portable_modified", "Portable-Dateien wurden nach der Prüfung verändert.", changed.Take(20).ToArray()));
            try
            {
                using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(portableManifest, cancellationToken).ConfigureAwait(false));
                var exe = Path.Combine(portableDirectory, "Missum Labs.exe");
                if (!File.Exists(exe) || !string.Equals(S(manifest.RootElement, "executableSha256"),
                        await HashAsync(exe, cancellationToken).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
                    problems.Add(new("selfImprovement.portable_manifest_mismatch", "Die Portable-Programmdatei entspricht nicht ihrem Publish-Manifest."));
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                problems.Add(new("selfImprovement.portable_manifest_invalid", "Das Portable-Manifest ist nicht lesbar: " + exception.Message));
            }
        }

        // Images: the exact digests recorded after the gateway smoke test.
        if (!artifacts.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Object || !images.EnumerateObject().Any())
            problems.Add(new("selfImprovement.images_missing", "Der Kandidat enthält keine Image-Digests."));
        else
        {
            imageDigest ??= InspectImageAsync;
            foreach (var image in images.EnumerateObject())
            {
                var expected = image.Value.GetString();
                var actual = await imageDigest(image.Name, cancellationToken).ConfigureAwait(false);
                if (expected is null || !expected.StartsWith("sha256:", StringComparison.Ordinal) || !string.Equals(expected, actual, StringComparison.Ordinal))
                    problems.Add(new("selfImprovement.image_digest_mismatch",
                        $"Image '{image.Name}' hat den Digest '{actual ?? "fehlt"}' statt '{expected}'."));
            }
        }
        return problems;
    }

    // Turns a list of drifted source paths into the report the AI receives.
    private static Problem DescribeSourceDrift(List<string> changed)
    {
        var ordered = changed.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var listed = ordered.Take(20).ToArray();
        var more = ordered.Length > listed.Length ? $" (weitere {ordered.Length - listed.Length})" : "";
        return new("selfImprovement.source_changed",
            $"Der Quellstand wurde nach der Prüfung verändert: {ordered.Length} Datei(en), u. a. {string.Join(", ", listed.Take(5))}{more}. "
            + "Erstelle mit selfImprovement.build einen neuen Kandidaten.", listed);
    }

    private static string S(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<Dictionary<string, string>?> ReadEntriesAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var items = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().ToArray()
                : document.RootElement.ValueKind == JsonValueKind.Object ? [document.RootElement] : [];
            foreach (var item in items)
            {
                var relative = S(item, "path").Replace('/', '\\');
                var sha = S(item, "sha256");
                if (relative.Length == 0 || sha.Length != 64 || Path.IsPathRooted(relative) || relative.Split('\\').Contains("..")) return null;
                result[relative] = sha;
            }
            return result;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    internal static async Task<HashSet<string>> ListWorkspaceFilesAsync(string workspace, CancellationToken cancellationToken)
    {
        var output = await RunAsync("git", ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], workspace, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Der aktuelle Quellstand konnte nicht über Git ermittelt werden.");
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(path => path.Replace('/', '\\'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<string?> InspectImageAsync(string image, CancellationToken cancellationToken) =>
        (await RunAsync("docker", ["image", "inspect", "--format", "{{.Id}}", image], null, cancellationToken).ConfigureAwait(false))?.Trim();

    private static async Task<string?> RunAsync(string file, string[] arguments, string? directory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (directory is not null) start.WorkingDirectory = directory;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var name in new[] { "GIT_DIR", "GIT_COMMON_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE" }) start.Environment.Remove(name);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            _ = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0 ? await stdout.ConfigureAwait(false) : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}
