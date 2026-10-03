using Missum.Ai.Contracts;
using Missum.App.Services;
using System.Text.Json;

namespace Missum.Tests;

public sealed class NativeRuntimeShutdownDrainTests
{
    private static readonly Uri Gateway = new("http://localhost:8080");

    [Fact]
    public async Task ShutdownWaitsForTheSnapshotWorkloadToDrainBeforeStopping()
    {
        var waitingForSnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotSaved = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        var stops = 0;
        using var service = Create(_ => { stops++; return Task.CompletedTask; }, async (_, token) =>
        {
            if (Interlocked.Increment(ref checks) == 1) return false;
            waitingForSnapshot.TrySetResult();
            return await snapshotSaved.Task.WaitAsync(token);
        });
        var shutdown = service.StopAsync(Gateway);
        await waitingForSnapshot.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(0, stops);
        snapshotSaved.SetResult(true);
        await shutdown;
        Assert.Equal(1, stops);
        Assert.Equal(2, checks);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task UnknownOrPersistentlyBusyGatewayLeavesSharedRuntimeAlive(bool? idle)
    {
        var stops = 0;
        using var service = Create(_ => { stops++; return Task.CompletedTask; },
            (_, _) => Task.FromResult(idle), TimeSpan.FromMilliseconds(30));
        await service.StopAsync(Gateway).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, stops);
    }

    [Fact]
    public async Task AlreadyIdleGatewayStopsNormallyOnlyOnce()
    {
        var stops = 0;
        var checks = 0;
        using var service = Create(_ => { stops++; return Task.CompletedTask; }, (_, _) =>
        {
            checks++;
            return Task.FromResult<bool?>(true);
        });
        await service.StopAsync(Gateway);
        await service.StopAsync(Gateway);
        Assert.Equal(1, stops);
        Assert.Equal(1, checks);
    }

