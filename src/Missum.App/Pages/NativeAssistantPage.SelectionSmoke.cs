using System.Runtime.InteropServices;
using System.Text.Json;
using Missum.App.Controls;
using Missum.App.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifySelectionClipboardSmokeAsync(JsonElement original)
    {
        if (!IsMessageFooterSmoke || _conversationSelection is null)
            throw new InvalidOperationException("Textauswahl-Smoke erfordert das isolierte WinUI-Profil.");
        var selection = _conversationSelection;
        var owner = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var id = messageId.ToString();
        const string initial = "Die Antwort erklärt detailliert ein physikalisches Beispiel.";
        var at = DateTimeOffset.UtcNow;
        var snapshot = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
        snapshot["activeSessionId"] = JsonSerializer.SerializeToElement(owner);
        snapshot["chatMode"] = JsonSerializer.SerializeToElement("general");
        snapshot["isRunning"] = JsonSerializer.SerializeToElement(true);
        snapshot["activeRunSessionId"] = JsonSerializer.SerializeToElement(owner);
        snapshot["activeRunId"] = JsonSerializer.SerializeToElement("run-selection-smoke");
        snapshot["runMessageId"] = JsonSerializer.SerializeToElement(messageId);
        snapshot["messages"] = JsonSerializer.SerializeToElement(new[] { new
        {
            id = messageId, sessionId = owner, role = "assistant", content = initial, status = "streaming", createdAt = at, updatedAt = at,
        } });
        var previousCopy = selection.CopySmokeAdapter;
        var previousFailure = selection.CopyFailed;
        var failures = new List<string>();
        try
        {
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(snapshot)); RenderMessagesNow(); UpdateLayout();
            var bubble = _messageViews[id].View;
            var markdown = MessageBody(bubble).Children.OfType<NativeStreamingMarkdown>().First();
            var paragraph = markdown.Children.OfType<TextBlock>().Single();
            var run = paragraph.Inlines.OfType<Run>().Single();
            var start = initial.IndexOf("physikalisches", StringComparison.Ordinal) + 3;
            const int length = 4;
            var expected = initial.Substring(start, length);
            paragraph.Select(run.ContentStart.GetPositionAtOffset(start, LogicalDirection.Forward),
                run.ContentStart.GetPositionAtOffset(start + length, LogicalDirection.Forward));
            if (selection.SelectedText != expected || !selection.HasSelection)
                throw new InvalidOperationException("Eine Auswahl innerhalb eines Wortes wurde auf den Satz ausgeweitet.");
            var nativeStart = paragraph.SelectionStart.Offset;
            var nativeEnd = paragraph.SelectionEnd.Offset;
            if (NativeConversationSelection.ReadableSuffix([markdown], paragraph) != initial
                || paragraph.SelectionStart.Offset != nativeStart || paragraph.SelectionEnd.Offset != nativeEnd || paragraph.SelectedText != expected)
                throw new InvalidOperationException("Der Vorlesemenüaufbau verändert die markierte Textspanne.");

            var offset = ConversationScroll.VerticalOffset;
            var latest = initial;
            for (var index = 0; index < 100; index++)
            {
                latest = initial + " Weitere Erkenntnisse: " + index;
                if (index == 99) latest += "\n\nEin **neuer** Absatz mit $x^2$.";
                ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = owner, messageId, content = latest }));
                RenderMessagesNow(); RefreshThinkingIndicators(); UpdateLayout();
                if (index % 10 == 0) await Task.Delay(20, _lifetime.Token);
                if (!ReferenceEquals(bubble, _messageViews[id].View) || !ReferenceEquals(paragraph, markdown.Children[0])
                    || run.Text != initial || paragraph.SelectedText != expected || selection.SelectedText != expected
                    || Math.Abs(ConversationScroll.VerticalOffset - offset) > 1 || !_running || MessageContent(_messages[id]) != latest
                    || _messageActionViews[id].Text != latest)
                    throw new InvalidOperationException("Streaming verschiebt die Markierung oder hält den AI-Lauf statt nur dessen Textprojektion an.");
            }
            await SaveMathPreviewAsync(bubble, "native-selection-streaming-preview.png");

            var attempts = 0;
            var copied = new List<string>();
            var uiThread = Environment.CurrentManagedThreadId;
            var stayedOnUiThread = true;
