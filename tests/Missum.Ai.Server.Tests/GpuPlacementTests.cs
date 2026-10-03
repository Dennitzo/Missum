using System.Net;
using System.Text;
using System.Text.Json;
using Missum.Ai.Server.Core.Configuration;
using Missum.Ai.Server.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Missum.Ai.Server.Tests;

public sealed class GpuPlacementTests
{
    private static readonly string[] ExpectedRestoredLoads = ["coding/test", "coding/test", "coding/test@subagent"];
    private static readonly string[] ManagedTags = ["missum-gpu-policy:single-preferred-v1"];
    private static readonly string[] ReplicaTags = ["missum-gpu-policy:single-preferred-v1", "missum-agent-instance:subagent", "missum-base-model:coding/test"];
    [Theory]
    [InlineData(true, 8082)]
    [InlineData(false, 8081)]
    public async Task LoadUsesAdvertisedWindowsGpuControllerOnly(bool managed, int port)
    {
        using var handler = new PlacementHandler(managed);
        using var http = new HttpClient(handler);
        using var runtime = new ModelRuntimeClient(http, Options.Create(new MissumAiServerOptions { ModelRuntimeUri = new("http://native.test:8081") }), NullLogger<ModelRuntimeClient>.Instance);
        await runtime.EnsureModelLoadedAsync("coding/test", 0);
        Assert.Equal(port, handler.LoadUri!.Port);
        await runtime.EnsureModelLoadedAsync("coding/test", 0);
        // The managed controller reconciles the already-loaded primary and its
        // companion; a legacy router retains its original no-reload behavior.
        Assert.Equal(managed ? 2 : 1, handler.Loads);
    }

    [Fact]
    public async Task ResidentPrimaryRemainsUsableWhenCompanionControllerFails()
    {
        using var handler = new PlacementHandler(true) { FailResidentReconciliation = true };
        using var http = new HttpClient(handler);
        using var runtime = new ModelRuntimeClient(http, Options.Create(new MissumAiServerOptions { ModelRuntimeUri = new("http://native.test:8081") }), NullLogger<ModelRuntimeClient>.Instance);
        await runtime.EnsureModelLoadedAsync("coding/test", 0);
        var prepared = await runtime.EnsureModelPreparedAsync("coding/test", 0, _ => throw new InvalidOperationException("A resident primary must not display a new main-model loading status."));
        Assert.Equal("coding/test", prepared.InstanceId);
        Assert.True(prepared.WasAlreadyLoaded);
        Assert.Equal(3, handler.Loads);
    }

    [Fact]
    public async Task ResidentPrimaryReconcilesMissingCompanionAfterDroppedControlResponse()
    {
        using var handler = new PlacementHandler(true) { DropResidentResponse = true };
        using var runtime = new ModelRuntimeClient(new HttpClient(handler), Options.Create(new MissumAiServerOptions
            { ModelRuntimeUri = new("http://native.test:8081") }), NullLogger<ModelRuntimeClient>.Instance);
        await runtime.EnsureModelLoadedAsync("coding/test", 0);
        var prepared = await runtime.EnsureModelPreparedAsync("coding/test", 0, null);
        Assert.True(prepared.WasAlreadyLoaded);
        Assert.Equal(3, handler.Loads);
    }

    [Fact]
    public async Task ChildRestoreReconcilesDroppedBasePairLoadBeforePreparingChild()
    {
        using var handler = new PlacementHandler(true) { DropInitialResponse = true, IncludeReplica = true };
        using var runtime = new ModelRuntimeClient(new HttpClient(handler), Options.Create(new MissumAiServerOptions
            { ModelRuntimeUri = new("http://native.test:8081") }), NullLogger<ModelRuntimeClient>.Instance);
        var prepared = await runtime.EnsureModelPreparedAsync("coding/test", 0, null, "coding/test@subagent");
        Assert.Equal("coding/test@subagent", prepared.InstanceId);
        Assert.Equal(ExpectedRestoredLoads, handler.LoadedModels);
    }

    [Fact]
    public async Task ManagedColdLoadReconcilesBothSlotsAfterDroppedControlResponse()
    {
        using var handler = new PlacementHandler(true) { DropInitialResponse = true };
        using var http = new HttpClient(handler);
        using var runtime = new ModelRuntimeClient(http, Options.Create(new MissumAiServerOptions { ModelRuntimeUri = new("http://native.test:8081") }), NullLogger<ModelRuntimeClient>.Instance);
        Assert.Equal("coding/test", await runtime.EnsureModelLoadedAsync("coding/test", 0));
        Assert.Equal(2, handler.Loads);
        Assert.Equal(8082, handler.LoadUri!.Port);
    }

    private sealed class PlacementHandler(bool managed) : HttpMessageHandler
    {
        internal Uri? LoadUri;
        internal int Loads;
        internal bool FailResidentReconciliation { get; init; }
        internal bool DropInitialResponse { get; init; }
        internal bool DropResidentResponse { get; init; }
        internal bool IncludeReplica { get; init; }
        internal List<string> LoadedModels { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string response;
            if (request.RequestUri!.AbsolutePath == "/models/load")
            {
                if (managed) Assert.True(request.Content!.Headers.ContentLength > 0);
                LoadUri = request.RequestUri;
                Loads++;
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync(token).GetAwaiter().GetResult());
                LoadedModels.Add(body.RootElement.GetProperty("model").GetString()!);
                if (DropInitialResponse && Loads == 1 || DropResidentResponse && Loads == 2)
                    throw new HttpRequestException("Control response dropped after the primary was allocated.");
                if (FailResidentReconciliation && Loads > 1)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Companion control unavailable") });
                response = "{}";
            }
            else if (request.RequestUri.AbsolutePath == "/props") response = """{"default_generation_settings":{"n_ctx":32768}}""";
            else if (IncludeReplica)
                response = JsonSerializer.Serialize(new { data = new[] {
                    new { id = "coding/test", status = new { value = Loads > 0 ? "loaded" : "unloaded" }, tags = ManagedTags },
                    new { id = "coding/test@subagent", status = new { value = Loads > 0 ? "loaded" : "unloaded" }, tags = ReplicaTags } } });
            else
                response = "{\"data\":[{\"id\":\"coding/test\",\"status\":{\"value\":\"" + (Loads > 0 ? "loaded" : "unloaded") + "\"},\"tags\":[\"" + (managed ? "missum-gpu-policy:single-preferred-v1" : "legacy") + "\"]}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") });
        }
    }
}
