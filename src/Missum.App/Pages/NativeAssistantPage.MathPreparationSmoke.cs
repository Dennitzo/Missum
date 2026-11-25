using Missum.App.Controls;
using Microsoft.UI.Xaml;
using System.Text.Json;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifyChatMathFixtureAsync(JsonElement original)
    {
        var path = Environment.GetEnvironmentVariable("MISSUM_SMOKE_MATH_INPUT")
            ?? throw new InvalidOperationException("Die Testdatei mit den Chat-Formeln fehlt.");
        var source = (await File.ReadAllTextAsync(path, _lifetime.Token)).TrimEnd();
        var expected = NativeMathSyntax.Split(source).Where(segment => segment.IsMath && segment.Display)
            .Select(segment => segment.Text).ToArray();
        if (expected.Length == 0) throw new InvalidOperationException("Die Testdatei enthält keine Formelblöcke.");

        var owner = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        var snapshot = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
        snapshot["activeSessionId"] = JsonSerializer.SerializeToElement(owner);
        snapshot["chatMode"] = JsonSerializer.SerializeToElement("claudescience");
        snapshot["isRunning"] = JsonSerializer.SerializeToElement(true);
        snapshot["messages"] = JsonSerializer.SerializeToElement(new[] { new { id = messageId, sessionId = owner,
            role = "assistant", content = "", status = "streaming", createdAt = at, updatedAt = at } });
        try
        {
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(snapshot));
            RenderMessagesNow();
            ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = owner, messageId, content = source }));
            RenderMessagesNow();
            var bubble = _messageViews[messageId.ToString()].View;
            await WaitForNativeMathPreparationAsync(bubble);
            var formulas = Descendants(bubble).OfType<NativeFormulaView>().ToArray();
            if (!formulas.Select(formula => formula.Source).SequenceEqual(expected) || formulas.Any(formula => !formula.IsTypeset))
                throw new InvalidOperationException("Die Formelblöcke wurden im Chat nicht vollständig gerendert.");
            await SaveMathPreviewAsync(bubble, "native-chat-math-fixture.png");
        }
        finally { ApplyEvent("state.snapshot", original); RenderMessagesNow(); }
    }

    private async Task WaitForNativeMathPreparationAsync(FrameworkElement root)
    {
        // Preparation and rasterization deliberately run outside the dispatcher.
        // A runtime check must await their publication instead of assuming the
        // constructor blocks until Skia and every Markdown section are ready.
        for (var attempt = 0; attempt < 500; attempt++)
        {
            LayoutRoot.UpdateLayout();
            root.UpdateLayout();
            var nodes = Descendants(root).Prepend(root).ToArray();
            var formulas = nodes.OfType<NativeFormulaView>()
                .Concat(nodes.OfType<NativeMathParagraph>().SelectMany(paragraph => paragraph.FormulaViews)).Distinct();
            if (!nodes.OfType<NativeVirtualizingTextView>().Any(view => view.IsPreparing)
                && !nodes.OfType<NativeStreamingMarkdown>().Any(view => view.IsPreparing)
                && !formulas.Any(view => view.IsRendering) && attempt > 0) return;
            await Task.Delay(10, _lifetime.Token);
        }
        throw new TimeoutException("Die asynchrone native Formelaufbereitung wurde nicht rechtzeitig abgeschlossen.");
    }
}
