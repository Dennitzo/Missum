using System.Text.Json;
using Missum.App.Controls;
using Microsoft.UI.Xaml.Automation;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private const string LooseMathSmokeSource = "## Temperatur, Leistung und Verdampfungszeit\n"
        + "Die Zusammenhänge werden mit unverändertem mathematischem Inhalt dargestellt.\n\n"
        + "T_ECT = T_HH/ln 2\n\n"
        + "P_ECT = A·T_ECT⁴ = A·(T_HH/ln 2)⁴ = P_std/(ln 2)⁴\n\n"
        + "dM/dt = -P/c²\n\n"
        + "t_ECT = M·c²/P_ECT = t_std/(0.2308)\n\n"
        + "Also t_ECT = t_std·(ln 2)⁴. Das ist korrekt: niedrigere Temperatur → geringere Leistung.\n\n"
        + @"Quelltext: `token_id=123`, Datei: T_ECT.py, Pfad: C:\research\T_ECT.py, URL: https://example.org/?T_ECT=1" + "\n\n"
        + "```python\ntoken_id=123\nT_ECT=1\n```\n\n"
        + @"Zum Vergleich bleibt die markierte Formel $T_{\mathrm{ECT}}=\frac{T_{\mathrm{HH}}}{\ln 2}$ ebenfalls im Text.";

    private static readonly string[] LooseMathSmokeFormulaFragments =
    ["T_ECT = T_HH/ln 2", "P_ECT = A·T_ECT⁴ = A·(T_HH/ln 2)⁴ = P_std/(ln 2)⁴", "dM/dt = -P/c²",
        "t_ECT = M·c²/P_ECT = t_std/(0.2308)", "t_ECT = t_std·(ln 2)⁴", @"$T_{\mathrm{ECT}}=\frac{T_{\mathrm{HH}}}{\ln 2}$"];
    private static readonly string[] LooseMathSmokeProtectedSources =
    ["`token_id=123`", "```python\ntoken_id=123\nT_ECT=1\n```", "T_ECT.py", @"C:\research\T_ECT.py", "https://example.org/?T_ECT=1", "a=b", "token_id=123"];

    private async Task VerifyLooseMathSmokeAsync(JsonElement original)
    {
        var owner = Guid.NewGuid(); var answerId = Guid.NewGuid().ToString(); var reasoningId = Guid.NewGuid().ToString();
        var at = DateTimeOffset.UtcNow;
        var snapshot = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
        snapshot["activeSessionId"] = JsonSerializer.SerializeToElement(owner);
        snapshot["chatMode"] = JsonSerializer.SerializeToElement("general");
        snapshot["isRunning"] = JsonSerializer.SerializeToElement(true);
        snapshot["runMessageId"] = JsonSerializer.SerializeToElement(answerId);
        snapshot["runStatus"] = JsonSerializer.SerializeToElement("Modell generiert");
        snapshot["generationState"] = JsonSerializer.SerializeToElement("tokenProgress");
        snapshot["generatedTokens"] = JsonSerializer.SerializeToElement(25);
        snapshot["contextUsed"] = JsonSerializer.SerializeToElement(1025);
        snapshot["generationUpdatedAt"] = JsonSerializer.SerializeToElement(at);
        snapshot["messages"] = JsonSerializer.SerializeToElement(new[]
        { new { id = answerId, sessionId = owner, role = "assistant", content = "", status = "streaming", createdAt = at, updatedAt = at } });
        try
        {
            _finishedSessions.Remove(owner);
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(snapshot));
            var streamedSources = new[]
            {
                LooseMathSmokeSource[..(LooseMathSmokeSource.IndexOf("T_ECT =", StringComparison.Ordinal) + "T_ECT =".Length)],
                LooseMathSmokeSource[..(LooseMathSmokeSource.IndexOf("P_ECT = A·T_ECT", StringComparison.Ordinal) + "P_ECT = A·T_ECT".Length)] + "^",
                LooseMathSmokeSource,
            };
            foreach (var streamedSource in streamedSources)
            {
                ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new
                { sessionId = owner, messageId = answerId, content = streamedSource }));
                // Exercise the same incremental render timer as a local-model delta.
                await Task.Delay(180);
                if (!_messageBlocks.TryGetValue(answerId, out var blocks) || !blocks.Values.OfType<NativeStreamingMarkdown>().Any())
                    throw new InvalidOperationException("Scientific math did not become visible before answer completion.");
                VerifyNoDanglingFormula(blocks.Values.OfType<NativeStreamingMarkdown>().Single());
                AssertChatCursorAbsent(MessageBody(_messageViews[answerId].View));
            }
            var answer = _messageBlocks[answerId].Values.OfType<NativeStreamingMarkdown>().Single();
            VerifyLooseMath(answer);
            var answerFormula = Descendants(answer).OfType<NativeFormulaView>().Last();
            ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new
            { sessionId = owner, messageId = answerId, content = LooseMathSmokeSource + " Die Herleitung wird fortgesetzt." }));
            await Task.Delay(180);
            if (!Descendants(answer).OfType<NativeFormulaView>().Any(formula => ReferenceEquals(formula, answerFormula)))
                throw new InvalidOperationException("Appending prose rebuilt an unchanged loose formula in the answer.");
            await SaveMathPreviewAsync(LayoutRoot, "native-loose-math-answer-preview.png");

            snapshot["runMessageId"] = JsonSerializer.SerializeToElement(reasoningId);
            snapshot["generationUpdatedAt"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow);
            snapshot["messages"] = JsonSerializer.SerializeToElement(new object[]
            {
                new { id = answerId, sessionId = owner, role = "assistant", content = LooseMathSmokeSource, status = "completed", createdAt = at, updatedAt = at },
                new { id = reasoningId, sessionId = owner, role = "assistant", content = "", status = "streaming", createdAt = at.AddSeconds(1), updatedAt = at,
                    toolSteps = new[] { new { id = "loose-math-reasoning", tool = "assistant.reasoning", status = "running", detail = "Ich prüfe die Gleichungen.", contentOffset = 0, updatedAt = at } } },
            });
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(snapshot)); RenderMessagesNow(); RefreshThinkingIndicators();
            var row = _thinkingIndicators[reasoningId]; row.SetExpanded(true);
            var reasoning = row.ReasoningView;
            foreach (var streamedSource in streamedSources)
            {
                ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new
                {
                    sessionId = owner, messageId = reasoningId, content = "", toolSteps = new[] { new
                    { id = "loose-math-reasoning", tool = "assistant.reasoning", status = "running", detail = streamedSource, contentOffset = 0, updatedAt = DateTimeOffset.UtcNow } },
                }));
                await Task.Delay(180); RefreshThinkingIndicators();
                if (!row.IsExpanded || !ReferenceEquals(reasoning, row.ReasoningView))
                    throw new InvalidOperationException("Loose math streaming rebuilt or collapsed the reasoning disclosure.");
                VerifyNoDanglingFormula(reasoning);
            }
            VerifyLooseMath(reasoning);
            var reasoningFormula = Descendants(reasoning).OfType<NativeFormulaView>().Last();
            ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new
            {
                sessionId = owner, messageId = reasoningId, content = "", toolSteps = new[] { new
                { id = "loose-math-reasoning", tool = "assistant.reasoning", status = "running", detail = LooseMathSmokeSource + " Die Herleitung wird fortgesetzt.", contentOffset = 0, updatedAt = DateTimeOffset.UtcNow } },
            }));
            await Task.Delay(180); RefreshThinkingIndicators();
            if (!Descendants(reasoning).OfType<NativeFormulaView>().Any(formula => ReferenceEquals(formula, reasoningFormula)))
                throw new InvalidOperationException("Appending prose rebuilt an unchanged loose formula in the reasoning disclosure.");
            AssertChatCursorAbsent(LayoutRoot);
            ConversationScroll.ChangeView(null, ConversationScroll.ScrollableHeight, null, true);
            await SaveMathPreviewAsync(LayoutRoot, "native-loose-math-reasoning-preview.png");
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-loose-math-validation.json"),
                JsonSerializer.Serialize(new { renderer = "WinUI3", passed = true, answerMath = true, reasoningMath = true,
                    visibleStreamingDeltas = streamedSources.Length, formulaControlRetained = true, reasoningDisclosureRetained = true, incompleteFormulaPreserved = true,
                    codePathsUrlsProtected = true, formulaFontMatchesText = true, chatCursorAbsent = true }));
        }
        finally { ApplyEvent("state.snapshot", original); RenderMessagesNow(); }

        static void VerifyLooseMath(NativeStreamingMarkdown markdown)
        {
            var formulas = Descendants(markdown).OfType<NativeFormulaView>().ToArray();
            if (formulas.Length != LooseMathSmokeFormulaFragments.Length)
                throw new InvalidOperationException("Scientific prose or protected source text was incorrectly promoted into an extra formula.");
            foreach (var fragment in LooseMathSmokeFormulaFragments)
                if (!formulas.Any(formula => formula.IsTypeset && AutomationProperties.GetName(formula).Contains(fragment, StringComparison.Ordinal)))
                    throw new InvalidOperationException("Scientific formula remained literal or failed native typesetting: " + fragment);
            if (formulas.Any(formula => !formula.IsTypeset || formula.FontSize != NativeStreamingMarkdown.BodyFontSize)
                || markdown.Children.OfType<NativeMathParagraph>().Any(paragraph => paragraph.FontSize != NativeStreamingMarkdown.BodyFontSize))
                throw new InvalidOperationException("Loose formulas and surrounding scientific prose use inconsistent font sizes.");
            foreach (var source in LooseMathSmokeProtectedSources)
            {
                var protectedMarkdown = new NativeStreamingMarkdown(source);
                if (Descendants(protectedMarkdown).OfType<NativeFormulaView>().Any())
                    throw new InvalidOperationException("Code, path or URL was incorrectly promoted into a formula: " + source);
            }
        }

        static void VerifyNoDanglingFormula(NativeStreamingMarkdown markdown)
        {
            if (Descendants(markdown).OfType<NativeFormulaView>().Any(formula => !formula.IsTypeset
                || AutomationProperties.GetName(formula).TrimEnd().EndsWith('=')
                || AutomationProperties.GetName(formula).TrimEnd().EndsWith('^')))
                throw new InvalidOperationException("An incomplete streamed mathematical operator was prematurely rendered as a formula.");
        }
    }
}
