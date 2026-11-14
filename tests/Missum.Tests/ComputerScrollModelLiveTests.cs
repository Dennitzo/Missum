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

/// <summary>Opt-in real-model scroll verification using a disposable Win32 listbox.</summary>
public sealed class ComputerScrollModelLiveTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task RealBrokerScrollsOwnedListDownAndUp()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_COMPUTER_SCROLL_BROKER_LIVE") != "1") return;
        var executable = Environment.GetEnvironmentVariable("MISSUM_COMPUTER_TEST_TARGET")!;
        var title = "Missum Computer Scroll Broker Test " + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.Environment["MISSUM_COMPUTER_TEST_WINDOW"] = title;
        start.Environment["MISSUM_COMPUTER_TEST_NO_ACTIVATE"] = "1";
        start.Environment["MISSUM_COMPUTER_SCROLL_TEST"] = "1";
        using var target = Process.Start(start) ?? throw new InvalidOperationException("Scroll-Testfenster fehlt.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var approval = new OwnedWindowApproval(title);
        using var broker = new ComputerDesktopBroker(approval, new DesktopScreenshotService(NullLogger<DesktopScreenshotService>.Instance));
        var session = Guid.NewGuid(); var runId = Guid.NewGuid().ToString("N");
        try
        {
            nint window = 0, list = 0;
            while (window == 0 || list == 0 || GetForegroundWindow() != window)
            {
                if (target.HasExited) throw new InvalidOperationException("Das Scroll-Testfenster wurde geschlossen.");
                window = FindWindow(null, title);
                list = window == 0 ? 0 : FindWindowEx(window, 0, "ListBox", null);
                if (window == 0 || list == 0 || GetForegroundWindow() != window)
                    await Task.Delay(100, timeout.Token);
            }
            Assert.True(GetWindowRect(list, out var bounds));
            var screenX = (bounds.Left + bounds.Right) / 2;
            var screenY = (bounds.Top + bounds.Bottom) / 2;
            broker.Begin(session, runId);
            var frame = await broker.ObserveAsync(session, runId, "window", timeout.Token);
            var region = Assert.IsType<ComputerImageRegion>(frame.ImageRegion);
            var x = (screenX - region.X) * (double)frame.Width / region.Width;
            var y = (screenY - region.Y) * (double)frame.Height / region.Height;
            Assert.InRange(x, 0, frame.Width - 1);
            Assert.InRange(y, 0, frame.Height - 1);
            var inspected = await broker.ExecuteAsync(session, runId, new(frame.ObservationId, "inspect", X: x, Y: y, CoordinateSpace: "pixels"), timeout.Token);
            Assert.NotNull(inspected.Element);
            output.WriteLine($"UIA-Listenmitte: Typ={inspected.Element.ControlType}, Name={inspected.Element.Name}");

            // A click focuses the controlled ListBox even if the host disables
            // Windows' setting for scrolling inactive controls.
            frame = await broker.ObserveAsync(session, runId, "window", timeout.Token);
            var beforeApproval = approval.RequestCount;
            var click = await broker.ExecuteAsync(session, runId, new(frame.ObservationId, "click", X: x, Y: y, CoordinateSpace: "pixels"), timeout.Token);
            Assert.Equal("completed", click.Status);
            output.WriteLine($"Klick: Freigabeanfragen={approval.RequestCount - beforeApproval}");

            frame = await broker.ObserveAsync(session, runId, "window", timeout.Token);
            var original = TopIndex(list);
            beforeApproval = approval.RequestCount;
            var down = await broker.ExecuteAsync(session, runId, new(frame.ObservationId, "scroll", X: x, Y: y, ScrollY: 120, CoordinateSpace: "pixels"), timeout.Token);
            await Task.Delay(200, timeout.Token);
            var lower = TopIndex(list);
            output.WriteLine($"scrollY=120: Position {original} -> {lower}; Freigabeanfragen={approval.RequestCount - beforeApproval}");
            Assert.Equal("completed", down.Status);
            Assert.Equal(0, approval.RequestCount - beforeApproval);
            Assert.True(lower > original, "Positives scrollY muss das tatsächliche Listenfenster nach unten bewegen.");

            frame = await broker.ObserveAsync(session, runId, "window", timeout.Token);
            beforeApproval = approval.RequestCount;
            var up = await broker.ExecuteAsync(session, runId, new(frame.ObservationId, "scroll", X: x, Y: y, ScrollY: -120, CoordinateSpace: "pixels"), timeout.Token);
            await Task.Delay(200, timeout.Token);
            var higher = TopIndex(list);
            output.WriteLine($"scrollY=-120: Position {lower} -> {higher}; Freigabeanfragen={approval.RequestCount - beforeApproval}");
            Assert.Equal("completed", up.Status);
            Assert.Equal(0, approval.RequestCount - beforeApproval);
            Assert.True(higher < lower, "Negatives scrollY muss das tatsächliche Listenfenster nach oben bewegen.");
            _ = await broker.ObserveAsync(session, runId, "window", timeout.Token);
        }
        finally
        {
            broker.End(session, runId);
            if (!target.HasExited) { target.Kill(entireProcessTree: true); await target.WaitForExitAsync(); }
        }
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task LocalModelScrollsBothDirectionsAndObservesResults()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_COMPUTER_SCROLL_MODEL_LIVE") != "1") return;
        var executable = Environment.GetEnvironmentVariable("MISSUM_COMPUTER_TEST_TARGET")!;
        var gateway = new Uri(Environment.GetEnvironmentVariable("MISSUM_COMPUTER_TEST_GATEWAY")!);
        var model = Environment.GetEnvironmentVariable("MISSUM_COMPUTER_TEST_MODEL")!;
        Assert.True(ComputerLocalEndpoint.IsLocal(gateway));
        var title = "Missum Computer Scroll Test " + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.Environment["MISSUM_COMPUTER_TEST_WINDOW"] = title;
        start.Environment["MISSUM_COMPUTER_TEST_NO_ACTIVATE"] = "1";
        start.Environment["MISSUM_COMPUTER_SCROLL_TEST"] = "1";
        using var target = Process.Start(start) ?? throw new InvalidOperationException("Scroll-Testfenster fehlt.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var http = new HttpClient { BaseAddress = gateway, Timeout = Timeout.InfiniteTimeSpan };
        using var client = new MissumAiClient(http, streamIdleTimeout: TimeSpan.FromMinutes(3));
        var approval = new OwnedWindowApproval(title);
        var screenshots = new DesktopScreenshotService(NullLogger<DesktopScreenshotService>.Instance);
        var inspectionPlatform = new WindowsComputerDesktopPlatform(screenshots);
        using var broker = new ComputerDesktopBroker(approval, screenshots);
        var session = Guid.NewGuid();
        var json = MissumAiProtocol.CreateJsonOptions();
        string? runId = null;
        try
        {
            nint window = 0, list = 0;
            while (window == 0 || list == 0)
            {
                if (target.HasExited) throw new InvalidOperationException("Das Scroll-Testfenster wurde geschlossen.");
                window = FindWindow(null, title);
                list = window == 0 ? 0 : FindWindowEx(window, 0, "ListBox", null);
                if (window == 0 || list == 0) await Task.Delay(100, timeout.Token);
            }
            var workspace = Path.Combine(Path.GetTempPath(), "missum-computer-scroll-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            var tools = BuiltInExtensionCatalog.Tools.Where(t => t.ActionId == BuiltInActionIds.ComputerUse)
                .Select(t => new ToolDescriptor(t.ModelToolName, t.Description,
                    t.RiskClass == ExtensionToolRiskClass.ReadOnly ? ToolRiskClass.ReadOnly : ToolRiskClass.LocalMutation,
                    t.InputSchema, t.TimeoutSeconds, t.MaximumOutputBytes)).ToArray();
            var prompt = $"Bediene ausschließlich das Testfenster '{title}'. Beobachte das Vordergrundfenster mit computer.observe und scope window. "
                + "Scrolle mit einem Bildpunkt in der Mitte der nummerierten Liste nach unten und beobachte die veränderten Zeilen. "
                + "Scrolle danach innerhalb derselben Liste wieder nach oben und beobachte das Ergebnis erneut. "
                + "Nutze für jede Aktion die neueste Beobachtung und deren Bildkoordinaten. Bestätige nur tatsächlich beobachtete Änderungen.";
            var request = new RunRequest(MissumAiProtocol.Version, RunMode.Coding,
                [new RunMessage("user", [new ContentPart("text", prompt)])],
                ClientCapabilities: ["coding", "computer.use"], Limits: new(MaximumOutputTokens: 2048, MaximumContextTokens: 16384),
                SessionId: session.ToString("D"), AllowedServerTools: [], PreferredCodingModelId: model,
                CodingOptions: new(WorkspacePath: workspace), ClientTools: tools);
            var accepted = await client.CreateRunAsync(request, cancellationToken: timeout.Token);
            runId = accepted.RunId;
            broker.Begin(session, runId);
            var down = false; var up = false; var observedDown = false; var observedUp = false;
            var latestScope = "desktop";
            await foreach (var item in client.StreamRunEventsAsync(runId, cancellationToken: timeout.Token))
            {
                if (item.Type == RunEventTypes.ClientToolProposed)
                {
                    var proposal = item.Data.Deserialize<ToolProposal>(json)!;
                    ClientToolResult result;
                    if (proposal.Name == "computer.observe")
                    {
                        while (GetForegroundWindow() != window)
                        {
                            if (target.HasExited) throw new InvalidOperationException("Das Scroll-Testfenster wurde geschlossen.");
                            await Task.Delay(200, timeout.Token);
                        }
                        var scope = proposal.Arguments.TryGetProperty("scope", out var requestedScope)
                            ? requestedScope.GetString() ?? "desktop" : "desktop";
                        latestScope = scope;
                        var frame = await broker.ObserveAsync(session, runId, scope, timeout.Token);
                        Assert.Equal(target.Id, frame.ForegroundProcessId);
                        var top = TopIndex(list);
                        if (down && !up && top > 0) observedDown = true;
                        if (up) observedUp = true;
                        var transfer = await client.UploadComputerObservationAsync(runId, proposal.ProposalId, frame.Content, timeout.Token);
                        result = new(proposal.ProposalId, "completed", JsonSerializer.SerializeToElement(new
                        {
                            frame.ObservationId, observationTransferId = transfer, frame.Width, frame.Height,
                            frame.DesktopX, frame.DesktopY, frame.DesktopWidth, frame.DesktopHeight,
                            frame.ForegroundTitle, frame.ForegroundProcessId, frame.Windows, frame.ImageRegion,
                        }, json));
                        output.WriteLine($"observe: erste sichtbare Zeile {top + 1}");
                    }
                    else if (proposal.Name == "computer.act")
                    {
                        var action = proposal.Arguments.Deserialize<ComputerAction>(json)!;
                        Assert.NotEqual("focus", action.Kind);
                        var before = TopIndex(list);
                        var approvalsBefore = approval.RequestCount;
                        try
                        {
                            if (action.Kind == "scroll" && action.X.HasValue && action.Y.HasValue)
                            {
                                // Read UIA at the exact model-selected point without consuming
                                // the broker's one-use frame or changing the test window.
                                try
                                {
                                    var frame = latestScope == "window"
                                        ? await inspectionPlatform.CaptureWindowAsync(timeout.Token)
                                        : await inspectionPlatform.CaptureAsync(timeout.Token);
                                    var point = ComputerDesktopBroker.NormalizeCoordinates(action, frame.Public);
                                    var element = await inspectionPlatform.InspectAsync(frame, point, timeout.Token);
                                    output.WriteLine($"UIA-Scrollziel: Typ={element.ControlType}, Name={element.Name}, Aktiv={element.IsEnabled}");
                                }
                                catch (InvalidOperationException exception)
                                {
                                    output.WriteLine("UIA-Scrollziel nicht bestimmbar: " + exception.Message);
                                }
                            }
                            else if (action.Kind == "scroll")
                            {
                                output.WriteLine("UIA-Inspektion übersprungen: Der Modellaufruf enthält keine x/y-Koordinaten.");
                            }
                            var outcome = await broker.ExecuteAsync(session, runId, action, timeout.Token);
                            await Task.Delay(200, timeout.Token); // Deliver the Win32 mouse-wheel message before measuring.
                            var after = TopIndex(list);
                            if (action.Kind == "scroll" && outcome.Status == "completed")
                            {
                                output.WriteLine($"scrollY={action.ScrollY}: Position {before} -> {after}; Freigabeanfragen={approval.RequestCount - approvalsBefore}");
                                Assert.Equal(0, approval.RequestCount - approvalsBefore);
                                if (action.ScrollY > 0 && after > before) down = true;
                                if (down && observedDown && action.ScrollY < 0 && after < before) up = true;
                            }
                            result = new(proposal.ProposalId, outcome.Status == "completed" ? "completed" : "failed",
                                JsonSerializer.SerializeToElement(outcome, json));
                        }
                        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
                        {
                            result = new(proposal.ProposalId, "failed", JsonSerializer.SerializeToElement(new { failed = true }),
                                "computer.action_not_executed", exception.Message);
                        }
                        output.WriteLine($"{action.Kind}: {result.Status} {result.Message}");
                    }
                    else throw new InvalidOperationException("Unerwartetes Werkzeug: " + proposal.Name);
                    await client.SubmitClientToolResultAsync(runId, result, timeout.Token);
                }
                else if (item.Type == RunEventTypes.RunFailed)
                    throw new InvalidOperationException("Scroll-Testlauf fehlgeschlagen: " + item.Data.GetRawText());
                else if (item.Type is RunEventTypes.RunCompleted or RunEventTypes.RunCancelled) break;
            }
            Assert.True(down, "Positives scrollY muss die Liste tatsächlich nach unten bewegen.");
            Assert.True(observedDown, "Nach dem Abwärtsscrollen muss das Modell neu beobachten.");
            Assert.True(up, "Negatives scrollY muss die Liste tatsächlich nach oben bewegen.");
            Assert.True(observedUp, "Nach dem Aufwärtsscrollen muss das Modell neu beobachten.");
        }
        finally
        {
            if (runId is not null)
            {
                broker.End(session, runId);
                try { await client.CancelRunAsync(runId); } catch (HttpRequestException) { }
            }
            if (!target.HasExited) { target.Kill(entireProcessTree: true); await target.WaitForExitAsync(); }
        }
    }

    private sealed class OwnedWindowApproval(string title) : IComputerActionApproval
    {
        private readonly ComputerActionApprovalService _activation = new();
        public int RequestCount { get; private set; }
        public Task<bool> ActivateAsync(Guid sessionId, string runId, CancellationToken cancellationToken) =>
            _activation.ActivateAsync(sessionId, runId, cancellationToken);
        public Task<ComputerActionDecision> RequestAsync(ComputerApprovalRequest request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(request.TargetTitle.Contains(title, StringComparison.Ordinal)
                && request.Action is "scroll" or "click" ? ComputerActionDecision.Execute : ComputerActionDecision.Cancel);
        }
    }

    private static int TopIndex(nint list) => checked((int)SendMessage(list, 0x018E, 0, 0));
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? className, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
}
