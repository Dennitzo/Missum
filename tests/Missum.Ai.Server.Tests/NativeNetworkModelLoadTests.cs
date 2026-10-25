using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Missum.Ai.Server.Core.Configuration;
using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Tests;

public sealed class NativeNetworkModelLoadTests
{
    private const string ModelId = "coding/NasFixture-Q4~abc123";

    [Theory]
    [InlineData(false, false, 5)]
    [InlineData(false, true, 10)]
    [InlineData(true, false, -1)]
    [InlineData(true, true, -1)]
    public void NetworkStorageIsReadFromCatalogAndOnlyNetworkLoadsLoseTheLocalDeadline(
        bool networkStorage, bool managedPlacement, int expectedMinutes)
    {
        var model = ReadModel(networkStorage, managedPlacement);

        Assert.Equal(networkStorage, model.NetworkStorage);
        Assert.Equal(expectedMinutes < 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromMinutes(expectedMinutes),
            ModelRuntimeClient.ResolveModelLoadTimeout(model));
    }

    [Theory]
    [InlineData(false, false, "unloaded", 5)]
    [InlineData(false, true, "unloaded", 10)]
    [InlineData(false, true, "loaded", 5)]
    [InlineData(true, true, "unloaded", -1)]
    [InlineData(true, true, "loaded", -1)]
    public void ChildRecoveryIncludesItsOwningModelsStorageAndRetainsLocalPairBudget(
        bool ownerNetworkStorage, bool ownerManagedPlacement, string ownerState, int expectedMinutes)
    {
        var child = ReadModel(false, false, child: true);
        var owner = ReadModel(ownerNetworkStorage, ownerManagedPlacement, state: ownerState);

        Assert.Equal(expectedMinutes < 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromMinutes(expectedMinutes),
            ModelRuntimeClient.ResolveModelLoadTimeout(child, owner));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserCancellationStillStopsANetworkLoadAndReleasesTheRuntimeGate(bool managedPlacement)
    {
        using var handler = new LoadingHandler(managedPlacement);
        using var http = new HttpClient(handler);
        using var runtime = CreateRuntime(http);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var loading = runtime.EnsureModelLoadedAsync(ModelId, 32768, stop.Token);
        await handler.LoadEntered.Task.WaitAsync(stop.Token);

        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        Assert.True(handler.Cancelled);
        Assert.Equal(1, handler.LoadCalls);
        Assert.True((await runtime.GetStatusAsync()).ProviderReachable);
    }

    [Fact]
    public async Task UnexpectedTransportCancellationDoesNotInventAZeroMinuteNetworkTimeout()
    {
        using var handler = new LoadingHandler(false, transportCancelled: true);
        using var http = new HttpClient(handler);
        using var runtime = CreateRuntime(http);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.EnsureModelLoadedAsync(ModelId, 32768));

        Assert.Contains("Netzwerkmodells", failure.Message);
        Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
        Assert.Equal(1, handler.LoadCalls);
    }

    private static ModelRuntimeClient.RuntimeModel ReadModel(bool networkStorage, bool managedPlacement,
        bool child = false, string state = "unloaded") => Assert.Single(ModelRuntimeClient.ReadRuntimeModels(
            JsonSerializer.SerializeToElement(Catalog(networkStorage, managedPlacement, child, state))));

    private static object Catalog(bool networkStorage, bool managedPlacement, bool child = false, string state = "unloaded")
    {
        var tags = new List<string> { "missum-context-train:32768" };
        if (networkStorage) tags.Add("missum-model-storage:network");
        if (managedPlacement) tags.Add("missum-gpu-policy:single-preferred-v1");
        if (child) { tags.Add("missum-agent-instance:subagent"); tags.Add("missum-base-model:" + ModelId); }
        return new { data = new[] { new { id = child ? ModelId + "@subagent" : ModelId, tags, status = new { value = state } } } };
    }

    private static ModelRuntimeClient CreateRuntime(HttpClient http) => new(http,
        Options.Create(new MissumAiServerOptions { ModelRuntimeUri = new Uri("http://native.test:8081") }),
        NullLogger<ModelRuntimeClient>.Instance);

    private sealed class LoadingHandler(bool managedPlacement, bool transportCancelled = false) : HttpMessageHandler
    {
        public TaskCompletionSource LoadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public int LoadCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/v1/models": return Json(Catalog(true, managedPlacement));
                case "/models/load":
                    LoadCalls++;
                    LoadEntered.TrySetResult();
                    if (transportCancelled) throw new OperationCanceledException("Native transport interrupted.");
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                    catch (OperationCanceledException) { Cancelled = true; throw; }
                    throw new InvalidOperationException("The held model load completed unexpectedly.");
                default: throw new InvalidOperationException("Unexpected runtime operation " + request.RequestUri);
            }
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
