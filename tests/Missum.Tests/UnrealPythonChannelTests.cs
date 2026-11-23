using System.Text.Json;
using Missum.App.Services.Extensions;
using Xunit.Abstractions;

namespace Missum.Tests;

/// <summary>
/// Verification of the Unreal integration that needs no running editor: engine and launcher
/// discovery, the packaged Python runtime, and the agreement between the tool manifest, the
/// adapter dispatch table and the operation registry inside the runtime.
/// </summary>
public sealed class UnrealPythonChannelTests(ITestOutputHelper output)
{
    private static string Repository => FindRepositoryRoot();

    private static string LauncherSource => Path.Combine(Repository, "src", "Missum.App", "Services",
        "Extensions", "UnrealApplicationLauncher.cs");

    private static string AdapterSource => Path.Combine(Repository, "src", "Missum.UnrealAdapter", "Program.cs");

    private static string RuntimeDirectory => Path.Combine(Repository, "extensions", "unreal", "runtime");

    [Fact]
    public void LauncherIsFoundWithoutAnyFixedPath()
    {
        var location = UnrealLauncherLocator.Locate();
        if (location is null)
        {
            output.WriteLine("Kein Epic Games Launcher auf diesem Rechner installiert.");
            return;
        }

        output.WriteLine($"Launcher: {location.Executable} über {location.Source}, läuft={location.Running}");
        Assert.True(File.Exists(location.Executable), "Der gemeldete Launcher-Pfad zeigt auf keine Datei.");
        Assert.False(string.IsNullOrWhiteSpace(location.Source), "Die Erkennungsquelle fehlt.");
    }

    [Fact]
    public void EngineDiscoveryCarriesNoHardCodedRootAndVerifiesEveryCandidate()
    {
        var source = File.ReadAllText(LauncherSource);
        Assert.DoesNotContain(@"E:\UnrealEngine", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RunUAT", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cmd.exe", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BuildPlugin", source, StringComparison.OrdinalIgnoreCase);

        var installations = UnrealApplicationLauncher.DiscoverInstallations();
        foreach (var installation in installations)
        {
            output.WriteLine($"Engine {installation.Association} {installation.Version} unter {installation.Root}");
            Assert.True(File.Exists(Path.Combine(installation.Root, "Engine", "Binaries", "Win64", "UnrealEditor.exe")),
                "Gemeldete Engine ohne Editor-Binary.");
            Assert.True(File.Exists(Path.Combine(installation.Root, "Engine", "Build", "Build.version")),
                "Gemeldete Engine ohne Build.version.");
            Assert.Equal(5, installation.Version.Major);
        }
    }

    [Fact]
    public void ExtensionPackageCarriesThePythonRuntimeAndNoPluginSource()
    {
        Assert.True(File.Exists(Path.Combine(RuntimeDirectory, "unreal_bootstrap.py")), "Die Python-Laufzeit fehlt.");
        Assert.True(File.Exists(Path.Combine(RuntimeDirectory, "unreal_ops.py")), "Das Operationsregister fehlt.");
        Assert.True(File.Exists(Path.Combine(RuntimeDirectory, "unreal_materials.py")), "Die Materialwerkzeuge fehlen.");
        Assert.False(Directory.Exists(Path.Combine(Repository, "extensions", "unreal", "editor")),
            "Der Ordner mit eigenem C++-Editor-Plugin darf nicht mehr existieren.");
    }

    [Fact]
    public void StartUsesThePythonChannelAndKeepsTheOwnershipCheck()
    {
        var launcher = File.ReadAllText(LauncherSource);
        Assert.Contains("-ExecutePythonScript=", launcher, StringComparison.Ordinal);
        Assert.Contains("unreal_bootstrap.py", launcher, StringComparison.Ordinal);
        Assert.Contains(".missum-owned", launcher, StringComparison.Ordinal);
        Assert.Contains("PythonScriptPlugin", launcher, StringComparison.Ordinal);

        var runtime = File.ReadAllText(Path.Combine(RuntimeDirectory, "unreal_bootstrap.py"));
        Assert.Contains("set_keep_python_script_alive", runtime, StringComparison.Ordinal);
        Assert.Contains("register_ticker_callback", runtime, StringComparison.Ordinal);
        Assert.Contains("compare_digest", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryManifestToolIsKnownToTheAdapter()
    {
        var tools = ManifestToolNames();
        Assert.NotEmpty(tools);

        var adapter = File.ReadAllText(AdapterSource);
        var unmapped = tools
            .Where(tool => tool is not ("unreal.status" or "unreal.connect" or "unreal.launcher.start"))
            .Where(tool => !adapter.Contains($"\"{tool}\"", StringComparison.Ordinal))
            .ToArray();
        Assert.True(unmapped.Length == 0, "Werkzeuge ohne Zuordnung im Adapter: " + string.Join(", ", unmapped));

        var duplicates = tools.GroupBy(tool => tool, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        Assert.True(duplicates.Length == 0, "Doppelte Werkzeugnamen im Manifest: " + string.Join(", ", duplicates));
    }

    [Fact]
    public void EveryToolDescriptionStaysBelowTheGatewayLimit()
    {
        using var manifest = OpenManifest();
        foreach (var tool in manifest.RootElement.GetProperty("tools").EnumerateArray())
        {
            var name = tool.GetProperty("modelToolName").GetString();
            var description = tool.GetProperty("description").GetString() ?? "";
            Assert.True(description.Length < 300,
                $"Die Beschreibung von {name} ist {description.Length} Zeichen lang und damit zu lang.");
        }
    }

    [Fact]
    public void PythonRuntimeRegistersEveryCommandTheAdapterCanSend()
    {
        var adapter = File.ReadAllText(AdapterSource);
        // Alle Laufzeitmodule zusammen: Zusatzmodule tragen sich in dasselbe Register ein.
        var runtime = string.Join("\n", Directory.GetFiles(RuntimeDirectory, "*.py")
            .Select(File.ReadAllText));

        var sent = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var match in System.Text.RegularExpressions.Regex.Matches(adapter,
                     @"""(?<name>(blueprint|camera|view|level|asset|material|actor|light|sky|class|operation|channel)\.[a-z.]+)""")
                     .Cast<System.Text.RegularExpressions.Match>())
            sent.Add(match.Groups["name"].Value);

        // The connection state itself travels as "status", not as a manifest tool.
        sent.Add("status");
        Assert.NotEmpty(sent);

        var missing = sent
            .Where(command => !runtime.Contains($"\"{command}\"", StringComparison.Ordinal))
            .ToArray();
        Assert.True(missing.Length == 0,
            "Kommandos ohne Registrierung in der Python-Laufzeit: " + string.Join(", ", missing));
    }

    private static string[] ManifestToolNames()
    {
        using var manifest = OpenManifest();
        return manifest.RootElement.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("modelToolName").GetString()!)
            .ToArray();
    }

    private static JsonDocument OpenManifest() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(Repository, "extensions", "unreal", "manifest", "extension.json")));

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Missum.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("The Missum repository was not found.");
    }
}
