using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Missum.Ai.Client;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Extensions;
using Xunit.Abstractions;

namespace Missum.Tests;

/// <summary>Opt-in test against an isolated local gateway, native vision model, and owned test window.</summary>
public sealed class ComputerModelLiveTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task LocalVisionModelCanObserveAndOperateOwnedWindow()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_COMPUTER_MODEL_LIVE") != "1") return;
        var executable = Environment.GetEnvironmentVariable("MISSUM_COMPUTER_TEST_TARGET")!;
        var gateway = new Uri(Environment.GetEnvironmentVariable("MISSUM_COMPUTER_TEST_GATEWAY")!);
        var model = Environment.GetEnvironmentVariable("MISSUM_COMPUTER_TEST_MODEL")!;
        // Optional interactive variant: exercise the real window-switch pause
        // while the model run is in progress, using only our own two windows.
        var switchSignal = Environment.GetEnvironmentVariable("MISSUM_COMPUTER_SWITCH_SIGNAL");
        Assert.True(ComputerLocalEndpoint.IsLocal(gateway));
        var title = "Missum Computer Test " + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.Environment["MISSUM_COMPUTER_TEST_WINDOW"] = title;
        if (!string.IsNullOrWhiteSpace(switchSignal)) start.Environment["MISSUM_COMPUTER_TEST_NO_ACTIVATE"] = "1";
        using var target = Process.Start(start) ?? throw new InvalidOperationException("Testfenster fehlt.");
        var alternateTitle = "Missum Computer Pause " + Guid.NewGuid().ToString("N");
        using var alternate = string.IsNullOrWhiteSpace(switchSignal) ? null : StartTestWindow(executable, alternateTitle);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var http = new HttpClient { BaseAddress = gateway, Timeout = Timeout.InfiniteTimeSpan };
        using var client = new MissumAiClient(http, streamIdleTimeout: TimeSpan.FromMinutes(3));
        var approval = new AutomaticOnlyApproval();
        using var broker = new ComputerDesktopBroker(approval, new DesktopScreenshotService(NullLogger<DesktopScreenshotService>.Instance));
        string? runId = null;
        var session = Guid.NewGuid();
        var json = MissumAiProtocol.CreateJsonOptions();
        try
        {
            nint window = 0, edit = 0;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
            while (DateTimeOffset.UtcNow < deadline && !target.HasExited)
            {
                window = FindWindow(null, title);
                edit = window == 0 ? 0 : FindWindowEx(window, 0, "Edit", null);
                if (window != 0 && edit != 0) break;
                await Task.Delay(100, timeout.Token);
            }
            Assert.NotEqual((nint)0, window);
            Assert.NotEqual((nint)0, edit);
            nint alternateWindow = 0;
            if (alternate is not null)
            {
                while ((alternateWindow = FindWindow(null, alternateTitle)) == 0)
                {
                    if (alternate.HasExited) throw new InvalidOperationException("Das zweite Testfenster wurde geschlossen.");
                    await Task.Delay(100, timeout.Token);
                }
                await File.WriteAllTextAsync(switchSignal!, "activate-a", timeout.Token);
            }
            var workspace = Path.Combine(Path.GetTempPath(), "missum-computer-model-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            var tools = BuiltInExtensionCatalog.Tools.Where(t => t.ActionId == BuiltInActionIds.ComputerUse)
                .Select(t => new ToolDescriptor(t.ModelToolName, t.Description,
                    t.RiskClass == ExtensionToolRiskClass.ReadOnly ? ToolRiskClass.ReadOnly : ToolRiskClass.LocalMutation,
                    t.InputSchema, t.TimeoutSeconds, t.MaximumOutputBytes)).ToArray();
            var prompt = $"Bediene ausschließlich das Testfenster '{title}'. Beobachte das Vordergrundfenster mit computer.observe und scope window. "
                + "Klicke in das leere Textfeld, gib exakt 'Missum Test' ein und beobachte danach erneut. "
                + "Nutze für jede Aktion eine frische Beobachtung und deren Bildmaße. Für deine normalisierten Koordinaten von 0 bis 999 setze coordinateSpace auf normalized1000. "
                + "Bestätige nur ein tatsächlich beobachtetes Ergebnis.";
            var request = new RunRequest(MissumAiProtocol.Version, RunMode.Coding,
                [new RunMessage("user", [new ContentPart("text", prompt)])],
                ClientCapabilities: ["coding", "computer.use"], Limits: new(MaximumOutputTokens: 2048, MaximumContextTokens: 16384),
                SessionId: session.ToString("D"), AllowedServerTools: [], PreferredCodingModelId: model,
                CodingOptions: new(WorkspacePath: workspace), ClientTools: tools);
            var accepted = await client.CreateRunAsync(request, cancellationToken: timeout.Token);
            runId = accepted.RunId;
            broker.Begin(session, runId);
            var observed = 0; var typed = false; var observedAfterTyping = false;
            var switched = false; var staleRejected = false; var observedAfterSwitch = false;
            string? firstObservation = null;
            await foreach (var item in client.StreamRunEventsAsync(runId, cancellationToken: timeout.Token))
            {
                if (item.Type == RunEventTypes.ClientToolProposed)
                {
                    var proposal = item.Data.Deserialize<ToolProposal>(json)!;
                    ClientToolResult result;
                    if (proposal.Name == "computer.observe")
                    {
                        if (switched && !staleRejected)
                        {
                            // If the model asks to observe again before its first action,
                            // explicitly verify the old frame cannot authorize even a move.
                            var stale = await Assert.ThrowsAsync<InvalidOperationException>(() => broker.ExecuteAsync(
                                session, runId, new(firstObservation!, "move", X: 1, Y: 1, CoordinateSpace: "pixels"), timeout.Token));
                            Assert.Contains("Vordergrundfenster", stale.Message);
                            staleRejected = true;
                        }
                        var scope = proposal.Arguments.TryGetProperty("scope", out var requestedScope)
                            ? requestedScope.GetString() ?? "desktop" : "desktop";
                        Assert.Equal("window", scope);
                        // The user may keep working while the model loads or reasons.
                        // Do not capture or transfer another application in this test;
                        // wait for the test window rather than asserting immediately.
                        if (GetForegroundWindow() != window)
                        {
                            output.WriteLine("Test pausiert: Bitte zum eigenen Testfenster zurückkehren.");
                            while (GetForegroundWindow() != window)
                            {
                                if (target.HasExited) throw new InvalidOperationException("Das Testfenster wurde geschlossen.");
                                await Task.Delay(200, timeout.Token);
                            }
                        }
                        var frame = await broker.ObserveAsync(session, runId, scope, timeout.Token);
                        Assert.True(target.Id == frame.ForegroundProcessId,
                            "Das Vordergrundfenster wechselte unmittelbar vor der Aufnahme. " +
                            "Das fremde Bild wurde nicht an das Modell übertragen.");
                        var transfer = await client.UploadComputerObservationAsync(runId, proposal.ProposalId, frame.Content, timeout.Token);
                        result = new(proposal.ProposalId, "completed", JsonSerializer.SerializeToElement(new
                        {
                            frame.ObservationId, observationTransferId = transfer, frame.Width, frame.Height,
                            frame.DesktopX, frame.DesktopY, frame.DesktopWidth, frame.DesktopHeight,
                            frame.ForegroundTitle, frame.ForegroundProcessId, frame.Windows, frame.ImageRegion,
                        }, json));
                        observed++;
                        firstObservation ??= frame.ObservationId;
                        if (switched) observedAfterSwitch = true;
                        if (typed) observedAfterTyping = true;
                    }
                    else if (proposal.Name == "computer.act")
                    {
                        var action = proposal.Arguments.Deserialize<ComputerAction>(json)!;
                        Assert.NotEqual("focus", action.Kind); // Never leave the explicitly owned foreground test window.
                        if (action.Kind == "type") Assert.Equal("Missum Test", action.Text);
                        output.WriteLine("action=" + proposal.Arguments.GetRawText());
                        try
                        {
                            var outcome = await broker.ExecuteAsync(session, runId, action, timeout.Token);
                            typed |= action.Kind == "type" && outcome.Status == "completed";
                            result = new(proposal.ProposalId, outcome.Status == "completed" ? "completed" : "failed",
                                JsonSerializer.SerializeToElement(outcome, json));
                        }
                        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
                        {
                            if (switched && !staleRejected && action.ObservationId == firstObservation)
                            {
                                Assert.Contains("Vordergrundfenster", exception.Message);
                                staleRejected = true;
                            }
                            result = new(proposal.ProposalId, "failed", JsonSerializer.SerializeToElement(new { failed = true }),
                                "computer.action_not_executed", exception.Message);
                        }
                    }
                    else throw new InvalidOperationException("Unerwartetes Werkzeug: " + proposal.Name);
                    output.WriteLine($"{proposal.Name}: {result.Status}" +
                        (string.IsNullOrWhiteSpace(result.Message) ? "" : $" · {result.Message}"));
                    await client.SubmitClientToolResultAsync(runId, result, timeout.Token);
                    if (alternate is not null && !switched && proposal.Name == "computer.observe" && result.Status == "completed")
                    {
                        // The model has received its first image. Let the test operator
                        // switch to B and back; this harness never steals foreground focus.
                        await File.WriteAllTextAsync(switchSignal!, "switch-b", timeout.Token);
                        while (GetForegroundWindow() != alternateWindow) await Task.Delay(100, timeout.Token);
                        await Task.Delay(400, timeout.Token); // Allow delivery of the Windows foreground event.
                        await File.WriteAllTextAsync(switchSignal!, "return-a", timeout.Token);
                        while (GetForegroundWindow() != window) await Task.Delay(100, timeout.Token);
                        await Task.Delay(400, timeout.Token);
                        switched = true;
                        output.WriteLine("Fensterwechsel erfolgt; die alte Beobachtung darf keine Eingabe mehr auslösen.");
                    }
                }
                else if (item.Type == RunEventTypes.RunFailed)
                    throw new InvalidOperationException("Testlauf fehlgeschlagen: " + item.Data.GetRawText());
                else if (item.Type is RunEventTypes.RunCompleted or RunEventTypes.RunCancelled) break;
            }
            var buffer = new char[256];
            _ = SendMessage(edit, 0x000D, (nuint)buffer.Length, buffer);
            Assert.Equal("Missum Test", new string(buffer).TrimEnd('\0'));
            Assert.True(observed >= 2 && typed && observedAfterTyping, "Das Modell muss vor und nach der Eingabe tatsächlich beobachten.");
            Assert.Equal(0, approval.RequestCount);
            if (alternate is not null)
            {
                Assert.True(switched && staleRejected && observedAfterSwitch,
                    "Nach dem Fensterwechsel muss die alte Aktion verworfen und erneut beobachtet werden.");
                await File.WriteAllTextAsync(switchSignal!, "complete", timeout.Token);
            }
        }
        finally
        {
            if (runId is not null)
            {
                broker.End(session, runId);
                try { await client.CancelRunAsync(runId); } catch (HttpRequestException) { }
            }
            if (!target.HasExited) { target.Kill(entireProcessTree: true); await target.WaitForExitAsync(); }
            if (alternate is not null && !alternate.HasExited) { alternate.Kill(entireProcessTree: true); await alternate.WaitForExitAsync(); }
        }
    }

    private static Process StartTestWindow(string executable, string title)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.Environment["MISSUM_COMPUTER_TEST_WINDOW"] = title;
        start.Environment["MISSUM_COMPUTER_TEST_NO_ACTIVATE"] = "1";
        return Process.Start(start) ?? throw new InvalidOperationException("Zweites Testfenster fehlt.");
    }

    private sealed class AutomaticOnlyApproval : IComputerActionApproval
    {
        private readonly ComputerActionApprovalService _service = new();
        public int RequestCount { get; private set; }
        public Task<bool> ActivateAsync(Guid sessionId, string runId, CancellationToken cancellationToken) =>
            _service.ActivateAsync(sessionId, runId, cancellationToken);
        public Task<ComputerActionDecision> RequestAsync(ComputerApprovalRequest request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(ComputerActionDecision.Cancel);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? className, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessage(nint window, uint message, nuint wParam, [Out] char[] buffer);
}
