using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Missum.Ai.Server.Core.Configuration;
using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Tests;

public sealed class NativeModelRootMetadataTests
{
    private const string ConfiguredRoot = @"C:\old-model-cache\hub";
    private const string NasRoot = @"\\umbrel.local\Melow's Umbrel\Huggingface";
    private static readonly string[] ModelTags = ["missum-context-train:32768"];

    [Fact]
    public async Task CatalogReportsCurrentWindowsRootWithoutReinterpretingUncOnGatewayHost()
    {
        using var handler = new RuntimeHandler(JsonSerializer.Serialize(new { modelRoot = NasRoot,
            discoveryRoot = NasRoot + @"\hub" }));
        using var http = new HttpClient(handler);
        using var runtime = CreateRuntime(http);

        var catalog = await runtime.GetCodingModelsAsync();

        Assert.True(catalog.RuntimeReachable);
        Assert.Single(catalog.Models);
        Assert.Equal(NasRoot, catalog.ModelRoot);
        Assert.Equal(new Uri("http://native.test:8082/runtime"), handler.MetadataUri);
        Assert.Equal(HttpMethod.Get, handler.MetadataMethod);
        Assert.False(handler.MetadataHadBody);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("{\"modelRoot\":null}")]
    [InlineData("{\"modelRoot\":42}")]
    [InlineData("{\"modelRoot\":\"   \"}")]
    [InlineData("{\"modelRoot\":\"C:\\nunsafe\"}")]
    public async Task InvalidOptionalMetadataKeepsLegacyRootAndUsableCatalog(string metadata)
    {
        using var handler = new RuntimeHandler(metadata);
        using var http = new HttpClient(handler);
        using var runtime = CreateRuntime(http);

        var catalog = await runtime.GetCodingModelsAsync();

        Assert.True(catalog.RuntimeReachable);
        Assert.Single(catalog.Models);
        Assert.Equal(ConfiguredRoot, catalog.ModelRoot);
        Assert.Null(catalog.Message);
    }

    [Theory]
    [InlineData("not-found")]
    [InlineData("unreachable")]
    [InlineData("timeout")]
    public async Task OlderOrUnavailableSupervisorDoesNotBreakReachableModelCatalog(string mode)
    {
        using var handler = new RuntimeHandler(mode: mode);
        using var http = new HttpClient(handler);
        using var runtime = CreateRuntime(http);

        var catalog = await runtime.GetCodingModelsAsync();

        Assert.True(catalog.RuntimeReachable);
        Assert.Single(catalog.Models);
        Assert.Equal(ConfiguredRoot, catalog.ModelRoot);
    }

    [Fact]
    public async Task UserCancellationWhileReadingOptionalMetadataIsNotSwallowed()
    {
        using var handler = new RuntimeHandler(mode: "hold");
        using var http = new HttpClient(handler);
        using var runtime = CreateRuntime(http);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = runtime.GetCodingModelsAsync(cancellation.Token);
        await handler.MetadataEntered.Task.WaitAsync(cancellation.Token);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task ModelRootChangesAreObservedWithoutRestartingTheGatewayClient()
    {
        using var handler = new RuntimeHandler(JsonSerializer.Serialize(new { modelRoot = NasRoot }));
        using var http = new HttpClient(handler);
        using var runtime = CreateRuntime(http);
        Assert.Equal(NasRoot, (await runtime.GetCodingModelsAsync()).ModelRoot);

        handler.Metadata = JsonSerializer.Serialize(new { modelRoot = @"D:\Other Models" });

        Assert.Equal(@"D:\Other Models", (await runtime.GetCodingModelsAsync()).ModelRoot);
    }

    [Fact]
    public async Task LegacyRouterOnLastValidPortDoesNotRequireANativeControlPort()
    {
        using var handler = new RuntimeHandler();
        using var http = new HttpClient(handler);
        using var runtime = CreateRuntime(http, "http://native.test:65535");

        var catalog = await runtime.GetCodingModelsAsync();

        Assert.True(catalog.RuntimeReachable);
        Assert.Equal(ConfiguredRoot, catalog.ModelRoot);
        Assert.Null(handler.MetadataUri);
    }

    private static ModelRuntimeClient CreateRuntime(HttpClient http, string endpoint = "http://native.test:8081") => new(http,
        Options.Create(new MissumAiServerOptions { ModelRuntimeUri = new Uri(endpoint),
            CodingModelRoot = ConfiguredRoot }), NullLogger<ModelRuntimeClient>.Instance);

    private sealed class RuntimeHandler(string? metadata = null, string? mode = null) : HttpMessageHandler
    {
        public string Metadata { get; set; } = metadata ?? "{}";
        public Uri? MetadataUri { get; private set; }
        public HttpMethod? MetadataMethod { get; private set; }
        public bool MetadataHadBody { get; private set; }
        public TaskCompletionSource MetadataEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/runtime")
            {
                MetadataUri = request.RequestUri;
                MetadataMethod = request.Method;
                MetadataHadBody = request.Content is not null;
                MetadataEntered.TrySetResult();
                if (mode == "not-found") return new(HttpStatusCode.NotFound);
                if (mode == "unreachable") throw new HttpRequestException("Old supervisor unavailable");
                if (mode == "timeout") throw new OperationCanceledException("Optional metadata timeout");
                if (mode == "hold") await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Json(Metadata);
            }
            Assert.Equal("/v1/models", request.RequestUri.AbsolutePath);
            return Json(JsonSerializer.Serialize(new { data = new[] { new
            {
                id = "coding/Fixture-Q4~abc123", tags = ModelTags,
                status = new { value = "unloaded" },
            } } }));
        }

        private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
        { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}
