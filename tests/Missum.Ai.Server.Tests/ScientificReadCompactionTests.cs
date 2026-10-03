using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;
using Missum.Ai.Server.Core.Runs;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class ScientificReadCompactionTests
{
    private const string Project = "research-context";
    private static readonly string FirstStamp = new('a', 64);
    private static readonly string SecondStamp = new('b', 64);
    private static readonly string[] SectionIds = ["one-section"];

    [Fact]
    public void CompactAcknowledgementAndFilteredReadCannotReplaceTheFullOverview()
    {
        var messages = Initial();
        var full = AddRead(messages, "overview", FirstStamp, new { items = new[] { new { id = "required-goal" } }, unchanged = false },
            new { projectId = Project, view = "overview", ids = Array.Empty<string>() });
        AddRead(messages, "overview", FirstStamp, new { unchanged = true }, new { projectId = Project, view = "overview", knownStateStamp = FirstStamp });
        AddRead(messages, "overview", FirstStamp, new { items = new[] { new { id = "one-section" } }, unchanged = false },
            new { projectId = Project, view = "overview", ids = SectionIds });
        AddPressure(messages);

        var plan = ScientificRunContextPlanner.Prepare(messages, 32768, null);

        Assert.True(plan.WasCompacted);
        Assert.Contains(full, plan.Messages);
        Assert.Equal(FirstStamp, RunProcessor.FindResearchOverviewStamp(plan.Messages, Project));
        AssertValidPairs(plan.Messages);
    }

    [Fact]
    public void AllOriginalTaskPagesRemainAfterUnchangedOverviewAndOtherReads()
    {
        var messages = Initial();
        AddRead(messages, "overview", FirstStamp, new { items = Array.Empty<object>(), unchanged = false });
        var task = AddTask(messages, FirstStamp, new string('t', 34_123));
        AddRead(messages, "overview", FirstStamp, new { unchanged = true }, new { projectId = Project, view = "overview", knownStateStamp = FirstStamp });
        AddRead(messages, "objects", FirstStamp, new { items = new[] { new { id = "section" } } });
        AddPressure(messages);

        var plan = ScientificRunContextPlanner.Prepare(messages, 32768, null);

        foreach (var page in task) Assert.Contains(page, plan.Messages);
        Assert.Equal(5, task.Count);
        Assert.Equal(new string('t', 34_123), string.Concat(task.Select(page =>
        {
            using var document = JsonDocument.Parse(page.Content!);
            return document.RootElement.GetProperty("result").GetProperty("originalQuestion").GetString();
        })));
        AssertValidPairs(plan.Messages);
    }

    [Fact]
    public void NewCompleteTaskChainReplacesOldChainWithoutProtectingHistoricalReads()
    {
        var messages = Initial();
        var oldOverview = AddRead(messages, "overview", FirstStamp, new { items = Array.Empty<object>(), unchanged = false });
        var oldTask = AddTask(messages, FirstStamp, new string('t', 24_123));
        var currentOverview = AddRead(messages, "overview", SecondStamp, new { items = Array.Empty<object>(), unchanged = false });
        var currentTask = AddTask(messages, SecondStamp, new string('t', 24_123));
        AddRead(messages, "checks", SecondStamp, new { items = Array.Empty<object>() });
        AddPressure(messages);

        var plan = ScientificRunContextPlanner.Prepare(messages, 32768, null);

        Assert.DoesNotContain(oldOverview, plan.Messages);
        foreach (var page in oldTask) Assert.DoesNotContain(page, plan.Messages);
        Assert.Contains(currentOverview, plan.Messages);
        foreach (var page in currentTask) Assert.Contains(page, plan.Messages);
        AssertValidPairs(plan.Messages);
    }

    [Fact]
    public void PartialRefreshRetainsLastCompleteOriginalTaskUntilNewReadFinishes()
    {
        var messages = Initial();
        var complete = AddTask(messages, FirstStamp, new string('t', 24_123));
        var partial = AddTaskPage(messages, SecondStamp, new string('t', 8000), 0, 24_123);
        AddRead(messages, "checks", SecondStamp, new { items = Array.Empty<object>() });
        AddPressure(messages);

        var plan = ScientificRunContextPlanner.Prepare(messages, 32768, null);

        foreach (var page in complete) Assert.Contains(page, plan.Messages);
        Assert.Contains(partial, plan.Messages);
        AssertValidPairs(plan.Messages);
    }

    [Fact]
    public void RuntimeMetadataOnlyProtectsLatestStateAndNeverReplacesUserTask()
    {
        var messages = Initial();
        var original = messages[1];
        var stale = new LmChatMessage("user", CompactAgentContextPolicy.RuntimeMarker + "\n{\"subagentAvailable\":true}");
        var current = new LmChatMessage("user", CompactAgentContextPolicy.RuntimeMarker + "\n{\"subagentAvailable\":false}");
        messages.Add(stale);
        messages.Add(current);
        AddPressure(messages);

        var plan = ScientificRunContextPlanner.Prepare(messages, 32768, null);

        Assert.DoesNotContain(stale, plan.Messages);
        Assert.Contains(current, plan.Messages);
        Assert.Contains(original, plan.Messages);
    }

    private static List<LmChatMessage> Initial() => [new("system", "Science policy"), new("user", "Continue the original research assignment.")];

    private static List<LmChatMessage> AddTask(List<LmChatMessage> messages, string stamp, string original)
    {
        var pages = new List<LmChatMessage>();
        for (var offset = 0; offset < original.Length; offset += 8000)
            pages.Add(AddTaskPage(messages, stamp, original.Substring(offset, Math.Min(8000, original.Length - offset)), offset, original.Length));
        return pages;
    }

    private static LmChatMessage AddTaskPage(List<LmChatMessage> messages, string stamp, string originalQuestion,
        int characterOffset, int totalCharacters) => AddRead(messages, "task", stamp, new
        {
            originalQuestion, characterOffset, totalCharacters,
            nextCursor = characterOffset + originalQuestion.Length < totalCharacters ? "next-page-" + (characterOffset + originalQuestion.Length) : null,
        }, characterOffset == 0 ? new { projectId = Project, view = "task" } : (object)new { projectId = Project, view = "task", cursor = "next-page-" + characterOffset });

    private static LmChatMessage AddRead(List<LmChatMessage> messages, string view, string stamp, object fields, object? arguments = null)
    {
        var id = "read-" + messages.Count;
        messages.Add(new("assistant", ToolCalls: [new(id, ClientToolNames.ResearchRead,
            JsonSerializer.SerializeToElement(arguments ?? new { projectId = Project, view }))]));
        var result = new Dictionary<string, object?>
        {
            ["success"] = true, ["projectId"] = Project, ["view"] = view, ["stateStamp"] = stamp,
            ["protocol"] = "section-delta-v1",
        };
        foreach (var field in JsonSerializer.SerializeToElement(fields).EnumerateObject()) result[field.Name] = field.Value;
        var receipt = new LmChatMessage("tool", JsonSerializer.Serialize(new { status = "completed", result }), ToolCallId: id);
        messages.Add(receipt);
        return receipt;
    }

    private static void AddPressure(List<LmChatMessage> messages)
    {
        for (var index = 0; index < 100; index++)
        {
            var id = "obsolete-" + index;
            messages.Add(new("assistant", ToolCalls: [new(id, "web.fetch", JsonSerializer.SerializeToElement(new { url = "https://example.org/old" }))]));
            messages.Add(new("tool", new string('o', 3000), ToolCallId: id));
        }
    }

    private static void AssertValidPairs(IReadOnlyList<LmChatMessage> messages)
    {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            foreach (var call in message.ToolCalls ?? []) Assert.True(pending.Add(call.Id));
            if (message.Role == "tool") Assert.True(message.ToolCallId is { } id && pending.Remove(id));
        }
        Assert.Empty(pending);
    }
}
