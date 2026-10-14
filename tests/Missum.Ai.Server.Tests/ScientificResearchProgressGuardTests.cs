using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Missum.Ai.Server.Tests;

public sealed class ScientificResearchProgressGuardTests
{
    private const string QuotaMessage = "Der eingefrorene Ausführungsstand überschreitet das 100-GiB-Sandboxkontingent; es wurden keine Eingaben kopiert.";
    private static readonly string[] ChangedSectionIds = ["section"];
    private static readonly string[] VisualReviewMissing = ["Der aktuelle Plot besitzt noch keinen Sichtprüfungsbeleg."];

    [Fact]
    public void ActualQuotaReceiptHasOneCauseAcrossScriptsParametersAndExecutionTools()
    {
        var messages = new List<LmChatMessage>();
        var progress = Observe(new(), Call("first.py", 100), QuotaReceipt(), messages);
        progress = Observe(progress, Call("different.py", 10000, ClientToolNames.ResearchCodeTest), QuotaReceipt(), messages);
        Assert.Equal(2, Assert.Single(progress.Failures!).Count);
        Assert.True(Assert.Single(progress.Failures!).PrerequisiteBlocked);
        Assert.Contains("kein neues Rechenergebnis", Assert.Single(messages).Content);

        Assert.True(ScientificResearchProgressGuard.TryBlock(Request(),
            Call("third.py", 20000, ClientToolNames.ResearchCodeBenchmark), progress, out var blocked));
        Assert.True(blocked!.PrerequisiteBlocked);
        Assert.Contains("nicht gestartet", blocked.Message);
        Assert.False(ScientificResearchProgressGuard.TryBlock(Request(),
            Call("read.py", 1, ClientToolNames.ResearchRead), progress, out _));
        Assert.False(ScientificResearchProgressGuard.TryBlock(Request(),
            Call("repair.py", 1, ClientToolNames.ResearchCodeWrite), progress, out _));
        Assert.False(ScientificResearchProgressGuard.TryBlock(Request(),
            Call("formula", 1, ClientToolNames.MathSymbolic), progress, out _));
    }

    [Fact]
    public void MetadataUpdatesMathAndFileWritesDoNotDischargeQuota()
    {
        var messages = new List<LmChatMessage>();
        var progress = QuotaBlocked(messages);
        progress = Observe(progress, Call("new-section", 1, ClientToolNames.ResearchUpdate),
            Success(new { success = true, projectId = "science-project", protocol = "section-delta-v1",
                revision = 10, changedIds = ChangedSectionIds }), messages, true);
        progress = Observe(progress, Call("formula", 1, ClientToolNames.MathSymbolic),
            Success(new { success = true }), messages, true);
        progress = Observe(progress, Call("repair.py", 1, ClientToolNames.ResearchCodeWrite),
            Success(new { success = true, beforeSha256 = new string('a', 64), afterSha256 = new string('b', 64) }), messages, true);
        Assert.True(ScientificResearchProgressGuard.TryBlock(Request(), Call("still-blocked.py", 1), progress, out _));
        Assert.Equal(1, progress.ExecutionEnvironmentRevision);
        Assert.Single(messages);
    }

    [Fact]
    public void OnlySuccessfulExecutionDischargesExecutionPrerequisite()
    {
        var messages = new List<LmChatMessage>();
        var progress = QuotaBlocked(messages);
        progress = Observe(progress, Call("repaired.py", 1), Success(new { success = true,
            runs = new[] { new { runId = "measured-run", exitCode = 0, timedOut = false } } }), messages, true);
        Assert.Empty(progress.Failures!);
        Assert.False(ScientificResearchProgressGuard.TryBlock(Request(), Call("next.py", 1), progress, out _));
    }

    [Fact]
    public void NominalSuccessWithoutActualProcessReceiptCannotDischargeQuota()
    {
        var messages = new List<LmChatMessage>();
        var progress = QuotaBlocked(messages);
        progress = Observe(progress, Call("claimed.py", 1), Success(new { success = true }), messages, true);
        Assert.True(ScientificResearchProgressGuard.TryBlock(Request(), Call("still-blocked.py", 1), progress, out _));
    }

