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
