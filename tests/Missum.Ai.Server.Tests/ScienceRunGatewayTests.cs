using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Gateway;
using Missum.Ai.Server.Core.Runs;
using Missum.Ai.Server.Core.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Missum.Ai.Server.Tests;

public sealed class ScienceRunGatewayTests
{
    [Theory]
    [InlineData(RunMode.Auto)]
    [InlineData(RunMode.General)]
    public async Task NewScienceProjectIsAcceptedThroughThePublicRunRouteWithDeliverablesVerification(RunMode mode)
    {
        await using var gateway = new GatewayFixture();
        var request = gateway.ScienceRequest(mode);

        var response = await gateway.SubmitAsync(request);

        Assert.Equal(StatusCodes.Status202Accepted, response.Status);
        var accepted = response.Body.Deserialize<RunAccepted>(MissumAiProtocol.CreateJsonOptions())!;
        Assert.Equal(RunState.Queued, accepted.State);
        var stored = (await gateway.Repository.GetRequestAsync(accepted.RunId))!;
        Assert.Equal(request.SessionId, stored.SessionId);
        Assert.Equal(request.WorkspacePath, stored.WorkspacePath);
        Assert.Equal(request.ResearchOptions, stored.ResearchOptions);
        Assert.Contains("research.deliverables", stored.ClientCapabilities!);
        var tools = new AgentToolCatalog().GetAvailableTools(stored);
        var verifier = Assert.Single(tools, tool => tool.Name == ClientToolNames.ResearchDeliverablesVerify);
        Assert.Equal(ToolRiskClass.ReadOnly, verifier.RiskClass);
        Assert.Contains(tools, tool => tool.Name == "research.code.write");
        Assert.Contains(tools, tool => tool.Name == "research.code.execute");
        Assert.Contains(tools, tool => tool.Name == "math.formalProof");
        Assert.True(ScientificRunCompletionPolicy.Applies(stored));
        Assert.False(ScientificRunCompletionPolicy.Assess(stored, [], tools).Complete);
    }

    [Fact]
    public async Task UnregisteredResearchCapabilityIsStillRejectedByThePublicRunRoute()
    {
        await using var gateway = new GatewayFixture();
        var request = gateway.ScienceRequest(RunMode.General);
        request = request with { ClientCapabilities = [.. request.ClientCapabilities!, "research.deliverables.unknown"] };

        var response = await gateway.SubmitAsync(request);

        Assert.Equal(StatusCodes.Status400BadRequest, response.Status);
        var problem = response.Body.Deserialize<MissumAiProblem>(MissumAiProtocol.CreateJsonOptions())!;
        Assert.Equal("request.invalid_argument", problem.ErrorCode);
        Assert.Contains("research.deliverables.unknown", problem.Detail, StringComparison.Ordinal);
    }

    private sealed class GatewayFixture : IAsyncDisposable
    {
        private readonly TestServerContext _context = new();
        private readonly ServiceProvider _services;
        private readonly RequestDelegate _route;
        internal RunRepository Repository { get; }

        internal GatewayFixture()
        {
            Repository = new RunRepository(_context.Database, new RunEventNotifier());
            _services = new ServiceCollection().AddLogging().AddRouting()
                .AddSingleton(Repository).AddSingleton<RunWorkChannel>()
                .AddSingleton(new ServerRuntimeState(_context.WrappedOptions)).BuildServiceProvider();
            var routes = new RouteBuilder(_services);
            GatewayEndpoints.Map(routes);
            var endpoint = Assert.Single(routes.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
                endpoint => endpoint.RoutePattern.RawText == "/v1/runs"
                    && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST"));
            _route = endpoint.RequestDelegate!;
        }

        internal RunRequest ScienceRequest(RunMode mode) => new(MissumAiProtocol.Version, mode,
            [new("user", [new("text", "Untersuche einen Oszillator mit Publikation und Python-Abbildung.")])],
            ClientCapabilities: ["documentIo", "documents", "visual-tools", "coding", "coding.evidence", "coding.process",
                "workspace", "workspace.open", "research.sandbox", "research.deliverables"],
            SessionId: "science-new-project", WorkspacePath: _context.Root,
            AllowedServerTools: ["web.search", "web.fetch", "web.deepResearch"], DeepResearch: true,
            ConversationProfile: ConversationProfile.General,
            ResearchOptions: new(ProjectId: "research-new-project", Profile: DeepResearchProfile.ScientificEvidence,
                AutonomyLevel: ResearchAutonomyLevel.SandboxResearch));

        internal async Task<(int Status, JsonElement Body)> SubmitAsync(RunRequest input)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(input, MissumAiProtocol.CreateJsonOptions());
            var http = new DefaultHttpContext { RequestServices = _services };
            http.Request.Method = "POST";
            http.Request.Path = "/v1/runs";
            http.Request.ContentType = "application/json";
            http.Request.ContentLength = body.Length;
            await using var request = new MemoryStream(body);
            await using var response = new MemoryStream();
            http.Request.Body = request;
            http.Response.Body = response;
            await new ProblemDetailsMiddleware(_route).InvokeAsync(http, _services.GetRequiredService<ServerRuntimeState>());
            return (http.Response.StatusCode, JsonSerializer.Deserialize<JsonElement>(response.ToArray()));
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            _context.Dispose();
        }
    }

    private sealed class RouteBuilder(IServiceProvider services) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = services;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }
}
