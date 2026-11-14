using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class ComputerToolBoundaryTests
{
    private static ToolProposal Proposal(string name, object args, ToolRiskClass risk) => new(
        "proposal-1", "run-1", name, JsonSerializer.SerializeToElement(args), risk,
        "Test", DateTimeOffset.UtcNow.AddMinutes(1));

    [Fact]
    public async Task ComputerProposalsAreRejectedUnlessTheRunExplicitlyEnabledThem()
    {
        var broker = new LocalToolBroker(null!, null!, null!, null!);
        var result = await broker.ExecuteForAgentAsync(Proposal("computer.observe", new { }, ToolRiskClass.ReadOnly),
            Guid.NewGuid(), null, runAction: PromptTriggerAction.Coding);
        Assert.Equal("failed", result.Status);
        Assert.Contains("nicht freigegeben", result.Message);
    }

    [Fact]
    public async Task ComputerRunRejectsOtherLocalToolsAtExecutionBoundary()
    {
        var broker = new LocalToolBroker(null!, null!, null!, null!);
        var result = await broker.ExecuteForAgentAsync(Proposal("coding.command", new { command = "ignored" }, ToolRiskClass.Process),
            Guid.NewGuid(), null, runAction: PromptTriggerAction.Coding, computerUseEnabled: true);
        Assert.Equal("failed", result.Status);
        Assert.Contains("gesperrt", result.Message);
    }

    [Fact]
    public void ValidatesRiskClassAndUnexpectedComputerArguments()
    {
        LocalToolBroker.ValidateProposal(Proposal("computer.observe", new { }, ToolRiskClass.ReadOnly));
        LocalToolBroker.ValidateProposal(Proposal("computer.observe", new { scope = "window" }, ToolRiskClass.ReadOnly));
        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(
            Proposal("computer.observe", new { scope = "screen" }, ToolRiskClass.ReadOnly)));
        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(
            Proposal("computer.observe", new { unrelated = true }, ToolRiskClass.ReadOnly)));
        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(
            Proposal("computer.act", new { observationId = "frame", kind = "click" }, ToolRiskClass.ReadOnly)));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("http://localhost:8080", true)]
    [InlineData("https://example.com", false)]
    [InlineData("http://192.0.2.1:8080", false)]
    public void DesktopImagesRequireGatewayOnThisHost(string uri, bool expected) =>
        Assert.Equal(expected, ComputerLocalEndpoint.IsLocal(new Uri(uri)));
}
