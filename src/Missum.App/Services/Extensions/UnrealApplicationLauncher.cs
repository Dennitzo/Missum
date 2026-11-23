using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Missum.Core.Coding;

namespace Missum.App.Services.Extensions;

internal sealed record UnrealApplicationEndpoint(int Port, int ProcessId, string Token);
internal sealed record UnrealEngineInstallation(string Association, string Root, Version Version);

/// <summary>What the running editor announced in its ready receipt: channel and capabilities.</summary>
internal sealed record UnrealLaunchInfo(string Channel, string Pump, string RuntimeVersion,
    IReadOnlyList<string> Capabilities, int Port, int ProcessId, string? BridgeNotice = null);

/// <summary>Owns the visible editor process independently of the short-lived extension host.</summary>
internal static class UnrealApplicationLauncher
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    internal static async Task<UnrealApplicationEndpoint> EnsureStartedAsync(string contentDirectory, string workspaceRoot,
        string? requestedProject, string? requestedEngine, CancellationToken cancellationToken)
    {
        var workspace = NormalizeWorkspace(workspaceRoot);
        var directory = LeaseDirectory(workspace);
        Directory.CreateDirectory(directory);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(12));
        try
        {
            using var gate = await LockWorkspaceAsync(directory, deadline.Token).ConfigureAwait(false);
            var project = FindProject(workspace, requestedProject);
            if (ReadLease(directory) is { } lease && IsAlive(lease))
            {
                if (project is not null && !string.Equals(project, lease.ProjectPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Für diesen Workspace ist bereits ein anderes Unreal-Projekt verbunden.");
                return await WaitForEndpointAsync(directory, lease, deadline.Token).ConfigureAwait(false);
            }

            var installations = DiscoverInstallations();
            var association = project is null ? null : ReadAssociation(project);
            var engine = SelectEngine(installations, association, requestedEngine);
            // The editor is driven exclusively through Epics Python: no plugin is compiled,
            // no C++ toolchain is required, and no build step can block a connection.
            var script = Path.Combine(contentDirectory, "runtime", "unreal_bootstrap.py");
            if (!File.Exists(script))
                throw new InvalidOperationException("Die Unreal-Erweiterung enthält ihre Python-Laufzeit nicht.");
            project ??= CreateProject(workspace, engine);
            var bridgeNotice = RemoveOwnedBridgePlugin(workspace, project);
            EnsurePythonPluginEnabled(project);

            var launchId = Guid.NewGuid();
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var executable = Path.Combine(engine.Root, "Engine", "Binaries", "Win64", "UnrealEditor.exe");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                WindowStyle = ProcessWindowStyle.Normal,
            };
            start.ArgumentList.Add(project);
            // Quoted inside the switch: the engine parses an unquoted value up to the first
            // space, which would break install roots such as "Program Files".
            start.ArgumentList.Add($"-ExecutePythonScript=\"{script.Replace('\\', '/')}\"");
            start.Environment["MISSUM_UNREAL_READY_PATH"] = ReadyPath(directory, launchId);
            start.Environment["MISSUM_UNREAL_TOKEN"] = token;
            start.Environment["MISSUM_UNREAL_PROJECT_PATH"] = project;
            start.Environment["MISSUM_UNREAL_WORKSPACE"] = workspace;
            var blankPending = File.Exists(BlankProjectMarker(project));
            if (blankPending) start.Environment["MISSUM_UNREAL_INITIALIZE_BLANK_PROJECT"] = "1";
            else start.Environment.Remove("MISSUM_UNREAL_INITIALIZE_BLANK_PROJECT");
            var captureRoot = Path.Combine(Path.GetTempPath(), ProgramIntegrationStatusReader.DirectoryName, "captures");
            Directory.CreateDirectory(captureRoot);
            start.Environment["MISSUM_UNREAL_CAPTURE_ROOT"] = captureRoot;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Unreal Editor konnte nicht gestartet werden.");
            var current = new LaunchLease(launchId, process.Id, process.StartTime.ToUniversalTime().Ticks, token, project, blankPending, bridgeNotice);
            WriteLease(directory, current);
            return await WaitForEndpointAsync(directory, current, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Der Unreal Editor wurde nicht rechtzeitig bereit. Prüfe das Editorfenster und das Log unter Saved/Logs.", exception);
        }
    }

    internal static bool TryGetEndpoint(string workspaceRoot, out UnrealApplicationEndpoint? endpoint)
    {
        var directory = LeaseDirectory(NormalizeWorkspace(workspaceRoot));
        endpoint = ReadLease(directory) is { } lease && IsAlive(lease) ? ReadEndpoint(directory, lease) : null;
        return endpoint is not null;
    }

    /// <summary>Reports which channel the running editor announced, without contacting it.</summary>
    internal static UnrealLaunchInfo? TryGetLaunchInfo(string workspaceRoot)
    {
        try
        {
            var directory = LeaseDirectory(NormalizeWorkspace(workspaceRoot));
            if (ReadLease(directory) is not { } lease || !IsAlive(lease)) return null;
            if (ReadJson<ReadyReceipt>(ReadyPath(directory, lease.LaunchId)) is not { } ready) return null;
            return new UnrealLaunchInfo(
                ready.Channel?.Type is { Length: > 0 } type ? type : "unknown",
                ready.Channel?.Pump ?? "",
                ready.Channel?.RuntimeVersion ?? "",
                ready.Capabilities ?? [],
                ready.Port,
                lease.ProcessId,
                lease.BridgeNotice);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static string? FindProject(string workspace, string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return ResolveProject(workspace, requested);
        var files = Directory.EnumerateFiles(workspace, "*.uproject", new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive,
        }).Take(2).ToArray();
        return files.Length switch
        {
            0 => null,
            1 => ResolveProject(workspace, files[0]),
            _ => throw new InvalidOperationException("Der Projektordner enthält mehrere Unreal-Projekte. Gib die gewünschte .uproject-Datei als projectPath an."),
        };
    }

    private static string ResolveProject(string workspace, string requested)
    {
        var relative = Path.IsPathFullyQualified(requested) ? Path.GetRelativePath(workspace, requested) : requested;
        var path = WorkspaceFilePath.Resolve(workspace, relative);
        if (!string.Equals(Path.GetExtension(path), ".uproject", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new FileNotFoundException("Wähle eine vorhandene .uproject-Datei im Projektordner.", requested);
        return path;
    }

    internal static string? ReadAssociation(string project)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(project));
        return document.RootElement.TryGetProperty("EngineAssociation", out var association) && association.ValueKind == JsonValueKind.String
            ? association.GetString() : null;
    }

    internal static UnrealEngineInstallation SelectEngine(IReadOnlyList<UnrealEngineInstallation> installations,
        string? association, string? requested)
    {
        var selected = string.IsNullOrWhiteSpace(requested) ? association : requested;
        if (!string.IsNullOrWhiteSpace(association) && !string.IsNullOrWhiteSpace(requested)
            && !string.Equals(association, requested, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Die ausgewählte Engine stimmt nicht mit der EngineAssociation des Projekts überein.");
        var matches = string.IsNullOrWhiteSpace(selected) ? installations
            : installations.Where(engine => string.Equals(engine.Association, selected, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Count switch
        {
            0 => throw new InvalidOperationException($"Die benötigte Unreal Engine 5 wurde nicht gefunden{(selected is null ? "." : $": {selected}.")} Installiere die passende Engine oder registriere den Source-Build."),
            1 => matches[0],
            _ => throw new InvalidOperationException("Mehrere Unreal-Engines sind verfügbar. Wähle engineVersion aus: "
                + string.Join(", ", matches.Select(engine => engine.Association))),
        };
    }

    internal static IReadOnlyList<UnrealEngineInstallation> DiscoverInstallations()
    {
        var candidates = new List<(string Association, string Root)>();
        var manifest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");
        try
        {
            if (File.Exists(manifest))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                if (document.RootElement.TryGetProperty("InstallationList", out var entries))
                    foreach (var entry in entries.EnumerateArray())
                    {
                        var app = entry.TryGetProperty("AppName", out var name) ? name.GetString() : null;
                        var root = entry.TryGetProperty("InstallLocation", out var location) ? location.GetString() : null;
                        if (app is not null && root is not null && app.StartsWith("UE_", StringComparison.OrdinalIgnoreCase))
                            candidates.Add((app[3..], root));
                    }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Epic Games\Unreal Engine\Builds");
            if (key is not null)
                foreach (var name in key.GetValueNames())
                    if (key.GetValue(name) is string root) candidates.Add((name, root));
        }
        catch (Exception exception) when (exception is IOException or SecurityException or UnauthorizedAccessException) { }
        var configured = Environment.GetEnvironmentVariable("MISSUM_UNREAL_ENGINE_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            AddEngineRoot(configured);
            if (Directory.Exists(configured))
                foreach (var child in Directory.EnumerateDirectories(configured, "UE_*", new EnumerationOptions { IgnoreInaccessible = true }))
                    AddEngineRoot(child);
        }
        // Machine registry of installed engines. A branch may exist without a usable path.
        try
        {
            using var machine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EpicGames\Unreal Engine");
            if (machine is not null)
                foreach (var name in machine.GetSubKeyNames())
                {
                    using var branch = machine.OpenSubKey(name);
                    if (branch?.GetValue("InstallDirectory") is string directory && !string.IsNullOrWhiteSpace(directory))
                        AddEngineRoot(directory, name);
                }
        }
        catch (Exception exception) when (exception is IOException or SecurityException or UnauthorizedAccessException) { }

        // Manifests and the registry do not list manually copied installations. Search the fixed
        // drives and the usual folders for UE_* directories, at most two levels deep, never
        // following reparse points. Every candidate is still verified below.
        foreach (var root in EngineSearchRoots())
        {
            foreach (var directory in ChildrenNamedUE(root))
                AddEngineRoot(directory);
            foreach (var parent in ImmediateSubDirectories(root))
                foreach (var directory in ChildrenNamedUE(parent))
                    AddEngineRoot(directory);
        }

        var result = new List<UnrealEngineInstallation>();
        foreach (var (id, root) in candidates)
        {
            try
            {
                var full = Path.GetFullPath(root);
                var executable = Path.Combine(full, "Engine", "Binaries", "Win64", "UnrealEditor.exe");
                var versionPath = Path.Combine(full, "Engine", "Build", "Build.version");
                if (!File.Exists(executable) || !File.Exists(versionPath)) continue;
                using var version = JsonDocument.Parse(File.ReadAllText(versionPath));
                var major = version.RootElement.GetProperty("MajorVersion").GetInt32();
                var minor = version.RootElement.GetProperty("MinorVersion").GetInt32();
                var patch = version.RootElement.GetProperty("PatchVersion").GetInt32();
                if (major == 5 && !result.Any(item => item.Association.Equals(id, StringComparison.OrdinalIgnoreCase)
                    && item.Root.Equals(full, StringComparison.OrdinalIgnoreCase)))
                    result.Add(new(id, full, new Version(major, minor, patch)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException or KeyNotFoundException) { }
        }
        return result;

        void AddEngineRoot(string root, string? association = null)
        {
            var explicitAssociation = Environment.GetEnvironmentVariable("MISSUM_UNREAL_ENGINE_ASSOCIATION");
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
            var id = !string.IsNullOrWhiteSpace(association) ? association
                : !string.IsNullOrWhiteSpace(explicitAssociation) && root == configured ? explicitAssociation
                : name.StartsWith("UE_", StringComparison.OrdinalIgnoreCase) ? name[3..] : "configured";
            candidates.Add((id, root));
        }
    }

    /// <summary>Fixed drives plus the folders an engine installation commonly lives in.</summary>
    private static IEnumerable<string> EngineSearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in DriveRoots().Concat(CommonEngineFolders()))
        {
            var trimmed = Path.TrimEndingDirectorySeparator(candidate);
            if (seen.Add(trimmed) && Directory.Exists(trimmed)) yield return trimmed;
        }
    }

    private static string[] DriveRoots()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(drive => drive.DriveType == DriveType.Fixed)
                .Where(drive => { try { return drive.IsReady; } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; } })
                .Select(drive => drive.RootDirectory.FullName)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return [];
        }
    }

    private static IEnumerable<string> CommonEngineFolders()
    {
        foreach (var folder in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        })
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            yield return Path.Combine(folder, "Epic Games");
            yield return Path.Combine(folder, "Epic", "UnrealEngine");
            yield return folder;
        }
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(user))
        {
            yield return Path.Combine(user, "Documents");
            yield return Path.Combine(user, "UnrealEngine");
        }
    }

    private static string[] ChildrenNamedUE(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root, "UE_*", ReparseSafe).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or SecurityException)
        {
            return [];
        }
    }

    private static string[] ImmediateSubDirectories(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root, "*", ReparseSafe).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or SecurityException)
        {
            return [];
        }
    }

    private static readonly EnumerationOptions ReparseSafe = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
    };

    /// <summary>
    /// Takes the bridge plugin of an earlier Missum version out of the project. The folder is
    /// deleted only when the ownership marker proves Missum created it; a foreign plugin of the
    /// same name is reported and left untouched, together with its project entry.
    /// </summary>
    private static string? RemoveOwnedBridgePlugin(string workspace, string project)
    {
        var projectDirectory = Path.GetDirectoryName(project)!;
        var relative = Path.GetRelativePath(workspace, Path.Combine(projectDirectory, "Plugins", "MissumBridge"));
        var directory = WorkspaceFilePath.Resolve(workspace, relative);
        if (Directory.Exists(directory))
        {
            if (!File.Exists(Path.Combine(directory, ".missum-owned")))
                return "Plugins/MissumBridge gehört nicht zu Missum. Ordner und Projekteintrag bleiben unberührt.";
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return "Plugins/MissumBridge konnte nicht entfernt werden: " + exception.Message;
            }
        }
        RewriteProjectPlugins(project, add: null, remove: "MissumBridge");
        return null;
    }

    /// <summary>The Python channel needs Epic's scripting plugin, so Missum enables it on first connect.</summary>
    private static void EnsurePythonPluginEnabled(string project) => RewriteProjectPlugins(project, add: "PythonScriptPlugin", remove: null);

    private static string CreateProject(string workspace, UnrealEngineInstallation engine)
    {
        var template = Path.Combine(engine.Root, "Templates", "TP_BlankBP");
        var templateProject = Path.Combine(template, "TP_BlankBP.uproject");
        if (!File.Exists(templateProject))
            throw new FileNotFoundException("Die Blueprint-Vorlage TP_BlankBP fehlt in dieser Unreal-Installation.", templateProject);
        var name = new string(Path.GetFileName(workspace).Where(static c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray());
        if (name.Length == 0 || !char.IsAsciiLetter(name[0])) name = "MissumProject";
        var project = WorkspaceFilePath.Resolve(workspace, name + ".uproject");
        var json = JsonNode.Parse(File.ReadAllText(templateProject))?.AsObject()
            ?? throw new InvalidDataException("Die Unreal-Blueprint-Projektvorlage ist ungültig.");
        json["EngineAssociation"] = engine.Association;
        foreach (var folder in new[] { "Config", "Content" })
        {
            var source = Path.Combine(template, folder);
            if (!Directory.Exists(source)) continue;
            foreach (var templateFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(templateFile) is "TemplateDefs.ini" or "config.ini") continue;
                var relative = Path.Combine(folder, Path.GetRelativePath(source, templateFile));
                var target = WorkspaceFilePath.Resolve(workspace, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!File.Exists(target)) File.Copy(templateFile, target);
            }
        }
        SetInitialMaps(WorkspaceFilePath.Resolve(workspace, Path.Combine("Config", "DefaultEngine.ini")));
        Directory.CreateDirectory(WorkspaceFilePath.Resolve(workspace, "Content"));
        using (var marker = new FileStream(BlankProjectMarker(project), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        using var file = new FileStream(project, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(file, new UTF8Encoding(false));
        writer.Write(json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return project;
    }

    private static void SetInitialMaps(string path)
    {
        const string section = "[/Script/EngineSettings.GameMapsSettings]";
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var start = lines.FindIndex(line => line.Trim().Equals(section, StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            start = lines.Count;
            lines.Add(section);
        }
        var end = start + 1;
        while (end < lines.Count && !lines[end].TrimStart().StartsWith('[')) end++;
        for (var index = end - 1; index > start; index--)
        {
            var line = lines[index].TrimStart();
            if (line.StartsWith("EditorStartupMap=", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("GameDefaultMap=", StringComparison.OrdinalIgnoreCase))
                lines.RemoveAt(index);
        }
        lines.Insert(start + 1, "GameDefaultMap=/Engine/Maps/Entry");
        lines.Insert(start + 1, "EditorStartupMap=/Engine/Maps/Entry");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    private static string BlankProjectMarker(string project) => project + ".missum-blank-pending";

    /// <summary>Adds or removes one plugin entry in the .uproject file, atomically and without a byte-order mark.</summary>
    private static void RewriteProjectPlugins(string project, string? add, string? remove)
    {
        var root = JsonNode.Parse(File.ReadAllText(project))?.AsObject() ?? throw new InvalidDataException("Ungültige .uproject-Datei.");
        var plugins = root["Plugins"] as JsonArray ?? new JsonArray();
        var changed = false;
        if (remove is not null)
            for (var index = plugins.Count - 1; index >= 0; index--)
                if (plugins[index] is JsonObject item && (string?)item["Name"] == remove)
                { plugins.RemoveAt(index); changed = true; }
        if (add is not null)
        {
            var entry = plugins.OfType<JsonObject>().FirstOrDefault(item => (string?)item["Name"] == add);
            if (entry is null) { plugins.Add(new JsonObject { ["Name"] = add, ["Enabled"] = true }); changed = true; }
            else if ((bool?)entry["Enabled"] != true) { entry["Enabled"] = true; changed = true; }
        }
        if (!changed) return;
        root["Plugins"] = plugins;
        var temp = project + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, project, overwrite: true);
    }

    private static string NormalizeWorkspace(string workspace) =>
        Path.IsPathFullyQualified(workspace) && Directory.Exists(workspace)
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace))
            : throw new InvalidOperationException("Wähle zuerst einen vorhandenen Projektordner für Unreal Engine.");

    private static string LeaseDirectory(string workspace) => Path.Combine(Path.GetTempPath(),
        ProgramIntegrationStatusReader.DirectoryName, "unreal-launches",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workspace.ToUpperInvariant()))));
    private static string ReadyPath(string directory, Guid id) => Path.Combine(directory, $"ready-{id:N}.json");

    private static async Task<FileStream> LockWorkspaceAsync(string directory, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(directory, "launch.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33)
            { await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false); }
        }
    }

    private static LaunchLease? ReadLease(string directory) => ReadJson<LaunchLease>(Path.Combine(directory, "lease.json"));
    private static void WriteLease(string directory, LaunchLease lease)
    {
        var temp = Path.Combine(directory, $"lease-{lease.LaunchId:N}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(lease, Json));
        File.Move(temp, Path.Combine(directory, "lease.json"), overwrite: true);
    }
    private static T? ReadJson<T>(string path)
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return default; }
    }
    private static bool IsAlive(LaunchLease lease)
    {
        if (lease.ProcessId <= 0 || lease.LaunchId == Guid.Empty || lease.Token is not { Length: 64 }) return false;
        try
        {
            using var process = Process.GetProcessById(lease.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == lease.StartTime
                && process.ProcessName.Equals("UnrealEditor", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
    }
    private static UnrealApplicationEndpoint? ReadEndpoint(string directory, LaunchLease lease)
    {
        var ready = ReadJson<ReadyReceipt>(ReadyPath(directory, lease.LaunchId));
        return ready is { Port: > 0 and <= 65535 } && ready.ProcessId == lease.ProcessId
            && (!lease.BlankPending || ready.Initialized)
            && !string.IsNullOrWhiteSpace(ready.ProjectPath)
            && string.Equals(Path.GetFullPath(ready.ProjectPath), Path.GetFullPath(lease.ProjectPath), StringComparison.OrdinalIgnoreCase)
            && string.Equals(ready.Token, lease.Token, StringComparison.Ordinal)
                ? new(ready.Port, lease.ProcessId, lease.Token) : null;
    }
    private static async Task<UnrealApplicationEndpoint> WaitForEndpointAsync(string directory, LaunchLease lease, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAlive(lease)) throw new InvalidOperationException("Unreal Editor wurde vor dem Aufbau der Verbindung beendet. Prüfe das Editor-Log und die Projektversion.");
            var ready = ReadJson<ReadyReceipt>(ReadyPath(directory, lease.LaunchId));
            if (!string.IsNullOrWhiteSpace(ready?.InitializationError))
                throw new InvalidOperationException("Die leere Unreal-Karte konnte nicht erstellt werden: " + ready.InitializationError);
            if (ReadEndpoint(directory, lease) is { } endpoint)
            {
                if (lease.BlankPending) File.Delete(BlankProjectMarker(lease.ProjectPath));
                return endpoint;
            }
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
    private sealed record LaunchLease(Guid LaunchId, int ProcessId, long StartTime, string Token, string ProjectPath,
        bool BlankPending = false, string? BridgeNotice = null);
    private sealed record ReadyReceipt(int Port, int ProcessId, string? Token, string? ProjectPath,
        bool Initialized = false, string? InitializationError = null,
        ReadyChannel? Channel = null, string[]? Capabilities = null);
    private sealed record ReadyChannel(string? Type, int Protocol, string? RuntimeVersion, string? Pump, string? Transport);
}
