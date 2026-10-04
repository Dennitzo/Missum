using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class EarlyResearchContextTests
{
    private static readonly string[] FirstIds = ["first"];
    private static readonly string[] SecondIds = ["second"];
    private static readonly string[] Capabilities = ["research.deliverables"];
    private static RunRequest Request() => new(MissumAiProtocol.Version, RunMode.General,
        [new("user", [new("text", Text: "Setze die Forschung fort.")])], ClientCapabilities: Capabilities,
        ResearchOptions: new(ProtocolVersion: 2, ProjectId: "research-context"));
    private static LmToolCall Read(object arguments, string id = "model-read") =>
        new(id, ClientToolNames.ResearchRead, JsonSerializer.SerializeToElement(arguments));
    private static string Receipt(bool success = true, string projectId = "research-context", string status = "completed") =>
        JsonSerializer.Serialize(new { status, result = new { success, projectId, protocol = "section-delta-v1" } });
    private static bool Allowed(LmToolCall call, IReadOnlyList<LmChatMessage> messages)
    {
        var request = Request();
        var catalog = new AgentToolCatalog();
        return RunProcessor.IsEarlyResearchContextRead(new("", [call], 0, 0), request,
            catalog.GetAvailableTools(request), catalog, messages);
    }

    [Theory]
    [InlineData("overview")]
    [InlineData("objects")]
    [InlineData("task")]
    public void SuccessfullyLoadedReadCannotKeepDelayingTheAssignment(string view)
    {
        var read = Read(new { projectId = "research-context", view });
        LmChatMessage[] messages = [new("assistant", ToolCalls: [read]), new("tool", Receipt(), ToolCallId: read.Id)];
        Assert.False(Allowed(Read(new { view, projectId = "research-context" }, "new-id"), messages));
        Assert.True(Allowed(read, []));
        Assert.True(Allowed(read, [messages[1]])); // A receipt needs its matching call.
    }

    [Theory]
    [InlineData(false, "research-context", "completed")]
    [InlineData(true, "research-context", "failed")]
    [InlineData(true, "another-project", "completed")]
    public void FailedOrForeignReadStillAllowsTheNecessaryContext(bool success, string projectId, string status)
    {
        var read = Read(new { projectId = "research-context", view = "task" });
        Assert.True(Allowed(read, [new("assistant", ToolCalls: [read]),
            new("tool", Receipt(success, projectId, status), ToolCallId: read.Id)]));
    }

    [Fact]
    public void MissingObjectAndOriginalTaskPagesRemainReachable()
    {
        var objects = Read(new { projectId = "research-context", view = "objects", ids = FirstIds });
        var firstPage = Read(new { projectId = "research-context", view = "task" }, "original-task");
        LmChatMessage[] messages = [new("assistant", ToolCalls: [objects]), new("tool", Receipt(), ToolCallId: objects.Id),
            new("assistant", ToolCalls: [firstPage]), new("tool", Receipt(), ToolCallId: firstPage.Id)];
        Assert.True(Allowed(Read(new { projectId = "research-context", view = "objects", ids = SecondIds }), messages));
        Assert.True(Allowed(Read(new { projectId = "research-context", view = "task", cursor = "next-page" }), messages));
        Assert.False(Allowed(firstPage, messages));
    }

    [Fact]
    public void AcceptedStateChangesAllowRefreshingAPreviouslyLoadedView()
    {
        var read = Read(new { projectId = "research-context", view = "objects", ids = FirstIds });
        var update = Update("research-context", validContent: true);
        var messages = new List<LmChatMessage> { new("assistant", ToolCalls: [read]), new("tool", Receipt(), ToolCallId: read.Id),
            new("assistant", ToolCalls: [update]), new("tool", Receipt(success: false), ToolCallId: update.Id) };
        Assert.False(Allowed(read, messages)); // A rejected change did not stale the read.
        messages.Add(new("assistant", ToolCalls: [update with { Id = "accepted-update" }]));
        messages.Add(new("tool", JsonSerializer.Serialize(new { status = "completed", result = new
        { success = true, projectId = "research-context", changedIds = FirstIds, publicationChanged = true } }), ToolCallId: "accepted-update"));
        Assert.True(Allowed(read, messages));
        messages.Add(new("assistant", ToolCalls: [read with { Id = "refreshed" }]));
        messages.Add(new("tool", Receipt(), ToolCallId: "refreshed"));
        Assert.False(Allowed(read, messages));
    }

    [Fact]
    public void ReminderIsAppendedOnceAfterTheModelReadAndSurvivesNativeNormalization()
    {
        var read = Read(new { projectId = "research-context", view = "objects", ids = FirstIds });
        var messages = new List<LmChatMessage> { new("system", "Fixed system prefix"), new("user", "Auftrag"),
            new("assistant", ToolCalls: [read]), new("tool", Receipt(), ToolCallId: read.Id) };
        RunProcessor.EnsureEarlyResearchContextHint(messages, "run-one", Request(), read, Receipt());
        Assert.Equal("Fixed system prefix", messages[0].Content);
        Assert.Contains("kleine unabhängige Teilfrage", messages[^1].Content, StringComparison.Ordinal);
        Assert.Single(messages.SelectMany(static message => message.ToolCalls ?? []));
        var normalized = ModelRuntimeClient.NormalizeMessageOrderForNativeRuntime(messages).ToList();
        var count = normalized.Count;
        RunProcessor.EnsureEarlyResearchContextHint(normalized, "run-one", Request(), read, Receipt());
        Assert.Equal(count, normalized.Count);
        RunProcessor.EnsureEarlyResearchContextHint(normalized, "run-two", Request(), read, Receipt());
        Assert.Equal(count + 1, normalized.Count); // A new run has its own early assignment.
    }

    [Fact]
    public void AutomaticOverviewAndFailedOrForeignReadsDoNotAddTheReminder()
    {
        var messages = new List<LmChatMessage> { new("system", "Fixed system prefix"), new("user", "Auftrag") };
        var read = Read(new { projectId = "research-context", view = "overview" });
        RunProcessor.EnsureEarlyResearchContextHint(messages, "run-one", Request(), read with { Id = "science-state-read-initial" }, Receipt());
        RunProcessor.EnsureEarlyResearchContextHint(messages, "run-one", Request(), read, Receipt(success: false));
        RunProcessor.EnsureEarlyResearchContextHint(messages, "run-one", Request(), read, Receipt(projectId: "another-project"));
        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public void ValidScientificSubmissionIsPreservedWithoutReleasingTheAssignmentGate()
    {
        var request = Request();
        var catalog = new AgentToolCatalog();
        var update = Update("research-context", validContent: true);
        var response = new LmChatResult("Neue fachliche Grundlagen.", [update], 0, 0);
        var tools = catalog.GetAvailableTools(request);
        Assert.True(RunProcessor.IsEarlyResearchProgressUpdate(response, request, tools, catalog));
        Assert.False(RunProcessor.IsValidEarlyDelegationResponse(response, tools, catalog));
        Assert.False(RunProcessor.IsEarlyResearchContextRead(response, request, tools, catalog, []));
        var messages = new List<LmChatMessage> { new("system", "Fixed prefix"), new("user", "Auftrag"),
            new("assistant", response.Content, ToolCalls: [update]), new("tool", Receipt(), ToolCallId: update.Id) };
        RunProcessor.EnsureEarlyResearchContextHint(messages, "run-one", request, update, Receipt());
        Assert.Contains("Einreichungen mit research.update bleiben zulässig", messages[^1].Content, StringComparison.Ordinal);
        Assert.Equal(update, Assert.Single(messages.SelectMany(static message => message.ToolCalls ?? [])));
    }

    [Theory]
    [InlineData("another-project", true)]
    [InlineData("research-context", false)]
    public void ForeignOrMalformedUpdateCannotBypassEarlyAssignment(string projectId, bool validContent)
    {
        var request = Request();
        var catalog = new AgentToolCatalog();
        Assert.False(RunProcessor.IsEarlyResearchProgressUpdate(new("", [Update(projectId, validContent)], 0, 0),
            request, catalog.GetAvailableTools(request), catalog));
    }

    [Fact]
    public void AllUpdatesInAValidSameProjectBatchArePreserved()
    {
        var request = Request();
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(request);
        var first = Update("research-context", validContent: true);
        var second = Update("research-context", validContent: true, id: "second-foundations");
        var batch = new LmChatResult("Fachliche Grundlagen und Ergänzung.", [first, second], 0, 0);
        Assert.True(RunProcessor.IsEarlyResearchProgressUpdate(batch, request, tools, catalog));
        Assert.False(RunProcessor.IsValidEarlyDelegationResponse(batch, tools, catalog));
        var foreign = Update("another-project", validContent: true);
        Assert.False(RunProcessor.IsEarlyResearchProgressUpdate(batch with { ToolCalls = [first, foreign] }, request, tools, catalog));
        Assert.False(RunProcessor.IsEarlyResearchProgressUpdate(batch with { ToolCalls = [first,
            new("web-search", "web.search", JsonSerializer.SerializeToElement(new { query = "not yet assigned" }))] }, request, tools, catalog));
    }

    private static LmToolCall Update(string projectId, bool validContent, string id = "foundations") => new("update-" + id, ClientToolNames.ResearchUpdate,
        JsonSerializer.SerializeToElement(new { projectId, changes = new[]
        {
            new { id, kind = "section", expectedRevision = 0,
                data = new { title = "Grundlagen", contentMarkdown = validContent ? "Neue wissenschaftliche Herleitung." : "" } },
        } }));
}