    [Fact]
    public void RepeatedRefusalsAreCheckpointedAndHaveBoundedFeedbackBeforeAttentionIsRequired()
    {
        var messages = new List<LmChatMessage>();
        var progress = QuotaBlocked(messages);
        for (var attempt = 0; attempt < ScientificResearchProgressGuard.MaximumBlockedAttempts; attempt++)
        {
            var call = Call("renamed-" + attempt + ".py", attempt + 1);
            Assert.True(ScientificResearchProgressGuard.TryBlock(Request(), call, progress, out var blocked));
            progress = Observe(progress, call, Rejected(blocked!), messages);
            // Simulate interruption/restart; no dependence on retained context.
            progress = JsonSerializer.Deserialize<ScientificStateRunProgress>(JsonSerializer.Serialize(progress))!;
            if (attempt + 1 < ScientificResearchProgressGuard.MaximumBlockedAttempts)
                ScientificResearchProgressGuard.ThrowIfStalled(progress);
        }
        Assert.Equal(2, messages.Count);
        Assert.Equal(2, Assert.Single(progress.Failures!).FeedbackCount);
        Assert.Equal(QuotaMessage, Assert.Single(progress.Failures!).LastError);
        var exception = Assert.Throws<ScientificResearchStalledException>(() =>
            ScientificResearchProgressGuard.ThrowIfStalled(progress));
        Assert.True(exception.PrerequisiteBlocked);
        Assert.Contains("offen geblieben", exception.Message);
    }

    [Fact]
    public void OrdinaryFailedExecutionCanProceedAfterMeasuredSourceCorrection()
    {
        var messages = new List<LmChatMessage>();
        var call = Call("broken.py", 1);
        const string failure = "{\"status\":\"failed\",\"errorCode\":\"research.execution_failed\",\"message\":\"Exit code 1.\"}";
        var progress = Observe(new(), call, failure, messages);
        progress = Observe(progress, call with { Id = "second" }, failure, messages);
        Assert.True(ScientificResearchProgressGuard.TryBlock(Request(), call, progress, out _));
        Assert.False(ScientificResearchProgressGuard.TryBlock(Request(), Call("different.py", 1), progress, out _));
        progress = Observe(progress, Call("broken.py", 1, ClientToolNames.ResearchCodeWrite),
            Success(new { success = true, beforeSha256 = new string('a', 64), afterSha256 = new string('b', 64) }), messages, true);
        Assert.False(ScientificResearchProgressGuard.TryBlock(Request(), call, progress, out _));
    }

    [Fact]
    public void SameByteFileWriteAndUnrelatedSuccessfulProbeDoNotManufactureRepair()
    {
        var messages = new List<LmChatMessage>();
        var call = Call("broken.py", 1);
        const string failure = "{\"status\":\"failed\",\"message\":\"Exit code 1.\"}";
        var progress = Observe(new(), call, failure, messages);
        progress = Observe(progress, call with { Id = "second" }, failure, messages);
        progress = Observe(progress, Call("broken.py", 1, ClientToolNames.ResearchCodeWrite),
            Success(new { success = true, beforeSha256 = new string('a', 64), afterSha256 = new string('a', 64) }), messages, true);
        progress = Observe(progress, Call("unrelated.py", 1), Success(new { success = true, exitCode = 0 }), messages, true);
        Assert.True(ScientificResearchProgressGuard.TryBlock(Request(), call, progress, out _));
        Assert.Equal(0, progress.ExecutionEnvironmentRevision);
    }

    [Fact]
    public void FailedVerificationMayBeRepeatedAfterCanonicalCorrection()
    {
        var messages = new List<LmChatMessage>();
        var call = VerificationCall();
        var progress = VerificationBlocked(messages);
        Assert.True(ScientificResearchProgressGuard.TryBlock(Request(), call, progress, out _));
        progress = Observe(progress, Call("new-section", 1, ClientToolNames.ResearchUpdate),
            Success(new { success = true, protocol = "section-delta-v1", projectId = "science-project",
                changedIds = ChangedSectionIds, publicationChanged = true }), messages, true);
        Assert.False(ScientificResearchProgressGuard.TryBlock(Request(), call, progress, out _));
    }

