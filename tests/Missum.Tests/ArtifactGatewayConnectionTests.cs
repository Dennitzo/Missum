using System.Net;
using System.Text;
using Missum.Ai.Client;
using Missum.App.Services;
using Missum.Core.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Missum.Tests;

public sealed class ArtifactGatewayConnectionTests
{
    [Fact]
    public async Task OriginalClientWaitsThroughProxyStartupWithoutStartingNativeRuntime()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with { MissumAiServerUrl = "http://127.0.0.1:8080" });
        var nativeStarts = 0;
        using var runtime = new NativeModelRuntimeService(_ => true, _ => Task.FromResult(false), _ =>
        { nativeStarts++; throw new InvalidOperationException("Opening an original must not start the model."); });
        using var handler = new ArtifactHandler();
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance,
            () => handler, nativeRuntime: runtime);

        using var client = await connection.CreateArtifactClientAsync();
        var original = await client.ExportOriginalUploadAsync("upload-image");

        Assert.Equal(0, nativeStarts);
        Assert.Equal("original", original.Metadata!["role"]);
        Assert.Equal(["/v1/health/live", "/v1/health/live", "/v1/uploads/upload-image/original-artifact"], handler.Paths);
    }

    [Theory]
    [InlineData("http://example.invalid:8080", "1.0")]
    [InlineData("http://127.0.0.1:8080", "wrong")]
    public async Task FailedArtifactAdmissionDisposesClientAndDoesNotExport(string address, string protocol)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with { MissumAiServerUrl = address });
        using var handler = new ArtifactHandler(protocol);
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance, () => handler);

        if (protocol == "wrong")
            await Assert.ThrowsAsync<InvalidOperationException>(() => connection.CreateArtifactClientAsync());
        else
            await Assert.ThrowsAsync<MissumAiApiException>(() => connection.CreateArtifactClientAsync());

        Assert.True(handler.IsDisposed);
        Assert.All(handler.Paths, path => Assert.Equal("/v1/health/live", path));
    }

    [Fact]
    public async Task LeavingTheCardCancelsArtifactAdmissionAndDisposesTheClient()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with { MissumAiServerUrl = "http://127.0.0.1:8080" });
        using var cancellation = new CancellationTokenSource();
        using var handler = new ArtifactHandler(onFirstProbe: cancellation.Cancel);
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance, () => handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.CreateArtifactClientAsync(cancellation.Token));

        Assert.True(handler.IsDisposed);
        Assert.Single(handler.Paths);
    }

    private sealed class ArtifactHandler(string protocol = "1.0", Action? onFirstProbe = null) : HttpMessageHandler
    {
        internal List<string> Paths { get; } = [];
        internal bool IsDisposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path == "/v1/health/live")
            {
                if (Paths.Count == 1 && protocol == "1.0")
                {
                    onFirstProbe?.Invoke();
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
                }
                return Task.FromResult(Json($$"""{"status":"live","protocolVersion":"{{protocol}}","timestamp":"2026-10-10T00:00:00Z"}"""));
            }
            Assert.Equal("/v1/uploads/upload-image/original-artifact", path);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(3, Paths.Count);
            return Task.FromResult(Json("""{"artifactId":"original-image","fileName":"Original.png","mediaType":"image/png","length":10,"sha256":"0123456789abcdef","metadata":{"role":"original","sourceUploadId":"upload-image"}}"""));
        }

        private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
        { Content = new StringContent(content, Encoding.UTF8, "application/json") };

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
