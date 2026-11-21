using System.Text;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Gateway;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class UserProfileHandoffTests
{
    private const string UserPrompt = "Rechne 2 plus 2.";

    private static bool Carries(LmChatMessage message, string needle)
        => message.Content is { } content && content.Contains(needle, StringComparison.Ordinal);

    private static RunRequest Request(UserProfileSection? profile, RunMode mode = RunMode.General) => new(
        MissumAiProtocol.Version,
        mode,
        [new RunMessage("user", [new ContentPart("text", Text: UserPrompt)])],
        SessionId: "user-profile-session",
        UserProfile: profile);

    private static LmChatMessage ProfileSection(IReadOnlyList<LmChatMessage> messages)
        => Assert.Single(messages, message => Carries(message, "NUTZERPROFIL"));

    [Fact]
    public void TheProfileBecomesItsOwnSystemSectionAndTheUserPromptStaysUntouched()
    {
        var messages = RunProcessor.CreateInitialMessages(
            Request(new UserProfileSection(
                "NUTZERPROFIL\ndauerhafte Vorgaben\n- [global; Arbeitsweise] Antworte auf Deutsch.",
                "profile-v1-abc",
                3)),
            "general",
            []);

        Assert.Equal("system", messages[0].Role);
        var section = ProfileSection(messages);
        Assert.NotSame(messages[0], section);
        Assert.Contains("Antworte auf Deutsch.", section.Content, StringComparison.Ordinal);
        Assert.Contains("=== ENDE NUTZERPROFIL ===", section.Content, StringComparison.Ordinal);
        Assert.Equal(UserPrompt, Assert.Single(messages, message => message.Role == "user").Content);
    }

    [Fact]
    public void WithoutAPersonalProfileNothingIsInserted()
    {
        var messages = RunProcessor.CreateInitialMessages(Request(profile: null), "general", []);
        Assert.Equal(2, messages.Count);
        Assert.DoesNotContain(messages, message => Carries(message, "NUTZERPROFIL"));
    }

    [Theory]
    [InlineData(RunMode.General, null)]
    [InlineData(RunMode.Auto, null)]
    [InlineData(RunMode.Coding, null)]
    [InlineData(RunMode.Coding, ScienceSkill.Profile)]
    public void AllChatModesCarryTheSameSection(RunMode mode, string? workflow)
    {
        var profile = new UserProfileSection("- [global] Build vor jedem Test.", "profile-v1-coding", 1);
        var messages = RunProcessor.CreateInitialMessages(Request(profile, mode) with { WorkflowProfile = workflow }, "coding", []);

        Assert.Contains("Build vor jedem Test.", ProfileSection(messages).Content, StringComparison.Ordinal);
        Assert.Equal(workflow is not null, messages.Any(message => Carries(message, ScienceSkill.Marker)));
    }

    [Fact]
    public void TheActionOfAMemoryOperationIsReportedInsideTheSameBoundedSection()
    {
        var messages = RunProcessor.CreateInitialMessages(
            Request(new UserProfileSection("", "profile-v1-note", 4, "- vergessen: Tabellen vermeiden.")),
            "general",
            []);

        var section = ProfileSection(messages);
        Assert.Contains("GERADE AUSGEFÜHRTE GEDÄCHTNISAKTION", section.Content, StringComparison.Ordinal);
        Assert.Contains("vergessen: Tabellen vermeiden.", section.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void BudgetBoundsTheEntireMultibyteBlockAndRetainsWholeRules()
    {
        var huge = new string('界', 350);
        var expandsWithNormalization = new string('\uFDFA', 70);
        const string wholeRule = "Halte Übersetzungen kurz.";
        var messages = RunProcessor.CreateInitialMessages(
            Request(new UserProfileSection(huge + "\n" + expandsWithNormalization + "\n" + wholeRule,
                "profile-v1-long", 1, "Vorgabe gemerkt.")),
            "general",
            []);

        var section = ProfileSection(messages).Content!;
        Assert.InRange(Encoding.UTF8.GetByteCount(section), 1, UserProfileContext.TokenBudget);
        Assert.DoesNotContain("界", section, StringComparison.Ordinal);
        Assert.DoesNotContain(expandsWithNormalization, section, StringComparison.Ordinal);
        Assert.Contains(wholeRule, section, StringComparison.Ordinal);
        Assert.Contains("Vorgabe gemerkt.", section, StringComparison.Ordinal);
    }

    [Fact]
    public void GatewayRejectsMalformedOrUnboundedPayloads()
    {
        RunRequestValidator.Validate(Request(new("", "profile-v1-note", 1, "Vorgabe vergessen.")));
        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(Request(new(
            new string('x', UserProfileContext.MaximumTextCharacters + 1), "profile-v1-long", 1))));
        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(Request(new(
            "", "profile-v1-long-note", 1, new string('x', UserProfileContext.MaximumOperationNoteCharacters + 1)))));
        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(Request(new("Vorgabe", "", 1))));
        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(Request(new("Vorgabe", "fingerprint", -1))));
    }

    [Fact]
    public void ThePrefixCacheKeyFollowsTheDeliveredProfileAndNotItsRevision()
    {
        var without = RunProcessor.BuildRunSessionCacheKey(Request(profile: null), "run-1", "general");
        var first = RunProcessor.BuildRunSessionCacheKey(
            Request(new UserProfileSection("alt", "profile-v1-equals", 1)), "run-1", "general");
        var sameProfile = RunProcessor.BuildRunSessionCacheKey(
            Request(new UserProfileSection("alt", "profile-v1-new", 9)), "run-1", "general");
        var changed = RunProcessor.BuildRunSessionCacheKey(
            Request(new UserProfileSection("neu", "profile-v1-equals", 1)), "run-1", "general");

        Assert.StartsWith("missum-session-v3-", without, StringComparison.Ordinal);
        Assert.NotEqual(without, first);
        Assert.Equal(first, sameProfile);
        Assert.NotEqual(first, changed);
    }

    [Theory]
    [InlineData(RunMode.General, null)]
    [InlineData(RunMode.Coding, null)]
    [InlineData(RunMode.Coding, ScienceSkill.Profile)]
    public void FollowUpReplacesOrRemovesTheMergedProfileWithoutLosingConversation(
        RunMode mode, string? workflow)
    {
        const string oldRule = "Altregel: Tabellen vermeiden.";
        const string oldReceipt = "Eine ältere Vorgabe wurde gelöscht.";
        const string newRule = "Neuregel: Tabellen nutzen.";
        const string answer = "Die frühere Antwort.";
        const string followUp = "Was gilt jetzt?";
        var role = mode == RunMode.Coding ? "coding" : "general";
        var previous = Request(new(oldRule, "profile-v1-old", 1, oldReceipt), mode) with { WorkflowProfile = workflow };
        var initial = RunProcessor.CreateInitialMessages(previous, role, []);
        var evaluated = ModelRuntimeClient.PrepareLanguageBoundMessages(initial);
        var hiddenAnswer = new LmChatMessage("assistant", answer, ReasoningContent: "Früherer Denkstand");
        var saved = new AgentRunCheckpoint([.. evaluated, hiddenAnswer], 1, 0, 100, 10, PreserveSessionPromptPrefix: true);
        var visible = previous with
        {
            Messages = [.. previous.Messages, new("assistant", [new("text", answer)]), new("user", [new("text", followUp)])],
        };

        var unchanged = Continue(visible with { UserProfile = previous.UserProfile! with { Revision = 2 } });
        Assert.Same(saved.Messages[0], unchanged[0]);
        var changed = Continue(visible with { UserProfile = new(newRule, "profile-v1-new", 2) });
        Assert.Contains(newRule, ProfileSection(changed).Content, StringComparison.Ordinal);
        Assert.DoesNotContain(changed, message => Carries(message, oldRule) || Carries(message, oldReceipt));
        var forgotten = Continue(visible with { UserProfile = null });
        Assert.DoesNotContain(forgotten, message => Carries(message, "NUTZERPROFIL"));
        Assert.Contains(hiddenAnswer, forgotten);
        Assert.Equal(followUp, forgotten[^1].Content);
        Assert.Equal(workflow is not null, forgotten.Any(message => Carries(message, ScienceSkill.Marker)));

        IReadOnlyList<LmChatMessage> Continue(RunRequest request)
        {
            var current = RunProcessor.CreateInitialMessages(request, role, []);
            if (mode == RunMode.Coding)
                return CodingSessionContext.Continue(saved, current, science: workflow is not null);
            Assert.True(GeneralSessionContext.TryContinue(new(saved, previous, answer), request, current, out var continued));
            return continued;
        }
    }
}
