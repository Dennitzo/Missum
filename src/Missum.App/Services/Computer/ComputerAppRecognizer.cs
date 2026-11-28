using System.Diagnostics;
using Missum.App.Services.Extensions;

namespace Missum.App.Services;

/// <summary>What the model is looking at in the foreground, with operating hints for known programs.</summary>
public sealed record ComputerApp(string Kind, string Process, string? Hints = null);

/// <summary>A resolved program start for the <c>launch</c> action.</summary>
internal sealed record ComputerLaunch(string FileName, string? Arguments, bool UseShellExecute, string Description);

/// <summary>
/// Recognizes Windows programs (with special support for the Unreal Engine editor) and resolves
/// launch targets: program aliases, paths, documents, URLs and Unreal projects.
/// </summary>
internal static class ComputerAppRecognizer
{
    /// <summary>Enables Slate's UI Automation provider (off by default in Unreal Engine 5).</summary>
    public const string UnrealAccessibilityArgument = "-ini:Engine:[ConsoleVariables]:Accessibility.Enable=1";

    public const string UnrealHints =
        "Unreal Editor erkannt. Menüleiste oben (File, Edit, Window, Tools, Build, Help), Werkzeugleiste mit Play (Alt+P), "
        + "Outliner rechts oben, Details rechts unten, Content Browser unten (Strg+Leertaste). Viewport: Rechtsklick halten + WASD zum Fliegen, "
        + "F fokussiert die Auswahl, W/E/R Verschieben/Drehen/Skalieren, Strg+S speichert, Strg+Shift+S speichert alles. "
        + "Menüs und Dropdowns öffnen eigene Popup-Fenster; nach dem Öffnen neu beobachten. Fehlen markierte Unreal-Elemente, "
        + "wurde der Editor ohne Bedienhilfen gestartet: über launch (target unreal oder .uproject) startet Missum ihn mit UI Automation.";

    public static ComputerApp Recognize(string processName, string? title)
    {
        var name = processName ?? "";
        var kind = name.ToLowerInvariant() switch
        {
            var p when p.StartsWith("unrealeditor", StringComparison.Ordinal) => "unreal-editor",
            "epicgameslauncher" or "unrealversionselector" => "epic-launcher",
            "explorer" => string.IsNullOrWhiteSpace(title) || title.Contains("Taskleiste", StringComparison.OrdinalIgnoreCase) ? "windows-shell" : "file-explorer",
            "chrome" or "msedge" or "firefox" or "brave" or "opera" => "browser",
            "windowsterminal" or "cmd" or "powershell" or "pwsh" or "conhost" or "wt" => "terminal",
            "winword" or "excel" or "powerpnt" or "outlook" or "onenote" => "office",
            "code" or "devenv" or "rider64" => "code-editor",
            "notepad" or "notepad++" => "text-editor",
            "calculatorapp" or "calc" => "calculator",
            "systemsettings" or "mmc" or "control" => "windows-settings",
            _ => "application",
        };
        var hints = kind switch
        {
            "unreal-editor" => UnrealHints,
            "epic-launcher" => "Epic Games Launcher: Unreal Engine → Bibliothek; Projekte per Doppelklick öffnen oder launch mit dem .uproject-Pfad.",
            "file-explorer" => "Explorer: Adressleiste mit Strg+L oder F4, Suche mit Strg+E/F3, Umbenennen mit F2, Eigenschaften mit Alt+Enter.",
            "terminal" => "Terminal: Befehle mit type eingeben und mit keypress enter ausführen.",
            "browser" => "Browser: Adressleiste mit Strg+L, neuer Tab Strg+T, zurück Alt+Links.",
            _ => null,
        };
        return new(kind, name, hints);
    }

    /// <summary>Resolves a launch target. Unknown words fall back to the Windows shell (App Paths, protocols).</summary>
    public static ComputerLaunch ResolveLaunch(string target, string? arguments,
        Func<string, bool>? fileExists = null, Func<IReadOnlyList<UnrealEngineInstallation>>? unrealEngines = null)
    {
        if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("launch benötigt ein Ziel (Programm, Datei, Ordner oder URL).");
        fileExists ??= File.Exists;
        unrealEngines ??= UnrealApplicationLauncher.DiscoverInstallations;
        var value = target.Trim().Trim('"');
        var extra = string.IsNullOrWhiteSpace(arguments) ? null : arguments.Trim();
        var lower = value.ToLowerInvariant();
        if (lower is "unreal" or "unreal engine" or "unrealengine" or "unreal editor" or "unrealeditor" or "ue" or "ue5"
            || lower.EndsWith(".uproject", StringComparison.Ordinal))
        {
            var engine = unrealEngines().OrderByDescending(installation => installation.Version).FirstOrDefault()
                ?? throw new InvalidOperationException("Es ist keine Unreal Engine 5 installiert.");
            var editor = Path.Combine(engine.Root, "Engine", "Binaries", "Win64", "UnrealEditor.exe");
            if (!fileExists(editor)) throw new InvalidOperationException($"UnrealEditor.exe fehlt in {engine.Root}.");
            var project = lower.EndsWith(".uproject", StringComparison.Ordinal) ? Quote(value) : null;
            // Slate exposes its widgets to Windows UI Automation only with this console variable,
            // so Computer-Use can number Unreal's menus, buttons and fields.
            var editorArguments = string.Join(' ', new[] { project, UnrealAccessibilityArgument, extra }.Where(part => !string.IsNullOrWhiteSpace(part)));
            return new(editor, editorArguments, false, $"Unreal Editor {engine.Association}");
        }
        var alias = lower switch
        {
            "editor" or "notepad" or "texteditor" => "notepad.exe",
            "rechner" or "calculator" or "calc" or "taschenrechner" => "calc.exe",
            "explorer" or "dateiexplorer" or "file explorer" => "explorer.exe",
            "paint" or "mspaint" => "mspaint.exe",
            "edge" or "microsoft edge" => "msedge.exe",
            "chrome" or "google chrome" => "chrome.exe",
            "terminal" or "windows terminal" => "wt.exe",
            "powershell" => "powershell.exe",
            "cmd" or "eingabeaufforderung" => "cmd.exe",
            "einstellungen" or "settings" => "ms-settings:",
            "task manager" or "taskmanager" or "taskmgr" => "taskmgr.exe",
            "epic" or "epic games" or "epic games launcher" => "com.epicgames.launcher://",
            _ => null,
        };
        // The shell resolves App Paths aliases, documents, folders and URLs exactly like Win+R.
        return new(alias ?? value, extra, true, alias ?? value);
    }

    public static Process? Start(ComputerLaunch launch)
    {
        var start = new ProcessStartInfo(launch.FileName) { UseShellExecute = launch.UseShellExecute };
        if (!string.IsNullOrWhiteSpace(launch.Arguments)) start.Arguments = launch.Arguments;
        if (!launch.UseShellExecute) start.WorkingDirectory = Path.GetDirectoryName(launch.FileName) ?? "";
        return Process.Start(start);
    }

    private static string Quote(string value) => value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
}
