using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class ScienceResearchStandardTests
{
    private static readonly string[] Tools = [ClientToolNames.MathFormalProof];

    private static List<LmChatMessage> Initial(bool science, string? contextProfile = null) =>
        RunProcessor.CreateInitialMessages(new RunRequest(MissumAiProtocol.Version, RunMode.Coding,
            [new("user", [new("text", "Untersuche die nächste Forschungsfrage.")])],
            ClientCapabilities: ["coding", "coding.process", "math.lean"],
            CodingOptions: new(WorkspacePath: "C:/science"),
            WorkflowProfile: science ? ScienceSkill.Profile : null), "coding", Tools, contextProfile);

    private static AgentRunCheckpoint Evaluated(IReadOnlyList<LmChatMessage> messages) => new(messages, 1, 0, 100, 20,
        PreserveSessionPromptPrefix: true,
        WorkingState: CodingWorkingState.Create("Bisherige Forschung") with { Science = new(ScienceSkill.Profile, []) });

    [Theory]
    [InlineData(null)]
    [InlineData("compact-v1")]
    public void FreshScienceRunsReceiveTheStandardWithoutChangingOtherProfiles(string? contextProfile)
    {
        var science = Initial(true, contextProfile);
        Assert.Single(science, message => message.Content?.Contains(ScienceSkill.ResearchStandardMarker, StringComparison.Ordinal) == true);
        Assert.DoesNotContain(Initial(false, contextProfile), message =>
            message.Content?.Contains(ScienceSkill.ResearchStandardMarker, StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("compact-v1")]
    public void ContinuedScienceHistoryStaysExactAndReceivesMissingStandardBeforeNewUser(string? contextProfile)
    {
        LmChatMessage[] history = [new("system", "Historischer Science-Systemkern"),
            new("user", "Frühere Forschungsfrage"), new("assistant", "Belegtes Zwischenergebnis")];
        var initial = Initial(true, contextProfile);
        var continued = CodingSessionContext.Continue(Evaluated(history), initial, science: true);

        Assert.Equal(history, continued.Take(history.Length));
        for (var index = 0; index < history.Length; index++) Assert.Same(history[index], continued[index]);
        var standard = Assert.Single(continued, message => message.Role == "system"
            && message.Content?.StartsWith(ScienceSkill.ResearchStandardMarker, StringComparison.Ordinal) == true);
        Assert.Same(standard, continued[^2]);
        Assert.Equal(initial.Last(message => message.Role == "user"), continued[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedContinuationRecognizesStandardInSystemOrNativeFoldedHistory(bool nativeFolded)
    {
        var standard = new List<LmChatMessage>();
        ScienceSkill.EnsureResearchStandard(standard);
        var instruction = Assert.Single(standard).Content!;
        LmChatMessage saved = nativeFolded
            ? new("user", "Missum-Laufanweisung:\n" + instruction)
            : new("system", instruction);
        LmChatMessage[] history = [new("system", "Unveränderter Systemkern"), saved,
            new("assistant", "Bisheriges Ergebnis")];

        var continued = CodingSessionContext.Continue(Evaluated(history), Initial(true), science: true);

        Assert.Equal(history, continued.Take(history.Length));
        Assert.Single(continued, message => message.Content?.Contains(ScienceSkill.ResearchStandardMarker, StringComparison.Ordinal) == true);
        Assert.Equal("user", continued[^1].Role);
        Assert.Same(saved, continued[1]);
    }

    [Fact]
    public void EarlierResearchStandardIsUpgradedWithoutRewritingTheEvaluatedPrefix()
    {
        LmChatMessage[] history = [new("system", "Historischer Science-Systemkern"),
            new("user", "Missum-Laufanweisung:\nMISSUM_SCIENCE_SKILL_MODULE_V1:research-standard\nQuellen unter Quellen/ speichern."),
            new("assistant", "Bisheriges Ergebnis")];

        var continued = CodingSessionContext.Continue(Evaluated(history), Initial(true), science: true);

        Assert.Equal(history, continued.Take(history.Length));
        for (var index = 0; index < history.Length; index++) Assert.Same(history[index], continued[index]);
        var update = Assert.Single(continued, message => message.Content?.StartsWith(ScienceSkill.ResearchStandardMarker, StringComparison.Ordinal) == true);
        Assert.Contains("inputs/", update.Content);
        Assert.Same(update, continued[^2]);
        var repeated = CodingSessionContext.Continue(Evaluated(continued), Initial(true), science: true);
        Assert.Single(repeated, message => message.Content?.StartsWith(ScienceSkill.ResearchStandardMarker, StringComparison.Ordinal) == true);
    }

    [Fact]
    public void OrdinaryCodingContinuationReceivesNoScienceStandard()
    {
        LmChatMessage[] history = [new("system", "Coding-Systemkern"), new("user", "Datei bearbeiten"),
            new("assistant", "Datei bearbeitet")];
        var continued = CodingSessionContext.Continue(Evaluated(history), Initial(false));
        Assert.Equal(history, continued.Take(history.Length));
        Assert.DoesNotContain(continued, message => message.Content?.Contains(ScienceSkill.ResearchStandardMarker, StringComparison.Ordinal) == true);
    }
}
