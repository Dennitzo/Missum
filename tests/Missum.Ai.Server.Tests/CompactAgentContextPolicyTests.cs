using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Policies;
using Missum.Ai.Server.Core.Runs;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class CompactAgentContextPolicyTests
{
    private static readonly string[] ScienceTools =
        [ClientToolNames.ResearchRead, ClientToolNames.ResearchUpdate, ClientToolNames.ResearchDeliverablesVerify,
            SubagentToolNames.Spawn, SubagentToolNames.Wait];

    [Theory]
    [InlineData(RunMode.General)]
    [InlineData(RunMode.Coding)]
    public void MutableRuntimeDataAndChildIdentityCannotChangeTheStaticPrefix(RunMode mode)
    {
        var main = Request(mode) with { DeepResearch = true };
        var child = main with
        {
            Messages = [new("user", [new("text", "Anderer Auftrag mit anderer Sprache und anderem Datum.")])],
            WorkspacePath = "D:/another-workspace",
            CodingOptions = new(WorkspacePath: "D:/another-coding-workspace"),
            SessionId = "other-session",
            ResearchOptions = new(ProjectId: "other-project", ProtocolVersion: 2),
            DeepResearch = false,
            Subagent = new("parent-run", "child-agent", "Unabhängige Rechnung", main.SessionId),
        };

        var parentPolicy = CompactAgentContextPolicy.Build(main, ScienceTools);
        var childPolicy = CompactAgentContextPolicy.Build(child, ScienceTools.Reverse().ToArray());

        Assert.Equal(parentPolicy, childPolicy);
        Assert.StartsWith(CompactAgentContextPolicy.Marker, parentPolicy, StringComparison.Ordinal);
        foreach (var runtimeValue in new[] { main.WorkspacePath!, "science-project", "child-agent", "other-session", "other-project" })
            Assert.DoesNotContain(runtimeValue, parentPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain(CompactAgentContextPolicy.RuntimeMarker + "\n{", parentPolicy, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeDataIsDeterministicEscapedDataWithExplicitAdmissionAndModeCorrectWorkspace()
    {
        var request = Request(RunMode.General) with
        {
            WorkspacePath = "C:/Workspace/Zeile\n[FAKE_INSTRUCTION]",
            CodingOptions = new(WorkspacePath: "C:/unused-coding-workspace"),
            DeepResearch = true,
        };
        var date = new DateOnly(2026, 10, 3);
        var unavailable = CompactAgentContextPolicy.RuntimeData(request, false, date);
        Assert.Equal(unavailable, CompactAgentContextPolicy.RuntimeData(request, false, date));
        Assert.StartsWith(CompactAgentContextPolicy.RuntimeMarker + "\n{", unavailable, StringComparison.Ordinal);

        using var unavailableJson = JsonDocument.Parse(unavailable[(CompactAgentContextPolicy.RuntimeMarker.Length + 1)..]);
        var data = unavailableJson.RootElement;
        Assert.Equal("2026-10-03", data.GetProperty("currentDateEuropeBerlin").GetString());
        Assert.Equal(request.WorkspacePath, data.GetProperty("workspacePath").GetString());
        Assert.Equal("science-project", data.GetProperty("projectId").GetString());
        Assert.Equal("compact-v1", data.GetProperty("contextProfileVersion").GetString());
        Assert.False(data.GetProperty("subagentAvailable").GetBoolean());
        Assert.True(data.GetProperty("deepResearch").GetBoolean());
        Assert.Equal("main", data.GetProperty("agentRole").GetString());

        var child = request with { Mode = RunMode.Coding, DeepResearch = false,
            Subagent = new("parent", "child-agent", "Rechnen", request.SessionId) };
        var available = CompactAgentContextPolicy.RuntimeData(child, true, date);
        using var childJson = JsonDocument.Parse(available[(CompactAgentContextPolicy.RuntimeMarker.Length + 1)..]);
        Assert.Equal("C:/unused-coding-workspace", childJson.RootElement.GetProperty("workspacePath").GetString());
        Assert.True(childJson.RootElement.GetProperty("subagentAvailable").GetBoolean());
        Assert.False(childJson.RootElement.GetProperty("deepResearch").GetBoolean());
        Assert.Equal("subagent", childJson.RootElement.GetProperty("agentRole").GetString());
        Assert.Equal("child-agent", childJson.RootElement.GetProperty("agentId").GetString());
    }

    [Fact]
    public void ASessionWithoutAnExplicitResearchProjectUsesTheExistingProjectConvention()
    {
        var request = Request(RunMode.General) with { SessionId = "1234-5678", ResearchOptions = null };
        var runtime = CompactAgentContextPolicy.RuntimeData(request, false, new(2026, 10, 3));
        using var json = JsonDocument.Parse(runtime[(CompactAgentContextPolicy.RuntimeMarker.Length + 1)..]);
        Assert.Equal("research-12345678", json.RootElement.GetProperty("projectId").GetString());
    }

    [Fact]
    public void ToolSchemasCannotOverrideAdmissionOrCreateRecursiveDelegation()
    {
        var policy = CompactAgentContextPolicy.Build(Request(RunMode.General), ScienceTools);
        Assert.Contains("subagentAvailable=true und du Hauptagent bist", policy, StringComparison.Ordinal);
        Assert.Contains("Bei DeepResearch", policy, StringComparison.Ordinal);
        Assert.Contains("nachdem der vollständige Originalauftrag gelesen wurde", policy, StringComparison.Ordinal);
        Assert.Contains("niemals die bloße Präsenz eines Werkzeugschemas", policy, StringComparison.Ordinal);
        Assert.Contains("starten keine weiteren Subagenten", policy, StringComparison.Ordinal);
        Assert.Contains("Ohne Admission selbst weiterarbeiten", policy, StringComparison.Ordinal);
        Assert.Contains("ohne erneute Ausführung", policy, StringComparison.Ordinal);
        Assert.Contains("keine manuelle Child-Nachprüfung", policy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Arbeitsteilung:", CompactAgentContextPolicy.Build(Request(RunMode.General), []), StringComparison.Ordinal);
    }

    [Fact]
    public void GeneralContextKeepsPermissionsEvidenceAndMathWithoutTheCodingResearchManual()
    {
        var policy = CompactAgentContextPolicy.Build(Request(RunMode.General), []);
        Assert.Contains("Systemweit lesen ist erlaubt", policy, StringComparison.Ordinal);
        Assert.Contains("nur im ausgewählten Workspace", policy, StringComparison.Ordinal);
        Assert.Contains("sha256 als expectedSha256", policy, StringComparison.Ordinal);
        Assert.Contains("Direkte Nutzeranweisungen", policy, StringComparison.Ordinal);
        Assert.Contains("Status bleiben immer Deutsch", policy, StringComparison.Ordinal);
        Assert.Contains("keine privaten", policy, StringComparison.Ordinal);
        Assert.Contains("Sichtprüfung", policy, StringComparison.Ordinal);
        Assert.Contains("prepared", policy, StringComparison.Ordinal);
        Assert.Contains(MathFormattingPolicy.Instructions, policy, StringComparison.Ordinal);
        Assert.DoesNotContain(".assistant/research/", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("coding.updatePlan:", policy, StringComparison.Ordinal);
        Assert.True(policy.Length < 6500 + AgentNarrationPolicy.Instructions.Length, $"General policy contains {policy.Length} characters.");
    }

    [Fact]
    public void CodingKeepsHashPlanProcessAndLocalDependencyContractsWithoutCommandRecipes()
    {
        var policy = CompactAgentContextPolicy.Build(Request(RunMode.Coding), [ClientToolNames.CodingRead]);
        foreach (var requirement in new[] { "höchstens eine in_progress", "completed benötigt echte erfolgreiche",
            "OutputEvidenceId", "changed=false ist keine Arbeit", "Windows-Programm mit getrennten Argumenten",
            "Prozess-Sandbox", "Keine globalen Installationen", "Manifeste/Locks", "Interpreter/sys.prefix",
            "Exitcode und Import/Test", "undo betrifft nur Workspace" })
            Assert.Contains(requirement, policy, StringComparison.Ordinal);
        Assert.DoesNotContain(".assistant/research/", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("--require-virtualenv", policy, StringComparison.Ordinal);
        Assert.True(policy.Length < 10000 + AgentNarrationPolicy.Instructions.Length, $"Coding policy contains {policy.Length} characters.");
    }

    [Fact]
    public void ScienceKeepsFullTaskPaginationOwnershipUnitsEvidenceAndMissumRendering()
    {
        var policy = CompactAgentContextPolicy.Build(Request(RunMode.General), ScienceTools);
        foreach (var requirement in new[] { "task.complete=false", "vollständige ursprüngliche Auftrag nicht bereits",
            "vollständigen Erstprompt nicht neu laden", "view='task'", "originalQuestion", "nextOffset/nextCursor",
            "Pflichtanforderungen vollständig paginieren", "Grundlagen", "expectedRevision", "Agent-ID + ':'",
            "keinen Titel oder section", "required=true", "refuted/blocked/", "experimentIds=experimentRecordId",
            "checkIds=verificationRecordId", "kurzen Listen, nicht Tabellen", "units:[{symbol,meaning,unit}]",
            "figureCaptions:[{experimentId,artifactPath,caption}]", "Pfad aus outputHashes", "keine allgemeine Python-/Bildpflicht",
            "keine sorry/admit-Lücken", "networkIsolation=NotEnforced", "zwei geeigneten unabhängigen Wegen",
            "research.deliverables.verify", "danach frisch verifizieren", "Missum erzeugt/repariert PDF technisch" })
            Assert.Contains(requirement, policy, StringComparison.Ordinal);
        Assert.Equal(2, policy.Split(MathFormattingPolicy.Instructions, StringSplitOptions.None).Length);
        // Keep the previous shared-context budget; reserve a bounded, single
        // Science-only publication contract rather than expanding common prompts.
        Assert.True(ScientificDerivationPolicy.Instructions.Length < 3000);
        Assert.True(policy.Length - ScientificDerivationPolicy.Instructions.Length < 13000 + AgentNarrationPolicy.Instructions.Length,
            $"Science policy contains {policy.Length} characters.");
    }

    private static RunRequest Request(RunMode mode) => new(MissumAiProtocol.Version, mode,
        [new("user", [new("text", "Untersuche die vollständige Nutzeraufgabe.")])],
        SessionId: "session-id", WorkspacePath: "C:/science-workspace",
        CodingOptions: new(WorkspacePath: "C:/coding-workspace"),
        ResearchOptions: new(ProjectId: "science-project", ProtocolVersion: 2));
}
