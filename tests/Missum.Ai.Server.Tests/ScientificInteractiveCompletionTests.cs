using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class ScientificInteractiveCompletionTests
{
    private static readonly string[] Capabilities = ["research.deliverables"];

    [Fact]
    public void CanonicalInteractiveSourceArtifactCanCompleteWithoutAPythonExecutionClaim()
    {
        Assert.True(Assess(Simulation()).Complete);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("scope")]
    [InlineData("project")]
    [InlineData("source")]
    [InlineData("executed")]
    [InlineData("missing")]
    public void InvalidInteractiveSourceArtifactCannotComplete(string failure)
    {
        Assert.False(Assess(Simulation(failure: failure)).Complete);
    }

    [Fact]
    public void InteractiveDeliveryDoesNotReplaceRequiredScientificExecution()
    {
        Assert.False(Assess(Simulation(executionRequired: true)).Complete);
        Assert.True(Assess(Simulation(executionRequired: true, executed: true)).Complete);
    }

    [Fact]
    public void ReceiptsWithoutKindAwareDeclarationsRetainTheExecutedFigureGate()
    {
        Assert.False(Assess(new { required = true, ready = true, executed = false, interactiveReady = true }).Complete);
        Assert.True(Assess(new { required = true, ready = true, executed = true }).Complete);
        Assert.False(Assess(new { required = true, ready = true, executed = false,
            executionRequired = false, interactiveRequired = false }).Complete);
    }

    private static object Simulation(bool executionRequired = false, bool executed = false, string? failure = null) => new
    {
        required = true, ready = true, executed, executionRequired, interactiveRequired = true, interactiveReady = true,
        interactiveArtifacts = failure == "missing" ? Array.Empty<object>() : new[] { new
        {
            kind = "interactive", projectId = failure == "project" ? "other-project" : "research-state",
            artifactPath = failure == "scope" ? "work/../../other/animation.html" : "work/animation.html",
            sha256 = failure == "hash" ? "invalid" : new string('b', 64),
            sourceArtifact = failure != "source", executed = failure == "executed", contentType = "text/html",
        } },
    };

    private static ScientificCompletionAssessment Assess(object simulation)
    {
        var request = new RunRequest(MissumAiProtocol.Version, RunMode.General,
            [new("user", [new("text", Text: "Erstelle eine interaktive Echtzeit-Simulation.")])],
            ClientCapabilities: Capabilities, ResearchOptions: new(ProtocolVersion: 2, ProjectId: "research-state"));
        var call = new LmToolCall("verify", ClientToolNames.ResearchDeliverablesVerify,
            JsonSerializer.SerializeToElement(new { projectId = "research-state" }));
        var messages = new List<LmChatMessage> { new("assistant", ToolCalls: [call]), new("tool", JsonSerializer.Serialize(new
        {
            status = "completed", result = new
            {
                success = true, projectId = "research-state",
                research = new { protocol = "section-delta-v1", revision = 2, publicationRevision = 1, ready = true },
                publication = new { ready = true, revision = 1, pdfPath = "Publikation.pdf", sourceSha256 = new string('a', 64) },
                simulation,
            },
        }), ToolCallId: "verify") };
        return ScientificStateCompletionPolicy.Assess(request, messages, new AgentToolCatalog().GetAvailableTools(request));
    }
}