#pragma warning disable CA2201 // Isolated smoke deliberately injects the exact clipboard HRESULT from the production crash.
            var writer = new ClipboardTextWriter(text =>
            {
                stayedOnUiThread &= Environment.CurrentManagedThreadId == uiThread;
                if (++attempts <= 2) throw new COMException("Zwischenablage gesperrt", unchecked((int)0x800401D0));
                copied.Add(text);
            });
            selection.CopySmokeAdapter = text => writer.WriteTextAsync(text);
            if (!await selection.CopySelectionAsync() || attempts != 3 || copied.SingleOrDefault() != expected || !stayedOnUiThread)
                throw new InvalidOperationException("Kopieren hat den markierten Teiltext bei temporärer Zwischenablagesperre verloren.");
            selection.CopyFailed = failures.Add;
            var locked = new ClipboardTextWriter(_ => throw new COMException("gesperrt", unchecked((int)0x800401D0)), (_, _) => Task.CompletedTask);
#pragma warning restore CA2201
            selection.CopySmokeAdapter = text => locked.WriteTextAsync(text);
            if (await selection.CopySelectionAsync() || failures.Count != 1 || selection.SelectedText != expected || !_running)
                throw new InvalidOperationException("Eine dauerhaft gesperrte Zwischenablage beendet die Auswahl oder den AI-Lauf.");

            selection.Clear();
            await Task.Delay(100, _lifetime.Token); RenderMessagesNow(); UpdateLayout();
            await WaitForNativeMathPreparationAsync(bubble);
            var displayed = MessageBody(bubble).Children.OfType<NativeStreamingMarkdown>().First();
            var formulas = Descendants(bubble).OfType<NativeFormulaView>().ToArray();
            if (selection.HasSelection || displayed.Source != latest
                || _renderedMessageVersions.GetValueOrDefault(id) != MessageVersion(_messages[id])
                || formulas.Length != 1 || formulas.Any(formula => !formula.IsTypeset) || !_running)
                throw new InvalidOperationException("Nach Aufhebung der Auswahl wird der neueste Antwortstand nicht einschließlich des Parserwechsels gerendert: "
                    + JsonSerializer.Serialize(new { selection.HasSelection, sourceCurrent = displayed.Source == latest,
                        renderedVersion = _renderedMessageVersions.GetValueOrDefault(id), sourceVersion = MessageVersion(_messages[id]),
                        formulas = formulas.Length, allTypeset = formulas.All(formula => formula.IsTypeset), _running }));
            var currentParagraph = MessageBody(bubble).Children.OfType<NativeStreamingMarkdown>().First().Children.OfType<TextBlock>().First();
            var currentRun = currentParagraph.Inlines.OfType<Run>().First();
            currentParagraph.Select(currentRun.ContentStart.GetPositionAtOffset(4, LogicalDirection.Forward),
                currentRun.ContentStart.GetPositionAtOffset(11, LogicalDirection.Forward));
            var nextExpected = currentParagraph.SelectedText;
            selection.CopySmokeAdapter = text => { copied.Add(text); return Task.FromResult(true); };
            if (nextExpected.Length != 7 || !await selection.CopySelectionAsync() || copied.Last() != nextExpected || nextExpected == expected)
                throw new InvalidOperationException("Eine neue Teilmarkierung kopiert weiterhin den alten Textbereich.");
            Composer.Text = copied.Last();
            if (Composer.Text != nextExpected) throw new InvalidOperationException("Der kopierte Teiltext kann nicht verlustfrei im Promptfeld eingefügt werden.");
            OnComposerGotFocus(Composer, new Microsoft.UI.Xaml.RoutedEventArgs());
            if (selection.HasSelection) throw new InvalidOperationException("Eine alte Chatauswahl bleibt beim Wechsel in das Eingabefeld aktiv.");

            await VerifyFormulaClipboardRangesSmokeAsync(selection);

            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-selection-clipboard-validation.json"), JsonSerializer.Serialize(new
            {
                passed = true, processId = Environment.ProcessId, partialWordSelection = true,
                readMenuPreservesSelection = true, streamingUpdates = 100, selectedRunsRetained = true,
                selectionAndScrollStable = true, modelContinues = true, footerSnapshotCurrent = true,
                busyClipboardRecovered = true, retryAttempts = attempts, uiThreadRetained = stayedOnUiThread,
                permanentLockHandled = true, selectionPreservedAfterCopyFailure = true,
                latestSnapshotRenderedAfterClear = true, newSelectionCopiesCurrentRange = true,
                pasteTextPreserved = true, composerClearsChatSelection = true, clipboardModified = false,
                inlineLatexPreserved = true, formulaOnlySelection = true, blockLatexPreserved = true,
                reverseLatexSelection = true, unloadedSelectionPointersSafe = true,
            }, JsonOptions), _lifetime.Token);
        }
        finally
        {
            selection.CopySmokeAdapter = previousCopy; selection.CopyFailed = previousFailure;
            selection.Clear(); ApplyEvent("state.snapshot", original); RenderMessagesNow(); UpdateLayout();
        }
    }

    private async Task VerifyFormulaClipboardRangesSmokeAsync(NativeConversationSelection selection)
    {
        const string inlineSource = @"Davor $\frac{x}{y}=z^2$ danach.";
        const string blockSource = @"$$\int_0^1 x^2\,dx=\frac{1}{3}$$";
        var detached = new RichTextBlock { IsTextSelectionEnabled = true };
        detached.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "Noch nicht geladen" } } });
        if (selection.PreserveSelectionWithin(detached))
            throw new InvalidOperationException("Ein ungeladener Textblock besitzt keine Textauswahl.");

        var fixture = new StackPanel { Spacing = 12 };
        var beforeRun = new Run { Text = "Anfang" };
        var before = new TextBlock { IsTextSelectionEnabled = true, Inlines = { beforeRun } };
        var paragraph = new NativeMathParagraph();
        paragraph.UpdateText(inlineSource);
        var block = new NativeFormulaView(blockSource, true);
        var afterRun = new Run { Text = "Ende" };
        var after = new TextBlock { IsTextSelectionEnabled = true, Inlines = { afterRun } };
        var boldRun = new Run { Text = "Alpha" };
        var linkRun = new Run { Text = "Omega" };
        var formatted = new RichTextBlock { IsTextSelectionEnabled = true };
        formatted.Blocks.Add(new Paragraph { Inlines =
        {
            new Bold { Inlines = { boldRun } }, new Run { Text = " " },
            new InlineUIContainer { Child = new NativeFormulaView("$x$", false) }, new Run { Text = " " },
            new Hyperlink { NavigateUri = new Uri("https://example.org/"), Inlines = { linkRun } },
        } });
        fixture.Children.Add(before); fixture.Children.Add(paragraph); fixture.Children.Add(block); fixture.Children.Add(after);
        fixture.Children.Add(formatted);
        MessagesPanel.Children.Add(fixture);
        fixture.UpdateLayout();
        var previousCopy = selection.CopySmokeAdapter;
        try
        {
            var rich = Descendants(paragraph).OfType<RichTextBlock>().Single();
            var container = paragraph.TrailingInlines.OfType<InlineUIContainer>().Single();
            var formula = (NativeFormulaView)container.Child;
            rich.Select(rich.ContentStart, rich.ContentEnd);
            var start = rich.SelectionStart.Offset;
            var end = rich.SelectionEnd.Offset;
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-formula-selection-offsets.json"), JsonSerializer.Serialize(new
            {
                processId = Environment.ProcessId, start, end, nativeText = rich.SelectedText, copyText = selection.SelectedText,
                formulaElementStart = container.ElementStart.Offset, formulaContentStart = container.ContentStart.Offset,
                formulaContentEnd = container.ContentEnd.Offset, formulaElementEnd = container.ElementEnd.Offset,
            }, JsonOptions), _lifetime.Token);
            if (selection.SelectedText != inlineSource || rich.SelectionStart.Offset != start || rich.SelectionEnd.Offset != end)
                throw new InvalidOperationException("Inline-LaTeX fehlt beim Kopieren oder die Auswahl wurde verschoben: " + selection.SelectedText);
            rich.Select(container.ElementStart, container.ElementEnd);
            if (selection.SelectedText != formula.Source)
                throw new InvalidOperationException("Eine ausschließlich markierte Formel muss ihren LaTeX-Quelltext liefern: " + selection.SelectedText);
            var trailing = paragraph.TrailingInlines.OfType<Run>().Last();
            rich.Select(trailing.ContentStart.GetPositionAtOffset(2, LogicalDirection.Forward),
                trailing.ContentStart.GetPositionAtOffset(5, LogicalDirection.Forward));
            if (selection.SelectedText != "ana")
                throw new InvalidOperationException("Eine Teilmarkierung nach der Formel benutzt falsche Zeichenabstände: " + selection.SelectedText);
            selection.Clear();

            formatted.Select(formatted.ContentStart, formatted.ContentEnd);
            if (selection.SelectedText != "Alpha $x$ Omega")
                throw new InvalidOperationException("Formatierte Textläufe oder Links verlieren Text beim Formelauswahl-Kopieren: " + selection.SelectedText);
            formatted.Select(linkRun.ContentStart.GetPositionAtOffset(1, LogicalDirection.Forward),
                linkRun.ContentStart.GetPositionAtOffset(4, LogicalDirection.Forward));
            if (selection.SelectedText != "meg")
                throw new InvalidOperationException("Teilmarkierung innerhalb eines Links verschiebt Zeichen: " + selection.SelectedText);
            formatted.Select(boldRun.ContentStart.GetPositionAtOffset(1, LogicalDirection.Forward),
                boldRun.ContentStart.GetPositionAtOffset(4, LogicalDirection.Forward));
            if (selection.SelectedText != "lph")
                throw new InvalidOperationException("Teilmarkierung innerhalb eines Fettdruck-Laufs verschiebt Zeichen: " + selection.SelectedText);
            selection.Clear();

            var expected = "fang\n\n" + inlineSource + "\n\n" + blockSource + "\n\nEn";
            selection.SelectForSmoke(before, beforeRun.ContentStart.Offset + 2, after, afterRun.ContentStart.Offset + 2);
            if (selection.SelectedText != expected)
                throw new InvalidOperationException("Absatzübergreifende Auswahl verliert eine Inline- oder Blockformel: " + selection.SelectedText);
            string? clipboardText = null;
            selection.CopySmokeAdapter = value => { clipboardText = value; return Task.FromResult(true); };
            if (!await selection.CopySelectionAsync() || clipboardText != expected)
                throw new InvalidOperationException("Der gemeinsame Kopierpfad verliert LaTeX aus der Auswahl.");
            selection.SelectForSmoke(after, afterRun.ContentStart.Offset + 2, before, beforeRun.ContentStart.Offset + 2);
            if (selection.SelectedText != expected)
                throw new InvalidOperationException("Rückwärtsauswahl verliert LaTeX oder verändert die Reihenfolge.");
        }
        finally
        {
            selection.Clear(); selection.CopySmokeAdapter = previousCopy;
            MessagesPanel.Children.Remove(fixture);
        }
    }
}