    [Fact]
    public async Task UnresponsiveProbeCannotHangShutdownOrStopRuntime()
    {
        var pending = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stops = 0;
        using var service = Create(_ => { stops++; return Task.CompletedTask; },
            (_, _) => pending.Task, TimeSpan.FromMilliseconds(30));
        await service.StopAsync(Gateway).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, stops);
    }

    [Fact]
    public async Task UnreachableGatewayNeverAuthorizesRuntimeStop()
    {
        var stops = 0;
        using var service = Create(_ => { stops++; return Task.CompletedTask; },
            (_, _) => throw new HttpRequestException("Gateway is restarting"));
        await service.StopAsync(Gateway);
        Assert.Equal(0, stops);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task ApplicationShutdownStopsOwnedRuntimeWithoutGatewayDrain(bool? idle)
    {
        var stops = 0;
        var checks = 0;
        using var service = Create(_ => { stops++; return Task.CompletedTask; }, (_, _) =>
        {
            checks++;
            return Task.FromResult(idle);
        });
        await service.StopOwnedAsync(Gateway).WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopOwnedAsync(Gateway);
        Assert.Equal(1, stops);
        Assert.Equal(0, checks);
    }

    [Fact]
    public async Task ApplicationShutdownDoesNotConsultAnUnreachableGateway()
    {
        var stops = 0;
        using var service = Create(_ => { stops++; return Task.CompletedTask; },
            (_, _) => throw new HttpRequestException("Stopped gateway"));
        await service.StopOwnedAsync(Gateway).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, stops);
    }

    [Fact]
    public async Task OwnedStopRetriesAfterAnUnsuccessfulSharedDrain()
    {
        var stops = 0;
        using var service = Create(_ => { stops++; return Task.CompletedTask; },
            (_, _) => Task.FromResult<bool?>(null));
        await service.StopAsync(Gateway);
        Assert.Equal(0, stops);
        await service.StopOwnedAsync(Gateway);
        Assert.Equal(1, stops);
    }

    [Fact]
    public async Task OwnedShutdownWaitsForPendingStartAndDisallowsLaterStarts()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var running = false;
        using var service = new NativeModelRuntimeService(_ => true, _ => Task.FromResult(running), async _ =>
        {
            starts++;
            entered.SetResult();
            await release.Task;
            running = true;
        }, stop: _ => { running = false; return Task.CompletedTask; });
        var start = service.EnsureStartedAsync(Gateway, CancellationToken.None);
        await entered.Task;
        var stop = service.StopOwnedAsync(Gateway);
        Assert.False(stop.IsCompleted);
        release.SetResult();
        await Task.WhenAll(start, stop);
        await service.EnsureStartedAsync(Gateway, CancellationToken.None);
        Assert.False(running);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task OwnedShutdownNeverStopsARemoteRuntime()
    {
        var stops = 0;
        using var service = new NativeModelRuntimeService(uri => uri.IsLoopback, _ => Task.FromResult(true),
            _ => Task.CompletedTask, stop: _ => { stops++; return Task.CompletedTask; });
        await service.StopOwnedAsync(new Uri("https://remote.example:8080"));
        Assert.Equal(0, stops);
    }

    [Fact]
    public async Task OwnedShutdownCanRetryAFailedStop()
    {
        var stops = 0;
        using var service = Create(_ =>
        {
            if (++stops == 1) throw new IOException("Temporary helper failure");
            return Task.CompletedTask;
        }, (_, _) => Task.FromResult<bool?>(true));
        await Assert.ThrowsAsync<IOException>(() => service.StopOwnedAsync(Gateway));
        await service.StopOwnedAsync(Gateway);
        Assert.Equal(2, stops);
    }

    [Fact]
    public void OwnedStopArgumentsPreserveTheProfileAndBoundCacheFlush()
    {
        var root = Path.Combine(Path.GetTempPath(), "native-stop-profile-" + Guid.NewGuid().ToString("N"));
        var profile = new AssistantRuntimeProfile("stable", "Missum", Path.Combine(root, "app"), Gateway,
            8181, 8182, Path.Combine(root, "native"), Path.Combine(root, "bin", "llama-server.exe"),
            Path.Combine(root, "stack"), "Missum.stable");
        var arguments = NativeModelRuntimeService.BuildRuntimeManagerArguments(profile, "Stop",
            Path.Combine(root, "support"), Path.Combine(root, "error.txt"), Path.Combine(root, "user"),
            shutdownTimeoutSeconds: 20);
        string Option(string name) => arguments[Array.IndexOf(arguments, name) + 1];
        Assert.Equal("Stop", Option("-Action"));
        Assert.Equal(profile.NativeStateDirectory, Option("-StateDirectory"));
        Assert.Equal("8181", Option("-Port"));
        Assert.Equal("20", Option("-ShutdownTimeoutSeconds"));
    }

    [Fact]
    public void ActualProtocolSerializerConfirmsIdleWithOmittedNullLease()
    {
        var snapshot = new GpuStatusSnapshot(true, 0, null, [], DateTimeOffset.UtcNow, ActiveWorkloads: []);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(snapshot, MissumAiProtocol.CreateJsonOptions()));
        Assert.False(json.RootElement.TryGetProperty("activeLease", out _));
        Assert.Equal(true, NativeModelRuntimeService.ReadGatewayIdle(json.RootElement));
    }

    [Theory]
    [InlineData("{\"queueLength\":0,\"activeWorkloads\":[],\"activeLease\":null}", true)]
    [InlineData("{\"queueLength\":1,\"activeWorkloads\":[]}", false)]
    [InlineData("{\"queueLength\":0,\"activeWorkloads\":[{}]}", false)]
    [InlineData("{\"queueLength\":0,\"activeWorkloads\":[],\"activeLease\":\"llm-coding\"}", false)]
    [InlineData("{\"queueLength\":0,\"activeWorkloads\":[],\"activeLease\":\"\"}", false)]
    [InlineData("{\"queueLength\":0,\"activeWorkloads\":[],\"activeLease\":{}}", null)]
    [InlineData("{\"queueLength\":0,\"activeWorkloads\":[],\"activeLease\":false}", null)]
    [InlineData("{\"queueLength\":0,\"activeLease\":null}", null)]
    [InlineData("{\"activeWorkloads\":[],\"activeLease\":null}", null)]
    [InlineData("{\"queueLength\":0,\"activeWorkloads\":null}", null)]
    [InlineData("{\"queueLength\":-1,\"activeWorkloads\":[]}", null)]
    [InlineData("null", null)]
    public void IncompleteOrBusyGpuStatusNeverConfirmsIdle(string payload, bool? expected)
    {
        using var json = JsonDocument.Parse(payload);
        Assert.Equal(expected, NativeModelRuntimeService.ReadGatewayIdle(json.RootElement));
    }

    private static NativeModelRuntimeService Create(Func<CancellationToken, Task> stop,
        Func<Uri, CancellationToken, Task<bool?>> idle, TimeSpan? timeout = null) =>
        new(_ => true, _ => Task.FromResult(true), _ => Task.CompletedTask, stop: stop,
            gatewayIdle: idle, shutdownDrainTimeout: timeout ?? TimeSpan.FromSeconds(5));
}
