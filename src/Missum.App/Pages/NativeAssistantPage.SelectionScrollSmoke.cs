using System.Text.Json;
using Missum.App.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Windows.Foundation;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifySelectionScrollSmokeAsync(JsonElement original)
    {
        if (!IsMessageFooterSmoke || _conversationSelection is null)
            throw new InvalidOperationException("Scroll-Auswahlprüfung ist nur im isolierten WinUI-Smoke erlaubt.");
        var selection = _conversationSelection;
        var owner = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        var paragraphs = Enumerable.Range(0, 36).Select(index => $"Absatz {index}: Ein physikalisches Beispiel erklärt den Zusammenhang zwischen Messung, Modell und Ergebnis. Weitere Einzelheiten folgen im nächsten Absatz.").ToArray();
        var snapshot = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
        snapshot["activeSessionId"] = JsonSerializer.SerializeToElement(owner);
        snapshot["chatMode"] = JsonSerializer.SerializeToElement("general");
        snapshot["isRunning"] = JsonSerializer.SerializeToElement(true);
        snapshot["activeRunSessionId"] = JsonSerializer.SerializeToElement(owner);
        snapshot["activeRunId"] = JsonSerializer.SerializeToElement("run-selection-scroll-smoke");
        snapshot["runMessageId"] = JsonSerializer.SerializeToElement(messageId);
        snapshot["messages"] = JsonSerializer.SerializeToElement(new[] { new
        {
            id = messageId, sessionId = owner, role = "assistant", content = string.Join("\n\n", paragraphs),
            status = "streaming", createdAt = at, updatedAt = at,
        } });
        var previousCopy = selection.CopySmokeAdapter;
        var copies = new List<string>();
        var pointerTrace = new List<object>();
        selection.PointerSmokeObserver = (kind, source, point) => pointerTrace.Add(new { kind, source, point.X, point.Y });
        selection.CopySmokeAdapter = text => { copies.Add(text); return Task.FromResult(true); };
        var scrollOffsets = new List<double>();
        void RecordScroll(object? sender, ScrollViewerViewChangedEventArgs args) => scrollOffsets.Add(ConversationScroll.VerticalOffset);
        async Task AtOffset(double offset)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                ConversationScroll.ChangeView(null, offset, null, true);
                await Task.Delay(20, _lifetime.Token); UpdateLayout();
                if (Math.Abs(ConversationScroll.VerticalOffset - offset) <= 1)
                {
                    await Task.Delay(40, _lifetime.Token);
                    if (Math.Abs(ConversationScroll.VerticalOffset - offset) <= 1) return;
                }
            }
            throw new InvalidOperationException("Die Scroll-Testposition wurde nicht erreicht: " + JsonSerializer.Serialize(new { wanted = offset, actual = ConversationScroll.VerticalOffset, currentSession = _session, expectedSession = owner }));
        }
        static Point CharacterPoint(TextBlock text, int offset)
        {
            var run = text.Inlines.OfType<Run>().First();
            var rect = run.ContentStart.GetPositionAtOffset(offset, LogicalDirection.Forward)!.GetCharacterRect(LogicalDirection.Forward);
            return new Point(rect.Left + .2, rect.Top + rect.Height / 2);
        }
        async Task WaitForPartialSelection(string expected)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (selection.IsDragging && selection.SelectedText == expected) return;
                await Task.Delay(20, _lifetime.Token);
            }
            throw new InvalidOperationException("Die reale Teilmarkierung wurde nicht angewendet: " + JsonSerializer.Serialize(new { expected, actual = selection.SelectedText, selection.IsDragging, pointerTrace }));
        }
        try
        {
            _selectionInputSmokeActive = true;
            using var input = new NativeSelectionInputSmoke();
            await input.EnsureForegroundAsync();
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(snapshot)); RenderMessagesNow(); UpdateLayout();
            await AtOffset(0);
            var bubble = _messageViews[messageId.ToString()].View;
            var blocks = MessageBody(bubble).Children.OfType<NativeStreamingMarkdown>().SelectMany(markdown => markdown.Children.OfType<TextBlock>()).ToArray();
            if (blocks.Length != paragraphs.Length || ConversationScroll.ScrollableHeight < ConversationScroll.ViewportHeight * 2)
                throw new InvalidOperationException("Die echte Scroll-Auswahlprüfung besitzt keine ausreichend lange Unterhaltung.");
            var start = paragraphs[0].IndexOf("physikalisches", StringComparison.Ordinal) + 3;
            var expected = paragraphs[0].Substring(start, 4);
            await input.MoveToAsync(blocks[0], CharacterPoint(blocks[0], start)); await Task.Delay(40, _lifetime.Token);
            await input.LeftDownAsync(); await Task.Delay(40, _lifetime.Token);
            await input.MoveToAsync(blocks[0], CharacterPoint(blocks[0], start + 4)); await WaitForPartialSelection(expected);
            await input.LeftUpAsync(); await Task.Delay(60, _lifetime.Token);
            if (selection.SelectedText != expected || selection.IsDragging)
            {
                await SaveMathPreviewAsync(LayoutRoot, "native-selection-input-layout-debug.png");
                var point = CharacterPoint(blocks[0], start);
                var origin = blocks[0].TransformToVisual(App.Current.MainWindow!.Content as UIElement).TransformPoint(point);
                throw new InvalidOperationException("Echtes Mausziehen markiert nicht genau vier Zeichen innerhalb eines Wortes: " + JsonSerializer.Serialize(new
                {
                    selected = selection.SelectedText, nativeSelected = blocks[0].SelectedText, selection.IsDragging,
                    pointerTrace, point, origin, scroll = ConversationScroll.VerticalOffset,
                    blocks[0].ActualWidth, blocks[0].ActualHeight, BodyGrid.Visibility, pageVisible = IsLoaded,
                    rootVisible = XamlRoot.IsHostVisible, scale = XamlRoot.RasterizationScale, uiFaults = _uiCallbackFaultCount,
                }));
            }
            for (var index = 0; index < 6; index++)
            {
                await input.WheelAsync(index < 3 ? -120 : 120); await Task.Delay(180, _lifetime.Token);
                if (selection.SelectedText != expected || selection.IsDragging)
                    throw new InvalidOperationException("Eine losgelassene Textmarkierung verändert sich durch echtes Mausrad-Scrollen.");
            }
            var thumb = Descendants(OuterChatScrollBar).OfType<Thumb>().FirstOrDefault(item => item.ActualHeight > 0 && item.ActualWidth > 0);
            if (thumb is null) throw new InvalidOperationException("Der echte Chat-Scrollbar besitzt keinen sichtbaren Scrollgriff.");
            await input.MoveToAsync(thumb, new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2)); await Task.Delay(40, _lifetime.Token);
            await input.LeftDownAsync(); await Task.Delay(40, _lifetime.Token);
            await input.MoveToAsync(OuterChatScrollBar, new Point(OuterChatScrollBar.ActualWidth / 2, OuterChatScrollBar.ActualHeight * .65)); await Task.Delay(60, _lifetime.Token);
            await input.LeftUpAsync(); await Task.Delay(120, _lifetime.Token);
            if (selection.SelectedText != expected || selection.IsDragging || !await selection.CopySelectionAsync() || copies.Last() != expected)
                throw new InvalidOperationException("Scrollbar-Ziehen verschiebt die Textmarkierung oder deren Kopiertext.");

            selection.Clear(); await AtOffset(0); await Task.Delay(550, _lifetime.Token);
            await input.MoveToAsync(blocks[0], CharacterPoint(blocks[0], start)); await Task.Delay(40, _lifetime.Token);
            await input.LeftDownAsync(); await Task.Delay(40, _lifetime.Token);
            await input.MoveToAsync(blocks[0], CharacterPoint(blocks[0], start + 4)); await WaitForPartialSelection(expected);
            await input.WheelAsync(-120); await Task.Delay(240, _lifetime.Token);
            if (selection.SelectedText != expected || !selection.IsDragging)
                throw new InvalidOperationException("Die gehaltene Einblock-Teilmarkierung springt beim Mausrad-Scrollen: " + JsonSerializer.Serialize(new { selected = selection.SelectedText, nativeSelected = blocks[0].SelectedText, selection.IsDragging, selection.IsCrossBlockDragging, pointerTrace, uiFaults = _uiCallbackFaultCount }));
            await input.LeftUpAsync(); await Task.Delay(60, _lifetime.Token);
            if (selection.SelectedText != expected || selection.IsDragging)
                throw new InvalidOperationException("Loslassen nach Einblock-Scrollen verändert die Teilmarkierung.");

            selection.Clear(); await AtOffset(0);
            await input.MoveToAsync(blocks[0], CharacterPoint(blocks[0], start)); await Task.Delay(40, _lifetime.Token);
            await input.LeftDownAsync(); await Task.Delay(40, _lifetime.Token);
            await input.MoveToAsync(blocks[1], CharacterPoint(blocks[1], 8)); await Task.Delay(60, _lifetime.Token);
            if (!selection.IsCrossBlockDragging || !selection.SelectedText.StartsWith(expected, StringComparison.Ordinal))
                throw new InvalidOperationException("Die echte Absatzgrenzen-Auswahl hat ihre Maus-Capture oder den Zeichenanker verloren.");
            var range = selection.SelectedText;
            var projections = selection.RangeProjectionCount;
            var beforeWheel = ConversationScroll.VerticalOffset;
            await input.WheelAsync(-120); await Task.Delay(240, _lifetime.Token);
            if (ConversationScroll.VerticalOffset <= beforeWheel || selection.SelectedText != range || selection.RangeProjectionCount != projections)
                throw new InvalidOperationException("Mausrad bei gehaltener Auswahl interpretiert den unbewegten Zeiger als neuen Textendpunkt: " + JsonSerializer.Serialize(new
                {
                    beforeWheel, afterWheel = ConversationScroll.VerticalOffset, expected = range, actual = selection.SelectedText,
                    projections, actualProjections = selection.RangeProjectionCount, selection.IsDragging, selection.IsCrossBlockDragging,
                    pointerTrace, uiFaults = _uiCallbackFaultCount,
                }));
            await input.MoveToAsync(blocks[3], CharacterPoint(blocks[3], 8)); await Task.Delay(80, _lifetime.Token);
            if (!selection.SelectedText.StartsWith(expected, StringComparison.Ordinal)
                || !selection.SelectedText.EndsWith(paragraphs[3][..8], StringComparison.Ordinal)
                || selection.SelectedText == range || selection.RangeProjectionCount <= projections)
                throw new InvalidOperationException("Echtes Weiterziehen nach dem Mausrad trifft nicht das sichtbare Zeichen am neuen Scrollstand.");
            range = selection.SelectedText;
            await input.LeftUpAsync(); await Task.Delay(80, _lifetime.Token);
            if (selection.SelectedText != range || selection.IsDragging)
                throw new InvalidOperationException("Loslassen nach Mausrad-Scrollen verändert den zuletzt gezogenen Zeichenbereich.");

            selection.Clear(); await AtOffset(0);
            await input.MoveToAsync(blocks[0], CharacterPoint(blocks[0], start)); await Task.Delay(40, _lifetime.Token);
            await input.LeftDownAsync(); await Task.Delay(40, _lifetime.Token);
            await input.MoveToAsync(blocks[1], CharacterPoint(blocks[1], 8)); await Task.Delay(50, _lifetime.Token);
            scrollOffsets.Clear();
            ConversationScroll.ViewChanged += RecordScroll;
            var beforeAuto = ConversationScroll.VerticalOffset;
            await input.MoveToAsync(ConversationScroll, new Point(ConversationScroll.ActualWidth / 2, ConversationScroll.ActualHeight - 8));
            await Task.Delay(550, _lifetime.Token);
            if (!selection.IsCrossBlockDragging || ConversationScroll.VerticalOffset <= beforeAuto + 20
                || !selection.SelectedText.StartsWith(expected, StringComparison.Ordinal)
                || scrollOffsets.Zip(scrollOffsets.Skip(1)).Any(pair => pair.Second < pair.First - 1 || pair.Second - pair.First > 72))
                throw new InvalidOperationException("Kanten-Autoscroll springt zum Auswahlanker zurück oder verschiebt den Zeichenanker.");
            ConversationScroll.ViewChanged -= RecordScroll;
            var beforeCaptureLost = selection.SelectedText;
            ConversationContent.ReleasePointerCaptures(); await Task.Delay(60, _lifetime.Token);
            if (selection.IsDragging || selection.SelectedText != beforeCaptureLost)
                throw new InvalidOperationException("Verlorene Maus-Capture beendet den Auswahl-Timer nicht verlustfrei.");
            await input.LeftUpAsync(); await Task.Delay(60, _lifetime.Token);
            await input.WheelAsync(120); await Task.Delay(180, _lifetime.Token);
            if (selection.IsDragging || selection.SelectedText != beforeCaptureLost || !await selection.CopySelectionAsync() || copies.Last() != beforeCaptureLost)
                throw new InvalidOperationException("Scrollen nach Captureverlust verändert den abgeschlossenen Kopierbereich.");
            await SaveMathPreviewAsync(ConversationScroll, "native-selection-scroll-preview.png");
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-selection-scroll-validation.json"), JsonSerializer.Serialize(new
            {
                passed = true, processId = Environment.ProcessId, realMouseInput = true, realWheelInput = true,
                partialWordDrag = true, releasedWheelSelectionStable = true, scrollbarSelectionStable = true,
                heldWheelSelectionStable = true, heldSingleBlockWheelStable = true, dragAfterWheelHitsVisibleCharacter = true,
                unchangedEndpointRetainsProjection = true, releaseAfterWheelStable = true,
                crossBlockAnchorStable = true, edgeAutoScrollMonotonic = true, captureLossStopsGesture = true,
                copyAfterScrollingExact = true, modelUnchanged = _running, input.Moves, input.Wheels,
                autoScrollSteps = scrollOffsets.Count, clipboardModified = false,
            }, JsonOptions), _lifetime.Token);
        }
        finally
        {
            ConversationScroll.ViewChanged -= RecordScroll;
            selection.CopySmokeAdapter = previousCopy; selection.Clear();
            selection.PointerSmokeObserver = null;
            _selectionInputSmokeActive = false;
            ApplyEvent("state.snapshot", original); RenderMessagesNow(); UpdateLayout();
        }
    }
}
