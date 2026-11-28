using System.Text.Json;
using Missum.App.Services;

namespace Missum.Tests;

public sealed class ComputerStepBundlingTests
{
    private static JsonElement Step(string id, string tool, int offset, string status = "completed", object? input = null) =>
        JsonSerializer.SerializeToElement(new
        {
            id, tool, status, contentOffset = offset,
            inputJson = input is null ? "" : JsonSerializer.Serialize(input),
        });

    [Fact]
    public void ConsecutiveComputerStepsAndTheirCommentsBecomeOneReceipt()
    {
        const string content = "Intro. Starte Rechner. Tippe 12*7. Ergebnis 84.";
        JsonElement[] steps =
        [
            Step("o1", "computer.observe", 7),
            Step("a1", "computer.act", 23, input: new { kind = "launch", target = "calc" }),
            Step("a2", "computer.act", 35, input: new { kind = "type", text = "12*7=" }),
            Step("s1", "web.search", 47),
        ];

        var bundled = ComputerStepBundling.Bundle(steps, content).ToArray();

        Assert.Equal(2, bundled.Length);
        var group = bundled[0];
        Assert.Equal(ComputerStepBundling.GroupTool, group.GetProperty("tool").GetString());
        Assert.Equal("computer-group:o1", group.GetProperty("id").GetString());
        Assert.Equal("Computer-Use · 3 Schritte", group.GetProperty("label").GetString());
        Assert.Equal("completed", group.GetProperty("status").GetString());
        Assert.Equal(7, group.GetProperty("contentOffset").GetInt32());
        Assert.Equal(35, ComputerStepBundling.ContentEnd(group));
        var detail = group.GetProperty("detail").GetString()!;
        Assert.Contains("1. **Bildschirm beobachtet**", detail);
        Assert.Contains("2. **Programm starten · calc**", detail);
        Assert.Contains("3. **Text eingeben · „12*7=“**", detail);
        Assert.Contains("Starte Rechner.", detail);
        Assert.Contains("Tippe 12*7.", detail);
        // The final answer after the last computer step stays in the visible chat text.
        Assert.DoesNotContain("Ergebnis 84.", detail);
        Assert.Equal("web.search", bundled[1].GetProperty("tool").GetString());
    }

    [Fact]
    public void ARunningStepKeepsTheReceiptRunningAndOtherToolsSplitRuns()
    {
        JsonElement[] steps =
        [
            Step("o1", "computer.observe", 0),
            Step("r1", "coding.read", 0),
            Step("o2", "computer.observe", 0),
            Step("a2", "computer.act", 0, "running", new { kind = "click", elementIndex = 4 }),
        ];

        var bundled = ComputerStepBundling.Bundle(steps, "").ToArray();

        Assert.Equal(["computer.session", "coding.read", "computer.session"], bundled.Select(step => step.GetProperty("tool").GetString()));
        Assert.Equal("Computer-Use · 1 Schritt", bundled[0].GetProperty("label").GetString());
        Assert.Equal("running", bundled[2].GetProperty("status").GetString());
        Assert.Contains("Klick · Element 4", bundled[2].GetProperty("detail").GetString());
    }
}
