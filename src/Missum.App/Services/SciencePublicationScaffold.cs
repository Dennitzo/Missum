using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Missum.Core.Models;
using Missum.Core.Coding;

namespace Missum.App.Services;

/// <summary>Installs the standalone publication tool only in user-selected Claude Science workspaces.</summary>
internal static class SciencePublicationScaffold
{
    private const string Owner = "missum.science.publication-tool.v1";
    private const string MarkerName = "installation.json";
    private static readonly string[] Templates = ["Publikation-erzeugen.ps1", "PDF-ERZEUGEN.md"];
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Lazy<Task<RendererPackage>>> Packages = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static Task EnsureAsync(ChatMode mode, string workspace, CancellationToken cancellationToken = default) =>
        mode == ChatMode.ClaudeScience
            ? EnsureForCreationFromPackageAsync(mode, workspace, ApplicationAssets.ResolvePath("Assets", "PublicationTool"), cancellationToken)
            : Task.CompletedTask;

    internal static Task<Exception?> TryEnsureAsync(ChatMode mode, string workspace, CancellationToken cancellationToken = default) =>
        mode == ChatMode.ClaudeScience
            ? TryEnsureFromPackageAsync(mode, workspace, ApplicationAssets.ResolvePath("Assets", "PublicationTool"), cancellationToken)
            : Task.FromResult<Exception?>(null);