    [Fact]
    public void NoOpUpdatesReadOnlyProbesAndArbitraryToolSuccessDoNotResetFailedVerification()
    {
        var messages = new List<LmChatMessage>();
        var progress = VerificationBlocked(messages);
        progress = Observe(progress, Call("section", 1, ClientToolNames.ResearchUpdate), Success(new
            { success = true, protocol = "section-delta-v1", projectId = "science-project",
                changedIds = Array.Empty<string>(), publicationChanged = false }), messages, true);
        progress = Observe(progress, Call("section", 1, ClientToolNames.ResearchUpdate), Success(new
            { success = true, protocol = "section-delta-v1", projectId = "science-project",
                changedIds = ChangedSectionIds, publicationChanged = true, replayed = true }), messages, true);
        progress = Observe(progress, Call("read", 1, ClientToolNames.ResearchRead), Success(new { success = true }), messages, true);
        progress = Observe(progress, Call("unrelated", 1, ClientToolNames.MathSymbolic), Success(new { success = true }), messages, true);
        progress = Observe(progress, VisionCall(), Success(new { success = true, analysis = "Alles sieht richtig aus." }), messages, true);
        Assert.True(ScientificResearchProgressGuard.TryBlock(Request(), VerificationCall(), progress, out _));
        Assert.False(ScientificResearchProgressGuard.TryBlock(Request(),
            Call("read", 1, ClientToolNames.ResearchRead), progress, out _));
    }

    [Fact]
    public void ActualMeasuredExecutionAllowsAFreshVerificationInsteadOfRepeatingTheOldResult()
    {
        var messages = new List<LmChatMessage>();
        var progress = VerificationBlocked(messages);
        progress = Observe(progress, Call("new-measurement.py", 1), Success(new { success = true,
            runs = new[] { new { runId = "new-measured-attempt", exitCode = 0, timedOut = false } } }), messages, true);
        Assert.False(ScientificResearchProgressGuard.TryBlock(Request(), VerificationCall(), progress, out _));
    }

    [Fact]
    public void ActualVisualReviewAllowsFreshVerificationButDoesNotDischargeExecutionPrerequisites()
    {
        var messages = new List<LmChatMessage>();
        var progress = QuotaBlocked(messages);
        progress = VerificationBlocked(messages, progress);
        progress = Observe(progress, VisionCall(), Success(new { success = true, visualReview = VisualReview() }), messages, true);
        Assert.False(ScientificResearchProgressGuard.TryBlock(Request(), VerificationCall(), progress, out _));
        Assert.True(ScientificResearchProgressGuard.TryBlock(Request(), Call("still-quota-blocked.py", 2), progress, out _));
        Assert.True(Assert.Single(progress.Failures!).PrerequisiteBlocked);
    }

    [Theory]
    [InlineData("protocol")]
    [InlineData("exactIdentity")]
    [InlineData("sourceUploadId")]
    [InlineData("sourceSha256")]
    [InlineData("analysisSha256")]
    [InlineData("analysis")]
    [InlineData("modelId")]
    public void IncompleteOrTamperedVisualReviewCannotResetVerification(string field)
    {
        var messages = new List<LmChatMessage>();
        var progress = VerificationBlocked(messages);
        var review = JsonNode.Parse(VisualReview().GetRawText())!.AsObject();
        switch (field)
        {
            case "protocol": review[field] = "unknown-review-v1"; break;
            case "exactIdentity": review[field] = false; break;
            case "sourceUploadId": review[field] = "other-upload"; break;
            case "sourceSha256": review[field] = "invalid"; break;
            case "analysisSha256": review[field] = new string('f', 64); break;
            case "analysis": review[field] = "Nachträglich geänderter Befund."; break;
            case "modelId": review[field] = ""; break;
        }
        progress = Observe(progress, VisionCall(), Success(new { success = true, visualReview = review }), messages, true);
        Assert.True(ScientificResearchProgressGuard.TryBlock(Request(), VerificationCall(), progress, out _));
    }

