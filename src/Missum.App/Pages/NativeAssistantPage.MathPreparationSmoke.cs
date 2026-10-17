using Missum.App.Controls;
using Microsoft.UI.Xaml;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
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
