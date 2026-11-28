using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Gateway;
using Missum.Ai.Server.Core.Policies;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

/// <summary>Gateway side of the read-only ChatGPT mode: its tool set, the memory tool and its prompt.</summary>
public sealed class ChatGptModePolicyTests
{
    private static readonly string[] ChatGptServerTools =
        ["math.evaluate", "context.embed", "context.retrieve", "web.search", "web.fetch", "web.deepResearch", "media.inspect", "media.analyze"];

    private static readonly string[] QueryRequired = ["query"];

    // Mirrors the app's memory.sessions.search descriptor (SessionMemorySearchTool.Descriptor).
    private static ToolDescriptor MemoryTool() => new(
        SessionMemoryToolContract.Name,
        "Durchsucht frühere Chats des Nutzers aus allen Modi und Projekten nach Stichworten und liefert kurze Auszüge mit Chat-Titel, Modus, Projekt und Datum. Für Bezüge auf frühere Gespräche oder andere Projekte.",
        ToolRiskClass.ReadOnly,
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                query = new { type = "string" },
                limit = new { type = "integer", minimum = 1, maximum = 8 },
            },
            required = QueryRequired,
            additionalProperties = false,
        }),
        30,
        16 * 1024);

    private static RunRequest ChatGptRequest(bool memory) => new(MissumAiProtocol.Version, RunMode.Auto,
        [new("user", [new("text", "Was hatten wir neulich zur Heizung besprochen?")])],
        SessionId: Guid.NewGuid().ToString("D"),
        ClientCapabilities: ["documents", "subagents"],
        AllowedServerTools: ChatGptServerTools,
        ClientTools: memory ? [MemoryTool()] : null);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChatGptRequestIsValidAndOffersOnlyReadingResearchAndMemoryTools(bool memory)
    {
        var request = ChatGptRequest(memory);
        RunRequestValidator.Validate(request);

        var tools = new AgentToolCatalog().GetAvailableTools(request).Select(static tool => tool.Name).ToArray();

        Assert.Contains(ClientToolNames.DocumentsSearch, tools);
        Assert.Contains(ClientToolNames.DocumentsReadPages, tools);
        Assert.Contains("web.search", tools);
        Assert.Contains("media.analyze", tools);
        Assert.Equal(memory, tools.Contains(SessionMemoryToolContract.Name));
        foreach (var forbidden in new[] { ClientToolNames.DocumentCreate, ClientToolNames.DocumentRead, WorkspaceTools.ImageInput,
                     WorkspaceTools.Open, ClientToolNames.MathFormalProof, "image.generate", "speech.synthesize" })
            Assert.DoesNotContain(forbidden, tools);
        Assert.DoesNotContain(tools, static name => name.StartsWith("coding.", StringComparison.Ordinal)
            || name.StartsWith("computer.", StringComparison.Ordinal));
        var memoryTool = new AgentToolCatalog().GetAvailableTools(request).SingleOrDefault(static tool => tool.Name == SessionMemoryToolContract.Name);
        if (memory) Assert.Equal(ToolRiskClass.ReadOnly, memoryTool!.RiskClass);
    }

    [Fact]
    public void LegacyGeneralPolicyMentionsFileWorkOnlyWhenItsToolsAreOffered()
    {
        var request = ChatGptRequest(memory: true);
        var readOnly = GeneralAgentPolicies.ForConversation("general", request,
            [ClientToolNames.DocumentsSearch, "web.search", "web.fetch", SessionMemoryToolContract.Name]);
        foreach (var fileRule in new[] { "Workspace-Werkzeuge", "document.create", "image.input", "Schreib-Operationen" })
            Assert.DoesNotContain(fileRule, readOnly, StringComparison.Ordinal);
        Assert.Contains("Schneller Antwortmodus", readOnly, StringComparison.Ordinal);
        Assert.Contains("keine Computersteuerung", readOnly, StringComparison.Ordinal);
        Assert.Contains("memory.sessions.search", readOnly, StringComparison.Ordinal);
        Assert.Contains("Nutzerauftrag", readOnly, StringComparison.Ordinal);
        Assert.Equal(2, readOnly.Split(MathFormattingPolicy.Instructions, StringSplitOptions.None).Length);

        var withoutMemory = GeneralAgentPolicies.ForConversation("general", ChatGptRequest(memory: false),
            [ClientToolNames.DocumentsSearch, "web.search", "web.fetch"]);
        Assert.DoesNotContain("memory.sessions.search", withoutMemory, StringComparison.Ordinal);

        // A general run with project tools (Meetings) keeps its file and document rules.
        var project = GeneralAgentPolicies.ForConversation("general", request with { ClientCapabilities = ["documentIo", "workspace", "coding"] },
            [ClientToolNames.DocumentRead, ClientToolNames.DocumentCreate, WorkspaceTools.ImageInput, ClientToolNames.CodingWrite]);
        Assert.Contains("Workspace-Werkzeuge", project, StringComparison.Ordinal);
        Assert.Contains("document.create angeboten ist", project, StringComparison.Ordinal);
        Assert.Contains("image.input lädt ein Projektbild", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Schneller Antwortmodus", project, StringComparison.Ordinal);
    }
}
