using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifyTabRetentionSmokeAsync(JsonElement original)
    {
        var owner = Guid.NewGuid();
        var agentId = "tab-retention-smoke:" + owner;
        var childRun = Guid.NewGuid();
        var originalChildren = _subagents.ToArray();
        var now = DateTimeOffset.UtcNow;
        var callbacks = new List<(DependencyObject Target, DependencyProperty Property, long Token)>();
        var detached = 0;
        var propertyChanges = 0;
        FrameworkElement[] retained = [];
        void OnUnloaded(object sender, RoutedEventArgs args) => detached++;
        void Observe(DependencyObject target, DependencyProperty property) => callbacks.Add((target, property,
            target.RegisterPropertyChangedCallback(property, (_, _) => propertyChanges++)));
        JsonElement Child(int tokens, string title = "Stabile Forschungsaufgabe") => JsonSerializer.SerializeToElement(new
        {
            sessionId = owner, agentId, runId = childRun, title, status = "running", isRunning = true,
            generationState = "generating", generatedTokens = tokens, generationUpdatedAt = now.AddMilliseconds(tokens),
            projectionRevision = tokens + 1L, messages = Array.Empty<object>(),
        });
        try
        {
            var snapshot = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            snapshot["activeSessionId"] = JsonSerializer.SerializeToElement(owner);
            snapshot["chatMode"] = JsonSerializer.SerializeToElement("claudescience");
            snapshot["isRunning"] = JsonSerializer.SerializeToElement(true);
            snapshot["isAiBusy"] = JsonSerializer.SerializeToElement(true);
            snapshot["runMessageId"] = JsonSerializer.SerializeToElement(Guid.NewGuid());
            snapshot["sessions"] = JsonSerializer.SerializeToElement(new[] { new { id = owner, title = "Stabile Sitzung" } });
            snapshot["messages"] = JsonSerializer.SerializeToElement(Array.Empty<object>());
            snapshot["subagents"] = JsonSerializer.SerializeToElement(Array.Empty<object>());
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(snapshot));
            ApplyEvent("subagent.snapshot", Child(0));
            _sourceTabs.Add(owner);
            OpenSubagentOverview();
            UpdateLayout();
            await Task.Delay(20);
            var source = _sourcesTabButton ?? throw new InvalidOperationException("The retention fixture lacks its sources tab.");
            var overview = _subagentOverviewTabButton ?? throw new InvalidOperationException("The retention fixture lacks its subagent overview tab.");
            var child = _subagents[agentId];
            retained = SessionTabsPanel.Children.OfType<FrameworkElement>().ToArray();
            var expectedOrder = SessionTabsPanel.Children.ToArray();
            var sourceHover = source.Resources["ButtonBackgroundPointerOver"];
            var overviewHover = overview.Resources["ButtonBackgroundPointerOver"];
            var childHover = child.Select.Resources["ButtonBackgroundPointerOver"];
            var sourceFont = source.FontFamily;
            var overviewFont = overview.FontFamily;
            var childFont = child.Select.FontFamily;
            var tooltip = ToolTipService.GetToolTip(overview);
            foreach (var tab in retained) tab.Unloaded += OnUnloaded;
            foreach (var tab in retained.OfType<Border>())
            {
                Observe(tab, Border.BackgroundProperty);
                Observe(tab, Border.BorderBrushProperty);
            }
            foreach (var select in new[] { source, overview, child.Select })
            {
                Observe(select, Control.ForegroundProperty);
                Observe(select, Control.FontFamilyProperty);
            }
            if (!overview.Focus(FocusState.Programmatic))
                throw new InvalidOperationException($"The retained overview tab could not receive focus: loaded={overview.IsLoaded}, enabled={overview.IsEnabled}.");
            overview.ApplyTemplate();
            var common = TabCommonStates(overview)
                ?? throw new InvalidOperationException("The retained overview button lacks a PointerOver visual state.");
            if (!VisualStateManager.GoToState(overview, "PointerOver", useTransitions: false))
                throw new InvalidOperationException("The retention fixture could not establish the tab hover state.");
            for (var iteration = 1; iteration <= 100; iteration++)
            {
                // Exercise the real projection event path, without starting a
                // model request, changing tabs or altering the user's profile.
                ApplyEvent("subagent.snapshot", Child(iteration));
                UpdateLayout();
                if (!expectedOrder.SequenceEqual(SessionTabsPanel.Children)
                    || !ReferenceEquals(_sourcesTabButton, source)
                    || !ReferenceEquals(_subagentOverviewTabButton, overview)
                    || !ReferenceEquals(_subagents[agentId].Select, child.Select))
                    throw new InvalidOperationException("A token-only projection replaced or reordered a retained native tab.");
                if (common.CurrentState?.Name != "PointerOver"
                    || !ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), overview))
                    throw new InvalidOperationException("A token-only projection reset the native tab's hover or focus.");
            }
            await Task.Delay(20);
            if (detached != 0 || propertyChanges != 0
                || !ReferenceEquals(sourceHover, source.Resources["ButtonBackgroundPointerOver"])
                || !ReferenceEquals(overviewHover, overview.Resources["ButtonBackgroundPointerOver"])
                || !ReferenceEquals(childHover, child.Select.Resources["ButtonBackgroundPointerOver"])
                || !ReferenceEquals(sourceFont, source.FontFamily) || !ReferenceEquals(overviewFont, overview.FontFamily)
                || !ReferenceEquals(childFont, child.Select.FontFamily) || !ReferenceEquals(tooltip, ToolTipService.GetToolTip(overview)))
                throw new InvalidOperationException($"Background projections reset native tab resources: unloaded={detached}, appearanceChanges={propertyChanges}.");
            foreach (var (target, property, token) in callbacks) target.UnregisterPropertyChangedCallback(property, token);
            callbacks.Clear();
            foreach (var tab in retained) tab.Unloaded -= OnUnloaded;
            retained = [];
            ApplyEvent("subagent.snapshot", Child(101, "Aktualisierte Forschungsaufgabe"));
            if (child.Label.Text != "Subagent · Aktualisierte Forschungsaufgabe"
                || !ReferenceEquals(_subagents[agentId].Select, child.Select))
                throw new InvalidOperationException("A changed title must update the retained tab label.");
            OpenSourcesTab();
            if (!ReferenceEquals(source.Foreground, ThemeBrush("MissumTextBrush", 242))
                || !ReferenceEquals(overview.Foreground, ThemeBrush("MissumMutedTextBrush", 170)))
                throw new InvalidOperationException("Retained tabs must still update their active appearance on explicit navigation.");
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-tab-retention-validation.json"),
                JsonSerializer.Serialize(new { passed = true, tokenUpdates = 100, retainedControls = true,
                    retainedOrder = true, hoverPreserved = true, focusPreserved = true, noVisualDetach = true,
                    appearancePropertiesStable = true, mutablePaletteResourcesRetained = true,
                    explicitNavigationUpdatesAppearance = true, titleChangesStillVisible = true }));
        }
        finally
        {
            foreach (var (target, property, token) in callbacks) target.UnregisterPropertyChangedCallback(property, token);
            foreach (var tab in retained) tab.Unloaded -= OnUnloaded;
            _sourceTabs.Remove(owner);
            _subagentOverviewTabs.Remove(owner);
            HideSubagentOverview();
            ShowParentConversation();
            foreach (var state in _subagents.Values) SessionTabsPanel.Children.Remove(state.Container);
            _subagents.Clear();
            foreach (var entry in originalChildren) _subagents[entry.Key] = entry.Value;
            _subagentOverlaySignature = null;
            ApplyEvent("state.snapshot", original);
            RenderMessagesNow();
        }
    }

    private static VisualStateGroup? TabCommonStates(DependencyObject element)
    {
        if (element is FrameworkElement framework)
            foreach (var group in VisualStateManager.GetVisualStateGroups(framework))
                if (group.States.Any(state => state.Name == "PointerOver")) return group;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (TabCommonStates(VisualTreeHelper.GetChild(element, index)) is { } group) return group;
        return null;
    }
}
