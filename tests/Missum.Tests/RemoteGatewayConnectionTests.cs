using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Missum.App.Services;
using Missum.Core.Contracts;

namespace Missum.Tests;

public sealed partial class MissumAiStackLifecycleServiceTests
{
    [Fact]
    public async Task RemoteGatewayProvidesModelsWithoutStartingOrStoppingClientServices()
    {
        const string remoteAddress = "http://missum-server.invalid:8080/";
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with { MissumAiServerUrl = remoteAddress });
        var docker = new FakeDocker(running: true);
        using var stack = Create(docker);
        var nativeProbes = 0;
        var nativeStarts = 0;
        var nativeStops = 0;
        using var native = new NativeModelRuntimeService(NativeModelRuntimeService.IsLocalGateway,
            _ => { nativeProbes++; return Task.FromResult(false); },
            _ => { nativeStarts++; return Task.CompletedTask; },
            stopOwned: _ => { nativeStops++; return Task.CompletedTask; });
        using var handler = new RemoteModelHandler();
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance,
            () => handler, nativeRuntime: native, stackLifecycle: stack);

        using var client = await connection.CreateClientAsync();
        var catalog = await client.GetCodingModelsAsync();
        await stack.StopAsync(client.BaseAddress!);
        await native.StopOwnedAsync(client.BaseAddress!);

        Assert.Equal(new Uri(remoteAddress), client.BaseAddress);
        Assert.Equal(new Uri(remoteAddress + "v1/models/coding"), handler.RequestUri);
        Assert.True(catalog.RuntimeReachable);
        Assert.Equal("coding/remote-model", Assert.Single(catalog.Models).Id);
        Assert.Empty(docker.Commands);
        Assert.Equal(0, nativeProbes);
        Assert.Equal(0, nativeStarts);
        Assert.Equal(0, nativeStops);
    }

    private sealed class RemoteModelHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"models":[{"id":"coding/remote-model","role":"coding","downloaded":true,
                    "loaded":false,"state":"ready","contextTokens":262144}],"modelRoot":"server-cache",
                    "runtimeReachable":true,"updatedAt":"2026-11-14T00:00:00Z"}
                    """, Encoding.UTF8, "application/json")
            });
        }
    }
}