    internal static async Task EnsureForCreationFromPackageAsync(ChatMode mode, string workspace, string packageRoot,
        CancellationToken cancellationToken = default)
    {
        if (mode != ChatMode.ClaudeScience) return;
        // An existing project can intentionally carry a different standalone
        // renderer. Starting another session must not require replacing it.
        if (File.Exists(Path.Combine(workspace, ".publication-renderer", "Missum.Publication.Cli.exe")))
        {
            var warning = await TryEnsureFromPackageAsync(mode, workspace, packageRoot, cancellationToken).ConfigureAwait(false);
            if (warning is not null)
                System.Diagnostics.Trace.TraceWarning("Vorhandenes Publikationspaket bleibt unverändert; optionale Aktualisierung zurückgestellt: {0}", warning.Message);
            return;
        }
        await EnsureFromPackageAsync(mode, workspace, packageRoot, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<Exception?> TryEnsureFromPackageAsync(ChatMode mode, string workspace, string packageRoot,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureFromPackageAsync(mode, workspace, packageRoot, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException)
        {
            // Optional refresh never makes an otherwise usable Science session
            // fail. Cancellation remains a cancellation and is not swallowed.
            return exception;
        }
    }

    internal static async Task EnsureFromPackageAsync(ChatMode mode, string workspace, string packageRoot,
        CancellationToken cancellationToken = default)
    {
        if (mode != ChatMode.ClaudeScience) return;
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        var root = Path.GetFullPath(workspace);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Der gewählte Science-Projektordner existiert nicht: " + root);
        RejectReparsePoint(root);
        var packagePath = Path.GetFullPath(packageRoot);
        foreach (var template in Templates)
            if (!File.Exists(Path.Combine(packagePath, template)))
                throw new FileNotFoundException("Das mitgelieferte Publikationswerkzeug ist unvollständig: " + template);
        var rendererSource = Path.Combine(packagePath, "Renderer");
        var package = await Packages.GetOrAdd(rendererSource,
            path => new Lazy<Task<RendererPackage>>(() => ReadPackageAsync(path))).Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        // Install ignore rules before copying the local runtime into an existing
        // project too, including optional refreshes of older Science sessions.
        await ProjectRepositoryScaffold.EnsureAsync(root, cancellationToken: cancellationToken).ConfigureAwait(false);
        var gate = Gates.GetOrAdd(root, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Use the same project lock as the CLI. An executing renderer must not
            // lose its executable or dependencies during a package update.
            var lockPath = SafePath(root, ".publication-build.lock");
            await using var projectLock = OpenProjectLock(lockPath);
            var target = SafePath(root, ".publication-renderer");
            var installed = await ReadInstallationAsync(target, cancellationToken).ConfigureAwait(false);
            if (Directory.Exists(target) && installed is null)
            {
                // A manually copied, byte-identical Missum payload can be adopted
                // without changing any of its files. Unknown content stays protected.
                if (!await IsExactPackageCopyAsync(target, package, cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Der vorhandene Ordner .publication-renderer gehört nicht zu einer von Missum verwalteten Installation und wird nicht überschrieben: " + target);
                installed = new Installation(Owner, package.PackageId, package.Files);
                await File.WriteAllTextAsync(SafePath(target, MarkerName), JsonSerializer.Serialize(installed, JsonOptions),
                    new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            }
            if (installed?.PackageId != package.PackageId || !package.Files.Keys.All(path => File.Exists(SafePath(target, path))))
            {
                if (installed is not null) await VerifyOwnedInstallationAsync(target, installed, cancellationToken).ConfigureAwait(false);
                await InstallRendererAsync(root, target, package, cancellationToken).ConfigureAwait(false);
            }
            foreach (var name in Templates)
            {
                var destination = SafePath(root, name);
                if (Directory.Exists(destination)) throw new IOException("Das Publikationsskript kann nicht angelegt werden, weil dort ein Ordner liegt: " + destination);
                if (File.Exists(destination)) continue; // Authored project scripts and documentation remain untouched.
                try { File.Copy(Path.Combine(packagePath, name), destination, overwrite: false); }
                catch (IOException) when (File.Exists(destination)) { }
            }
        }
        finally { gate.Release(); }
    }

    private static FileStream OpenProjectLock(string path)
    {
        try { return File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception)
        {
            throw new IOException("Das Publikationswerkzeug kann während einer laufenden PDF-Erzeugung nicht installiert oder aktualisiert werden. Bitte die Erzeugung zuerst beenden lassen.", exception);
        }
    }

    private static async Task<RendererPackage> ReadPackageAsync(string root)
    {
        if (!Directory.Exists(root) || !File.Exists(Path.Combine(root, "Missum.Publication.Cli.exe")))
            throw new FileNotFoundException("Das eigenständige Publikationspaket fehlt in dieser Missum-Installation. Erstelle oder installiere ein vollständiges Missum-Paket: " + root);
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, source).Replace('\\', '/');
            _ = SafePath(root, relative);
            files.Add(relative, await HashFileAsync(source, CancellationToken.None).ConfigureAwait(false));
        }
        if (!files.ContainsKey("Assets/Scripts/export-document.ps1") || !files.ContainsKey("Assets/DocumentRendering/markdown.js")
            || !files.ContainsKey("Assets/DocumentRendering/styles.css"))
            throw new InvalidDataException("Das Publikationspaket enthält nicht alle gemeinsamen Missum-Rendererdateien.");
        var identity = JsonSerializer.SerializeToUtf8Bytes(files, JsonOptions);
        return new(root, Convert.ToHexStringLower(SHA256.HashData(identity)), files);
    }

    private static async Task<Installation?> ReadInstallationAsync(string target, CancellationToken token)
    {
        if (!Directory.Exists(target)) return null;
        RejectReparsePoint(target);
        var marker = SafePath(target, MarkerName);
        if (!File.Exists(marker)) return null;
        try
        {
            var value = JsonSerializer.Deserialize<Installation>(await File.ReadAllTextAsync(marker, token).ConfigureAwait(false), JsonOptions);
            return value is { Owner: Owner, Files.Count: > 0 } ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static async Task<bool> IsExactPackageCopyAsync(string target, RendererPackage package, CancellationToken token)
    {
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(target, path).Replace('\\', '/');
            _ = SafePath(target, relative);
            if (!package.Files.TryGetValue(relative, out var expected) || await HashFileAsync(path, token).ConfigureAwait(false) != expected) return false;
            count++;
        }
        return count == package.Files.Count;
    }

    private static async Task VerifyOwnedInstallationAsync(string target, Installation installed, CancellationToken token)
    {
        foreach (var path in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(target, path).Replace('\\', '/');
            if (relative == MarkerName) continue;
            _ = SafePath(target, relative);
            if (!installed.Files.TryGetValue(relative, out var expected)
                || await HashFileAsync(path, token).ConfigureAwait(false) != expected)
                throw new IOException("Eine Datei im bisherigen Publikationspaket wurde verändert oder hinzugefügt. Missum erhält diese Änderung und überschreibt den Ordner nicht: " + path);
        }
    }

    private static async Task InstallRendererAsync(string projectRoot, string target, RendererPackage package, CancellationToken token)
    {
        var staging = SafePath(projectRoot, ".publication-renderer.install-" + Guid.NewGuid().ToString("N"));
        var backup = SafePath(projectRoot, ".publication-renderer.previous-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var installationSucceeded = false;
        try
        {
            foreach (var (relative, expected) in package.Files)
            {
                token.ThrowIfCancellationRequested();
                var source = SafePath(package.Root, relative);
                var destination = SafePath(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
                await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    await input.CopyToAsync(output, token).ConfigureAwait(false);
                if (await HashFileAsync(destination, token).ConfigureAwait(false) != expected)
                    throw new IOException("Eine Datei des Publikationspakets hat sich beim Installieren geändert: " + relative);
            }
            await File.WriteAllTextAsync(Path.Combine(staging, MarkerName),
                JsonSerializer.Serialize(new Installation(Owner, package.PackageId, package.Files), JsonOptions), new UTF8Encoding(false), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var previousMoved = false;
            try
            {
                if (Directory.Exists(target)) { Directory.Move(target, backup); previousMoved = true; }
                Directory.Move(staging, target);
                installationSucceeded = true;
            }
            catch
            {
                if (previousMoved && !Directory.Exists(target)) Directory.Move(backup, target);
                throw;
            }
        }
        finally
        {
            DeleteOwnedTemporaryDirectory(projectRoot, staging);
            // A failed rollback must leave the previous package available for recovery.
            if (installationSucceeded) DeleteOwnedTemporaryDirectory(projectRoot, backup);
        }
    }

    private static void DeleteOwnedTemporaryDirectory(string root, string path)
    {
        _ = SafePath(root, Path.GetRelativePath(root, path));
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.TraceWarning("Temporäres Publikationspaket bleibt erhalten: {0}; {1}", path, exception.Message); }
    }

    private static string SafePath(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Any(char.IsControl))
            throw new UnauthorizedAccessException("Ungültiger Pfad im Publikationspaket.");
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var destination = Path.GetFullPath(Path.Combine(parent, relative));
        if (!destination.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Der Publikationspfad verlässt den ausgewählten Ordner.");
        for (var current = destination; current is not null; current = Path.GetDirectoryName(current))
        {
            RejectReparsePoint(current);
            if (string.Equals(current, parent, StringComparison.OrdinalIgnoreCase)) break;
        }
        return destination;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Verknüpfungen dürfen für die Publikationsinstallation nicht verfolgt werden: " + path);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    private sealed record RendererPackage(string Root, string PackageId, SortedDictionary<string, string> Files);
    private sealed record Installation(string Owner, string PackageId, SortedDictionary<string, string> Files);
}
