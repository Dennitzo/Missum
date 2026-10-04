using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class MathFormattingPolicyTests
{
    private static readonly string[] ScienceTools =
        [ClientToolNames.ResearchRead, ClientToolNames.ResearchUpdate, ClientToolNames.ResearchDeliverablesVerify];
    private static readonly string[] ScienceCapabilities = ["research.deliverables"];

    [Theory]
    [InlineData(RunMode.General, false)]
    [InlineData(RunMode.Coding, false)]
    [InlineData(RunMode.General, true)]
    public void InitialModelMessagesContainSharedMathInstructionsWithoutChangingUserNotation(RunMode mode, bool science)
    {
        const string prompt = "Erkläre $f(x)=\\frac{x^{2}}{2}$ und prüfe die Ableitung.";
        var request = new RunRequest(MissumAiProtocol.Version, mode, [new("user", [new("text", prompt)])],
            ClientCapabilities: science ? ScienceCapabilities : [],
            ResearchOptions: science ? new(ProjectId: "science-project", ProtocolVersion: 2) : null);

        var messages = RunProcessor.CreateInitialMessages(request, "general", science ? ScienceTools : []);

        var system = Assert.Single(messages, message => message.Role == "system").Content!;
        Assert.Equal(2, system.Split(MathFormattingPolicy.Instructions, StringSplitOptions.None).Length);
        AssertVisibleReasoningNotation(system);
        Assert.Equal(prompt, Assert.Single(messages, message => message.Role == "user").Content);
    }

    [Theory]
    [InlineData(RunMode.General, false, false)]
    [InlineData(RunMode.Coding, false, false)]
    [InlineData(RunMode.General, true, false)]
    [InlineData(RunMode.General, true, true)]
    public void CompactProfilesAndScienceSubagentsReceiveTheSameVisibleReasoningMathRules(
        RunMode mode, bool science, bool child)
    {
        const string prompt = "T_ECT = T_HH/ln 2; P_ECT = A·T_ECT⁴; dM/dt=-P/c²";
        var request = new RunRequest(MissumAiProtocol.Version, mode, [new("user", [new("text", prompt)])],
            ClientCapabilities: science ? ScienceCapabilities : [],
            ResearchOptions: science ? new(ProjectId: "science-project", ProtocolVersion: 2) : null,
            Subagent: child ? new("parent-run", "child-agent", "Erkläre die gegebenen Formeln.", "science-session") : null);

        var messages = RunProcessor.CreateInitialMessages(request, "general", science ? ScienceTools : [],
            contextProfileVersion: ToolContextProfiles.Current);

        var system = Assert.Single(messages, message => message.Role == "system").Content!;
        Assert.Equal(2, system.Split(MathFormattingPolicy.Instructions, StringSplitOptions.None).Length);
        AssertVisibleReasoningNotation(system);
        // Rendering instructions must not silently alter the user's calculation.
        Assert.Equal(prompt, Assert.Single(messages, message => message.Role == "user").Content);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CodingAndScientificResearchKeepMathRulesWithOrWithoutWorkingState(bool workingState)
    {
        var policy = CodingAgentPolicy.ForWorkingState(workingState);

        Assert.Contains(CodingAgentPolicy.ScientificResearchPrompt, policy, StringComparison.Ordinal);
        Assert.Contains(MathFormattingPolicy.Instructions, policy, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResumedCodingAddsMathRulesOnceAndDoesNotTrustTheirOccurrenceInUserData(bool hasSystemMessage)
    {
        var user = new LmChatMessage("user", MathFormattingPolicy.Instructions);
        List<LmChatMessage> messages = hasSystemMessage
            ? [new("system", "Gespeicherte Projektanweisung."), user]
            : [user];

        CodingAgentPolicy.EnsureCurrentInstructions(messages);
        var first = messages.ToArray();
        CodingAgentPolicy.EnsureCurrentInstructions(messages);

        Assert.Equal(first, messages);
        var system = Assert.Single(messages, message => message.Role == "system").Content!;
        Assert.Equal(2, system.Split(MathFormattingPolicy.Instructions, StringSplitOptions.None).Length);
        if (hasSystemMessage) Assert.StartsWith("Gespeicherte Projektanweisung.", system, StringComparison.Ordinal);
        Assert.Same(user, Assert.Single(messages, message => message.Role == "user"));
    }

    [Theory]
    [InlineData(ConversationProfile.Audiobook)]
    [InlineData(ConversationProfile.ContextPreparation)]
    public void ProseAndInternalCompactionDoNotReceiveVisibleMathFormattingRules(ConversationProfile profile)
    {
        var request = new RunRequest(MissumAiProtocol.Version, RunMode.General,
            [new("user", [new("text", "Fortsetzen")])], ConversationProfile: profile);

        Assert.DoesNotContain(MathFormattingPolicy.Instructions,
            GeneralAgentPolicies.ForConversation("general", request, []), StringComparison.Ordinal);
    }

    private static void AssertVisibleReasoningNotation(string policy)
    {
        Assert.Contains("in sämtlichen Modi", policy, StringComparison.Ordinal);
        Assert.Contains("sichtbaren Reasoning-/Analysekanal im Denkprozess", policy, StringComparison.Ordinal);
        Assert.Contains("Umrahme jeden mathematischen Ausdruck", policy, StringComparison.Ordinal);
        Assert.Contains("Keine nackten Formelzeilen", policy, StringComparison.Ordinal);
        Assert.Contains("keine Unicode-Hoch-/Tiefstellungen", policy, StringComparison.Ordinal);
        Assert.Contains("$a_{i}=b^{2}$", policy, StringComparison.Ordinal);
        Assert.Contains("inline $...$ oder \\(...\\)", policy, StringComparison.Ordinal);
        Assert.Contains("$$...$$ oder \\[...\\]", policy, StringComparison.Ordinal);
        Assert.Contains("bleiben unverändert", policy, StringComparison.Ordinal);
    }
}
