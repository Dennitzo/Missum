using System.Text.Json;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private string? _researchNavigationSignature;

    private static string ResearchTaskLabel(string title, bool research = true) => !research
        || title.StartsWith("Deep Research", StringComparison.OrdinalIgnoreCase) ? title : "Deep Research · " + title;

    private static bool IsLegacyResearchIdentity(string agentId, string runId) =>
        agentId.StartsWith("deep-research:", StringComparison.Ordinal)
        || runId.StartsWith("deep-research-run:", StringComparison.Ordinal);

    private static bool IsDeepResearchReceipt(JsonElement step)
    {
        if (S(step, "tool") is not ("subagent" or "subagent.completed")) return false;
        var output = ToolStepView.ReadMetadata(S(step, "outputJson"));
        return (S(step, "kind") == "deepResearch" || S(output, "kind") == "deepResearch")
            && IsLegacyResearchIdentity(S(output, "agentId", S(step, "agentId")), S(output, "runId"));
    }

    // A real child owns the normal lifecycle row. Coalesce its original tool
    // receipt using persisted IDs, including in the background block builder.
    private static bool IsAcceptedDeepResearchStep(JsonElement step, IReadOnlyList<JsonElement> receipts)
    {
        if (S(step, "tool") != "web.deepResearch") return false;
        var id = S(step, "id");
        if (id.Length == 0) return false;
        foreach (var receipt in receipts)
        {
            var output = ToolStepView.ReadMetadata(S(receipt, "outputJson"));
            if (S(output, "originToolStepId", S(receipt, "originToolStepId")) != id) continue;
            var agentId = S(output, "agentId", S(receipt, "agentId"));
            var runId = S(output, "runId");
            if (agentId.Length > 0 && runId.Length > 0 && !IsLegacyResearchIdentity(agentId, runId)) return true;
        }
        return false;
    }

    private NativeSubagentState? FindDeepResearchView(string sourceMessageId, string toolStepId) =>
        _subagents.Values.FirstOrDefault(item => item.SessionId == _session
            && S(item.Snapshot, "originMessageId", S(item.Snapshot, "parentMessageId")) == sourceMessageId
            && S(item.Snapshot, "originToolStepId") == toolStepId);

    private async Task<bool> OpenDeepResearchTabAsync(string sourceMessageId, string toolStepId)
    {
        if (FindDeepResearchView(sourceMessageId, toolStepId) is not { } child) return false;
        await ActivateSubagentTabAsync(child);
        return ReferenceEquals(ActiveSubagent, child);
    }

    private (string AgentId, int PlanetIndex, string Title)? ResearchIdentity(string sourceMessageId, string toolStepId) =>
        FindDeepResearchView(sourceMessageId, toolStepId) is { } child ? (child.AgentId, child.PlanetIndex, child.Title) : null;

    private void RefreshResearchNavigation()
    {
        var signature = SubagentListSignature(SessionSubagents());
        if (_researchNavigationSignature == signature) return;
        _researchNavigationSignature = signature;
        // Historical headers may already exist when their child projection is
        // restored. Keep them navigable without rebuilding the parent answer.
        foreach (var tool in _messageBlocks.Values.SelectMany(blocks => blocks.Values).OfType<ToolStepView>())
            tool.RefreshResearchNavigation();
    }
}
