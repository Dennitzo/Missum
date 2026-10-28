using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class SpecializedAgentSelectionTests
{
    [Theory]
    [InlineData(ChatMode.Coding)]
    [InlineData(ChatMode.ClaudeScience)]
    public void CapturesIndependentProfileAndExactUserGoal(ChatMode mode)
    {
        const string goal = "Finde eine allgemeine Gleichung.\n\\Box \\psi = 0; keine verfrühte Erfolgsmeldung.";
        var option = SpecializedAgentSelection.Capture(SpecializedAgentProfiles.Loop, mode, goal);
        Assert.NotNull(option);
        Assert.Equal(SpecializedAgentProfiles.Loop, option.ProfileId);
        Assert.Equal(goal, option.Goal);
        var originalMessage = new RunMessage("user", [new("text", Text: goal)]);
        var request = new RunRequest(MissumAiProtocol.Version, RunMode.Coding, [originalMessage], SpecializedAgent: option);
        var json = JsonSerializer.Serialize(request, MissumAiProtocol.CreateJsonOptions());
        var loaded = JsonSerializer.Deserialize<RunRequest>(json, MissumAiProtocol.CreateJsonOptions());
        Assert.Equal(goal, loaded!.SpecializedAgent!.Goal);
        Assert.Equal(goal, Assert.Single(Assert.Single(loaded.Messages).Content).Text);
        Assert.Null(loaded.Subagent);
        Assert.Null(SpecializedAgentSelection.Capture(null, mode, goal));
    }

    [Fact]
    public void RejectsUnknownProfilesGeneralModeAndMissingGoal()
    {
        Assert.Throws<ArgumentException>(() => SpecializedAgentSelection.Capture("unknown", ChatMode.Coding, "Ziel"));
        Assert.Throws<InvalidOperationException>(() => SpecializedAgentSelection.Capture(SpecializedAgentProfiles.Loop, ChatMode.General, "Ziel"));
        Assert.Throws<ArgumentException>(() => SpecializedAgentSelection.Capture(SpecializedAgentProfiles.Loop, ChatMode.Coding, "  "));
        Assert.Throws<ArgumentException>(() => SpecializedAgentSelection.Capture(SpecializedAgentProfiles.Loop, ChatMode.Coding, new string('x', 100_001)));
    }

    [Fact]
    public async Task SessionSelectionSurvivesRestartWithoutLeakingToAnotherSession()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var owner = Guid.NewGuid(); var other = Guid.NewGuid();
        var store = new SessionAgentProfileStore(environment.Directory);
        await store.WriteAsync(owner, SpecializedAgentProfiles.Loop);
        var restored = new SessionAgentProfileStore(environment.Directory);
        Assert.Equal(SpecializedAgentProfiles.Loop, await restored.ReadAsync(owner));
        Assert.Null(await restored.ReadAsync(other));
        await restored.WriteAsync(owner, null);
        Assert.Null(await store.ReadAsync(owner));
    }

    [Fact]
    public async Task OriginalGoalIsBoundToTheSpecificSessionAndMessageAcrossRestart()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var session = Guid.NewGuid(); var message = Guid.NewGuid();
        var goal = new SpecializedAgentOptions(SpecializedAgentProfiles.Loop, "Ursprüngliches Ziel \\Box \\psi = 0.");
        var store = new SessionAgentProfileStore(environment.Directory);
        await store.WriteGoalAsync(session, message, goal);
        var restored = new SessionAgentProfileStore(environment.Directory);
        Assert.Equal(goal, await restored.ReadGoalAsync(session, message));
        Assert.Null(await restored.ReadGoalAsync(Guid.NewGuid(), message));
        Assert.Null(await restored.ReadGoalAsync(session, Guid.NewGuid()));
        await restored.WriteAsync(session, null);
        Assert.Null(await restored.ReadAsync(session));
        Assert.Equal(goal, await restored.ReadGoalAsync(session, message));
    }
}