    [Fact]
    public void OnlyChangedSourceBytesPermitAnotherVerificationAfterAFileWrite()
    {
        var messages = new List<LmChatMessage>();
        var progress = VerificationBlocked(messages);
        progress = Observe(progress, Call("same.py", 1, ClientToolNames.ResearchCodeWrite), Success(new
            { success = true, beforeSha256 = new string('a', 64), afterSha256 = new string('a', 64) }), messages, true);
        Assert.True(ScientificResearchProgressGuard.TryBlock(Request(), VerificationCall(), progress, out _));
        progress = Observe(progress, Call("changed.py", 1, ClientToolNames.ResearchCodeWrite), Success(new
            { success = true, beforeSha256 = new string('a', 64), afterSha256 = new string('b', 64) }), messages, true);
        Assert.False(ScientificResearchProgressGuard.TryBlock(Request(), VerificationCall(), progress, out _));
    }

    [Fact]
    public void StructuredVerificationDiagnosisIsPreservedInTheBlockedExplanation()
    {
        var messages = new List<LmChatMessage>();
        var failure = Success(new { success = false, scientificReview = new { missing = VisualReviewMissing } });
        var progress = Observe(new(), VerificationCall(), failure, messages);
        progress = Observe(progress, VerificationCall(), failure, messages);
        Assert.True(ScientificResearchProgressGuard.TryBlock(Request(), VerificationCall(), progress, out var blocked));
        Assert.Contains(VisualReviewMissing[0], blocked!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonSpacingCannotManufactureANewVerificationAttempt()
    {
        var messages = new List<LmChatMessage>();
        const string failure = "{\"status\":\"completed\",\"result\":{\"success\":false,\"message\":\"Plot requires actual analysis.\"}}";
        var compact = VerificationCall();
        using var spaced = JsonDocument.Parse("{ \"projectId\" : \"science-project\" }");
        var differentlyFormatted = compact with { Id = "different-format", Arguments = spaced.RootElement.Clone() };
        var progress = Observe(new(), compact, failure, messages);
        progress = Observe(progress, differentlyFormatted, failure, messages);
        Assert.True(ScientificResearchProgressGuard.TryBlock(Request(), VerificationCall(), progress, out _));
    }

    [Fact]
    public void ArbitraryLargeNumbersAndLegacyCheckpointDoNotCreateGlobalPrerequisites()
    {
        var call = Call("physical.py", 1);
        Assert.Null(ScientificResearchProgressGuard.PrerequisiteCause(call,
            "{\"status\":\"failed\",\"message\":\"100 GiB is a parameter in this model.\"}"));
        var legacy = JsonSerializer.Deserialize<ScientificStateRunProgress>(
            "{\"SourcesSinceProgress\":2,\"Revision\":3,\"Failures\":[{\"Fingerprint\":\"old\",\"Count\":2,\"LastHint\":\"old:3\"}]}")!;
        Assert.False(ScientificResearchProgressGuard.TryBlock(Request(), call, legacy, out _));
        Assert.Equal(0, legacy.ExecutionEnvironmentRevision);
    }

    [Fact]
    public void SecondaryErrorsCannotEvictAnUnresolvedPrerequisiteFromTheBoundedLedger()
    {
        var messages = new List<LmChatMessage>();
        var progress = QuotaBlocked(messages);
        for (var index = 0; index < 50; index++)
            progress = Observe(progress, Call("invalid-" + index, 1, ClientToolNames.ResearchCodeWrite),
                "{\"status\":\"failed\",\"message\":\"Different argument failure.\"}", messages);
        Assert.Equal(32, progress.Failures!.Count);
        Assert.Single(progress.Failures, static failure => failure.PrerequisiteBlocked);
        Assert.True(ScientificResearchProgressGuard.TryBlock(Request(), Call("still-blocked.py", 1), progress, out _));
    }

    [Theory]
    [InlineData("Das 100-GiB-Kontingent der aktiven Sandboxdateien ist ausgeschöpft. Historische Ausführungsstände werden nicht erneut als aktive Eingaben gezählt.")]
    [InlineData("Das 100-GiB-Kontingent für neue reproduzierbare Ausführungsstände ist ausgeschöpft. Frühere Belege bleiben erhalten; weitere Ausführung benötigt Speicherbereinigung oder einen anderen Projektordner.")]
    [InlineData("Das Kontingent für neue reproduzierbare Ausführungsstände ist ausgeschöpft. Historische Belege wurden erhalten; dieselbe Berechnung darf ohne Speicherbereinigung oder anderen Projektordner nicht wiederholt werden.")]
    [InlineData("Auf dem Projektlaufwerk reicht der freie Speicher für den unabhängigen Ausführungsstand und 2 GiB Reserve nicht aus. Es wurden keine Eingaben kopiert; dieselbe Berechnung benötigt erst freien Speicher oder einen anderen Projektordner.")]
    [InlineData("Der vollständige Ausführungsstand besitzt mehr als 4096 Eingabedateien; es wurden keine Eingaben kopiert.")]
    public void RepairedSandboxPrerequisiteMessagesRemainGlobalAcrossParameterChanges(string error)
    {
        var messages = new List<LmChatMessage>();
        var receipt = JsonSerializer.Serialize(new { status = "failed", errorCode = "client.tool_failed", message = error });
        var progress = Observe(new(), Call("first.py", 1), receipt, messages);
        progress = Observe(progress, Call("second.py", 2), receipt, messages);
        Assert.True(ScientificResearchProgressGuard.TryBlock(Request(), Call("third.py", 3), progress, out _));
        Assert.True(Assert.Single(progress.Failures!).PrerequisiteBlocked);
    }

    private static ScientificStateRunProgress QuotaBlocked(List<LmChatMessage> messages)
    {
        var progress = Observe(new(), Call("first.py", 1), QuotaReceipt(), messages);
        return Observe(progress, Call("second.py", 2), QuotaReceipt(), messages);
    }

    private static ScientificStateRunProgress VerificationBlocked(List<LmChatMessage> messages,
        ScientificStateRunProgress? existing = null)
    {
        const string failure = "{\"status\":\"completed\",\"result\":{\"success\":false,\"message\":\"Numerical goal remains open.\"}}";
        var progress = Observe(existing ?? new(), VerificationCall(), failure, messages);
        return Observe(progress, VerificationCall(), failure, messages);
    }

    private static LmToolCall VerificationCall() => new(Guid.NewGuid().ToString("N"), ClientToolNames.ResearchDeliverablesVerify,
        JsonSerializer.SerializeToElement(new { projectId = "science-project" }));

    private static LmToolCall VisionCall() => new(Guid.NewGuid().ToString("N"), "media.analyze",
        JsonSerializer.SerializeToElement(new { uploadId = "upload-current-plot", prompt = "Prüfe Achsen und Randartefakte." }));

    private static JsonElement VisualReview() => AgentToolExecutor.CreateVisualReviewReceipt(new(
        "upload-current-plot", "plot.png", "image/png", 100, new string('a', 64), DateTimeOffset.UtcNow.AddDays(1)),
        "upload-current-plot", "coding/DeepSeek~current", "Die Achsen stimmen; ein Randartefakt ist sichtbar.")!.Value;

    private static ScientificStateRunProgress Observe(ScientificStateRunProgress progress, LmToolCall call,
        string content, List<LmChatMessage> messages, bool succeeded = false) =>
        RunProcessor.ReduceScientificStateReceipt(Request(), call, content, succeeded, progress, messages).Progress;

    private static string QuotaReceipt() => JsonSerializer.Serialize(new
    {
        proposalId = "proposal-example", status = "failed", result = new { failed = true },
        errorCode = "client.tool_failed", message = QuotaMessage,
    });

    private static string Success(object result) => JsonSerializer.Serialize(new { status = "completed", result });

    private static string Rejected(ScientificResearchToolRejection rejection) => JsonSerializer.Serialize(new
    {
        status = "not_executed", errorCode = ScientificResearchProgressGuard.RejectionCode,
        scienceFailureFingerprint = rejection.Fingerprint, message = rejection.Message,
    });

    private static LmToolCall Call(string script, int particles, string tool = ClientToolNames.ResearchCodeExecute) =>
        new(Guid.NewGuid().ToString("N"), tool, JsonSerializer.SerializeToElement(new
        { projectId = "science-project", executable = "python", arguments = new[] { script, particles.ToString(CultureInfo.InvariantCulture) } }));

    private static RunRequest Request() => new(MissumAiProtocol.Version, RunMode.General,
        [new("user", [new("text", "Quantitative research.")])],
        ClientCapabilities: ["research.deliverables"], ResearchOptions: new(ProjectId: "science-project", ProtocolVersion: 2));
}
