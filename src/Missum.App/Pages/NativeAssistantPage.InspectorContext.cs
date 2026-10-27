using Missum.App.Services;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private void RefreshInspectorContext()
    {
        // Use the same effective conversation owner and values as the composer
        // ring. Navigation, compaction and the streaming refresh therefore
        // cannot leave the sidebar displaying a different context budget.
        var text = NativeContextSummary.Format(DisplayContextUsed, DisplayContextLimit);
        if (InspectorContextCount.Text != text) InspectorContextCount.Text = text;
        var name = "Kontext: " + text;
        if (AutomationProperties.GetName(InspectorContextCount) != name)
            AutomationProperties.SetName(InspectorContextCount, name);
        if (!Equals(ToolTipService.GetToolTip(InspectorContextCount), ContextToolTip.Content))
            ToolTipService.SetToolTip(InspectorContextCount, ContextToolTip.Content);
    }
}
