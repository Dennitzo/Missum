using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Gateway;
using Missum.Ai.Server.Core.Configuration;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using Missum.Ai.Server.Core.Status;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class CompactContextLifecycleTests
{
    private static readonly string[] SingleObjectIds = ["single"];
    private static RunRequest Request() => new(MissumAiProtocol.Version, RunMode.General,
        [new("user", [new("text", Text: "Erkläre Trägheit.")])], SessionId: "same-session");

    [Fact]
    public void OnlyNegotiatedRunsUseCompactWhileMissingProfilesRemainCompatible()
    {
        Assert.Null(RunProcessor.ResolveNewContextProfile(Request()));
        Assert.Equal(ToolContextProfiles.Current,
            RunProcessor.ResolveNewContextProfile(Request() with { ContextProfileVersion = ToolContextProfiles.Current }));
        Assert.Null(RunProcessor.ResolveNewContextProfile(Request() with { ContextProfileVersion = "legacy" }));
        var science = Request() with { ClientCapabilities = ["research.deliverables"],
            ResearchOptions = new(ProtocolVersion: 2, ProjectId: "research-one") };
        Assert.Null(RunProcessor.ResolveNewContextProfile(science));
        Assert.Null(RunProcessor.ResolveNewContextProfile(science with { ResearchOptions = new(ProtocolVersion: 1, ProjectId: "research-one") }));
        Assert.Null(RunProcessor.ResolveNewContextProfile(science with { ResearchOptions = new(ProtocolVersion: 1, ProjectId: "research-one"),
            ContextProfileVersion = ToolContextProfiles.Current }));
        Assert.Equal(ToolContextProfiles.Current,
            RunProcessor.ResolveNewContextProfile(science with { ContextProfileVersion = ToolContextProfiles.Current }));
        Assert.Null(RunProcessor.ResolveNewContextProfile(Request() with { ConversationProfile = ConversationProfile.Audiobook }));
    }

    [Fact]
    public void AutomaticAdoptionWaitsForRuntimeAcceptanceButExplicitCandidateRunsRemainAvailable()
    {
        Assert.Empty(new CapabilityService(Options.Create(new MissumAiServerOptions())).GetSnapshot().ContextProfiles!);
        Assert.Contains(ToolContextProfiles.Current, new CapabilityService(Options.Create(
            new MissumAiServerOptions { EnableCompactContextProfile = true })).GetSnapshot().ContextProfiles!);
        RunRequestValidator.Validate(Request() with { ContextProfileVersion = ToolContextProfiles.Current });
    }

    [Fact]
    public void ProfileNegotiationRejectsUnknownVersionsWithoutWeakeningRunValidation()
    {
        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(Request() with { ContextProfileVersion = "unknown" }));
        RunRequestValidator.Validate(Request() with { ContextProfileVersion = ToolContextProfiles.Current });
    }

    [Fact]
    public void RuntimeChangesAppendDataWithoutRewritingTheSystemPrefix()
    {
        var request = Request();
        var messages = RunProcessor.CreateInitialMessages(request, "general", [], ToolContextProfiles.Current);
        var system = messages[0];
        RunProcessor.AppendRuntimeContext(messages, request, false, beforeLatestUser: true);
        Assert.Same(system, messages[0]);
        Assert.Equal("Erkläre Trägheit.", messages[^1].Content);
        var count = messages.Count;
        RunProcessor.AppendRuntimeContext(messages, request, false);
        Assert.Equal(count, messages.Count);
        RunProcessor.AppendRuntimeContext(messages, request, true);
        Assert.Equal(count + 1, messages.Count);
        Assert.Same(system, messages[0]);
    }

    [Fact]
    public void CompactCatalogOffersRealToolsWithoutASelectorAndSurvivesCheckpointSerialization()
    {
        var request = Request() with { ClientCapabilities = ["documentIo"] };
        var available = new AgentToolCatalog().GetAvailableTools(request);
        var definitions = RunProcessor.CreateModelToolDefinitions(available, null, true, ToolContextProfiles.Current);
        Assert.Equal(available.Select(tool => tool.Name), definitions.Select(tool => tool.Name));
        Assert.DoesNotContain(definitions, tool => AgentToolCatalog.IsSelectorToolName(tool.Name));
        var checkpoint = new AgentRunCheckpoint([], 1, 1, 100, 10,
            ContextProfileVersion: ToolContextProfiles.Current, ContextTools: definitions,
            ToolCatalogSignature: ToolContextProfiles.Signature(definitions));
        var restored = JsonSerializer.Deserialize<AgentRunCheckpoint>(JsonSerializer.Serialize(checkpoint))!;
        Assert.Equal(ToolContextProfiles.Current, restored.ContextProfileVersion);
        Assert.Equal(checkpoint.ToolCatalogSignature, ToolContextProfiles.Signature(restored.ContextTools!));
        var legacy = JsonSerializer.Deserialize<AgentRunCheckpoint>("""{"Messages":[],"RoundCount":0,"ToolCallCount":0,"InputTokens":0,"OutputTokens":0}""")!;
        Assert.Null(legacy.ContextProfileVersion);
        Assert.Null(legacy.ContextTools);
    }

    [Fact]
    public void ResearchRefreshReusesOnlyAnAuthenticatedOverviewStillPresentInContext()
    {
        var stamp = new string('a', 64);
        var request = Request() with { ResearchOptions = new(ProtocolVersion: 2, ProjectId: "research-one") };
        var result = JsonSerializer.Serialize(new { status = "completed", result = new
        { success = true, projectId = "research-one", view = "overview", stateStamp = stamp, items = Array.Empty<object>() } });
        var call = new LmToolCall("read-1", ClientToolNames.ResearchRead,
            JsonSerializer.SerializeToElement(new { projectId = "research-one", view = "overview" }));
        LmChatMessage[] context = [new("assistant", ToolCalls: [call]), new("tool", result, ToolCallId: "read-1")];
        var args = RunProcessor.CreateResearchStartArguments(request, context, true);
        Assert.Equal(stamp, args.GetProperty("knownStateStamp").GetString());
        Assert.Equal("overview", args.GetProperty("view").GetString());
        Assert.False(RunProcessor.CreateResearchStartArguments(request, [], true).TryGetProperty("knownStateStamp", out _));
        Assert.Null(RunProcessor.FindResearchOverviewStamp([context[1]], "research-one"));
        Assert.Null(RunProcessor.FindResearchOverviewStamp(context, "different-project"));
        var filtered = call with { Arguments = JsonSerializer.SerializeToElement(new { projectId = "research-one", view = "overview", ids = SingleObjectIds }) };
        Assert.Null(RunProcessor.FindResearchOverviewStamp([new("assistant", ToolCalls: [filtered]), context[1]], "research-one"));
        var continuation = call with { Arguments = JsonSerializer.SerializeToElement(new { projectId = "research-one", view = "overview", cursor = "page2" }) };
        Assert.Null(RunProcessor.FindResearchOverviewStamp([new("assistant", ToolCalls: [continuation]), context[1]], "research-one"));
        Assert.False(RunProcessor.CreateResearchStartArguments(request, context, false).TryGetProperty("view", out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CanonicalScienceStartsWithOneCompactStateViewWithoutChangingItsProfile(bool compactContext)
    {
        var request = Request() with { ClientCapabilities = ["research.deliverables"],
            ResearchOptions = new(ProtocolVersion: 2, ProjectId: "research-one") };
        var arguments = RunProcessor.CreateResearchStartArguments(request, [], compactContext);
        Assert.Equal("research-one", arguments.GetProperty("projectId").GetString());
        Assert.Equal("overview", arguments.GetProperty("view").GetString());
        Assert.Null(RunProcessor.ResolveNewContextProfile(request));
    }

    [Theory]
    [InlineData("task", "research-one", true)]
    [InlineData("objects", "research-one", true)]
    [InlineData("overview", "research-one", true)]
    [InlineData("sources", "research-one", false)]
    [InlineData("task", "another-project", false)]
    public void EarlyDelegationAllowsNecessaryTaskReadsWithoutReleasingTheDelegationGate(string view, string projectId, bool expected)
    {
        var request = Request() with { ClientCapabilities = ["research.deliverables"],
            ResearchOptions = new(ProtocolVersion: 2, ProjectId: "research-one") };
        var catalog = new AgentToolCatalog();
        var call = new LmToolCall("read-1", ClientToolNames.ResearchRead,
            JsonSerializer.SerializeToElement(new { projectId, view }));
        var response = new LmChatResult("", [call], 0, 0);
        Assert.Equal(expected, RunProcessor.IsEarlyResearchContextRead(response, request, catalog.GetAvailableTools(request), catalog));
        Assert.False(RunProcessor.IsEarlyResearchContextRead(response with { ToolCalls = [call, call] },
            request, catalog.GetAvailableTools(request), catalog));
    }
}
