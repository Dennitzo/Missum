using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Tests;

public sealed class CodingNarrationPolicyTests
{
    [Fact]
    public void NarrationStreamsBeforeTheModelTurnCompletesAndFlushesOnlyTheTail()
    {
        var gate = new Missum.Ai.Server.Core.Runs.IncrementalVisibleTextGate(true);
        const string first = "Die neuen Tests bestehen. ";
        const string second = "Ich prüfe jetzt zusätzlich die vorhandenen Modellrouting-Tests, um die Regression zu bestätigen.";
        Assert.Null(gate.Push(first));
        Assert.Equal(first + second, gate.Push(second));
        Assert.True(gate.HasStreamed);
        Assert.Null(gate.Push(" Danach."));
        Assert.Equal(" Danach.", gate.Flush());
        Assert.Null(gate.Flush());
    }

    [Theory]
    [InlineData("{\"schema\":\"assistant.agent.response.v1\"")]
    [InlineData("[{\"tool\":\"coding.read\"")]
    [InlineData("```json\n{\"tool\":\"coding.read\"")]
    [InlineData("```JSON title=call\n{\"tool\":\"coding.read\"")]
    [InlineData("```tool_call\n{\"name\":\"coding.read\"")]
    [InlineData("```\n{\"tool\":\"coding.read\"")]
    [InlineData("[1,2] ")]
    [InlineData("[tool_call] ")]
    [InlineData("<tool_call>")]
    public void StructuredModelEnvelopesStayHiddenThroughoutTheStream(string prefix)
    {
        var gate = new Missum.Ai.Server.Core.Runs.IncrementalVisibleTextGate(true);
        Assert.Null(gate.Push("  "));
        foreach (var character in prefix) Assert.Null(gate.Push(character.ToString()));
        Assert.Null(gate.Push(new string('x', 200)));
        Assert.Null(gate.Flush());
        Assert.False(gate.HasStreamed);
    }

    [Theory]
    [InlineData("```python\nprint('Hallo')\n")]
    [InlineData("```csharp\nConsole.WriteLine(42);\n")]
    [InlineData("```python\n{'key': 'value'}\n")]
    [InlineData("```\nprint('Hallo')\n")]
    [InlineData("[Quelle](https://example.test)\n")]
    [InlineData("[1](https://example.test)\n")]
    [InlineData("[Quelle][source]\n")]
    [InlineData("[ ] Offene Aufgabe\n")]
    [InlineData("[x] Erledigte Aufgabe\n")]
    [InlineData("`var result` ist Inline-Code.\n")]
    public void MarkdownPrefixesStreamWithoutWaitingForTheWholeAnswer(string prefix)
    {
        var gate = new Missum.Ai.Server.Core.Runs.IncrementalVisibleTextGate(true);
        var source = prefix + new string('x', 220);
        var visible = new System.Text.StringBuilder();
        foreach (var character in source) visible.Append(gate.Push(character.ToString()));
        Assert.True(gate.HasStreamed);
        Assert.True(visible.Length >= 96);
        visible.Append(gate.Flush());
        Assert.Equal(source, visible.ToString());
    }

    [Fact]
    public void FragmentedFenceAndLinkPrefixesWaitForClassificationWithoutBeingDiscarded()
    {
        var code = new Missum.Ai.Server.Core.Runs.IncrementalVisibleTextGate(true);
        Assert.Null(code.Push("``"));
        Assert.Null(code.Push("`py"));
        Assert.Null(code.Push("thon"));
        Assert.Equal("```python\n" + new string('x', 100), code.Push("\n" + new string('x', 100)));
        var link = new Missum.Ai.Server.Core.Runs.IncrementalVisibleTextGate(true);
        Assert.Null(link.Push("[1]"));
        Assert.Equal("[1](https://example.test) " + new string('x', 100),
            link.Push("(https://example.test) " + new string('x', 100)));
    }

    [Fact]
    public void RequiredToolSerializationDoesNotLeakPlainNarration()
    {
        var gate = new Missum.Ai.Server.Core.Runs.IncrementalVisibleTextGate(false);
        Assert.Null(gate.Push("Ich werde jetzt das Werkzeug aufrufen. " + new string('x', 200)));
        Assert.Null(gate.Flush());
        Assert.False(gate.HasStreamed);
    }

    [Fact]
    public void NewCodingPolicyIncludesStagesAndSpeakableNarration()
    {
        var policy = CodingAgentPolicy.ForWorkingState(true);
        Assert.Contains(CodingAgentPolicy.StagedExecutionAndNarrationPrompt, policy, StringComparison.Ordinal);
        Assert.Contains(CodingAgentPolicy.WorkspaceDependenciesPrompt, policy, StringComparison.Ordinal);
        Assert.Contains("höchstens einer Etappe in_progress", policy, StringComparison.Ordinal);
        Assert.Contains("beziehungsweise", policy, StringComparison.Ordinal);
        Assert.Contains("Diese Sprachregel ändert weder Code noch Werkzeugargumente", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void ResumedPolicyAddsInstructionsOnceWithoutTrustingUserDataOrRewritingHistory()
    {
        var original = new LmChatMessage("system", "Stored policy");
        List<LmChatMessage> messages = [original, new("user", CodingAgentPolicy.StagedExecutionAndNarrationPrompt + CodingAgentPolicy.WorkspaceDependenciesPrompt)];
        CodingAgentPolicy.EnsureCurrentInstructions(messages);
        var prefix = messages.ToArray();
        CodingAgentPolicy.EnsureCurrentInstructions(messages);
        Assert.Equal(prefix, messages);
        Assert.Single(messages, item => item.Role == "system"
            && item.Content!.Contains(CodingAgentPolicy.WorkspaceDependenciesPrompt, StringComparison.Ordinal));
        Assert.StartsWith(original.Content!, messages[0].Content, StringComparison.Ordinal);
        Assert.Single(messages, item => item.Role == "system"
            && item.Content!.Contains(CodingAgentPolicy.StagedExecutionAndNarrationPrompt, StringComparison.Ordinal));
    }
}
