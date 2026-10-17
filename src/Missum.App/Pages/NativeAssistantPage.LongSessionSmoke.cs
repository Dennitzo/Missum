using System.Diagnostics;
using System.Text.Json;
using Missum.App.Controls;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifyLongSessionProjectionSmokeAsync(JsonElement original)
    {
        if (!IsMessageFooterSmoke || Environment.GetEnvironmentVariable("MISSUM_SMOKE_UI_LONG_SESSION") != "1") return;
        var directory = Path.GetFullPath(App.Current.DataDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var ownerDirectory = Directory.GetParent(directory);
        var smokeBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Missum-Smoke"));
        var database = App.Current.GetService<IMissumDatabase>();
        if (ownerDirectory?.Parent?.FullName != smokeBase || !Guid.TryParseExact(ownerDirectory.Name, "N", out _)
            || Environment.GetEnvironmentVariable("ASSISTANT_DISABLE_RUNTIME_AUTOSTART") != "1"
            || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(database.DatabasePath)), directory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Der Langsitzungs-Smoke darf nur die isolierte Portable-Smoke-Datenbank verwenden.");
        var chats = App.Current.GetService<IChatRepository>();
        var snapshots = App.Current.GetService<IConversationSnapshotRepository>();
        var previousFixture = _selectionInputSmokeActive;
        _selectionInputSmokeActive = true;
        var originalSession = Guid.Parse(S(original, "activeSessionId"));
        var fixtureSession = Guid.Empty;
        var measurements = new List<(string Phase, long ManagedBytes, long WorkingSet, int RealizedBlocks, int LogicalBlocks)>();
        var latencies = new List<double>();
        var warmSessionOpens = new List<double>();
        var faults = _uiCallbackFaultCount;
        try
        {
            var fixture = await Task.Run(async () =>
            {
                var session = await chats.CreateSessionAsync("Isolierter Langsitzungs-Smoke", ChatMode.General, _lifetime.Token);
                fixtureSession = session.Id;
                await chats.AddMessageAsync(session.Id, ChatRole.User, "Prüfe die unveränderten mathematischen Quellbereiche.",
                    MessageStatus.Completed, cancellationToken: _lifetime.Token);
                var content = string.Join("\n\n", Enumerable.Range(0, 600).Select(index =>
                    $"Abschnitt {index}: Der Kontrollfall wird geprüft. Die Gleichung $E=mc^2$ und der Bruch $\\frac{{a+b}}{{c^2}}$ bleiben unverändert kopierbar. "
                    + "Die numerischen Voraussetzungen und der mathematische Gültigkeitsbereich werden getrennt erläutert."));
                var answer = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, content, MessageStatus.Streaming,
                    cancellationToken: _lifetime.Token);
                var output = JsonSerializer.Serialize(new { stdout = new string('x', 8_704), exitCode = 0 });
                var reasoning = string.Join("\n\n", Enumerable.Range(0, 5000).Select(index =>
                    $"Beweisschritt {index}: Unter den angegebenen Voraussetzungen bleibt $a=b$ unverändert gültig. "
                    + "Dieser originale Denkabschnitt wird erst bei ausdrücklichem Aufklappen nachgeladen."));
                var steps = Enumerable.Range(0, 2500).Select(index => new AssistantToolStep("long-step-" + index,
                    index == 2498 ? "assistant.reasoning" : "coding.command", "completed",
                    Detail: index == 2498 ? reasoning : "Kontrollrechnung " + index, InputJson: "{\"command\":\"check\"}",
                    OutputJson: index == 2498 ? null : output, StartedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow,
                    ContentOffset: content.Length * index / 2500)).ToArray();
                await chats.UpdateMessageWithToolStepsAsync(answer.Id, content, MessageStatus.Streaming, steps, _lifetime.Token);
                var page = (await snapshots.GetPageAsync(session.Id, cancellationToken: _lifetime.Token))!;
                var projected = page.Conversation.Messages.Single(message => message.Id == answer.Id);
                if (projected.ToolSteps?.Count != 64 || page.ToolStepWindows?.GetValueOrDefault(answer.Id)?.TotalSteps != 2500
                    || (projected.ToolSteps ?? []).Any(step => step.OutputJson is { Length: > 0 }))
                    throw new InvalidOperationException("Die echte SQLite-Anzeigeprojektion lädt weiterhin sämtliche Werkzeugausgaben.");
                return new { SessionId = session.Id, MessageId = answer.Id.ToString(), Content = content,
                    DurableToolSteps = steps.Length, DurableOutputBytes = (long)output.Length * (steps.Length - 1),
                    ReasoningCharacters = reasoning.Length };
            }, _lifetime.Token);

            if (!await CommandAsync("session.open", new { sessionId = fixture.SessionId }))
                throw new InvalidOperationException("Der gespeicherte Langsitzungs-Smoke konnte nicht geöffnet werden.");
            ShowChatView();
            _running = true;
            _scrollNewSessionToLatest = true;
            RenderMessagesNow();
            await WaitForPagingSmokeAsync(() => _virtualMessages.TryGetValue(fixture.MessageId, out var state)
                && !state.Preparing && state.Panel.LogicalCount > 100 && state.Panel.IsLoaded,
                "Die gespeicherte große AI-Nachricht wurde nicht als logisches Blockfenster geöffnet.");
            await Task.Delay(250, _lifetime.Token);
            await WaitForNativeMathPreparationAsync(LayoutRoot);
            if (MessageSteps(_messages[fixture.MessageId]).Length != 64 || _virtualMessages[fixture.MessageId].Panel.RealizedCount > 256)
                throw new InvalidOperationException("Der native Langsitzungs-Smoke besitzt kein begrenztes Anzeigefenster.");
            await MeasureRetainedAsync("cold-open");
            var (expandedLogicalBlocks, expandedRealizedBlocks) = await VerifyExpandedReasoningAsync(fixture.MessageId);
            _scrollNewSessionToLatest = true;
            OnScrollToBottom(this, new RoutedEventArgs());
            // Include first-use Markdown/math/disclosure and native navigation
            // caches in the warm baseline. The helpers below return values,
            // never retain the first native control tree across later opens.
            await OpenAndMeasureAsync(originalSession);
            await OpenAndMeasureAsync(fixture.SessionId);
            await MeasureRetainedAsync("warm");

            using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var probes = Task.Run(async () =>
            {
                while (!probeCancellation.IsCancellationRequested)
                {
                    var ready = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var started = Stopwatch.GetTimestamp();
                    if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal,
                        () => ready.TrySetResult(Stopwatch.GetElapsedTime(started).TotalMilliseconds))) break;
                    latencies.Add(await ready.Task.WaitAsync(TimeSpan.FromSeconds(3), probeCancellation.Token));
                    await Task.Delay(17, probeCancellation.Token);
                }
            }, probeCancellation.Token);
            try
            {
                for (var fragment = 0; fragment < 2048; fragment++)
                {
                    ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = fixture.SessionId,
                        messageId = fixture.MessageId, revision = fragment * 2 + 1, contentAppend = $" Schritt {fragment}.",
                        updatedAt = DateTimeOffset.UtcNow }));
                    ApplyEvent("status.changed", JsonSerializer.SerializeToElement(new { sessionId = fixture.SessionId,
                        messageId = fixture.MessageId, revision = fragment * 2 + 2, runStatus = "Modell generiert",
                        toolStepOrdinal = 2499, toolStepCount = 2500, updatedAt = DateTimeOffset.UtcNow,
                        toolStep = new { id = "long-step-2499", tool = "coding.command", status = "running",
                            detailsDeferred = true, contentOffset = fixture.Content.Length,
                            updatedAt = DateTimeOffset.UtcNow } }));
                    if (fragment % 32 == 31) await Task.Delay(20, _lifetime.Token);
                    if (fragment % 512 == 511) { await Task.Delay(100, _lifetime.Token); Measure("stream-" + (fragment + 1)); }
                }
                await Task.Delay(200, _lifetime.Token);
            }
            finally
            {
                await probeCancellation.CancelAsync();
                try { await probes; }
                catch (OperationCanceledException) { }
            }
            RenderMessagesNow();
            await WaitForPagingSmokeAsync(() => !_virtualMessages[fixture.MessageId].Preparing, "Die letzte Langsitzungs-Projektion wurde nicht übernommen.");
            if (!MessageContent(_messages[fixture.MessageId]).EndsWith(" Schritt 2047.", StringComparison.Ordinal)
                || MessageSteps(_messages[fixture.MessageId]).Length != 64 || _virtualMessages[fixture.MessageId].Panel.RealizedCount > 256)
                throw new InvalidOperationException("Deltas fehlen oder die Zahl nativer Werkzeugkontrollen wächst mit dem Lauf.");
            await VerifyOriginalLatexSelectionAsync(fixture.MessageId);
            Measure("scrolled");

            // Repeated real host opens release old controls without touching
            // stored messages or constructing another model prompt/KV cache.
            for (var series = 1; series <= 2; series++)
            for (var cycle = 1; cycle <= 8; cycle++)
            {
                await OpenAndMeasureAsync(originalSession);
                await OpenAndMeasureAsync(fixture.SessionId);
                await MeasureRetainedAsync($"navigation-{series}-{cycle}");
            }
            await Task.Delay(200, _lifetime.Token);
            await MeasureRetainedAsync("reopened");
            var durablePage = await snapshots.GetPageAsync(fixture.SessionId, cancellationToken: _lifetime.Token);
            if (durablePage!.Conversation.Messages.Single(message => message.Id.ToString() == fixture.MessageId).Content != fixture.Content
                || durablePage.ToolStepWindows![Guid.Parse(fixture.MessageId)].TotalSteps != 2500 || _uiCallbackFaultCount != faults)
                throw new InvalidOperationException("Reine Darstellungsvorgänge haben den gespeicherten Verlauf verändert oder UI-Fehler ausgelöst.");
            latencies.Sort();
            if (latencies.Count == 0) throw new InvalidOperationException("Der Langsitzungs-Smoke hat keine Dispatcher-Eingaben gemessen.");
            var p95 = latencies[(int)Math.Ceiling(latencies.Count * .95) - 1];
            var p99 = latencies[(int)Math.Ceiling(latencies.Count * .99) - 1];
            var coldGrowth = measurements[^1].ManagedBytes - measurements[0].ManagedBytes;
            var stabilizedGrowth = measurements[^1].ManagedBytes - measurements.Single(value => value.Phase == "warm").ManagedBytes;
            var finalCycles = measurements.Where(value => value.Phase.StartsWith("navigation-2-", StringComparison.Ordinal)).TakeLast(4).ToArray();
            var plateauRange = finalCycles.Max(value => value.ManagedBytes) - finalCycles.Min(value => value.ManagedBytes);
            var orderedOpenTimes = warmSessionOpens.Order().ToArray();
            var openP95 = orderedOpenTimes[(int)Math.Ceiling(orderedOpenTimes.Length * .95) - 1];
            await File.WriteAllTextAsync(Path.Combine(directory, "native-long-session-validation.json"), JsonSerializer.Serialize(new
            {
                passed = p95 <= 100 && p99 <= 250 && latencies[^1] <= 500 && openP95 < 1000 && stabilizedGrowth <= 32L * 1024 * 1024
                    && plateauRange <= 4L * 1024 * 1024,
                processId = Environment.ProcessId, fixture.DurableToolSteps, fixture.DurableOutputBytes,
                fixture.ReasoningCharacters, expandedLogicalBlocks, expandedRealizedBlocks,
                expandedReasoningBounded = true, initialToolHeaders = 64, eventCount = 4096, sessionOpenOperations = warmSessionOpens.Count, inputProbes = latencies.Count,
                dispatcherP95Milliseconds = p95, dispatcherP99Milliseconds = p99, dispatcherMaxMilliseconds = latencies[^1],
                warmSessionOpenMilliseconds = warmSessionOpens, warmSessionOpenP95Milliseconds = openP95,
                managedGrowthBytes = stabilizedGrowth, coldGrowthBytes = coldGrowth, finalFourCyclePlateauRangeBytes = plateauRange,
                navigationSeries = 2, cyclesPerSeries = 8, measurements = measurements.Select(value => new { value.Phase,
                    value.ManagedBytes, value.WorkingSet, value.RealizedBlocks, value.LogicalBlocks }),
                exactLatexSelectionPreserved = true, durableSourceUnchanged = true, toolPayloadsDeferred = true,
                inactiveProjectionBytes = _inactiveProjectionBytes, nativeInactiveBudgetBytes = InactiveProjectionBudget,
                publicationViewerCount = _publicationPool.Count, uiFaults = _uiCallbackFaultCount - faults,
                actualOvernightRun = false, modelRequests = 0, kvCacheModified = false,
            }, JsonOptions), _lifetime.Token);
            if (p95 > 100 || p99 > 250 || latencies[^1] > 500 || openP95 >= 1000 || stabilizedGrowth > 32L * 1024 * 1024
                || plateauRange > 4L * 1024 * 1024)
                throw new InvalidOperationException($"Langsitzungs-Anzeige überschreitet die Eingabe-/Cachegrenzen: p95 {p95:F1} ms, p99 {p99:F1} ms, Maximum {latencies[^1]:F1} ms, Sitzungswechsel p95 {openP95:F1} ms, verwaltetes Wachstum {stabilizedGrowth:N0} Bytes, letzte vier Wechsel {plateauRange:N0} Bytes.");
        }
        finally
        {
            _conversationSelection?.Clear();
            await CommandAsync("session.open", new { sessionId = originalSession });
            if (fixtureSession != Guid.Empty) await chats.DeleteSessionAsync(fixtureSession, _lifetime.Token);
            ApplyEvent("state.snapshot", original); RenderMessagesNow();
            _selectionInputSmokeActive = previousFixture;
        }

        async Task OpenAndMeasureAsync(Guid session)
        {
            var watch = Stopwatch.StartNew();
            if (!await CommandAsync("session.open", new { sessionId = session }))
                throw new InvalidOperationException("Der Warmstart der isolierten Sitzung ist fehlgeschlagen.");
            ShowChatView();
            RenderMessagesNow();
            await WaitForPagingSmokeAsync(() => _virtualMessages.All(pair => !DisplayMessages.TryGetValue(pair.Key, out var message)
                || !pair.Value.Preparing && pair.Value.Version == MessageVersion(message)
                    && pair.Value.Panel.LogicalCount > 0 && pair.Value.Panel.IsLoaded && pair.Value.Panel.ActualHeight > 0),
                "Die Sitzung konnte nach dem Wechsel nicht nachgeladen werden.");
            await WaitForNativeMathPreparationAsync(LayoutRoot);
            warmSessionOpens.Add(watch.Elapsed.TotalMilliseconds);
        }

        async Task<(int Logical, int Realized)> VerifyExpandedReasoningAsync(string messageId)
        {
            if (!_messageBlocks[messageId].TryGetValue("tool:long-step-2498", out var reasoningStep)
                || reasoningStep is not ToolStepView expandedReasoning)
                throw new InvalidOperationException("Der gespeicherte große Denkprozess ist im letzten Werkzeugfenster nicht verfügbar.");
            _scrollNewSessionToLatest = false;
            expandedReasoning.SetExpanded(true);
            await WaitForPagingSmokeAsync(() => Descendants(expandedReasoning).OfType<NativeVirtualizingTextView>()
                .Any(view => !view.IsPreparing && view.LogicalCount >= 5000 && view.RealizedCount <= 256),
                "Der ausdrücklich ausgeklappte große Denkprozess besitzt kein begrenztes Blockfenster.");
            var expanded = Descendants(expandedReasoning).OfType<NativeVirtualizingTextView>().Single();
            var counts = (expanded.LogicalCount, expanded.RealizedCount);
            expandedReasoning.SetExpanded(false);
            return counts;
        }

        async Task VerifyOriginalLatexSelectionAsync(string messageId)
        {
            _scrollNewSessionToLatest = false;
            ConversationScroll.ChangeView(null, ConversationScroll.ScrollableHeight / 2, null, true);
            RefreshVirtualConversationViewport();
            await Task.Delay(200, _lifetime.Token);
            await WaitForNativeMathPreparationAsync(LayoutRoot);
            var panel = _virtualMessages[messageId].Panel;
            var formula = Descendants(panel).OfType<NativeFormulaView>().FirstOrDefault()
                ?? throw new InvalidOperationException("Der nachgeladene Langsitzungs-Abschnitt enthält keine native Formel.");
            var textOwner = Descendants(panel).OfType<RichTextBlock>()
                .FirstOrDefault(text => text.Blocks.OfType<Paragraph>().Any(paragraph =>
                    paragraph.Inlines.OfType<InlineUIContainer>().Any(container => ReferenceEquals(container.Child, formula))))
                ?? throw new InvalidOperationException("Die Inlineformel besitzt keinen auswählbaren nativen Textbereich.");
            _conversationSelection!.SelectForSmoke(textOwner, textOwner.ContentStart.Offset, textOwner, textOwner.ContentEnd.Offset);
            var selected = _conversationSelection.SelectedText;
            ConversationScroll.ChangeView(null, ConversationScroll.VerticalOffset + 100, null, true);
            RefreshVirtualConversationViewport();
            await Task.Delay(60, _lifetime.Token);
            if (!selected.Contains(formula.Source, StringComparison.Ordinal) || _conversationSelection.SelectedText != selected)
                throw new InvalidOperationException("Recycling oder Scrollen verändert den ursprünglichen ausgewählten LaTeX-Ausdruck.");
            _conversationSelection.Clear();
        }

        async Task MeasureRetainedAsync(string phase)
        {
            // WinRT visual wrappers can release through finalizers and then the
            // dispatcher/compositor. Pump UI while finalizers finish; never
            // block the dispatcher with WaitForPendingFinalizers itself.
            await WaitForNativePreviewFrameAsync();
            await Task.Run(() => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); });
            await WaitForNativePreviewFrameAsync();
            Measure(phase);
        }

        void Measure(string phase)
        {
            var managed = GC.GetTotalMemory(forceFullCollection: false);
            using var process = Process.GetCurrentProcess();
            var state = _virtualMessages.Values.FirstOrDefault();
            measurements.Add((phase, managed, process.WorkingSet64, state?.Panel.RealizedCount ?? 0, state?.Panel.LogicalCount ?? 0));
        }
    }
}
