using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class ScientificRunCompletionPolicyTests
{
    private static readonly string[] PythonArguments = ["work/model.py"];
    internal const string Project = "research-completion-fixture";
    internal const string Experiment = "scalar-experiment";
    internal const string Manuscript = """
        <!-- MISSUM_PUBLICATION_BEGIN -->
        # Überprüfbare Skalarfeldrechnung
        ## Kurzfassung
        Wir prüfen eine begrenzte Referenzrechnung und weisen ihre Voraussetzungen ausdrücklich aus.
        ## Forschungsfrage und Einordnung
        Welche Skalierung liefert die betrachtete Näherung?
        ## Voraussetzungen und Konventionen
        Die Rechnung verwendet natürliche Einheiten und festgelegte Randbedingungen.
        ## Herleitungen und Rechenschritte
        Aus $E^2=p^2+m^2$ folgt $E=\sqrt{p^2+m^2}$. Die Größe $E$ ist die Energie in Joule.
        ## Ergebnisse
        Die ausgeführte Python-Rechnung und ihre Abbildung zeigen die Referenzskalierung.
        ## Diskussion und Grenzen
        Die Rechnung beweist keine neue physikalische Theorie.
        ## Literatur
        Die tatsächlich geprüften Originalquellen werden mit ihrer Aussagegrenze dokumentiert.
        <!-- MISSUM_PUBLICATION_END -->
        """;

    internal static RunRequest Request() => new(MissumAiProtocol.Version, RunMode.General,
        [new("user", [new("text", "Untersuche das Skalarfeld und erstelle die Publikation mit Python-Abbildungen.")])],
        ClientCapabilities: ["research.sandbox", "research.deliverables"], AllowedServerTools: [], DeepResearch: true,
        SessionId: "completion-session", ResearchOptions: new(ProjectId: Project, AutonomyLevel: ResearchAutonomyLevel.SandboxResearch));

    private static IReadOnlyList<AgentToolSpec> Tools() => new AgentToolCatalog().GetAvailableTools(Request());

    [Fact]
    public void TemporarilyUnavailablePythonRuntimeDoesNotDisableScienceCompletionChecks()
    {
        var request = Request() with { ClientCapabilities = ["research.deliverables"] };
        Assert.True(ScientificRunCompletionPolicy.Applies(request));
        var assessment = ScientificRunCompletionPolicy.Assess(request, [], new AgentToolCatalog().GetAvailableTools(request));
        Assert.False(assessment.Complete);
        Assert.Contains(assessment.Missing, problem => problem.Contains("Python", StringComparison.Ordinal));
    }

    [Fact]
    public void ASourceFileAndAnAssistantSuccessClaimCannotCompleteTheResearch()
    {
        var messages = new List<LmChatMessage> { new("assistant", Manuscript) };
        AddReceipt(messages, ClientToolNames.ResearchCodeWrite, new { projectId = Project, path = "work/model.py", content = "print(42)" }, new { success = true });
        messages.Add(new("assistant", "Das Skript ist gespeichert. Eine Ausführung ist hier nicht möglich, die Arbeit ist abgeschlossen."));

        var assessment = ScientificRunCompletionPolicy.Assess(Request(), messages, Tools());

        Assert.False(assessment.Complete);
        Assert.False(assessment.NeedsVerification);
        Assert.Contains(assessment.Missing, problem => problem.Contains("Python", StringComparison.Ordinal));
        Assert.Contains("research.code.execute", ScientificRunCompletionPolicy.RepairPrompt(assessment), StringComparison.Ordinal);
    }

    [Fact]
    public void OnlySuccessfulProcessesAndFreshVerifiedPublicationAndPlotsAdmitCompletion()
    {
        var messages = SuccessfulWork();
        var pending = ScientificRunCompletionPolicy.Assess(Request(), messages, Tools());
        Assert.False(pending.Complete);
        Assert.True(pending.NeedsVerification);
        AddVerification(messages, success: true);

        var assessment = ScientificRunCompletionPolicy.Assess(Request(), messages, Tools());

        Assert.True(assessment.Complete);
        messages.Add(new("assistant", "Die überprüften Ergebnisse und ihre Grenzen sind dokumentiert."));
        Assert.True(ScientificRunCompletionPolicy.Assess(Request(), messages, Tools()).Complete);
    }

    [Fact]
    public void AChangedManuscriptInvalidatesThePreviousPublicationVerification()
    {
        var messages = SuccessfulWork();
        AddVerification(messages, success: true);
        messages.Add(new("assistant", Manuscript.Replace("Referenzskalierung", "korrigierte Referenzskalierung", StringComparison.Ordinal)));

        var assessment = ScientificRunCompletionPolicy.Assess(Request(), messages, Tools());

        Assert.False(assessment.Complete);
        Assert.True(assessment.NeedsVerification);
        AddVerification(messages, success: true);
        Assert.True(ScientificRunCompletionPolicy.Assess(Request(), messages, Tools()).Complete);
    }

    [Fact]
    public void NewResearchSourceRequiresAnActualExecutionAndAnotherArtifactCheck()
    {
        var messages = SuccessfulWork();
        AddVerification(messages, success: true);
        AddReceipt(messages, ClientToolNames.ResearchCodeWrite, new { projectId = Project, path = "work/model.py", content = "print(43)" }, new { success = true });

        Assert.False(ScientificRunCompletionPolicy.Assess(Request(), messages, Tools()).Complete);
        AddPython(messages, success: true);
        Assert.True(ScientificRunCompletionPolicy.Assess(Request(), messages, Tools()).NeedsVerification);
        AddVerification(messages, success: true);
        Assert.True(ScientificRunCompletionPolicy.Assess(Request(), messages, Tools()).Complete);
    }

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(true, 1, false)]
    [InlineData(true, 0, true)]
    public void AFailedOrTimedOutProcessNeverCountsAsAnExecutedSimulation(bool success, int exitCode, bool timedOut)
    {
        var messages = new List<LmChatMessage> { new("assistant", Manuscript) };
        AddReceipt(messages, ClientToolNames.ResearchCodeExecute,
            new { projectId = Project, experimentId = Experiment, executable = "python", arguments = PythonArguments },
            new { success, runs = new[] { new { exitCode, timedOut } }, error = "Numerische Ausführung fehlgeschlagen" });
        AddVerification(messages, success: true);

        var assessment = ScientificRunCompletionPolicy.Assess(Request(), messages, Tools());

        Assert.False(assessment.Complete);
        Assert.Contains(assessment.Missing, problem => problem.Contains("fehlgeschlagen", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void APreviousOrForeignProjectsArtifactsCannotSatisfyTheCurrentRun()
    {
        var messages = SuccessfulWork();
        AddVerification(messages, success: true, projectId: "research-foreign");

        Assert.False(ScientificRunCompletionPolicy.Assess(Request(), messages, Tools()).Complete);
    }

    [Fact]
    public void AnOlderPlotCannotCompleteANewExperimentWhichCreatedNoCurrentPlot()
    {
        var messages = SuccessfulWork();
        AddReceipt(messages, ClientToolNames.ResearchCodeExecute,
            new { projectId = Project, experimentId = "new-experiment", executable = "python", arguments = PythonArguments },
            new { success = true, runs = new[] { new { exitCode = 0, timedOut = false } } });
        AddVerification(messages, success: true);

        Assert.False(ScientificRunCompletionPolicy.Assess(Request(), messages, Tools()).Complete);
    }

    [Fact]
    public void ReusingAnExperimentIdCannotRevalidateAnOldPlotOutsideTheNewProcessWindow()
    {
        var messages = SuccessfulWork();
        var startedAt = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        AddReceipt(messages, ClientToolNames.ResearchCodeExecute,
            new { projectId = Project, experimentId = Experiment, executable = "python", arguments = PythonArguments },
            new { success = true, startedAt, completedAt = startedAt.AddSeconds(10), runs = new[] { new { exitCode = 0, timedOut = false } } });
        AddVerification(messages, success: true, artifactModifiedAt: startedAt.AddHours(-1));

        Assert.False(ScientificRunCompletionPolicy.Assess(Request(), messages, Tools()).Complete);
        AddVerification(messages, success: true, artifactModifiedAt: startedAt.AddSeconds(1));
        Assert.True(ScientificRunCompletionPolicy.Assess(Request(), messages, Tools()).Complete);
    }

    [Fact]
    public void ACurrentFailedVerificationRequiresAModelRepairInsteadOfAnotherAutomaticVerification()
    {
        var messages = SuccessfulWork();
        AddVerification(messages, success: false, pdfReady: false);

        var assessment = ScientificRunCompletionPolicy.Assess(Request(), messages, Tools());

        Assert.False(assessment.Complete);
        Assert.False(assessment.NeedsVerification);
        Assert.Contains("PDF", ScientificRunCompletionPolicy.RepairPrompt(assessment), StringComparison.Ordinal);
        messages.Add(new("assistant", Manuscript.Replace("Referenzskalierung", "korrigierte Referenzskalierung", StringComparison.Ordinal)));
        Assert.True(ScientificRunCompletionPolicy.Assess(Request(), messages, Tools()).NeedsVerification);
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void MissingPdfOrFailedSimulationOrMissingHashesPreventsAFalseSuccess(bool pdfReady, bool simulationReady, bool executed, bool hashes)
    {
        var messages = SuccessfulWork();
        AddVerification(messages, true, pdfReady: pdfReady, simulationReady: simulationReady, executed: executed, hashes: hashes);

        Assert.False(ScientificRunCompletionPolicy.Assess(Request(), messages, Tools()).Complete);
    }

    [Fact]
    public void MalformedOrIncompletePublicationCannotBorrowAnOlderValidBlock()
    {
        var messages = SuccessfulWork();
        AddVerification(messages, success: true);
        messages.Add(new("assistant", "<!-- MISSUM_PUBLICATION_BEGIN -->\n# Neuer Entwurf\n## Ergebnisse\nUnvollständig"));

        var assessment = ScientificRunCompletionPolicy.Assess(Request(), messages, Tools());

        Assert.False(assessment.Complete);
        Assert.Contains(assessment.Missing, problem => problem.Contains("Publikationsmanuskript", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("````text", "````")]
    [InlineData("~~~text", "~~~")]
    public void MarkerExamplesAndHeadingsInsideCodeFencesCannotReplaceTheRealManuscript(string fenceStart, string fenceEnd)
    {
        var messages = SuccessfulWork();
        AddVerification(messages, success: true);
        messages.Add(new("assistant", Manuscript + "\n" + fenceStart + "\n" + ScientificRunCompletionPolicy.PublicationBegin
            + "\n# Falsches Beispiel\n" + ScientificRunCompletionPolicy.PublicationEnd + "\n" + fenceEnd));

        Assert.True(ScientificRunCompletionPolicy.Assess(Request(), messages, Tools()).Complete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARecognizedJsonResponseEnvelopeContainsTheSameCanonicalManuscript(bool fenced)
    {
        var messages = SuccessfulWork();
        AddVerification(messages, success: true);
        var envelope = JsonSerializer.Serialize(new { schema = "assistant.agent.response.v1", type = "message", message = Manuscript, sessionTitle = "Skalarfeldrechnung" });
        messages.Add(new("assistant", fenced ? "```json\n" + envelope + "\n```" : envelope));

        Assert.True(ScientificRunCompletionPolicy.Assess(Request(), messages, Tools()).Complete);
    }

    [Fact]
    public void ALongTextWithEmptyRequiredSectionsCannotCountAsACompleteManuscript()
    {
        var messages = SuccessfulWork();
        AddVerification(messages, success: true);
        messages.Add(new("assistant", ScientificRunCompletionPolicy.PublicationBegin + "\n# Skalarfeldrechnung\n"
            + "## Kurzfassung\n" + new string('x', 300) + "\n## Forschungsfrage\n## Voraussetzungen\n## Herleitungen\n## Ergebnisse\n## Diskussion\n## Literatur\n"
            + ScientificRunCompletionPolicy.PublicationEnd));

        Assert.False(ScientificRunCompletionPolicy.Assess(Request(), messages, Tools()).Complete);
    }

    [Fact]
    public void RepeatedFailureKeepsPersistentAlternativePlansAndBoundedBackoffWithoutAnyTerminalLimit()
    {
        var messages = SuccessfulWork();
        AddVerification(messages, false, pdfReady: false);
        var first = ScientificRunCompletionPolicy.Assess(Request(), messages, Tools());
        Assert.Equal(TimeSpan.Zero, ScientificRunCompletionPolicy.RetryDelay(first));
        var initialPrompt = ScientificRunCompletionPolicy.RepairPrompt(first);
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            var previous = ScientificRunCompletionPolicy.Assess(Request(), messages, Tools());
            messages.Add(new("system", ScientificRunCompletionPolicy.RepairPrompt(previous)));
        }
        var restored = ScientificRunCompletionPolicy.Assess(Request(), messages.ToArray(), Tools());

        Assert.False(restored.Complete);
        Assert.Equal(8, restored.RepeatedAttempts);
        Assert.Equal(TimeSpan.FromSeconds(30), ScientificRunCompletionPolicy.RetryDelay(restored));
        Assert.NotEqual(initialPrompt, ScientificRunCompletionPolicy.RepairPrompt(restored));
    }

    [Fact]
    public void RecoveryCounterSurvivesNativeSystemToUserNormalization()
    {
        var messages = SuccessfulWork();
        AddVerification(messages, false, pdfReady: false);
        for (var index = 0; index < 2; index++)
            messages.Add(new("system", ScientificRunCompletionPolicy.RepairPrompt(
                ScientificRunCompletionPolicy.Assess(Request(), messages, Tools()))));
        var normalized = ModelRuntimeClient.PrepareLanguageBoundMessages(messages).ToList();
        var restored = ScientificRunCompletionPolicy.Assess(Request(), normalized, Tools());

        Assert.Equal(2, restored.RepeatedAttempts);
        ScientificRunCompletionPolicy.UpsertRecoveryPrompt(normalized, restored);
        var renormalized = ModelRuntimeClient.PrepareLanguageBoundMessages(normalized).ToList();
        var next = ScientificRunCompletionPolicy.Assess(Request(), renormalized, Tools());
        Assert.Equal(3, next.RepeatedAttempts);
        Assert.Equal(TimeSpan.FromSeconds(30), ScientificRunCompletionPolicy.RetryDelay(next));
        Assert.Single(renormalized, message => message.Content?.Contains(ScientificRunCompletionPolicy.RecoveryMarker, StringComparison.Ordinal) == true);
    }

    [Fact]
    public void OneThousandRecoveryAttemptsKeepOneBoundedPersistentPrompt()
    {
        var messages = SuccessfulWork();
        AddVerification(messages, false, pdfReady: false);
        var originalCount = messages.Count;
        var originalTokens = ContextPlanner.EstimateTokens(messages);
        for (var index = 0; index < 1000; index++)
            ScientificRunCompletionPolicy.UpsertRecoveryPrompt(messages,
                ScientificRunCompletionPolicy.Assess(Request(), messages, Tools()));
        var restored = ScientificRunCompletionPolicy.Assess(Request(), messages.ToArray(), Tools());

        Assert.Equal(1000, restored.RepeatedAttempts);
        Assert.Equal(originalCount + 1, messages.Count);
        Assert.InRange(ContextPlanner.EstimateTokens(messages) - originalTokens, 1, 1000);
        Assert.Equal(TimeSpan.FromSeconds(30), ScientificRunCompletionPolicy.RetryDelay(restored));
        Assert.False(restored.Complete);
    }

    [Fact]
    public void LegacyRecoveryMessagesMigrateWithoutTouchingQuotedOrToolContent()
    {
        var messages = SuccessfulWork();
        AddVerification(messages, false, pdfReady: false);
        var assessment = ScientificRunCompletionPolicy.Assess(Request(), messages, Tools());
        var legacy = ScientificRunCompletionPolicy.RecoveryMarker + "\n" + assessment.Fingerprint + "\nAlte Reparaturanweisung";
        messages.Add(new("system", legacy));
        messages.Add(new("user", "Missum-Laufanweisung:\n" + legacy));
        messages.Add(new("assistant", "Beispiel:\n" + legacy));
        messages.Add(new("user", "Bitte untersuche dieses Zitat:\n" + legacy));
        messages.Add(new("tool", legacy, ToolCallId: "unrelated-call"));
        var restored = ScientificRunCompletionPolicy.Assess(Request(), messages, Tools());

        Assert.Equal(2, restored.RepeatedAttempts);
        ScientificRunCompletionPolicy.UpsertRecoveryPrompt(messages, restored);

        Assert.Equal(3, ScientificRunCompletionPolicy.Assess(Request(), messages, Tools()).RepeatedAttempts);
        Assert.Contains(messages, message => message.Role == "assistant" && message.Content == "Beispiel:\n" + legacy);
        Assert.Contains(messages, message => message.Role == "user" && message.Content == "Bitte untersuche dieses Zitat:\n" + legacy);
        Assert.Contains(messages, message => message.Role == "tool" && message.Content == legacy);
    }

    [Fact]
    public void OrdinaryDeepResearchWithSandboxButNoSciencePresentationIsUnaffected()
    {
        var request = Request() with { ClientCapabilities = ["coding", "workspace", "research.sandbox"] };
        Assert.False(ScientificRunCompletionPolicy.Applies(request));
        Assert.True(ScientificRunCompletionPolicy.Assess(request, [], []).Complete);
        request = request with { Messages = [new("user", [new("text", RunProcessor.ScienceSessionContextStart + "\nGespeicherter Kontext")])] };
        Assert.True(ScientificRunCompletionPolicy.Applies(request));
    }

    internal static List<LmChatMessage> SuccessfulWork()
    {
        var messages = new List<LmChatMessage> { new("assistant", Manuscript) };
        AddReceipt(messages, ClientToolNames.ResearchCodeWrite, new { projectId = Project, path = "work/model.py", content = "print(42)" }, new { success = true });
        AddPython(messages, true);
        return messages;
    }

    private static void AddPython(List<LmChatMessage> messages, bool success) => AddReceipt(messages, ClientToolNames.ResearchCodeExecute,
        new { projectId = Project, experimentId = Experiment, executable = "python", arguments = PythonArguments },
        new { success, runs = new[] { new { exitCode = success ? 0 : 1, timedOut = false } } });

    internal static void AddVerification(List<LmChatMessage> messages, bool success, string projectId = Project,
        bool pdfReady = true, bool simulationReady = true, bool executed = true, bool hashes = true, DateTimeOffset? artifactModifiedAt = null) =>
        AddReceipt(messages, ClientToolNames.ResearchDeliverablesVerify, new { projectId },
            new { success, projectId, retryable = !success, publication = new { ready = pdfReady, pdfPath = "publications/Publikation.pdf", sourceSha256 = hashes ? new string('a', 64) : "", error = pdfReady ? "" : "PDF-Renderfehler" },
                simulation = new { ready = simulationReady, executed, artifacts = new[] { new { path = "artifacts/result.png", sha256 = hashes ? new string('b', 64) : "",
                    experimentRecordId = "experiment-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectId + "\nexperiment\n" + Experiment))).ToLowerInvariant()[..24],
                    lastModifiedAt = artifactModifiedAt,
                    scriptPath = "work/model.py", scriptSha256 = hashes ? new string('c', 64) : "" } } } });

    private static void AddReceipt(List<LmChatMessage> messages, string tool, object arguments, object result)
    {
        var id = "call-" + Guid.NewGuid().ToString("N");
        messages.Add(new("assistant", null, ToolCalls: [new(id, tool, JsonSerializer.SerializeToElement(arguments))]));
        messages.Add(new("tool", JsonSerializer.Serialize(new { status = "completed", result }), ToolCallId: id));
    }
}
