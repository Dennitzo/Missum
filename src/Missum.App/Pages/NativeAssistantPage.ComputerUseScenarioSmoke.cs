using System.Diagnostics;
using System.Text.Json;
using Missum.App.ViewModels;
using Missum.Core.Extensions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private static readonly JsonSerializerOptions ComputerScenarioReportOptions = new() { WriteIndented = true };

    /// <summary>
    /// Real Computer-Use scenario (MISSUM_SMOKE_UI_COMPUTERUSE=&lt;dir&gt;, prompt in
    /// MISSUM_SMOKE_UI_COMPUTERUSE_PROMPT): selects Computersteuerung in ChatGPT mode through the
    /// real "+" menu, sends the task and records every computer.* step and the final answer.
    /// Screens are announced like the Browser-Use smoke (browser-N.json, answered with .done).
    /// </summary>
    private async Task VerifyComputerUseScenarioSmokeAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var report = new Dictionary<string, object?>();
        var step = 0;
        var window = App.Current.MainWindow ?? throw new InvalidOperationException("Hauptfenster fehlt.");
        window.AppWindow.Move(new Windows.Graphics.PointInt32(40, 40));
        window.AppWindow.Resize(SmokeWindowSize(1100, 760));
        void Progress(string text) =>
            File.AppendAllText(Path.Combine(directory, "browser-progress.txt"), $"{DateTime.Now:HH:mm:ss} {text}{Environment.NewLine}");
        try
        {
            Progress("Warte auf die lokale AI.");
            var shell = App.Current.GetService<ShellViewModel>();
            await WaitForBrowserUseSmokeAsync(() => shell.IsAiServerReady, TimeSpan.FromMinutes(20), "Die lokale AI wurde nicht bereit.");
            // MISSUM_SMOKE_UI_COMPUTERUSE_TOOL=none sends a plain ChatGPT prompt (e.g. a cold model start check);
            // Computersteuerung belongs to Codex and runs in a scratch project there.
            // TOOL = menu entry name (default Computersteuerung, e.g. "Browser-Use"); WEBDESIGN=1 turns the
            // Webdesign tool on first, with a small test page in the scratch project.
            var toolName = Environment.GetEnvironmentVariable("MISSUM_SMOKE_UI_COMPUTERUSE_TOOL") is { Length: > 0 } named ? named : "Computersteuerung";
            var withTool = toolName != "none";
            var webdesign = Environment.GetEnvironmentVariable("MISSUM_SMOKE_UI_COMPUTERUSE_WEBDESIGN") == "1";
            var mode = withTool && toolName != "Browser-Use" || webdesign ? "coding" : "general";
            if (mode == "coding")
            {
                var project = Path.Combine(directory, "project");
                Directory.CreateDirectory(project);
                if (webdesign)
                    await File.WriteAllTextAsync(Path.Combine(project, "index.html"), """
                        <!doctype html><html lang="de"><head><meta charset="utf-8"><title>Testladen</title></head>
                        <body><h1>Testladen</h1><label>Name <input id="name"></label>
                        <button id="send" onclick="document.getElementById('out').textContent='Danke, '+document.getElementById('name').value+'!'">Absenden</button>
                        <p id="out"></p></body></html>
                        """);
                await NavigateAsync("session.projectCreate", new { workspacePath = project, chatMode = mode });
            }
            else if (_mode != mode) await NavigateAsync("mode.switch", new { chatMode = mode });
            await WaitForBrowserUseSmokeAsync(() => _mode == mode, TimeSpan.FromSeconds(30), "Der Modus " + mode + " wurde nicht geöffnet.");
            async Task SelectToolAsync(string name, Func<bool> selected)
            {
                Progress("Wähle " + name + " im Werkzeugmenü.");
                var menu = ShowComposerTools();
                try
                {
                    await Task.Delay(600, _lifetime.Token);
                    var rows = (StackPanel)((ScrollViewer)menu.Content).Content;
                    Button? entry = null;
                    foreach (var row in rows.Children)
                        if (row is Button button && AutomationProperties.GetName(button) == name) entry = button;
                    report["menuEnabled:" + name] = entry?.IsEnabled;
                    if (entry is not { IsEnabled: true })
                        throw new InvalidOperationException(name + " fehlt im Werkzeugmenü oder ist gesperrt.");
                    await CaptureBrowserUseSmokeAsync(directory, ++step, "tools-menu");
                    if (new ButtonAutomationPeer(entry).GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) invoke.Invoke();
                    await WaitForBrowserUseSmokeAsync(() => !_composerToolSelectionBusy && selected(), TimeSpan.FromSeconds(20), name + " wurde nicht aktiviert.");
                }
                finally { menu.Hide(); }
            }
            if (webdesign) await SelectToolAsync("Webdesign", () => WebdesignToolChip.Visibility == Visibility.Visible);
            if (withTool)
            {
                await SelectToolAsync(toolName, () => SelectedToolChip.Visibility == Visibility.Visible
                    && _selectedAction == (toolName == "Browser-Use" ? BuiltInActionIds.BrowserUse : BuiltInActionIds.ComputerUse));
                report["chip"] = AutomationProperties.GetName(SelectedToolChip);
            }

            var prompt = Environment.GetEnvironmentVariable("MISSUM_SMOKE_UI_COMPUTERUSE_PROMPT") is { Length: > 0 } custom ? custom
                : "Öffne den Windows-Rechner und berechne 12 mal 7. Nenne mir das Ergebnis, das der Rechner anzeigt.";
            Composer.Text = prompt;
            report["prompt"] = prompt;
            Progress("Sende den Prompt.");
            if (new ButtonAutomationPeer(SendButton).GetPattern(PatternInterface.Invoke) is IInvokeProvider send) send.Invoke();
            await WaitForBrowserUseSmokeAsync(() => _running, TimeSpan.FromSeconds(60), "Der AI-Lauf startete nicht.");
            var watch = Stopwatch.StartNew();
            var lastCount = 0;
            while (_running && watch.Elapsed < TimeSpan.FromMinutes(30))
            {
                var count = ComputerScenarioSteps().Count();
                if (count != lastCount) { Progress($"{count} Computer-Schritte."); lastCount = count; }
                await Task.Delay(1000, _lifetime.Token);
            }
            report["runFinished"] = !_running;
            report["elapsedSeconds"] = (int)watch.Elapsed.TotalSeconds;
            var steps = ComputerScenarioSteps().ToArray();
            report["computerSteps"] = steps.Select(item => new { tool = S(item, "tool"), status = S(item, "status"), summary = S(item, "summary") }).ToArray();
            report["answer"] = _messages.Values.Where(message => S(message, "role") == "assistant").Select(message => S(message, "content")).LastOrDefault();
            report["errorBar"] = ErrorBar.IsOpen ? $"{ErrorBar.Title} {ErrorBar.Message}".Trim() : null;
            await CaptureBrowserUseSmokeAsync(directory, ++step, "desktop-result");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            report["error"] = exception.ToString();
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "browser-report.json"), JsonSerializer.Serialize(report, ComputerScenarioReportOptions));
            DispatcherQueue.TryEnqueue(() => App.Current.MainWindow?.Close());
        }
    }

    private IEnumerable<JsonElement> ComputerScenarioSteps() => _messages.Values
        .SelectMany(MessageSteps)
        .Where(item => S(item, "tool") is "computer.observe" or "computer.act" or "browser.use" or "memory.sessions.search");
}
