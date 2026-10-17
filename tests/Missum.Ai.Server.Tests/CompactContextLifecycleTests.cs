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
    [Fact]
    public void EquivalentContextToolsIgnoreCatalogOrderButRespectSchemaDescriptionAndProfileChanges()
    {
        var first = new LmToolDefinition("first", "First description", JsonSerializer.SerializeToElement(new
            { type = "object", properties = new { path = new { type = "string" } } }));
        var second = new LmToolDefinition("second", "Second description", JsonSerializer.SerializeToElement(new
            { type = "object", properties = new { line = new { type = "integer" } } }));
        Assert.True(RunProcessor.HaveEquivalentContextTools([first, second], [second, first]));
        Assert.False(RunProcessor.HaveEquivalentContextTools([first], [first, second]));
        Assert.False(RunProcessor.HaveEquivalentContextTools([first, first], [first, second]));
        Assert.False(RunProcessor.HaveEquivalentContextTools([first], [first with { Description = "Changed" }]));
        Assert.False(RunProcessor.HaveEquivalentContextTools([first], [first with { ContextProfileVersion = ToolContextProfiles.Current }]));
        Assert.False(RunProcessor.HaveEquivalentContextTools([first], [first with { Parameters = second.Parameters }]));
    }

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
        Assert.Equal(ToolContextProfiles.Current,
            RunProcessor.ResolveNewContextProfile(science with { ResearchOptions = new(ProtocolVersion: 1, ProjectId: "research-one"),
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

}
