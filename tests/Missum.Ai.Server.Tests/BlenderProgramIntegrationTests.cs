using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Gateway;
using Missum.Ai.Server.Core.Runs;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

/// <summary>
/// The Blender package is the first integration that unlocks several client tools for one coding
/// run. These checks read the manifest that actually ships, so the gateway rules and the package
/// cannot drift apart while the program integrations grow.
/// </summary>
public sealed class BlenderProgramIntegrationTests
{
    private static readonly string[] ExpectedToolNames =
    [
        "blender.status",
        "blender.scene.read",
        "blender.scene.edit",
        "blender.view.capture",
        "blender.custom.run",
    ];

    [Fact]
    public void ShippedBlenderManifestPassesTheGatewayClientToolRules()
    {
        var tools = ReadShippedBlenderTools();

        Assert.Equal(ExpectedToolNames, tools.Select(static tool => tool.Name).ToArray());

        RunRequestValidator.Validate(CodingRequest(tools));
    }

    [Fact]
    public void EveryBlenderToolIsAdvertisedAndResolvableInTheCodingRun()
    {
        var tools = ReadShippedBlenderTools();
        var catalog = new AgentToolCatalog();

        var available = catalog.GetAvailableTools(CodingRequest(tools));

        foreach (var descriptor in tools)
        {
            var tool = catalog.Resolve(descriptor.Name, available);
            Assert.False(tool.ServerSide);
            Assert.Equal(descriptor.Description, tool.Description);
            Assert.Equal(descriptor.RiskClass, tool.RiskClass);
            Assert.Equal(descriptor.InputSchema.GetRawText(), tool.Schema.GetRawText());
        }
    }

    [Fact]
    public void BlenderToolArgumentsFollowTheirOwnSchema()
    {
        var catalog = new AgentToolCatalog();
        var available = catalog.GetAvailableTools(CodingRequest(ReadShippedBlenderTools()));

        var status = catalog.Resolve("blender.status", available);
        catalog.Validate(status, JsonSerializer.SerializeToElement(new { }));
        catalog.Validate(status, JsonSerializer.SerializeToElement(new { projectPath = "models/Cup.blend", connect = true }));
        catalog.Validate(status, JsonSerializer.SerializeToElement(new
        {
            visionReview = new
            {
                operationId = "missum-test-aendere-1",
                analyzedSha256 = new string('a', 64),
                verdict = "verified",
                summary = "Wuerfel und Kugel sichtbar, Kamera passt.",
            },
        }));

        // The local connection belongs to the launcher. The model can name a project file,
        // and cannot set connection coordinates or credentials.
        Assert.Throws<ArgumentException>(() => catalog.Validate(
            status, JsonSerializer.SerializeToElement(new { port = 8765 })));
        Assert.Throws<ArgumentException>(() => catalog.Validate(
            status, JsonSerializer.SerializeToElement(new { token = "model-supplied" })));
        Assert.Throws<ArgumentException>(() => catalog.Validate(
            status, JsonSerializer.SerializeToElement(new { unexpected = true })));

        // The vision verdict stays reportable: without these declared fields the mandatory
        // visual review could never be closed by the run.
        var visionReview = status.Schema.GetProperty("properties").GetProperty("visionReview");
        Assert.Equal(
            ["operationId", "analyzedSha256", "verdict", "summary"],
            visionReview.GetProperty("properties").EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.Equal(
            ["verdict", "summary"],
            visionReview.GetProperty("required").EnumerateArray().Select(static item => item.GetString()!).ToArray());

        var custom = catalog.Resolve("blender.custom.run", available);
        catalog.Validate(custom, JsonSerializer.SerializeToElement(new { code = "result = bpy.context.scene.name" }));
        Assert.Throws<ArgumentException>(() => catalog.Validate(
            custom, JsonSerializer.SerializeToElement(new { save = true })));
    }

    [Fact]
    public async Task BlenderCompletionRequiresCurrentReviewAndRequestsCorrectionForDeviation()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var runId = (await repository.CreateAsync(CodingRequest(ReadShippedBlenderTools()), null)).Snapshot.RunId;
        async Task RecordAsync(string name, object receipt)
        {
            var id = Guid.NewGuid().ToString("N");
            await repository.SaveToolProposalAsync(new ToolProposal(id, runId, name, JsonSerializer.SerializeToElement(new { }),
                ToolRiskClass.LocalMutation, name, DateTimeOffset.UtcNow.AddMinutes(1)));
            Assert.True(await repository.SaveClientToolResultAsync(runId,
                new ClientToolResult(id, "completed", JsonSerializer.SerializeToElement(receipt))));
        }
        Task EditAsync(string id) => RecordAsync("blender.custom.run", new { operationId = id, visualCheck = new { required = true } });
        Task ReviewAsync(string id, string verdict) => RecordAsync("blender.status",
            new { visionReview = new { operationId = id, verdict, matchesCapture = true } });
        Task<string?> RepairAsync() => repository.GetBlenderCompletionRepairAsync(runId, CancellationToken.None);

        Assert.Null(await RepairAsync());
        await EditAsync("draft");
        await ReviewAsync("old", "verified");
        Assert.Contains("fehlt", await RepairAsync());
        await ReviewAsync("draft", "deviation");
        Assert.Contains("nach", await RepairAsync());
        await EditAsync("correction");
        Assert.Contains("fehlt", await RepairAsync());
        await ReviewAsync("correction", "verified");
        Assert.Null(await RepairAsync());
    }

