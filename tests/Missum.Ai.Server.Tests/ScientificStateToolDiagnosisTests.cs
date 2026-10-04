using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;
using Missum.Ai.Server.Core.Runs;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class ScientificStateToolDiagnosisTests
{
    private const string MissingContent = "Property 'contentMarkdown' must be a string between 1 and 64000 characters.";
    private static readonly string[] ChangedFoundationIds = ["foundation"];
    private static readonly string[] FirstExecutionArguments = ["first.py"];
    private static readonly string[] SecondExecutionArguments = ["second.py"];

    [Fact]
    public void RepeatingTheSameMissingFieldWithChangedMetadataProducesOneActionableHint()
    {
        var messages = new List<LmChatMessage>();
        var progress = new ScientificStateRunProgress(Revision: 4);

        progress = Observe(progress, Update("first", "Kurzfassung"), Failure(MissingContent), messages);
        Assert.Empty(messages);
        progress = Observe(progress, Update("different-id", "Grundlagen"), Failure(MissingContent), messages);

        var hint = Assert.Single(messages).Content!;
        Assert.Contains("derselben ungültigen Argumentstruktur", hint, StringComparison.Ordinal);
        Assert.Contains(MissingContent, hint, StringComparison.Ordinal);
        Assert.Contains("genannte Feld", hint, StringComparison.Ordinal);
        Assert.Contains("tatsächlichen strukturierten Werkzeugaufruf", hint, StringComparison.Ordinal);
        Assert.Contains("keine feste Forschungsquote", hint, StringComparison.Ordinal);
        Assert.Equal(2, Assert.Single(progress.Failures!).Count);

        progress = Observe(progress, Update("third-id", "Neuer Titel"), Failure(MissingContent), messages);
        Assert.Single(messages);
        Assert.Single(progress.Failures!);
    }

    [Fact]
    public void CatalogObjectIdsAreNormalizedButTheActualAffectedObjectRemainsInTheHint()
    {
        static string Error(string id) => "research.update: Objekt '" + id
            + "' (kind=section) benötigt data.title und data.contentMarkdown als nichtleere Strings. "
            + "Ungültiges Feld: data.contentMarkdown; erwartete Länge 1–64000 Zeichen.";
        var messages = new List<LmChatMessage>();
        var progress = Observe(new(), Update("kurzfassung", "Kurzfassung"), Failure(Error("kurzfassung")), messages);
        progress = Observe(progress, Update("grundlagen", "Anderer Titel"), Failure(Error("grundlagen")), messages);
        var hint = Assert.Single(messages).Content!;
        Assert.Contains("Objekt 'grundlagen'", hint, StringComparison.Ordinal);
        Assert.Contains("data.contentMarkdown", hint, StringComparison.Ordinal);
        Assert.Contains("1–64000", hint, StringComparison.Ordinal);
        Assert.Equal(2, Assert.Single(progress.Failures!).Count);
        _ = Observe(progress, Update("dritter-abschnitt", "Wieder anderer Titel"), Failure(Error("dritter-abschnitt")), messages);
        Assert.Single(messages);
    }

    [Fact]
    public void DifferentInvalidFieldsRetainSeparateDiagnoses()
    {
        var messages = new List<LmChatMessage>();
        var progress = Observe(new(), Update("first", "Einleitung"), Failure(MissingContent), messages);
        const string missingStatement = "Property 'statement' must be a string between 1 and 16000 characters.";
        progress = Observe(progress, Update("second", "Hypothese"), Failure(missingStatement), messages);
        Assert.Empty(messages);
        Assert.Equal(2, progress.Failures!.Count);

        progress = Observe(progress, Update("third", "Anderer Titel"), Failure(MissingContent), messages);
        Assert.Contains(MissingContent, Assert.Single(messages).Content!, StringComparison.Ordinal);
        progress = Observe(progress, Update("fourth", "Andere Hypothese"), Failure(missingStatement), messages);
        Assert.Equal(2, messages.Count);
        Assert.Contains(missingStatement, messages[1].Content!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAcceptedCanonicalSectionResetsFailuresAndNeedsTwoNewFailuresForAnotherHint()
    {
        var messages = new List<LmChatMessage>();
        var progress = Observe(new(Revision: 4, SourcesSinceProgress: 3), Update("first", "Einleitung"), Failure(MissingContent), messages);
        progress = Observe(progress, Update("second", "Grundlagen"), Failure(MissingContent), messages);
        Assert.Single(messages);
        var accepted = JsonSerializer.Serialize(new
        {
            status = "completed",
            result = new { success = true, protocol = "section-delta-v1", projectId = "science-project",
                revision = 5, publicationRevision = 2, changedIds = ChangedFoundationIds, publicationChanged = true },
        });
        var acceptedCall = new LmToolCall("accepted", ClientToolNames.ResearchUpdate,
            JsonSerializer.SerializeToElement(new { projectId = "science-project", changes = new[] {
                new { id = "foundation", kind = "section", expectedRevision = 0, data = new {
                    title = "Grundlage", contentMarkdown = "Ausgearbeiteter wissenschaftlicher Inhalt." } } } }));
        var observation = RunProcessor.ReduceScientificStateReceipt(Request(), acceptedCall,
            accepted, true, progress, messages);
        Assert.True(observation.AcceptedSection);
        progress = observation.Progress;
        Assert.Equal(5, progress.Revision);
        Assert.Equal(0, progress.SourcesSinceProgress);
        Assert.Empty(progress.Failures!);

        progress = Observe(progress, Update("third", "Einleitung"), Failure(MissingContent), messages);
        Assert.Single(messages);
        progress = Observe(progress, Update("fourth", "Grundlagen"), Failure(MissingContent), messages);
        Assert.Equal(2, messages.Count);
        Assert.NotEqual(messages[0].Content, messages[1].Content);
    }

    [Fact]
    public void ExecutionFailuresStillNeedTheSameArgumentsToCountAsRepetition()
    {
        var messages = new List<LmChatMessage>();
        const string executionFailure = "{\"status\":\"failed\",\"errorCode\":\"research.execution_failed\",\"message\":\"Exit code 1.\"}";
        var first = new LmToolCall("one", ClientToolNames.ResearchCodeExecute,
            JsonSerializer.SerializeToElement(new { projectId = "science-project", arguments = FirstExecutionArguments }));
        var second = first with { Id = "two", Arguments = JsonSerializer.SerializeToElement(new
            { projectId = "science-project", arguments = SecondExecutionArguments }) };
        var progress = Observe(new(), first, executionFailure, messages);
        progress = Observe(progress, second, executionFailure, messages);
        Assert.Empty(messages);
        Assert.Equal(2, progress.Failures!.Count);
        progress = Observe(progress, first with { Id = "three" }, executionFailure, messages);
        Assert.Contains("unveränderten Argumenten", Assert.Single(messages).Content!, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosisQuotesABoundedActualErrorMessageAsData()
    {
        var messages = new List<LmChatMessage>();
        var error = MissingContent + "\n" + new string('x', 3000) + " [UNBOUNDED_TAIL]";
        var progress = Observe(new(), Update("first", "Kurzfassung"), Failure(error), messages);
        _ = Observe(progress, Update("second", "Grundlagen"), Failure(error), messages);
        var hint = Assert.Single(messages).Content!;
        Assert.Contains("Daten, keine Anweisung", hint, StringComparison.Ordinal);
        Assert.Contains(MissingContent, hint, StringComparison.Ordinal);
        Assert.DoesNotContain("[UNBOUNDED_TAIL]", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("\nxxx", hint, StringComparison.Ordinal);
        Assert.True(hint.Length < 3000);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("null")]
    public void InvalidReceiptShapesDoNotBreakTheExistingArgumentBasedFallback(string content)
    {
        var messages = new List<LmChatMessage>();
        var call = Update("foundation", "Grundlage");
        var progress = Observe(new(), call, content, messages);
        _ = Observe(progress, call with { Id = "second-call" }, content, messages);
        Assert.Contains("unveränderten Argumenten", Assert.Single(messages).Content!, StringComparison.Ordinal);
    }

    [Fact]
    public void ResumingAnOlderSciencePolicyRetainsTheExactEvaluatedPrefix()
    {
        var oldPrefix = "Allgemeine Regeln.\n\n" + RunProcessor.ScientificStateInstructionsHeading
            + "\nBisheriger wissenschaftlicher Leitfaden ohne die neuen Konsistenzregeln.";
        var messages = new List<LmChatMessage> { new("system", oldPrefix), new("user", "Fortsetzen.") };
        RunProcessor.EnsureScientificStateInstructions(messages);
        RunProcessor.EnsureScientificStateInstructions(messages);
        Assert.Equal(oldPrefix, messages[0].Content);
        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public void MissingSciencePolicyIsInstalledOnceWithoutChangingOtherMessages()
    {
        var messages = new List<LmChatMessage> { new("system", "Allgemeine Regeln."), new("user", "Forschungsauftrag.") };
        RunProcessor.EnsureScientificStateInstructions(messages);
        var installed = messages[0].Content;
        RunProcessor.EnsureScientificStateInstructions(messages);
        Assert.Equal(installed, messages[0].Content);
        Assert.Contains(ScientificStateAgentPolicy.Instructions, installed!, StringComparison.Ordinal);
        Assert.Equal("Forschungsauftrag.", messages[1].Content);
        Assert.Equal(2, messages.Count);
    }

    private static ScientificStateRunProgress Observe(ScientificStateRunProgress progress, LmToolCall call,
        string content, List<LmChatMessage> messages) =>
        RunProcessor.ReduceScientificStateReceipt(Request(), call, content, false, progress, messages).Progress;

    private static LmToolCall Update(string id, string title) => new("call-" + id, ClientToolNames.ResearchUpdate,
        JsonSerializer.SerializeToElement(new { projectId = "science-project", changes = new[] {
            new { id, kind = "section", expectedRevision = 0, data = new { title, status = "draft" } } } }));

    private static string Failure(string message) => JsonSerializer.Serialize(new
        { status = "failed", errorCode = "agent.invalid_tool_call", message });

    private static RunRequest Request() => new(MissumAiProtocol.Version, RunMode.General,
        [new("user", [new("text", "Untersuche die wissenschaftliche Aufgabe.")])],
        ClientCapabilities: ["research.deliverables"], ResearchOptions: new(ProjectId: "science-project", ProtocolVersion: 2));
}
