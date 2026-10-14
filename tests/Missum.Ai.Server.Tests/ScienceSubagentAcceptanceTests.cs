using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Gateway;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;
using Missum.Ai.Server.Core.Runs;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class ScienceSubagentAcceptanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedScienceChildReturnsReusableEvidenceWithoutDeclaringScientificAcceptance(bool science)
    {
        using var context = new TestServerContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMissumAiServerServices(context.Options, includeHostedServices: false);
        services.AddSingleton(context.Database);
        using var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<RunRepository>();
        var processor = provider.GetRequiredService<RunProcessor>();
        var request = Request(science);
        var parent = (await repository.CreateAsync(request, null)).Snapshot;
        var childRequest = request with { SessionId = "child-session", Subagent = new(parent.RunId,
            "reference-agent", "Calculate an independent reference.", request.SessionId) };
        var child = await repository.CreateSubagentAsync(childRequest, new([], 0, 0, 0, 0), "reference-operation");
        await repository.AppendEventAsync(child.RunId, RunEventTypes.TextDelta,
            new TextDeltaEvent("Norm preserved, but fringe spacing does not satisfy the criterion."));
        await repository.UpdateStateAsync(child.RunId, RunState.Completed);

        var receipt = await processor.ExecuteSubagentToolAsync(SubagentToolNames.Wait,
            JsonSerializer.SerializeToElement(new { runId = child.RunId }), parent.RunId, "wait-result", request,
            new("coding/fixture", "coding", 32768), [], CancellationToken.None);

        Assert.True(receipt.Succeeded); // Workflow completion remains reusable.
        Assert.Equal("completed", receipt.Result.GetProperty("status").GetString());
        Assert.Contains("does not satisfy", receipt.Result.GetProperty("result").GetString());
        Assert.Equal(science, receipt.Result.GetProperty("scientificReviewRequired").GetBoolean());
        Assert.Empty(receipt.Result.GetProperty("completionEvidence").EnumerateArray());
        List<LmChatMessage> messages = [new("system", "Original policy"), new("tool", receipt.Result.GetRawText(), ToolCallId: "wait-result")];
        Assert.True(RunProcessor.AppendSubagentResultAcceptanceInstruction(messages, receipt.Result));
        if (science)
        {
            Assert.Equal(RunProcessor.ScientificSubagentAcceptanceInstruction,
                receipt.Result.GetProperty("instruction").GetString());
            Assert.Contains("nicht ihre fachliche Richtigkeit", messages[^1].Content);
            Assert.Contains("tatsächlich gespeicherten Ergebnisartefakte", messages[^1].Content);
            Assert.Contains("dieselbe Observable", messages[^1].Content);
            Assert.Contains("image.input bestätigt nur", messages[^1].Content);
            Assert.Contains("media.analyze", messages[^1].Content);
        }
        else Assert.EndsWith(SubagentAgentPolicy.CompletedWork, messages[^1].Content);
        Assert.Equal(RunState.Completed, (await repository.GetAsync(child.RunId))!.State);
        Assert.DoesNotContain(await repository.GetEventsAfterAsync(child.RunId, 0), item => item.Type == RunEventTypes.ModelGeneration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveredScienceReceiptGetsOneReviewInstructionWithoutRewritingEvaluatedPrefix(bool compact)
    {
        var receipt = JsonSerializer.SerializeToElement(new { status = "completed", runId = "old-child", result = "Reference remains inconsistent." });
        List<LmChatMessage> messages = [new("system", compact ? CompactAgentContextPolicy.Marker : "Existing policy"),
            new("tool", receipt.GetRawText(), ToolCallId: "wait"),
            new("system", "[MISSUM_SUBAGENT_RESULT_ACCEPTED:old-child]\n" + SubagentAgentPolicy.CompletedWork)];
        var prefix = messages.ToArray();

        Assert.True(RunProcessor.AppendSubagentResultAcceptanceInstruction(messages, receipt, Request(true)));
        Assert.Equal(prefix, messages.Take(prefix.Length));
        Assert.EndsWith(RunProcessor.ScientificSubagentAcceptanceInstruction, messages[^1].Content);
        Assert.False(RunProcessor.AppendSubagentResultAcceptanceInstruction(messages, receipt, Request(true)));
        Assert.Equal(prefix.Length + 1, messages.Count);
    }

    [Fact]
    public void ScienceForkRequiresCheckingAssignedEquationsAndReportsUnmetCriteria()
    {
        IReadOnlyList<LmChatMessage> original = [new("system", "Preserved exact parent prefix"), new("user", "Research task")];
        var fork = RunProcessor.ForkSubagentMessages(original, "Use the supplied propagation convention.", "reference-agent", scientificResearch: true);
        Assert.Equal(original, fork.Take(original.Count));
        Assert.Contains("vorgegebenen Formeln, Einheiten", fork[^1].Content);
        Assert.Contains("Widerspruch im Auftrag", fork[^1].Content);
        Assert.Contains("unerfüllte Kriterien", fork[^1].Content);
        Assert.Contains("reference-agent", fork[^1].Content);
    }

    private static RunRequest Request(bool science) => new(MissumAiProtocol.Version, RunMode.General,
        [new("user", [new("text", "Quantitative double-slit research.")])], SessionId: "parent-session",
        ClientCapabilities: science ? ["research.deliverables", "subagents"] : ["subagents"],
        ResearchOptions: science ? new(ProjectId: "science-project", ProtocolVersion: 2) : null);
}
