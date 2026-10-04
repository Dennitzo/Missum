using System.Text.Json;
using Missum.App.Controls;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifyMarkdownHeadingSmokeAsync(JsonElement original)
    {
        const string source = "Einleitung.\n    ## Entropische Planck-Gravitation (EPT): Eine überprüfbare Theorie\n"
            + "    ### Ausgangspunkt\nDie Voraussetzungen werden erklärt.\n    #### Energie $E=mc^2$\n"
            + "```markdown\n## bleibt Quelltext\n```";
        var owner = Guid.NewGuid(); var answerId = Guid.NewGuid().ToString(); var reasoningId = Guid.NewGuid().ToString();
        var at = DateTimeOffset.UtcNow;
        var snapshot = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
        snapshot["activeSessionId"] = JsonSerializer.SerializeToElement(owner);
        snapshot["chatMode"] = JsonSerializer.SerializeToElement("general");
        snapshot["isRunning"] = JsonSerializer.SerializeToElement(true);
        snapshot["runMessageId"] = JsonSerializer.SerializeToElement(reasoningId);
        snapshot["runStatus"] = JsonSerializer.SerializeToElement("Modell generiert");
        snapshot["generationState"] = JsonSerializer.SerializeToElement("tokenProgress");
        snapshot["generatedTokens"] = JsonSerializer.SerializeToElement(25);
        snapshot["contextUsed"] = JsonSerializer.SerializeToElement(1025);
        snapshot["generationUpdatedAt"] = JsonSerializer.SerializeToElement(at);
        snapshot["messages"] = JsonSerializer.SerializeToElement(new object[]
        {
            new { id = answerId, sessionId = owner, role = "assistant", content = source, status = "completed", createdAt = at, updatedAt = at },
            new { id = reasoningId, sessionId = owner, role = "assistant", content = "", status = "streaming", createdAt = at, updatedAt = at,
                toolSteps = new[] { new { id = "headings-reasoning", tool = "assistant.reasoning", status = "running", detail = "Einleitung.\n    ## Entropische", contentOffset = 0, updatedAt = at } } },
        });
        try
        {
            _finishedSessions.Remove(owner);
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(snapshot));
            RenderMessagesNow(); RefreshThinkingIndicators();
            var answer = _messageBlocks[answerId].Values.OfType<NativeStreamingMarkdown>().Single();
            VerifyHeadings(answer);
            var row = _thinkingIndicators[reasoningId];
            row.SetExpanded(true);
            var reasoning = row.ReasoningView;
            var headingBefore = reasoning.Children[1];
            ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new
            {
                sessionId = owner, messageId = reasoningId, content = "", toolSteps = new[] { new
                { id = "headings-reasoning", tool = "assistant.reasoning", status = "running", detail = source, contentOffset = 0, updatedAt = at.AddMilliseconds(100) } },
            }));
            RenderMessagesNow(); RefreshThinkingIndicators();
            if (!row.IsExpanded || !ReferenceEquals(reasoning, row.ReasoningView) || !ReferenceEquals(headingBefore, reasoning.Children[1]))
                throw new InvalidOperationException("Streaming an indented heading rebuilt or collapsed the native reasoning disclosure.");
            VerifyHeadings(reasoning);
            AssertChatCursorAbsent(LayoutRoot);
            await SaveMathPreviewAsync(LayoutRoot, "native-markdown-headings-preview.png");
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-markdown-headings-validation.json"),
                JsonSerializer.Serialize(new { passed = true, answerHeadings = true, reasoningHeadings = true,
                    headingStreamRetainsControl = true, indentedModelDraft = true, mathHeadingTypeset = true, fencedCodePreserved = true }));
        }
        finally { ApplyEvent("state.snapshot", original); RenderMessagesNow(); }

        static void VerifyHeadings(NativeStreamingMarkdown markdown)
        {
            if (markdown.Children.Count != 6 || markdown.Children[1] is not TextBlock heading || markdown.Children[2] is not TextBlock subsection
                || markdown.Children[4] is not NativeMathParagraph mathHeading)
                throw new InvalidOperationException("Scientific headings did not become separate native text/formula sections.");
            var title = string.Concat(heading.Inlines.OfType<Run>().Select(run => run.Text));
            var subtitle = string.Concat(subsection.Inlines.OfType<Run>().Select(run => run.Text));
            if (title != "Entropische Planck-Gravitation (EPT): Eine überprüfbare Theorie" || subtitle != "Ausgangspunkt"
                || heading.FontSize != NativeStreamingMarkdown.SectionFontSize(2) || subsection.FontSize != NativeStreamingMarkdown.SectionFontSize(3)
                || mathHeading.FontSize != NativeStreamingMarkdown.SectionFontSize(4)
                || heading.FontWeight.Weight != Microsoft.UI.Text.FontWeights.SemiBold.Weight
                || subsection.FontWeight.Weight != Microsoft.UI.Text.FontWeights.SemiBold.Weight
                || heading.Foreground is not SolidColorBrush headingColor || mathHeading.Foreground is not SolidColorBrush mathColor
                || headingColor.Color != mathColor.Color)
                throw new InvalidOperationException("Headings retained hash markers or lost consistent native typography.");
            if (!mathHeading.TrailingInlines.OfType<InlineUIContainer>().Any(inline => inline.Child is NativeFormulaView { IsTypeset: true }))
                throw new InvalidOperationException("A mathematical heading lost its native KaTeX formula rendering.");
        }
    }
}
