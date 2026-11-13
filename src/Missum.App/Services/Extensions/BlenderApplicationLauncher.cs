using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Missum.Core.Coding;

namespace Missum.App.Services.Extensions;

internal sealed record BlenderApplicationEndpoint(int Port, int ProcessId, string Token);

/// <summary>Starts a normal Blender window with Missum's bundled runtime for one workspace.</summary>
internal static class BlenderApplicationLauncher
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] UninstallKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    internal static async Task<BlenderApplicationEndpoint> EnsureStartedAsync(string contentDirectory, string workspaceRoot,
        string? projectPath, CancellationToken cancellationToken)
    {
        var workspace = NormalizeWorkspace(workspaceRoot);
        var explicitProject = string.IsNullOrWhiteSpace(projectPath) ? null : ResolveProject(workspace, projectPath);
        var directory = LeaseDirectory(workspace);
        Directory.CreateDirectory(directory);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(StartupTimeout);
        try
        {
            // Coordinate with other Missum processes as well as parallel tool calls. A restart
            // reuses the owned Blender window, including unsaved edits in its scene.
            using var leaseLock = await LockWorkspaceAsync(directory, deadline.Token).ConfigureAwait(false);
            if (ReadLease(directory) is { } existing && IsAlive(existing))
                return await WaitForEndpointAsync(directory, existing, deadline.Token).ConfigureAwait(false);

            var bootstrap = Path.Combine(contentDirectory, "runtime", "bootstrap.py");
            if (!File.Exists(bootstrap))
                throw new InvalidOperationException("Die Blender-Erweiterung enthält die Missum-Laufzeit nicht. Aktualisiere das Erweiterungspaket.");

            var executable = FindExecutable(workspace)
                ?? throw new InvalidOperationException("Blender wurde auf diesem Rechner nicht gefunden. Installiere Blender oder setze MISSUM_BLENDER_EXECUTABLE auf den vollständigen Pfad zu blender.exe.");
            var initialProject = explicitProject ?? FindInitialProject(workspace);
            var launchId = Guid.NewGuid();
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            using var process = StartBlender(executable, bootstrap, workspace, initialProject, ReadyPath(directory, launchId), token);
            var lease = new LaunchLease(launchId, process.Id, process.StartTime.ToUniversalTime().Ticks, token);
            WriteLease(directory, lease);
            return await WaitForEndpointAsync(directory, lease, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Blender hat seine Missum-Verbindung innerhalb von 30 Sekunden nicht bereitgestellt. Prüfe das Blender-Fenster und versuche es erneut.", exception);
        }
    }

    internal static bool TryGetEndpoint(string workspaceRoot, out BlenderApplicationEndpoint? endpoint)
    {
        var directory = LeaseDirectory(NormalizeWorkspace(workspaceRoot));
        endpoint = ReadLease(directory) is { } lease && IsAlive(lease) ? ReadEndpoint(directory, lease) : null;
        return endpoint is not null;
    }

    internal static string? FindExecutable(string workspaceRoot)
    {
        var configured = Environment.GetEnvironmentVariable("MISSUM_BLENDER_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(configured))
            return ValidExecutable(configured, workspaceRoot)
                ?? throw new InvalidOperationException("MISSUM_BLENDER_EXECUTABLE muss auf eine vorhandene blender.exe außerhalb des Projektordners zeigen.");

        var runningPaths = new List<string>();
        foreach (var process in Process.GetProcessesByName("blender"))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is { } path) runningPaths.Add(path);
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { }
            }
        }

        return ChooseExecutable(runningPaths, workspaceRoot)
            ?? ChooseExecutable(InstalledExecutables(), workspaceRoot);
    }

    private static string NormalizeWorkspace(string workspaceRoot)
    {
        if (!Path.IsPathFullyQualified(workspaceRoot) || !Directory.Exists(workspaceRoot))
            throw new InvalidOperationException("Wähle zuerst einen vorhandenen Workspace für Blender.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
    }

    private static string? FindInitialProject(string workspace)
    {
        var files = Directory.EnumerateFiles(workspace, "*.blend", new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
            MatchCasing = MatchCasing.CaseInsensitive,
        }).Take(2).ToArray();
        return files.Length switch
        {
            0 => null,
            1 => ResolveProject(workspace, files[0]),
            _ => throw new InvalidOperationException("Der Workspace enthält mehrere Blender-Dateien. Gib die gewünschte .blend-Datei bei blender.status als projectPath an."),
        };
    }

    private static string ResolveProject(string workspace, string path)
    {
        var relative = Path.IsPathFullyQualified(path) ? Path.GetRelativePath(workspace, path) : path;
        var resolved = WorkspaceFilePath.Resolve(workspace, relative);
        if (!string.Equals(Path.GetExtension(resolved), ".blend", StringComparison.OrdinalIgnoreCase) || !File.Exists(resolved))
            throw new FileNotFoundException("Wähle eine vorhandene .blend-Datei im Workspace.", path);
        return resolved;
    }

    private static IEnumerable<string> InstalledExecutables()
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var appPaths = OpenKey(root, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\blender.exe");
            if (ReadString(appPaths, null) is { } executable) yield return executable;
            foreach (var uninstallKey in UninstallKeys)
            {
                using var uninstall = OpenKey(root, uninstallKey);
                if (uninstall is null) continue;
                foreach (var entry in uninstall.GetSubKeyNames())
                {
                    using var product = OpenKey(uninstall, entry);
                    if (ReadString(product, "DisplayName")?.StartsWith("Blender", StringComparison.OrdinalIgnoreCase) == true
                        && ReadString(product, "InstallLocation") is { Length: > 0 } install)
                        yield return Path.Combine(install, "blender.exe");
                }
            }
        }

        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programsX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var parent in new[] { programs, programsX86, Path.Combine(local, "Programs") })
        {
            if (parent.Length == 0) continue;
            var foundation = Path.Combine(parent, "Blender Foundation");
            yield return Path.Combine(foundation, "blender.exe");
            if (!Directory.Exists(foundation)) continue;
            foreach (var version in Directory.EnumerateDirectories(foundation, "Blender*", new EnumerationOptions
                     { IgnoreInaccessible = true }).Where(Directory.Exists))
                yield return Path.Combine(version, "blender.exe");
        }

        if (programsX86.Length > 0)
            yield return Path.Combine(programsX86, "Steam", "steamapps", "common", "Blender", "blender.exe");
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var path = directory.Trim('"');
            if (Path.IsPathFullyQualified(path)) yield return Path.Combine(path, "blender.exe");
        }
    }

    private static RegistryKey? OpenKey(RegistryKey root, string path)
    {
        try { return root.OpenSubKey(path); }
        catch (Exception exception) when (exception is IOException or SecurityException or UnauthorizedAccessException) { return null; }
    }

    private static string? ReadString(RegistryKey? key, string? name)
    {
        try { return (key?.GetValue(name) as string)?.Trim().Trim('"'); }
        catch (Exception exception) when (exception is IOException or SecurityException or UnauthorizedAccessException) { return null; }
    }

    private static string? ChooseExecutable(IEnumerable<string> candidates, string workspace) =>
        candidates.Select(path => ValidExecutable(path, workspace)).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(ExecutableVersion)
            .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

    private static string? ValidExecutable(string candidate, string workspace)
    {
        var path = candidate.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetFileName(path), "blender.exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path))
            return null;
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(workspace, full);
        return Path.IsPathRooted(relative) || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? full
            : null;
    }

    private static Version ExecutableVersion(string path)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            return new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart, version.FilePrivatePart);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or ArgumentException)
        {
            return new Version(0, 0);
        }
    }

    private static Process StartBlender(string executable, string bootstrap, string workspace, string? project,
        string readyPath, string token)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Normal,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        start.ArgumentList.Add("--factory-startup");
        start.ArgumentList.Add("--disable-autoexec");
        if (project is not null) start.ArgumentList.Add(project);
        start.ArgumentList.Add("--python-exit-code");
        start.ArgumentList.Add("1");
        start.ArgumentList.Add("--python");
        start.ArgumentList.Add(bootstrap);
        start.Environment["MISSUM_BLENDER_SESSION_FILE"] = readyPath;
        start.Environment["MISSUM_BLENDER_TOKEN"] = token;
        start.Environment["MISSUM_BLENDER_WORKSPACE"] = workspace;
        start.Environment.Remove("MISSUM_BLENDER_NEW_PROJECT");
        if (project is null) start.Environment["MISSUM_BLENDER_NEW_PROJECT"] = Path.Combine(workspace, "Szene.blend");
        return Process.Start(start) ?? throw new InvalidOperationException("Blender konnte nicht gestartet werden.");
    }

    private static string LeaseDirectory(string workspace)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workspace.ToUpperInvariant())));
        return Path.Combine(Path.GetTempPath(), ProgramIntegrationStatusReader.DirectoryName, "blender-launches", key);
    }

    private static string ReadyPath(string directory, Guid launchId) => Path.Combine(directory, $"ready-{launchId:N}.json");

    private static async Task<FileStream> LockWorkspaceAsync(string directory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, "launch.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33)
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static LaunchLease? ReadLease(string directory) => ReadJson<LaunchLease>(Path.Combine(directory, "lease.json"));

    private static void WriteLease(string directory, LaunchLease lease)
    {
        var path = Path.Combine(directory, "lease.json");
        var temporary = Path.Combine(directory, $"lease-{lease.LaunchId:N}.tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(lease, Json));
        File.Move(temporary, path, overwrite: true);
    }

    private static bool IsAlive(LaunchLease lease)
    {
        if (lease.ProcessId <= 0 || lease.LaunchId == Guid.Empty || lease.Token is not { Length: 64 }) return false;
        try
        {
            using var process = Process.GetProcessById(lease.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == lease.StartTime
                && process.ProcessName.Equals("blender", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
    }

    private static BlenderApplicationEndpoint? ReadEndpoint(string directory, LaunchLease lease)
    {
        var ready = ReadJson<ReadyReceipt>(ReadyPath(directory, lease.LaunchId));
        return ready is { Port: > 0 and <= 65_535 } && ready.ProcessId == lease.ProcessId
            ? new BlenderApplicationEndpoint(ready.Port, lease.ProcessId, lease.Token)
            : null;
    }

    private static T? ReadJson<T>(string path)
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return default; }
    }

    private static async Task<BlenderApplicationEndpoint> WaitForEndpointAsync(string directory, LaunchLease lease,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAlive(lease))
                throw new InvalidOperationException("Blender wurde vor dem Aufbau der Missum-Verbindung beendet.");
            if (ReadEndpoint(directory, lease) is { } endpoint) return endpoint;
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record LaunchLease(Guid LaunchId, int ProcessId, long StartTime, string Token);
    private sealed record ReadyReceipt(int Port, int ProcessId);
}