    [Fact]
    public void ContinuationCarriesTheSameBlenderToolSet()
    {
        var tools = ReadShippedBlenderTools();
        var request = CodingRequest(tools) with
        {
            SessionId = "session-blender-continuation",
            CodingOptions = new CodingRunOptions(WorkspacePath: "C:/project", ContinueSessionContext: true),
        };

        RunRequestValidator.Validate(request);

        Assert.NotNull(request.ClientTools);
        Assert.Equal(ExpectedToolNames, request.ClientTools!.Select(static tool => tool.Name).ToArray());
    }

    [Fact]
    public void GatewayStillRefusesMoreThanSixteenDynamicClientTools()
    {
        var tools = ReadShippedBlenderTools().ToList();
        while (tools.Count < 17)
        {
            tools.Add(tools[0] with { Name = $"blender.probe{tools.Count}" });
        }

        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(CodingRequest(tools)));
    }

    private static RunRequest CodingRequest(IReadOnlyList<ToolDescriptor> tools) => new(
        MissumAiProtocol.Version,
        RunMode.Coding,
        [new RunMessage("user", [new ContentPart("text", "Lege eine Kugel auf den Wuerfel und speichere.")])],
        ClientCapabilities: ["coding", "coding.evidence", "workspace", "visual-tools"],
        ClientTools: tools)
    {
        CodingOptions = new CodingRunOptions(WorkspacePath: "C:/project"),
    };

    private static List<ToolDescriptor> ReadShippedBlenderTools()
    {
        var path = Path.Combine(FindRepositoryRoot(), "extensions", "blender", "manifest", "extension.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        var action = root.GetProperty("actions")[0];
        Assert.Equal("integrations", action.GetProperty("groupId").GetString());
        Assert.Contains("coding", action.GetProperty("supportedChatModes").EnumerateArray()
            .Select(static mode => mode.GetString()).ToArray());

        var tools = new List<ToolDescriptor>();
        foreach (var entry in root.GetProperty("tools").EnumerateArray())
        {
            tools.Add(new ToolDescriptor(
                entry.GetProperty("modelToolName").GetString()!,
                entry.GetProperty("description").GetString()!,
                ReadRiskClass(entry.GetProperty("riskClass").GetString()),
                entry.GetProperty("inputSchema").Clone(),
                TimeoutSeconds: entry.GetProperty("timeoutSeconds").GetInt32(),
                MaximumOutputBytes: entry.GetProperty("maximumOutputBytes").GetInt32()));
        }

        return tools;
    }

    private static ToolRiskClass ReadRiskClass(string? value) => value switch
    {
        "localMutation" => ToolRiskClass.LocalMutation,
        "process" => ToolRiskClass.Process,
        _ => ToolRiskClass.ReadOnly,
    };

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Missum.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Das Missum-Repository wurde aus dem Testausgabeverzeichnis nicht gefunden.");
    }
}
