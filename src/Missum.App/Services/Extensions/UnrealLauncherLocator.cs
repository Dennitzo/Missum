using System.Diagnostics;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;

namespace Missum.App.Services.Extensions;

/// <summary>Where the Epic Games Launcher was found, how it was found, and whether it runs.</summary>
internal sealed record UnrealLauncherLocation(string Executable, bool Running, string Source, int? ProcessId);

/// <summary>
/// Finds the Epic Games Launcher without any hard-coded installation path. Four independent
/// stages are tried in order of trustworthiness: a running process, the Windows uninstall
/// registry, the launcher's own manifests and, last, the usual installation folders.
/// </summary>
internal static class UnrealLauncherLocator
{
    private const string ExecutableName = "EpicGamesLauncher.exe";

    internal static UnrealLauncherLocation? Locate()
        => FromRunningProcess() ?? FromUninstallRegistry() ?? FromLauncherManifests() ?? FromKnownLocations();

    /// <summary>Opens the launcher when needed and reports the resulting state.</summary>
    internal static async Task<UnrealLauncherLocation?> StartOrReportAsync(CancellationToken cancellationToken)
    {
        var located = Locate();
        if (located is null) return null;
        if (located.Running) return located;
        try
        {
            var start = new ProcessStartInfo(located.Executable)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(located.Executable)!,
                WindowStyle = ProcessWindowStyle.Normal,
            };
            using var process = Process.Start(start);
            var identifier = process?.Id;
            process?.Dispose();
            // The launcher shows its window a moment later; report the freshest observation.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                if (FromRunningProcess() is { } running) return running;
            }
            return new UnrealLauncherLocation(located.Executable, true, "gestartet", identifier);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or SecurityException)
        {
            return new UnrealLauncherLocation(located.Executable, false, located.Source + " (Start fehlgeschlagen: " + exception.Message + ")", null);
        }
    }

    private static UnrealLauncherLocation? FromRunningProcess()
    {
        foreach (var process in Process.GetProcessesByName("EpicGamesLauncher"))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                    return new UnrealLauncherLocation(path, true, "laufender Prozess", process.Id);
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Access to another user's module list is denied in common setups.
                }
            }
        }
        return null;
    }

    private static UnrealLauncherLocation? FromUninstallRegistry()
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                var path = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(path);
                    if (baseKey is null) continue;
                    foreach (var name in baseKey.GetSubKeyNames())
                    {
                        using var entry = baseKey.OpenSubKey(name);
                        if (entry?.GetValue("DisplayName") is not string displayName) continue;
                        if (!displayName.Contains("Epic Games Launcher", StringComparison.OrdinalIgnoreCase)) continue;
                        var location = entry.GetValue("InstallLocation") as string;
                        if (string.IsNullOrWhiteSpace(location)) continue;
                        if (ResolveUnder(location) is { } executable)
                            return new UnrealLauncherLocation(executable, false, "Uninstall-Registry", null);
                    }
                }
                catch (Exception exception) when (exception is IOException or SecurityException or UnauthorizedAccessException)
                {
                    // A hive without permission is simply not a source of information.
                }
            }
        }
        return null;
    }

    private static UnrealLauncherLocation? FromLauncherManifests()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(directory)) return null;
        try
        {
            foreach (var manifest in Directory.EnumerateFiles(directory, "*.manifest"))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                    if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                    if (!document.RootElement.TryGetProperty("InstallationDirectory", out var installation)) continue;
                    var root = installation.GetString();
                    if (string.IsNullOrWhiteSpace(root)) continue;
                    if (ResolveUnder(root) is { } executable)
                        return new UnrealLauncherLocation(executable, false, "Launcher-Manifest", null);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    // One unreadable manifest must not end the search.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return null;
    }

    private static UnrealLauncherLocation? FromKnownLocations()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        foreach (var root in new[]
        {
            Path.Combine(programFiles, "Epic Games"),
            Path.Combine(programFilesX86, "Epic Games"),
            Path.Combine(localAppData, "Programs", "Epic Games"),
            Path.Combine(commonAppData, "Epic"),
        })
        {
            if (ResolveUnder(root) is { } executable)
                return new UnrealLauncherLocation(executable, false, "bekannter Installationsordner", null);
        }
        return null;
    }

    /// <summary>
    /// Looks for the launcher binary under a installation root. The portal build is the entry
    /// point users actually start, so it is preferred over the launcher's bundled engine copy.
    /// </summary>
    private static string? ResolveUnder(string root)
    {
        foreach (var candidate in new[]
        {
            Path.Combine(root, "Launcher", "Portal", "Binaries", "Win64", ExecutableName),
            Path.Combine(root, "Portal", "Binaries", "Win64", ExecutableName),
            Path.Combine(root, "Launcher", "Engine", "Binaries", "Win64", ExecutableName),
            Path.Combine(root, ExecutableName),
        })
        {
            try
            {
                if (File.Exists(Path.GetFullPath(candidate))) return Path.GetFullPath(candidate);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A malformed path is not a hit.
            }
        }
        return null;
    }
}
