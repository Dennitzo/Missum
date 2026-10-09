using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class ScientificStateRecoveryTests
{
    private static readonly string[] Capabilities = ["research.deliverables"];
    private static readonly string[] FirstMissing = ["Herleitung fehlt."];
    private static readonly string[] ChangedMissing = ["Die neue Formel ist ungültig."];
    private static RunRequest Request() => new(MissumAiProtocol.Version, RunMode.General,
        [new("user", [new("text", Text: "Untersuche den theoretischen Zusammenhang.")])],
        ClientCapabilities: Capabilities, ResearchOptions: new(ProtocolVersion: 2, ProjectId: "research-state"));
    private static ScientificCompletionAssessment Assess(IReadOnlyList<LmChatMessage> messages) =>
        ScientificStateCompletionPolicy.Assess(Request(), messages, new AgentToolCatalog().GetAvailableTools(Request()));

    [Fact]
    public void UnchangedDiagnosisIsNotAppendedOrPublishedAgainAfterNativeRecovery()
    {
        var messages = new List<LmChatMessage> { new("system", "Stable system prefix"), new("user", "Auftrag") };
        var first = Assess(messages);
        Assert.False(first.Complete);
        Assert.True(ScientificStateCompletionPolicy.UpsertRecoveryPrompt(messages, first));
        var normalized = ModelRuntimeClient.NormalizeMessageOrderForNativeRuntime(messages).ToList();
        var original = normalized.ToArray();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var current = Assess(normalized);
            Assert.Equal(first.Fingerprint, current.Fingerprint);
            Assert.False(ScientificStateCompletionPolicy.UpsertRecoveryPrompt(normalized, current));
        }
        Assert.Equal(original, normalized); // Return value gates the visible diagnosis.
        Assert.Equal("Stable system prefix", normalized[0].Content);
        Assert.Single(normalized, message => message.Content?.Contains(ScientificStateCompletionPolicy.RecoveryMarker,
            StringComparison.Ordinal) == true);
    }

    [Fact]
    public void ChangedVerifiedScientificDiagnosisIsAppendedAndCanBePublished()
    {
        var messages = new List<LmChatMessage> { new("system", "Stable system prefix"), new("user", "Auftrag") };
        var first = Assess(messages);
        Assert.True(ScientificStateCompletionPolicy.UpsertRecoveryPrompt(messages, first));
        messages = ModelRuntimeClient.NormalizeMessageOrderForNativeRuntime(messages).ToList();
        AddVerification(messages, "verify-one", FirstMissing);
        var verified = Assess(messages);
        Assert.NotEqual(first.Fingerprint, verified.Fingerprint);
        Assert.Contains("Herleitung fehlt.", verified.Missing);
        Assert.True(ScientificStateCompletionPolicy.UpsertRecoveryPrompt(messages, verified));
        Assert.False(ScientificStateCompletionPolicy.UpsertRecoveryPrompt(messages, Assess(messages)));

        messages = ModelRuntimeClient.NormalizeMessageOrderForNativeRuntime(messages).ToList();
        AddVerification(messages, "verify-two", ChangedMissing);
        var changed = Assess(messages);
        Assert.NotEqual(verified.Fingerprint, changed.Fingerprint);
        Assert.Contains("Die neue Formel ist ungültig.", changed.Missing);
        Assert.True(ScientificStateCompletionPolicy.UpsertRecoveryPrompt(messages, changed));
        Assert.Equal("Stable system prefix", messages[0].Content);
        Assert.Equal(2, messages.Count(static message => message.Role == "tool"));
    }

    [Fact]
    public void FailedReviewProvidesAMetadataRepairBeforeTheNextModelTurnAndIsIdempotentOnRestore()
    {
        var messages = new List<LmChatMessage> { new("system", "Stable system prefix"), new("user", "Auftrag") };
        var (call, content) = AddStatusVerification(messages, "first-status-check", 3);

        Assert.True(ScientificStateCompletionPolicy.AppendVerificationRepairPrompt(messages, Request(), call, content));
        Assert.Contains("changes[].patch", messages[^1].Content);
        Assert.Contains("data.status", messages[^1].Content);
        Assert.Contains("sec-intro", messages[^1].Content);
        Assert.Equal(1, Assess(messages).RepeatedAttempts);
        messages = ModelRuntimeClient.NormalizeMessageOrderForNativeRuntime(messages).ToList();
        var prefix = messages.ToArray();

        Assert.False(ScientificStateCompletionPolicy.AppendVerificationRepairPrompt(messages, Request(), call, content));
        Assert.Equal(prefix, messages);
        Assert.Equal("Stable system prefix", messages[0].Content);
    }

    [Fact]
    public void RepeatedReviewCauseSurvivesChangingRevisionAndPdfPathAndTargetsTheMissingField()
    {
        var messages = new List<LmChatMessage> { new("system", "Stable system prefix"), new("user", "Auftrag") };
        var (first, firstContent) = AddStatusVerification(messages, "status-check-one", 3);
        Assert.True(ScientificStateCompletionPolicy.AppendVerificationRepairPrompt(messages, Request(), first, firstContent));
        messages = ModelRuntimeClient.NormalizeMessageOrderForNativeRuntime(messages).ToList();
        var (next, nextContent) = AddStatusVerification(messages, "status-check-two", 4);

        Assert.True(ScientificStateCompletionPolicy.AppendVerificationRepairPrompt(messages, Request(), next, nextContent));
        Assert.Contains("Dieselben Prüfursachen", messages[^1].Content);
        Assert.Contains("Attempt: 2", messages[^1].Content);
        Assert.Equal(2, Assess(messages).RepeatedAttempts);
        Assert.False(Assess(messages).Complete);
        Assert.Equal("Stable system prefix", messages[0].Content);
    }

    [Fact]
    public void ANewReviewCauseStartsANewRepairWithoutRevisitingFixedFields()
    {
        var messages = new List<LmChatMessage> { new("system", "Stable system prefix"), new("user", "Auftrag") };
        var (first, firstContent) = AddStatusVerification(messages, "status-check-one", 3);
        Assert.True(ScientificStateCompletionPolicy.AppendVerificationRepairPrompt(messages, Request(), first, firstContent));
        var (next, nextContent) = AddStatusVerification(messages, "status-check-two", 4, "missing_evidence", "data.evidenceIds");

        Assert.True(ScientificStateCompletionPolicy.AppendVerificationRepairPrompt(messages, Request(), next, nextContent));
        Assert.Contains("Attempt: 1", messages[^1].Content);
        Assert.Equal(1, Assess(messages).RepeatedAttempts);
        Assert.False(Assess(messages).Complete);
    }

    [Fact]
    public void LegacyReviewRevisionAnnotationsDoNotResetTheRepairCause()
    {
        var messages = new List<LmChatMessage>();
        AddVerification(messages, "legacy-one", ["Fachliche Prüfung sec-intro (Revision 3): Status fehlt."]);
        var firstCall = messages[0].ToolCalls![0];
        Assert.True(ScientificStateCompletionPolicy.AppendVerificationRepairPrompt(messages, Request(), firstCall, messages[1].Content!));
        AddVerification(messages, "legacy-two", ["Fachliche Prüfung sec-intro (Revision 4): Status fehlt."]);
        var nextCall = messages[^2].ToolCalls![0];

        Assert.True(ScientificStateCompletionPolicy.AppendVerificationRepairPrompt(messages, Request(), nextCall, messages[^1].Content!));
        Assert.Contains("Attempt: 2", messages[^1].Content);
        Assert.Equal(2, Assess(messages).RepeatedAttempts);
    }

    [Fact]
    public void SuccessfulOrForeignVerificationAddsNoRepairGuidance()
    {
        var messages = new List<LmChatMessage>();
        var (call, content) = AddStatusVerification(messages, "status-check", 3);
        Assert.False(ScientificStateCompletionPolicy.AppendVerificationRepairPrompt(messages, Request(), call,
            content.Replace("\"success\":false", "\"success\":true", StringComparison.Ordinal)));
        Assert.False(ScientificStateCompletionPolicy.AppendVerificationRepairPrompt(messages, Request(), call,
            content.Replace("research-state", "foreign-project", StringComparison.Ordinal)));
        Assert.Equal(2, messages.Count);
    }

    private static (LmToolCall Call, string Content) AddStatusVerification(List<LmChatMessage> messages, string id,
        int revision, string code = "status_missing", string field = "data.status")
    {
        var call = new LmToolCall(id, ClientToolNames.ResearchDeliverablesVerify,
            JsonSerializer.SerializeToElement(new { projectId = "research-state" }));
        var content = JsonSerializer.Serialize(new { status = "completed", result = new
        {
            success = false, projectId = "research-state",
            research = new { protocol = "section-delta-v1", revision, publicationRevision = revision, ready = false,
                missing = new[] { $"Fachliche Prüfung sec-intro (Revision {revision}): Status fehlt." } },
            scientificReview = new { ready = false, issues = new[] { new
            {
                id = "sec-intro", revision, code, field, currentValue = "", message = "Status fehlt.",
                nextAction = "changes[].patch: fachlich erreichten Status und review mit vorhandenen Belegen setzen.",
            } } },
            publication = new { ready = true, revision, pdfPath = $"r{revision}/publication.pdf", sourceSha256 = new string('a', 64) },
            simulation = new { required = false },
        } });
        messages.Add(new("assistant", ToolCalls: [call]));
        messages.Add(new("tool", content, ToolCallId: id));
        return (call, content);
    }

    private static void AddVerification(List<LmChatMessage> messages, string id, string[] missing)
    {
        var call = new LmToolCall(id, ClientToolNames.ResearchDeliverablesVerify,
            JsonSerializer.SerializeToElement(new { projectId = "research-state" }));
        messages.Add(new("assistant", ToolCalls: [call]));
        messages.Add(new("tool", JsonSerializer.Serialize(new { status = "completed", result = new
        {
            success = false, projectId = "research-state",
            research = new { protocol = "section-delta-v1", revision = 3, publicationRevision = 2, ready = false, missing },
            publication = new { ready = true, revision = 2, pdfPath = "publication.pdf", sourceSha256 = new string('a', 64) },
            simulation = new { required = false },
        } }), ToolCallId: id));
    }
}
