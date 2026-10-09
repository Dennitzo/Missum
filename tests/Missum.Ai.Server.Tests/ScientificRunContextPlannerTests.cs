using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class ScientificRunContextPlannerTests
{
    private static readonly string[] ModelArguments = ["work/model.py"];
    private static readonly string[] OtherArguments = ["work/other.py"];
    private static readonly string[] OldArguments = ["old-log.py"];
    private static IReadOnlyList<AgentToolSpec> Tools() => new AgentToolCatalog().GetAvailableTools(ScientificRunCompletionPolicyTests.Request());

    [Fact]
    public void EvaluatedScientificPrefixBelowTheWorkingLimitRetainsReasoningAndChronologicalInstructions()
    {
        var messages = InitialInstructions();
        messages.Add(new("assistant", "Frühere fachliche Entscheidung.", ReasoningContent: "Die Herleitung verwendet SI-Einheiten."));
        messages.Add(new("user", "Missum-Laufanweisung zur Sprache: Frühere Sprachbindung."));
        messages.Add(new("user", "Missum-Laufanweisung:\nBewahre die gespeicherten Randbedingungen."));
        messages.Add(new("user", "Missum-Laufanweisung:\nBewahre die gespeicherten Randbedingungen."));
        AddReceipt(messages, "web.fetch", new { url = "https://example.org/source" }, new { success = true, content = "Beleg" });
        messages.Add(new("assistant", "Aktuelle Entscheidung.", ReasoningContent: "Die Dimensionsprüfung ist konsistent."));
        messages.Add(new("user", "Missum-Laufanweisung zur Sprache: Aktuelle Sprachbindung."));
        var original = messages.ToArray();

        var plan = ScientificRunContextPlanner.Prepare(messages, 1_048_576, null,
            preserveConversationPrefix: true, maximumInputTokens: ScientificRunContextPlanner.CanonicalWorkingInputTokens);

        Assert.False(plan.WasCompacted);
        Assert.Null(plan.Notice);
        Assert.Equal(original, plan.Messages);
        Assert.Equal(original, messages);
        Assert.Equal(ScientificRunContextPlanner.CanonicalWorkingInputTokens, plan.InputTokenBudget);
        AssertValidToolPairs(plan.Messages);
    }

    [Fact]
    public void CanonicalWorkingLimitCompactsLongHistoryWithoutChangingTheNativeWindowOrCurrentEvidence()
    {
        var messages = InitialInstructions();
        AddOldLogs(messages, 200);
        var current = ScientificRunCompletionPolicyTests.SuccessfulWork();
        messages.AddRange(current);
        ScientificRunCompletionPolicyTests.AddVerification(messages, success: true);
        var journal = messages.ToArray();
        Assert.True(ContextPlanner.EstimateTokens(messages) > ScientificRunContextPlanner.CanonicalWorkingInputTokens);

        var plan = ScientificRunContextPlanner.Prepare(messages, 1_048_576, null,
            preserveConversationPrefix: true, maximumInputTokens: ScientificRunContextPlanner.CanonicalWorkingInputTokens);

        Assert.True(plan.WasCompacted);
        Assert.Equal(ScientificRunContextPlanner.CanonicalWorkingInputTokens, plan.InputTokenBudget);
        Assert.InRange(plan.EstimatedInputTokens, 1, plan.InputTokenBudget);
        Assert.Equal(journal, messages);
        Assert.Contains("Laufjournal bleibt erhalten", plan.Notice);
        Assert.True(ScientificRunCompletionPolicy.Assess(ScientificRunCompletionPolicyTests.Request(), plan.Messages, Tools()).Complete);
        foreach (var message in current) Assert.Contains(message, plan.Messages);
        AssertValidToolPairs(plan.Messages);
    }

    [Fact]
    public void WorkingLimitNeverSilentlyTruncatesAnOversizedCurrentUserTask()
    {
        var messages = InitialInstructions();
        messages.Add(new("user", new string('q', 400_000)));
        var original = messages.ToArray();

        Assert.Throws<ContextBudgetException>(() => ScientificRunContextPlanner.Prepare(messages, 1_048_576, null,
            preserveConversationPrefix: true, maximumInputTokens: ScientificRunContextPlanner.CanonicalWorkingInputTokens));
        Assert.Equal(original, messages);
    }

    [Fact]
    public void MoreThanOneThousandOldManuscriptsRemindersAndLogsDoNotTruncateCurrentEvidence()
    {
        var messages = InitialInstructions();
        AddObsoleteManuscripts(messages, 1001);
        var current = ScientificRunCompletionPolicyTests.SuccessfulWork();
        messages.AddRange(current);
        ScientificRunCompletionPolicyTests.AddVerification(messages, success: true);
        AddOldLogs(messages, 100);
        var original = messages.ToArray();
        var before = ScientificRunCompletionPolicy.Assess(ScientificRunCompletionPolicyTests.Request(), messages, Tools());

        var plan = ScientificRunContextPlanner.Prepare(messages, 32768, null);
        var after = ScientificRunCompletionPolicy.Assess(ScientificRunCompletionPolicyTests.Request(), plan.Messages, Tools());

        Assert.True(plan.WasCompacted);
        Assert.True(before.Complete);
        Assert.Equal(before.Complete, after.Complete);
        Assert.Equal(before.NeedsVerification, after.NeedsVerification);
        Assert.InRange(plan.EstimatedInputTokens, 1, plan.InputTokenBudget);
        Assert.True(plan.Messages.Count < 150);
        Assert.Contains(current[0], plan.Messages);
        foreach (var message in current.Where(static message => message.ToolCalls is { Count: > 0 } || message.Role == "tool"))
            Assert.Contains(message, plan.Messages);
        Assert.Equal(original, messages.ToArray()); // The source/checkpoint journal is untouched.
        AssertValidToolPairs(plan.Messages);
    }

    [Theory]
    [InlineData("verified")]
    [InlineData("changed-manuscript")]
    [InlineData("failed-verify")]
    [InlineData("new-write")]
    [InlineData("failed-python")]
    [InlineData("malformed-python")]
    [InlineData("document-mutation")]
    [InlineData("foreign-project")]
    public void CompletionAndFreshnessAreIdenticalAcrossCompactionRestoreAndNativeNormalization(string scenario)
    {
        var messages = InitialInstructions();
        AddObsoleteManuscripts(messages, 30);
        messages.AddRange(ScientificRunCompletionPolicyTests.SuccessfulWork());
        ScientificRunCompletionPolicyTests.AddVerification(messages, success: true);
        for (var index = 0; index < 30; index++) messages.Add(new("assistant", ScientificRunCompletionPolicyTests.Manuscript));
        switch (scenario)
        {
            case "changed-manuscript":
                messages.Add(new("assistant", ScientificRunCompletionPolicyTests.Manuscript.Replace("Referenzskalierung", "neue Referenzskalierung", StringComparison.Ordinal)));
                break;
            case "failed-verify":
                ScientificRunCompletionPolicyTests.AddVerification(messages, success: false, pdfReady: false);
                break;
            case "new-write":
                AddReceipt(messages, ClientToolNames.ResearchCodeWrite,
                    new { projectId = ScientificRunCompletionPolicyTests.Project, path = "work/revised.py", content = "print(43)" }, new { success = true });
                break;
            case "failed-python":
                AddReceipt(messages, ClientToolNames.ResearchCodeExecute,
                    new { projectId = ScientificRunCompletionPolicyTests.Project, experimentId = "failed-current", executable = "python", arguments = ModelArguments },
                    new { success = false, runs = new[] { new { exitCode = 1, timedOut = false } }, error = "Numerischer Fehler" });
                break;
            case "malformed-python":
                AddReceipt(messages, ClientToolNames.ResearchCodeExecute,
                    new { projectId = ScientificRunCompletionPolicyTests.Project, experimentId = "malformed-current", executable = "python", arguments = ModelArguments },
                    new { success = false });
                messages[^1] = messages[^1] with { Content = "{invalid-result" };
                break;
            case "document-mutation":
                AddReceipt(messages, "document.create", new { path = "publication.docx" }, new { success = true });
                break;
            case "foreign-project":
                AddReceipt(messages, ClientToolNames.ResearchCodeExecute,
                    new { projectId = "research-other", experimentId = "other-experiment", executable = "python", arguments = OtherArguments },
                    new { success = true, runs = new[] { new { exitCode = 0, timedOut = false } } });
                break;
        }
        AddOldLogs(messages, 100);
        var before = ScientificRunCompletionPolicy.Assess(ScientificRunCompletionPolicyTests.Request(), messages, Tools());
        var plan = ScientificRunContextPlanner.Prepare(messages, 32768, null);
        var restored = JsonSerializer.Deserialize<LmChatMessage[]>(JsonSerializer.Serialize(plan.Messages))!;
        var normalized = ModelRuntimeClient.PrepareLanguageBoundMessages(restored);

        foreach (var projected in new[] { plan.Messages, restored, normalized })
        {
            var after = ScientificRunCompletionPolicy.Assess(ScientificRunCompletionPolicyTests.Request(), projected, Tools());
            Assert.Equal(before.Complete, after.Complete);
            Assert.Equal(before.NeedsVerification, after.NeedsVerification);
            Assert.Equal(before.Missing, after.Missing);
            Assert.Equal(before.Fingerprint, after.Fingerprint);
            AssertValidToolPairs(projected);
        }
    }

    [Fact]
    public void CurrentPublicationLongerThanFourThousandCharactersRemainsByteForByteIntact()
    {
        var messages = InitialInstructions();
        var current = ScientificRunCompletionPolicyTests.Manuscript.Replace("## Ergebnisse", new string('x', 18000) + "\n## Ergebnisse", StringComparison.Ordinal);
        messages.Add(new("assistant", current));
        AddOldLogs(messages, 100);

        var plan = ScientificRunContextPlanner.Prepare(messages, 32768, null);

        Assert.Contains(plan.Messages, message => message.Role == "assistant" && message.Content == current);
        Assert.DoesNotContain(plan.Messages, message => message.Content?.Contains("[Kontext verdichtet]", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void CurrentPrivateReasoningCanYieldItsBudgetWithoutChangingManuscriptOrCompletionEvidence()
    {
        var messages = InitialInstructions();
        var current = ScientificRunCompletionPolicyTests.SuccessfulWork();
        current[0] = current[0] with { ReasoningContent = new string('r', 100000) };
        messages.AddRange(current);
        ScientificRunCompletionPolicyTests.AddVerification(messages, success: true);
        var before = ScientificRunCompletionPolicy.Assess(ScientificRunCompletionPolicyTests.Request(), messages, Tools());

        var plan = ScientificRunContextPlanner.Prepare(messages, 32768, null);
        var after = ScientificRunCompletionPolicy.Assess(ScientificRunCompletionPolicyTests.Request(), plan.Messages, Tools());

        Assert.Contains(plan.Messages, message => message.Content == current[0].Content && message.ReasoningContent is null);
        Assert.True(after.Complete);
        Assert.Equal(before.Fingerprint, after.Fingerprint);
        Assert.NotNull(current[0].ReasoningContent);
    }

    [Fact]
    public void OnlyIdenticalNativeHostInstructionsAreDeduplicatedWhileGenuineCorrectionsStayInOrder()
    {
        const string host = "Missum-Laufanweisung:\nKorrigiere den Werkzeugaufruf anhand des zuletzt gespeicherten Fehlers.";
        const string correction = "Korrektur: Verwende ausdrücklich SI-Einheiten.";
        var messages = InitialInstructions();
        messages.Add(new("user", correction));
        for (var index = 0; index < 1000; index++) messages.Add(new("user", host));
        messages.Add(new("user", correction));
        messages.AddRange(ScientificRunCompletionPolicyTests.SuccessfulWork());
        ScientificRunCompletionPolicyTests.AddVerification(messages, success: true);

        var plan = ScientificRunContextPlanner.Prepare(messages, 32768, null);

        Assert.Single(plan.Messages, message => message.Role == "user" && message.Content == host);
        Assert.Equal(2, plan.Messages.Count(message => message.Role == "user" && message.Content == correction));
        Assert.Equal(new[] { correction, host, correction }, plan.Messages.Where(message => message.Content == correction || message.Content == host).Select(static message => message.Content));
        Assert.True(ScientificRunCompletionPolicy.Assess(ScientificRunCompletionPolicyTests.Request(), plan.Messages, Tools()).Complete);
    }

    [Fact]
    public void OriginalTaskCorrectionsRecoveryAndScienceContextRemainComplete()
    {
        var messages = InitialInstructions();
        messages.Add(new("user", RunProcessor.ScienceSessionContextStart + "\nVerbindlicher gespeicherter Forschungsstand"));
        messages.Add(new("user", "Korrektur: Verwende SI-Einheiten und erkläre jede Näherung ausdrücklich."));
        var required = messages.ToArray();
        messages.AddRange(ScientificRunCompletionPolicyTests.SuccessfulWork());
        ScientificRunCompletionPolicyTests.AddVerification(messages, success: false, pdfReady: false);
        ScientificRunCompletionPolicy.UpsertRecoveryPrompt(messages,
            ScientificRunCompletionPolicy.Assess(ScientificRunCompletionPolicyTests.Request(), messages, Tools()));
        var recovery = messages[^1];
        AddOldLogs(messages, 100);
        var normalized = ModelRuntimeClient.PrepareLanguageBoundMessages(messages);

        var plan = ScientificRunContextPlanner.Prepare(normalized, 32768, null);

        foreach (var message in required.Where(static message => message.Role == "user")) Assert.Contains(message, plan.Messages);
        Assert.Contains(plan.Messages, message => message.Content?.Contains(recovery.Content!, StringComparison.Ordinal) == true);
        Assert.Equal(1, ScientificRunCompletionPolicy.Assess(ScientificRunCompletionPolicyTests.Request(), plan.Messages, Tools()).RepeatedAttempts);
    }

    [Fact]
    public void ANewIncompletePublicationNeverRegainsAnOlderCompletedManuscript()
    {
        var messages = InitialInstructions();
        messages.AddRange(ScientificRunCompletionPolicyTests.SuccessfulWork());
        ScientificRunCompletionPolicyTests.AddVerification(messages, success: true);
        var incomplete = ScientificRunCompletionPolicy.PublicationBegin + "\n# Neuer unvollständiger Stand\n## Kurzfassung\nNoch in Bearbeitung";
        messages.Add(new("assistant", incomplete));
        AddOldLogs(messages, 100);

        var plan = ScientificRunContextPlanner.Prepare(messages, 32768, null);
        var after = ScientificRunCompletionPolicy.Assess(ScientificRunCompletionPolicyTests.Request(), plan.Messages, Tools());

        Assert.Contains(plan.Messages, message => message.Content == incomplete);
        Assert.False(after.Complete);
        Assert.False(after.NeedsVerification);
    }

    [Fact]
    public void PendingToolCallsAndCurrentReceiptContentAreNeverSeparatedOrShortened()
    {
        var messages = InitialInstructions();
        AddOldLogs(messages, 100);
        var receipt = new { success = true, stdout = new string('p', 18000), runs = new[] { new { exitCode = 0, timedOut = false } } };
        AddReceipt(messages, ClientToolNames.ResearchCodeExecute,
            new { projectId = ScientificRunCompletionPolicyTests.Project, experimentId = "latest", executable = "python", arguments = ModelArguments }, receipt);
        var completed = messages.TakeLast(2).ToArray();
        var pending = new LmChatMessage("assistant", null, ToolCalls: [new("pending-check", ClientToolNames.ResearchDeliverablesVerify,
            JsonSerializer.SerializeToElement(new { projectId = ScientificRunCompletionPolicyTests.Project }))]);
        messages.Add(pending);

        var plan = ScientificRunContextPlanner.Prepare(messages, 32768, null);

        foreach (var message in completed) Assert.Contains(message, plan.Messages);
        Assert.Contains(pending, plan.Messages);
        AssertValidToolPairs(plan.Messages, allowPending: true);
    }

    [Fact]
    public void ProtectedTaskOrManuscriptExceedingBudgetFailsHonestlyInsteadOfBeingTruncated()
    {
        var messages = InitialInstructions();
        messages.Add(new("assistant", ScientificRunCompletionPolicyTests.Manuscript.Replace("## Ergebnisse", new string('x', 100000) + "\n## Ergebnisse", StringComparison.Ordinal)));

        Assert.Throws<ContextBudgetException>(() => ScientificRunContextPlanner.Prepare(messages, 32768, null));
    }

    private static List<LmChatMessage> InitialInstructions() =>
        [new("system", "Untersuche wissenschaftliche Fragen und halte Belege und Hypothesen getrennt."),
         new("user", "Untersuche das Skalarfeld und erstelle eine vollständige Publikation mit ausgeführter Python-Abbildung.")];

    private static void AddObsoleteManuscripts(List<LmChatMessage> messages, int count)
    {
        for (var index = 0; index < count; index++)
        {
            messages.Add(new("assistant", ScientificRunCompletionPolicyTests.Manuscript.Replace("Überprüfbare Skalarfeldrechnung", "Veralteter Entwurf " + index, StringComparison.Ordinal),
                ReasoningContent: new string('r', 3000)));
            messages.Add(new("user", "Missum-Laufanweisung zur Sprache: Veraltete Erinnerung " + index));
        }
    }

    private static void AddOldLogs(List<LmChatMessage> messages, int count)
    {
        for (var index = 0; index < count; index++)
            AddReceipt(messages, "coding.command", new { executable = "python", arguments = OldArguments, iteration = index },
                new { success = true, stdout = new string('l', 2200) });
    }

    private static void AddReceipt(List<LmChatMessage> messages, string tool, object arguments, object result)
    {
        var id = "context-call-" + Guid.NewGuid().ToString("N");
        messages.Add(new("assistant", null, ToolCalls: [new(id, tool, JsonSerializer.SerializeToElement(arguments))]));
        messages.Add(new("tool", JsonSerializer.Serialize(new { status = "completed", result }), ToolCallId: id));
    }

    private static void AssertValidToolPairs(IReadOnlyList<LmChatMessage> messages, bool allowPending = false)
    {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            foreach (var call in message.ToolCalls ?? []) Assert.True(pending.Add(call.Id));
            if (message.Role == "tool") Assert.True(message.ToolCallId is { } id && pending.Remove(id));
        }
        if (!allowPending) Assert.Empty(pending);
    }
}
