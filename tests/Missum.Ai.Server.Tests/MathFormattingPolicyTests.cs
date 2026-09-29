using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class MathFormattingPolicyTests
{
    [Theory]
    [InlineData(RunMode.General)]
    [InlineData(RunMode.Coding)]
    public void InitialModelMessagesContainSharedMathInstructionsWithoutChangingUserNotation(RunMode mode)
    {
        const string prompt = "Erkläre $f(x)=\\frac{x^{2}}{2}$ und prüfe die Ableitung.";
        var request = new RunRequest(MissumAiProtocol.Version, mode, [new("user", [new("text", prompt)])]);

        var messages = RunProcessor.CreateInitialMessages(request, "general", []);

        var system = Assert.Single(messages, message => message.Role == "system").Content!;
        Assert.Equal(2, system.Split(MathFormattingPolicy.Instructions, StringSplitOptions.None).Length);
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
}
