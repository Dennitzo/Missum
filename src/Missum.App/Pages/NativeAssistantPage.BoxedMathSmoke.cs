using System.Text.Json;
using Missum.App.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifyBoxedMathSmokeAsync(JsonElement original)
    {
        if (!IsMessageFooterSmoke) throw new InvalidOperationException("Boxformel-Smoke erfordert das isolierte Testprofil.");
        const string source = @"$$\boxed{\text{Metrik (Krümmung)} \quad \longleftrightarrow \quad \text{Energie-Impuls-Dichte}}$$";
        var owner = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        var state = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
        state["activeSessionId"] = JsonSerializer.SerializeToElement(owner);
        state["chatMode"] = JsonSerializer.SerializeToElement("general");
        state["isRunning"] = JsonSerializer.SerializeToElement(true);
        state["messages"] = JsonSerializer.SerializeToElement(new[] { new { id = messageId, sessionId = owner, role = "assistant",
            content = "", status = "streaming", createdAt = at, updatedAt = at } });
        try
        {
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(state)); RenderMessagesNow();
            ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = owner, messageId, content = source[..^2] }));
            await Task.Delay(180, _lifetime.Token);
            if (Descendants(_messageViews[messageId.ToString()].View).OfType<NativeFormulaView>().Any())
                throw new InvalidOperationException("Die offene Boxformel wurde vorzeitig dargestellt.");
            ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = owner, messageId, content = source }));
            await Task.Delay(180, _lifetime.Token);
            var view = Descendants(_messageViews[messageId.ToString()].View).OfType<NativeFormulaView>().Single();
            if (!view.IsTypeset || view.Source != source || AutomationProperties.GetName(view) != "Formel: " + source)
                throw new InvalidOperationException("Die Originalformel bleibt roh oder ihr kopierbarer LaTeX-Text wurde verändert.");
            ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = owner, messageId,
                content = source + "\n\nZusätzlich $E=\\boxed{mc^2}+1$ und $$\\boxed{a+\\boxed{b}}$$" }));
            await Task.Delay(180, _lifetime.Token); UpdateLayout();
            var formulas = Descendants(_messageViews[messageId.ToString()].View).OfType<NativeFormulaView>().ToArray();
            if (formulas.Length != 3 || formulas.Any(formula => !formula.IsTypeset) || !formulas.Contains(view))
                throw new InvalidOperationException("Teilkästen oder verschachtelte Kästen werden nicht stabil im nativen Chat dargestellt.");
            ConversationScroll.ChangeView(null, 0, null, true);
            await SaveMathPreviewAsync(ConversationScroll, "native-boxed-latex-preview.png");
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-boxed-latex-validation.json"), JsonSerializer.Serialize(new
            {
                passed = true, processId = Environment.ProcessId, exactScreenshotFormula = true, boxedTypeset = true,
                longArrowAndGermanTextPreserved = true, partialBoxesTypeset = true, nestedBoxesTypeset = true,
                incompleteStreamingFormulaRemainsLiteral = true, appendedTextRetainsFormula = true,
                originalCopySourcePreserved = true, aiRequests = 0, userDataModified = false,
            }, JsonOptions), _lifetime.Token);
        }
        finally { ApplyEvent("state.snapshot", original); RenderMessagesNow(); }
    }
}
