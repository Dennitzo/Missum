using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class ComputerRoutingTests
{
    [Theory]
    [InlineData("{\"useComputer\":true}", true)]
    [InlineData("{\"useComputer\":false}", false)]
    [InlineData("{\"useComputer\":\"true\"}", false)]
    [InlineData("true", false)]
    [InlineData("not json", false)]
    [InlineData("{\"useComputer\":true} trailing", false)]
    public void OnlyExplicitTrueSelectsComputer(string response, bool expected) =>
        Assert.Equal(expected, ComputerRoutingService.ParseDecision(response));
}
