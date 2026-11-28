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
    private static readonly JsonSerializerOptions BrowserUseReportOptions = new() { WriteIndented = true };

    /// <summary>
    /// Real Browser-Use scenario (MISSUM_SMOKE_UI_BROWSERUSE=&lt;dir&gt;): waits for the local AI,
    /// selects Browser-Use in the real "+" menu, checks the chip, sends a short web task through
    /// the real send path and waits until the local model has used browser.use. Screens are
    /// announced as browser-N.json for an external real-screen capture (answered with .done).
    /// The window closes at the end so Missum stops the AI stack it started.
    /// </summary>
    private async Task VerifyBrowserUseSmokeAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var report = new Dictionary<string, object?>();
        var step = 0;
        var window = App.Current.MainWindow ?? throw new InvalidOperationException("Hauptfenster fehlt.");
        window.AppWindow.Move(new Windows.Graphics.PointInt32(40, 40));
        window.AppWindow.Resize(SmokeWindowSize(1360, 860));
        window.Activate();
        void Progress(string text)
        {
            report["progress"] = text;
            File.AppendAllText(Path.Combine(directory, "browser-progress.txt"), $"{DateTime.Now:HH:mm:ss} {text}{Environment.NewLine}");
        }
        try
        {
            Progress("Warte auf die lokale AI.");
            var shell = App.Current.GetService<ShellViewModel>();
            await WaitForBrowserUseSmokeAsync(() => shell.IsAiServerReady, TimeSpan.FromMinutes(20), "Die lokale AI wurde nicht bereit.");
            if (_mode != "general")
            {
                await NavigateAsync("mode.switch", new { chatMode = "general" });
                await WaitForBrowserUseSmokeAsync(() => _mode == "general", TimeSpan.FromSeconds(30), "Der ChatGPT-Modus wurde nicht geöffnet.");
            }

            Progress("Wähle Browser-Use im Werkzeugmenü.");
            var menu = ShowComposerTools();
            try
            {
                await Task.Delay(600, _lifetime.Token);
                var rows = (StackPanel)((ScrollViewer)menu.Content).Content;
                TextBlock? group = null;
                Button? browserUse = null;
                foreach (var row in rows.Children)
                {
                    if (row is TextBlock heading) group = heading;
                    if (row is Button button && AutomationProperties.GetName(button) == "Browser-Use")
                    {
                        report["menuGroup"] = group?.Text;
                        report["menuEnabled"] = button.IsEnabled;
                        browserUse = button;
                    }
                }
                if (browserUse is null || !browserUse.IsEnabled || report["menuGroup"] as string != "Recherche")
                    throw new InvalidOperationException("Browser-Use fehlt im Werkzeugmenü unter Recherche oder ist gesperrt.");
                await CaptureBrowserUseSmokeAsync(directory, ++step, "tools-menu");
                if (new ButtonAutomationPeer(browserUse).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
                    throw new InvalidOperationException("Browser-Use ist nicht barrierefrei bedienbar.");
                invoke.Invoke();
                await WaitForBrowserUseSmokeAsync(() => !_composerToolSelectionBusy && _selectedAction == BuiltInActionIds.BrowserUse
                    && SelectedToolChip.Visibility == Visibility.Visible, TimeSpan.FromSeconds(15), "Der Browser-Use-Chip erscheint nicht.");
            }
            finally { menu.Hide(); }
            report["chip"] = AutomationProperties.GetName(SelectedToolChip);
            if (report["chip"] as string != "Browser-Use entfernen")
                throw new InvalidOperationException("Der Werkzeugchip heißt nicht Browser-Use.");

            const string prompt = "Öffne https://example.com und nenne mir die Hauptüberschrift der Seite und den Text des Links darunter.";
            Composer.Text = prompt;
            await Task.Delay(300, _lifetime.Token);
            await CaptureBrowserUseSmokeAsync(directory, ++step, "chip-and-prompt");
            Progress("Sende den Prompt.");
            if (new ButtonAutomationPeer(SendButton).GetPattern(PatternInterface.Invoke) is not IInvokeProvider send)
                throw new InvalidOperationException("Senden ist nicht barrierefrei bedienbar.");
            send.Invoke();
            await WaitForBrowserUseSmokeAsync(() => _running, TimeSpan.FromSeconds(60), "Der AI-Lauf startete nicht.");
            Progress("AI-Lauf läuft.");
            var watch = Stopwatch.StartNew();
            var browserStepSeen = false;
            while (_running && watch.Elapsed < TimeSpan.FromMinutes(25))
            {
                if (!browserStepSeen && BrowserUseSmokeSteps().Any())
                {
                    browserStepSeen = true;
                    Progress("browser.use wurde aufgerufen.");
                }
                await Task.Delay(1000, _lifetime.Token);
            }
            report["runFinished"] = !_running;
            report["elapsedSeconds"] = (int)watch.Elapsed.TotalSeconds;
            var steps = BrowserUseSmokeSteps().ToArray();
            report["browserUseSteps"] = steps.Select(item => new { status = S(item, "status"), summary = S(item, "summary"), label = S(item, "label") }).ToArray();
            var answer = _messages.Values.Where(message => S(message, "role") == "assistant").Select(message => S(message, "content")).LastOrDefault();
            report["answer"] = answer;
            report["errorBar"] = ErrorBar.IsOpen ? $"{ErrorBar.Title} {ErrorBar.Message}".Trim() : null;
            report["statusText"] = StatusText.Visibility == Visibility.Visible ? StatusText.Text : null;
            report["answerMentionsExampleDomain"] = answer?.Contains("Example Domain", StringComparison.OrdinalIgnoreCase) == true;
            await CaptureBrowserUseSmokeAsync(directory, ++step, "result");
            report["ok"] = steps.Any(item => S(item, "status") == "completed") && (bool)report["answerMentionsExampleDomain"]!;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            report["error"] = exception.ToString();
            try { await CaptureBrowserUseSmokeAsync(directory, ++step, "failure"); }
            catch (Exception captureError) when (captureError is not OutOfMemoryException) { report["captureError"] = captureError.Message; }
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "browser-report.json"), JsonSerializer.Serialize(report, BrowserUseReportOptions));
            // A clean exit lets Missum stop the AI stack it started for this check.
            DispatcherQueue.TryEnqueue(() => App.Current.MainWindow?.Close());
        }
    }

    private IEnumerable<JsonElement> BrowserUseSmokeSteps() => _messages.Values
        .SelectMany(MessageSteps)
        .Where(item => S(item, "tool") == BuiltInExtensionCatalog.BrowserUseToolName);

    private async Task WaitForBrowserUseSmokeAsync(Func<bool> condition, TimeSpan timeout, string failure)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > timeout) throw new TimeoutException(failure);
            await Task.Delay(250, _lifetime.Token);
        }
    }

    private async Task CaptureBrowserUseSmokeAsync(string directory, int step, string name)
    {
        var window = App.Current.MainWindow!;
        await Task.Delay(500, _lifetime.Token);
        var position = window.AppWindow.Position;
        var size = window.AppWindow.Size;
        await File.WriteAllTextAsync(Path.Combine(directory, $"browser-{step}.json"), JsonSerializer.Serialize(new
        {
            step, name, x = position.X, y = position.Y, width = size.Width, height = size.Height,
        }), _lifetime.Token);
        var done = Path.Combine(directory, $"browser-{step}.done");
        var watch = Stopwatch.StartNew();
        while (!File.Exists(done))
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(90)) throw new TimeoutException($"Keine Bildschirmaufnahme für {name} erhalten.");
            await Task.Delay(100, _lifetime.Token);
        }
    }
}
