using System.Diagnostics;
using System.Text.Json;
using Missum.App.Controls;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    // Real SQLite -> host -> dispatcher -> XAML path. Only the fresh, isolated
    // profile created by windows/smoke.ps1 may receive these disposable rows.
    private async Task VerifyPagingSmokeAsync(JsonElement original)
    {
        if (!IsMessageFooterSmoke) return;
        var directory = Path.GetFullPath(App.Current.DataDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var smokeBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Missum-Smoke"));
        var parent = Directory.GetParent(directory);
        var database = App.Current.GetService<IMissumDatabase>();
        if (parent is null || !Guid.TryParseExact(parent.Name, "N", out _)
            || !string.Equals(parent.Parent?.FullName, smokeBase, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(directory), "Data", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(database.DatabasePath)), directory, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Environment.GetEnvironmentVariable("MISSUM_DATA_DIRECTORY"), directory, StringComparison.OrdinalIgnoreCase)
            || Environment.GetEnvironmentVariable("ASSISTANT_DISABLE_RUNTIME_AUTOSTART") != "1")
            throw new InvalidOperationException("Der Paging-Smoke benötigt die frische, isolierte Datenbank des Portable-Smoke.");

        var chats = App.Current.GetService<IChatRepository>();
        var originalSession = Guid.Parse(S(original, "activeSessionId"));
        var originalExpansion = _settings.Current.CodingToolStepsExpanded;
        var fixtureSession = Guid.Empty;
        var previousFixture = _selectionInputSmokeActive;
        _selectionInputSmokeActive = true;
        try
        {
            ApplyEvent("state.snapshot", original);
            RenderMessagesNow();
            await _settings.UpdateAsync(settings => settings with { CodingToolStepsExpanded = false });
            var fixture = await Task.Run(async () =>
            {
                var session = await chats.CreateSessionAsync("Isolierter Verlaufstest", ChatMode.General, _lifetime.Token);
                fixtureSession = session.Id;
                var messages = new List<ChatMessage>();
                for (var index = 0; index < 60; index++)
                    messages.Add(await chats.AddMessageAsync(session.Id, index % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                        $"Nachricht {index:D2}: Analytischer Kontrollfall und numerische Berechnung.\n\n"
                        + "Dieser unveränderte Quelltext bleibt vollständig für den Modellkontext erhalten, auch wenn nur der letzte Abschnitt dargestellt wird.",
                        MessageStatus.Completed, cancellationToken: _lifetime.Token));
                var largeOutput = string.Join('\n', Enumerable.Range(0, 3200).Select(index => $"Zeile {index:D4}: Kontrollrechnung und Genauigkeit geprüft."));
                var step = new AssistantToolStep("paging-receipt", "coding.command", "completed",
                    InputJson: "{\"command\":\"python calculation.py\"}",
                    OutputJson: JsonSerializer.Serialize(new { stdout = largeOutput, exitCode = 0 }), UpdatedAt: DateTimeOffset.UtcNow);
                await chats.SaveToolStepAsync(messages[^3].Id, step, _lifetime.Token);
                await chats.SetMessageContextSummaryAsync(messages[^3].Id, "Unveränderte Vorbereitung des Hauptmodell-Kontexts.", _lifetime.Token);
                var at = DateTimeOffset.UtcNow;
                var childStep = step with { Id = "child-paging-receipt" };
                var storedChild = new SubagentChatState("paging-smoke-" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString(),
                    Guid.NewGuid().ToString(), session.Id, "Unabhängige Kontrollrechnung", "completed",
                    new(Guid.NewGuid(), session.Id, ChatRole.User, "Prüfe den Kontrollfall.", MessageStatus.Completed, at, at),
                    new(Guid.NewGuid(), session.Id, ChatRole.Assistant, "Der Kontrollfall wurde geprüft.", MessageStatus.Completed, at, at, ToolSteps: [childStep]),
                    ResultDelivered: true, ProjectionRevision: 1);
                await chats.SaveToolStepAsync(messages[^1].Id, new AssistantToolStep(storedChild.ReceiptId, "subagent", "completed",
                    OutputJson: await storedChild.PersistAsync(directory), UpdatedAt: at, AgentId: storedChild.AgentId), _lifetime.Token);
                var durable = await chats.ListMessagesAsync(session.Id, _lifetime.Token);
                var revision = (await chats.GetSessionAsync(session.Id, _lifetime.Token))!.ConversationRevision;
                var preparation = new SessionContextPreparation(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes("paging-smoke-" + session.Id))), session.Id,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                        revision.ToString(System.Globalization.CultureInfo.InvariantCulture)))),
                    "smoke-context-model", 131072, durable[^1].Id, durable.Count, "Stabiler vorbereiteter Promptpräfix.", at);
                await chats.SaveSessionContextPreparationAsync(preparation, _lifetime.Token);
                return new { Session = session, Messages = durable, Revision = revision, Preparation = preparation,
                    Step = step, ToolMessageId = messages[^3].Id.ToString(), Child = storedChild };
            }, _lifetime.Token);

            var openWatch = Stopwatch.StartNew();
            if (!await CommandAsync("session.open", new { sessionId = fixture.Session.Id }))
                throw new InvalidOperationException("Die gespeicherte Paging-Sitzung konnte nicht geöffnet werden.");
            RenderMessagesNow();
            await WaitForPagingSmokeAsync(() => ConversationScroll.ScrollableHeight > 300
                && ConversationScroll.ScrollableHeight - ConversationScroll.VerticalOffset <= 4,
                "Die neue Sitzung wurde nicht auf den neuesten Abschnitt gescrollt.");
            openWatch.Stop();
            if (_messages.Count != 24 || !_historyHasMore
                || !_messages.Keys.ToHashSet().SetEquals(fixture.Messages.TakeLast(24).Select(message => message.Id.ToString()))
                || ConversationScroll.ScrollableHeight - ConversationScroll.VerticalOffset > 4)
                throw new InvalidOperationException("Der erste UI-Abruf muss genau die neuesten 24 Nachrichten laden und unten beginnen.");
            var header = Items(_messages[fixture.ToolMessageId], "toolSteps").Single(step => S(step, "id") == fixture.Step.Id);
            if (!PagingSmokeFlag(header, "detailsDeferred") || S(header, "outputJson").Length != 0)
                throw new InvalidOperationException("Der eingeklappte Werkzeugschritt hat seine große Ausgabe vorzeitig geladen.");
            if (!_subagents.TryGetValue(fixture.Child.AgentId, out var child) || !child.TranscriptDeferred || child.Messages.Count != 0)
                throw new InvalidOperationException("Das Subagententranskript wurde vor dem Öffnen seines Tabs geladen.");

            // ChangeView raises the same ViewChanged path used by wheel/scrollbar.
            // Capture the first already visible control before the DB response.
            FrameworkElement? anchor = null;
            double anchorViewportY = 0;
            void CaptureAnchor(object? sender, ScrollViewerViewChangedEventArgs args)
            {
                if (args.IsIntermediate || _messages.Count != 24 || ConversationScroll.VerticalOffset >= 240 || anchor is not null) return;
                anchor = MessagesPanel.Children.OfType<FrameworkElement>().FirstOrDefault(element =>
                    element.TransformToVisual(ConversationContent).TransformPoint(new()).Y + element.ActualHeight >= ConversationScroll.VerticalOffset);
                if (anchor is not null)
                    anchorViewportY = anchor.TransformToVisual(ConversationContent).TransformPoint(new()).Y - ConversationScroll.VerticalOffset;
            }
            var pageWatch = Stopwatch.StartNew();
            ConversationScroll.ViewChanged += CaptureAnchor;
            try
            {
                OuterChatScrollBar.Value = 120;
                await WaitForPagingSmokeAsync(() => _messages.Count >= 48 && !_historyLoading,
                    "Beim Scrollen wurden die älteren Nachrichten nicht aus SQLite nachgeladen.");
                await Task.Delay(80, _lifetime.Token);
                ConversationContent.UpdateLayout();
            }
            finally { ConversationScroll.ViewChanged -= CaptureAnchor; }
            pageWatch.Stop();
            var anchorError = anchor is null ? double.PositiveInfinity
                : Math.Abs(anchor.TransformToVisual(ConversationContent).TransformPoint(new()).Y - ConversationScroll.VerticalOffset - anchorViewportY);
            if (_messages.Count != 48 || !_historyHasMore || anchor is null || !MessagesPanel.Children.Contains(anchor) || anchorError > 4)
                throw new InvalidOperationException($"Der ältere Abschnitt hat den sichtbaren Scrollanker verschoben ({anchorError:F1} px; {_messages.Count} Nachrichten).");
            OuterChatScrollBar.Value = 120;
            await WaitForPagingSmokeAsync(() => _messages.Count == 60 && !_historyLoading && !_historyHasMore,
                "Der letzte ältere Abschnitt wurde nicht vollständig nachgeladen.");
            if (!_messages.Keys.ToHashSet().SetEquals(fixture.Messages.Select(message => message.Id.ToString())))
                throw new InvalidOperationException("Die dargestellte Historie enthält nach dem Nachladen fehlende oder doppelte Nachrichten.");

            var tool = _messageBlocks[fixture.ToolMessageId]["tool:" + fixture.Step.Id] as ToolStepView
                ?? throw new InvalidOperationException("Der native Werkzeugschritt fehlt.");
            if (tool.IsExpanded || Descendants(tool).OfType<NativeToolResultView>().Any())
                throw new InvalidOperationException("Eingeklappte Werkzeugdetails wurden bereits gerendert.");
            var detailWatch = Stopwatch.StartNew();
            InvokePagingSmokeTool(tool);
            await WaitForPagingSmokeAsync(() => !PagingSmokeFlag(Items(_messages[fixture.ToolMessageId], "toolSteps").Single(step => S(step, "id") == fixture.Step.Id), "detailsDeferred")
                && Descendants(tool).OfType<NativeToolResultView>().Any(), "Aufklappen hat die Werkzeugdetails nicht geladen und gerendert.");
            detailWatch.Stop();
            if (S(Items(_messages[fixture.ToolMessageId], "toolSteps").Single(step => S(step, "id") == fixture.Step.Id), "outputJson") != fixture.Step.OutputJson)
                throw new InvalidOperationException("Die nachgeladene Werkzeugausgabe wurde verändert oder abgeschnitten.");

            await ActivateSubagentTabAsync(child);
            if (!ReferenceEquals(ActiveSubagent, child) || child.TranscriptDeferred || child.Messages.Count != 2)
                throw new InvalidOperationException("Der Subagententab hat sein gespeichertes Transkript nicht bedarfsgerecht geladen.");
            var childMessageId = fixture.Child.AssistantMessage.Id.ToString();
            var childTool = _messageBlocks[childMessageId]["tool:child-paging-receipt"] as ToolStepView
                ?? throw new InvalidOperationException("Der Subagent verwendet nicht den gemeinsamen nativen Werkzeugrenderer.");
            if (childTool.IsExpanded || !PagingSmokeFlag(Items(child.Messages[childMessageId], "toolSteps").Single(), "detailsDeferred"))
                throw new InvalidOperationException("Der Subagent hat eingeklappte Werkzeugdetails vorzeitig geladen.");
            InvokePagingSmokeTool(childTool);
            await WaitForPagingSmokeAsync(() => !PagingSmokeFlag(Items(child.Messages[childMessageId], "toolSteps").Single(), "detailsDeferred")
                && Descendants(childTool).OfType<NativeToolResultView>().Any(), "Der Subagent hat seine Werkzeugdetails nicht nachgeladen.");
            ShowParentConversation();
            if (tool.IsExpanded) throw new InvalidOperationException("Der Tabwechsel hat die Standard-Einklappung nicht wiederhergestellt.");
            await ActivateSubagentTabAsync(child);
            if (childTool.IsExpanded) throw new InvalidOperationException("Der Subagententab hat die Standard-Einklappung nicht wiederhergestellt.");
            ShowParentConversation();

            var unchanged = await Task.Run(async () =>
                JsonSerializer.Serialize(await chats.ListMessagesAsync(fixture.Session.Id, _lifetime.Token)) == JsonSerializer.Serialize(fixture.Messages)
                && (await chats.GetSessionAsync(fixture.Session.Id, _lifetime.Token))!.ConversationRevision == fixture.Revision
                && await chats.GetSessionContextPreparationAsync(fixture.Preparation.CacheKey, _lifetime.Token) == fixture.Preparation, _lifetime.Token);
            if (!unchanged) throw new InvalidOperationException("UI-Nachladen hat persistente Nachrichten oder die Vorbereitung des Modellkontexts verändert.");
            await SaveMathPreviewAsync(LayoutRoot, "native-paging-preview.png");
            await File.WriteAllTextAsync(Path.Combine(directory, "native-paging-validation.json"), JsonSerializer.Serialize(new
            {
                processId = Environment.ProcessId, initialMessages = 24, afterFirstPage = 48, totalMessages = _messages.Count,
                scrollAnchorErrorPixels = anchorError, initialLoadMilliseconds = openWatch.Elapsed.TotalMilliseconds,
                olderPageMilliseconds = pageWatch.Elapsed.TotalMilliseconds, toolDetailsMilliseconds = detailWatch.Elapsed.TotalMilliseconds,
                initialDetailsDeferred = true, toolDetailsLoadedOnExpand = true, subagentTranscriptLoadedOnOpen = true,
                subagentToolDetailsLoadedOnExpand = true, disclosureResetOnNavigation = true,
                durableMessagesUnchanged = true, conversationRevisionUnchanged = true, contextPreparationUnchanged = true,
                liveModelCacheReuseMeasured = false,
            }, JsonOptions), _lifetime.Token);
        }
        finally
        {
            ShowParentConversation();
            await _settings.UpdateAsync(settings => settings with { CodingToolStepsExpanded = originalExpansion });
            await CommandAsync("session.open", new { sessionId = originalSession });
            if (fixtureSession != Guid.Empty) await chats.DeleteSessionAsync(fixtureSession, _lifetime.Token);
            ApplyEvent("state.snapshot", original);
            RenderMessagesNow();
            _selectionInputSmokeActive = previousFixture;
        }
    }

    private async Task WaitForPagingSmokeAsync(Func<bool> condition, string failure)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new InvalidOperationException(failure);
            await Task.Delay(20, _lifetime.Token);
        }
    }

    private static void InvokePagingSmokeTool(ToolStepView tool)
    {
        var toggle = Descendants(tool).OfType<Button>().First();
        if (new ButtonAutomationPeer(toggle).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new InvalidOperationException("Der Werkzeugschritt besitzt keinen bedienbaren nativen Aufklappschalter.");
        invoke.Invoke();
    }

    private static bool PagingSmokeFlag(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(property, out var flag) && flag.ValueKind == JsonValueKind.True;
}
