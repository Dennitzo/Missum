using Missum.App.Services;

namespace Missum.Tests;

public sealed class NativeModelRootRuntimeTests
{
    private const string Nas = @"\\umbrel.local\Melow's Umbrel\Huggingface";
    private static readonly Uri Gateway = new("http://localhost:8080");

    [Fact]
    public async Task SavedRootChangeBypassesProbeCacheAndRestartsOnlyIdleOwnedRuntime()
    {
        var desired = @"C:\models";
        var actual = desired;
        var running = true;
        var stops = 0;
        var starts = 0;
        var validations = 0;
        using var service = new NativeModelRuntimeService(_ => true, _ => Task.FromResult(running),
            _ => throw new InvalidOperationException("Must start with the captured selected folder"),
            stop: _ => { stops++; running = false; return Task.CompletedTask; },
            gatewayIdle: (_, _) => Task.FromResult<bool?>(true),
            modelRoot: () => desired, runningModelRoot: _ => Task.FromResult<string?>(actual),
            startAtModelRoot: (root, _) => { starts++; actual = root; running = true; return Task.CompletedTask; },
            validateModelRoot: (_, _) => { validations++; return Task.CompletedTask; });

        await service.EnsureStartedAsync(Gateway, CancellationToken.None);
        desired = Nas;
        await service.EnsureStartedAsync(Gateway, CancellationToken.None);
        await service.EnsureStartedAsync(Gateway, CancellationToken.None);
        Assert.Equal(Nas, actual);
        Assert.Equal((1, 1, 1), (stops, starts, validations));
        // Reconfiguration must not freeze the service's normal shutdown/start state.
        await service.StopAsync(Gateway);
        Assert.Equal(2, stops);
        Assert.False(running);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task ActiveOrUnknownGatewayKeepsModelAndKvUntilIdle(bool? initialIdle)
    {
        var now = DateTimeOffset.UtcNow;
        var idle = initialIdle;
        var actual = @"C:\models";
        var stops = 0;
        var validations = 0;
        using var service = new NativeModelRuntimeService(_ => true, _ => Task.FromResult(true), _ => Task.CompletedTask,
            now: () => now, stop: _ => { stops++; return Task.CompletedTask; },
            gatewayIdle: (_, _) => Task.FromResult(idle), modelRoot: () => Nas,
            runningModelRoot: _ => Task.FromResult<string?>(actual),
            startAtModelRoot: (root, _) => { actual = root; return Task.CompletedTask; },
            validateModelRoot: (_, _) => { validations++; return Task.CompletedTask; });
        await service.EnsureStartedAsync(Gateway, CancellationToken.None);
        Assert.Equal(@"C:\models", actual);
        Assert.Equal((0, 0), (stops, validations));
        now = now.AddSeconds(3);
        idle = true;
        await service.EnsureStartedAsync(Gateway, CancellationToken.None);
        Assert.Equal(Nas, actual);
        Assert.Equal((1, 1), (stops, validations));
    }

    [Fact]
    public async Task UnreachableNasPreservesUsableRuntimeAndCorrectedPreferenceRetriesImmediately()
    {
        var desired = Nas;
        var actual = @"C:\models";
        var reachable = false;
        var stops = 0;
        using var service = new NativeModelRuntimeService(_ => true, _ => Task.FromResult(true), _ => Task.CompletedTask,
            stop: _ => { stops++; return Task.CompletedTask; }, gatewayIdle: (_, _) => Task.FromResult<bool?>(true),
            modelRoot: () => desired, runningModelRoot: _ => Task.FromResult<string?>(actual),
            startAtModelRoot: (root, _) => { actual = root; return Task.CompletedTask; },
            validateModelRoot: (_, _) => reachable ? Task.CompletedTask : throw new DirectoryNotFoundException("NAS offline"));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => service.EnsureStartedAsync(Gateway, CancellationToken.None));
        Assert.Equal(0, stops);
        Assert.Equal(@"C:\models", actual);
        reachable = true;
        desired = @"D:\models";
        await service.EnsureStartedAsync(Gateway, CancellationToken.None);
        Assert.Equal(desired, actual);
        Assert.Equal(1, stops);
    }

    [Fact]
    public async Task NewWorkDuringNetworkProbeDefersSwitchWithoutStoppingRuntime()
    {
        var idle = true;
        var stops = 0;
        using var service = new NativeModelRuntimeService(_ => true, _ => Task.FromResult(true), _ => Task.CompletedTask,
            stop: _ => { stops++; return Task.CompletedTask; }, gatewayIdle: (_, _) => Task.FromResult<bool?>(idle),
            modelRoot: () => Nas, runningModelRoot: _ => Task.FromResult<string?>(@"C:\models"),
            validateModelRoot: (_, _) => { idle = false; return Task.CompletedTask; });
        await service.EnsureStartedAsync(Gateway, CancellationToken.None);
        Assert.Equal(0, stops);
    }

    [Fact]
    public async Task HealthyUnidentifiedRuntimeIsNeverStoppedForFolderChange()
    {
        var stops = 0;
        using var service = new NativeModelRuntimeService(_ => true, _ => Task.FromResult(true), _ => Task.CompletedTask,
            stop: _ => { stops++; return Task.CompletedTask; }, gatewayIdle: (_, _) => Task.FromResult<bool?>(true),
            modelRoot: () => Nas, runningModelRoot: _ => Task.FromResult<string?>(null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureStartedAsync(Gateway, CancellationToken.None));
        Assert.Equal(0, stops);
    }

    [Fact]
    public async Task ColdStartupUsesCapturedSavedFolder()
    {
        var running = false;
        string? selected = null;
        using var service = new NativeModelRuntimeService(_ => true, _ => Task.FromResult(running),
            _ => throw new InvalidOperationException("Unexpected legacy path"), modelRoot: () => Nas,
            startAtModelRoot: (root, _) => { selected = root; running = true; return Task.CompletedTask; });
        await service.EnsureStartedAsync(Gateway, CancellationToken.None);
        Assert.Equal(Nas, selected);
    }

    [Fact]
    public void ManagerReceivesUncFolderAsOneLiteralArgumentAndKeepsCacheLocal()
    {
        var profile = new AssistantRuntimeProfile("stable", "Missum", @"C:\app-data", Gateway, 8081, 8082,
            @"C:\native-state", @"C:\native\llama-server.exe", @"C:\stack", "test");
        var arguments = NativeModelRuntimeService.BuildRuntimeManagerArguments(profile, "Start", @"C:\support",
            @"C:\native-state\error.txt", @"C:\user", nativeModelRoot: Nas);
        Assert.Equal(Nas, arguments[Array.IndexOf(arguments, "-ModelRoot") + 1]);
        Assert.Equal(profile.NativeStateDirectory, arguments[Array.IndexOf(arguments, "-StateDirectory") + 1]);
    }
}
