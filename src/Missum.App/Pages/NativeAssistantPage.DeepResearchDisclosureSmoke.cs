using System.Text.Json;
using Missum.App.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifyDeepResearchLiveDisclosureSmokeAsync(NativeSubagentState child)
    {
        VerifyLiveToolDetailRefreshRace();
        var at = DateTimeOffset.UtcNow;
        // The preceding revision fixture intentionally uses future timestamps.
        // Start a separate assistant source so this real-time UI case cannot be
        // rejected correctly as an older update to that earlier fixture.
        var id = Guid.NewGuid().ToString();
        var initial = child.Messages.Values.Single();
        var prefix = string.Join("\n\n", Enumerable.Range(0, 25).Select(i =>
            $"Befund {i}: Die Quelle wird mit dem ursprünglichen Ergebnis und den Einschränkungen verglichen."));
        var full = JsonSerializer.SerializeToElement(new { id = "source-live", tool = "web.fetch", status = "completed",
            updatedAt = at, detailsDeferred = false, contentOffset = prefix.Length,
            outputJson = JsonSerializer.Serialize(new { url = "https://example.org", content = string.Join("\n",
                Enumerable.Range(0, 30).Select(i => "Quellenauszug " + i + ": Die vollständige Textstelle bleibt lesbar.")) }) });
        var compact = JsonSerializer.SerializeToElement(new { id = "source-live", tool = "web.fetch", status = "completed",
            updatedAt = at, detailsDeferred = true, contentOffset = prefix.Length });
        void Snapshot(int tick)
        {
            var now = DateTimeOffset.UtcNow;
            var message = initial.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            message["id"] = JsonSerializer.SerializeToElement(id);
            message["content"] = JsonSerializer.SerializeToElement(prefix);
            message["updatedAt"] = JsonSerializer.SerializeToElement(now);
            message["toolSteps"] = JsonSerializer.SerializeToElement(new[] { tick == 0 ? full : compact,
                JsonSerializer.SerializeToElement(new { id = "reasoning-live", tool = "assistant.reasoning", status = "running",
                    detail = "Ich vergleiche den Originalbeleg. Aktueller Prüfschritt " + tick + ".", updatedAt = now,
                    contentOffset = prefix.Length, detailsDeferred = false }) });
            var data = child.Snapshot.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            data["projectionRevision"] = JsonSerializer.SerializeToElement(100 + tick);
            data["transcriptRevision"] = JsonSerializer.SerializeToElement(100 + tick);
            data["generationState"] = JsonSerializer.SerializeToElement("tokenProgress");
            data["generatedTokens"] = JsonSerializer.SerializeToElement(1000 + tick);
            data["generationUpdatedAt"] = JsonSerializer.SerializeToElement(now);
            data["runMessageId"] = JsonSerializer.SerializeToElement(id);
            data["messages"] = JsonSerializer.SerializeToElement(new[] { JsonSerializer.SerializeToElement(message) });
            ApplyEvent("subagent.snapshot", JsonSerializer.SerializeToElement(data));
            RenderMessagesNow(); UpdateLayout();
        }
        Snapshot(0);
        await Task.Delay(800);
        // Tool details use the same native, independently scrolling tab as
        // ordinary chat tools. Research progress must not replace its controls.
        var tool = (ToolStepView)_messageBlocks[id]["tool:source-live"];
        if (tool.IsExpanded) throw new InvalidOperationException("Werkzeugdetails werden wieder innerhalb des Rechercheverlaufs ausgeklappt.");
        await OpenToolTabAsync(id, "source-live"); UpdateLayout();
        var tab = _activeToolTab ?? throw new InvalidOperationException("Recherche-Werkzeugdetails öffnen keinen nativen Tab.");
        var detail = Descendants(tab.Details).OfType<NativeToolResultView>().Single();
        var offset = tab.Scroll.VerticalOffset;
        Snapshot(1);
        for (var tick = 2; tick <= 9; tick++)
        {
            Snapshot(tick);
            RefreshOpenToolTabs();
            await Task.Delay(40);
            if (!ReferenceEquals(_activeToolTab, tab) || !Descendants(tab.Details).Contains(detail)
                || Math.Abs(tab.Scroll.VerticalOffset - offset) > 2)
                throw new InvalidOperationException("Live-Recherche ersetzt geladene Werkzeugdetails oder verschiebt die Leseposition im Tab.");
        }
        await SaveMathPreviewAsync(LayoutRoot, "native-deep-research-live-disclosure-preview.png");
        CloseToolTab(tab);
        await ActivateSubagentTabAsync(child);

    }

    private static void VerifyLiveToolDetailRefreshRace()
    {
        var tool = new ToolStepView();
        var at = DateTimeOffset.UtcNow;
        var loads = 0;
        JsonElement Receipt(bool newer, bool deferred) => JsonSerializer.SerializeToElement(new
        {
            id = "changing-tool", tool = "web.fetch", status = newer ? "completed" : "running",
            updatedAt = newer ? at.AddSeconds(1) : at, detailsDeferred = deferred,
            outputJson = deferred ? null : "{\"content\":\"Aktuelle Originalquelle\"}",
        });
        tool.LoadDetails = () =>
        {
            loads++;
            // A new revision arrives during the first request; the old answer
            // was rejected by the paging layer. There is no later streaming tick.
            tool.Update(Receipt(newer: true, deferred: loads == 1));
            return Task.FromResult(true);
        };
        tool.Update(Receipt(newer: false, deferred: true));
        tool.SetExpanded(true);
        if (loads != 2 || Descendants(tool).OfType<NativeToolResultView>().Count() != 1)
            throw new InvalidOperationException("Ein veraltetes Detailergebnis verhindert das Nachladen der aktuellen Werkzeugrevision.");
    }
}
