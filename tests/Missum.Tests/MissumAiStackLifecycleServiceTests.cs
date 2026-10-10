using System.Text.Json;
using System.Net;
using System.Text;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Missum.Tests;

public sealed class MissumAiStackLifecycleServiceTests
{
    [Fact]
    public async Task ArtifactClientFactoryStartsOnlyHttpServicesAndNeverProbesOrStartsNativeModels()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var docker = new FakeDocker();
        await settings.UpdateAsync(current => current with { MissumAiServerUrl = docker.Profile.GatewayUri.AbsoluteUri });
        using var stack = Create(docker);
        var starts = 0;
        var probes = 0;
        using var native = new NativeModelRuntimeService(_ => true,
            _ => { probes++; return Task.FromResult(false); },
            _ => { starts++; return Task.CompletedTask; });
        var http = new ArtifactGatewayHandler(docker);
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance,
            () => http, nativeRuntime: native, stackLifecycle: stack);

        using var client = await connection.CreateArtifactClientAsync();

        Assert.Equal(docker.Profile.GatewayUri, client.BaseAddress);
        Assert.Equal(2, docker.Mutations.Count);
        Assert.Equal(["start", docker.Gateway.Id], docker.Mutations[0]);
        Assert.Equal(["start", docker.Proxy.Id], docker.Mutations[1]);
        Assert.All(docker.Containers.Where(container => container.Service is not ("gateway" or "caddy")),
            container => Assert.False(container.Running));
        Assert.Equal(0, starts);
        Assert.Equal(0, probes);
        Assert.Null(connection.NativeRuntimeError);
        Assert.Equal(1, http.Requests);
    }

    [Fact]
    public async Task ArtifactAccessStartsOnlyGatewayAndProxyAndDoesNotCacheAFullStackStart()
    {
        var docker = new FakeDocker();
        using var service = Create(docker);

        await service.EnsureGatewayStartedAsync(docker.Profile.GatewayUri);

        Assert.Equal(2, docker.Mutations.Count);
        Assert.Equal(["start", docker.Gateway.Id], docker.Mutations[0]);
        Assert.Equal(["start", docker.Proxy.Id], docker.Mutations[1]);
        Assert.All(docker.Containers.Where(container => container.Service is not ("gateway" or "caddy")),
            container => Assert.False(container.Running));
        await service.EnsureStartedAsync(docker.Profile.GatewayUri);
        Assert.Equal(3, docker.Mutations.Count);
        Assert.Equal(4, docker.Mutations[2].Length - 1);
        Assert.DoesNotContain(docker.Gateway.Id, docker.Mutations[2]);
        Assert.DoesNotContain(docker.Proxy.Id, docker.Mutations[2]);
        Assert.All(docker.Containers, container => Assert.True(container.Running));
    }

    [Fact]
    public async Task ArtifactAccessPreservesRunningWorkersAndUnrelatedContainers()
    {
        var docker = new FakeDocker();
        foreach (var container in docker.Containers.Where(container => container.Service is not ("gateway" or "caddy")))
            container.Running = true;
        docker.AddRunner(running: true);
        var foreign = docker.AddForeign();
        using var service = Create(docker);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => service.EnsureGatewayStartedAsync(docker.Profile.GatewayUri)));

        Assert.Equal(2, docker.Mutations.Count);
        Assert.All(docker.Containers, container => Assert.True(container.Running));
        Assert.All(docker.Mutations, command => Assert.DoesNotContain(foreign.Id, command));
        Assert.All(docker.Mutations, command => Assert.DoesNotContain(docker.Runner!.Id, command));
    }

    [Fact]
    public async Task ArtifactAccessCannotRestartAfterShutdownBegins()
    {
        var docker = new FakeDocker();
        using var service = Create(docker);
        service.BeginShutdown();

        await service.EnsureGatewayStartedAsync(docker.Profile.GatewayUri);

        Assert.Empty(docker.Commands);
    }

    [Fact]
    public async Task ArtifactAccessHonorsStableLocalAndDisabledOwnershipRules()
    {
        var docker = new FakeDocker();
        using (var service = Create(docker))
            await service.EnsureGatewayStartedAsync(new Uri("https://external.example:8080"));
        using (var service = new MissumAiStackLifecycleService(docker.Profile, docker.RunAsync,
                   disabled: () => true, environment: _ => null))
            await service.EnsureGatewayStartedAsync(docker.Profile.GatewayUri);
        using (var service = new MissumAiStackLifecycleService(docker.Profile with { Name = "preview" }, docker.RunAsync,
                   environment: _ => null))
            await service.EnsureGatewayStartedAsync(docker.Profile.GatewayUri);

        Assert.Empty(docker.Commands);
    }

    [Fact]
    public async Task ArtifactAccessRejectsForeignDataRootBeforeStartingAnything()
    {
        var docker = new FakeDocker { GatewayDataRoot = @"C:\another-project\data" };
        using var service = Create(docker);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureGatewayStartedAsync(docker.Profile.GatewayUri));

        Assert.Contains("anderen Missum-Datenverzeichnis", exception.Message, StringComparison.Ordinal);
        Assert.Empty(docker.Mutations);
    }

    [Fact]
    public async Task ArtifactAccessRejectsMismatchedPortAndRemoteDockerEngine()
    {
        var wrongPort = new FakeDocker { PublishedPort = 9090 };
        using (var service = Create(wrongPort))
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureGatewayStartedAsync(wrongPort.Profile.GatewayUri));
        Assert.Empty(wrongPort.Mutations);
        var remote = new FakeDocker { Endpoint = "ssh://research-server" };
        using (var service = Create(remote))
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureGatewayStartedAsync(remote.Profile.GatewayUri));
        Assert.Single(remote.Commands);
        Assert.Empty(remote.Mutations);
    }

    [Fact]
    public async Task StartsInstalledWorkersBeforeGatewayAndProxyWithoutRepositoryFiles()
    {
        var docker = new FakeDocker();
        docker.AddRunner(running: false);
        using var service = Create(docker);
        await service.EnsureStartedAsync(docker.Profile.GatewayUri);
        Assert.Equal(3, docker.Mutations.Count);
        Assert.All(docker.Mutations, command => Assert.Equal("start", command[0]));
        Assert.Equal(4, docker.Mutations[0].Length - 1);
        Assert.Equal(docker.Gateway.Id, docker.Mutations[1][1]);
        Assert.Equal(docker.Proxy.Id, docker.Mutations[2][1]);
        Assert.DoesNotContain(docker.Runner!.Id, docker.Mutations.SelectMany(command => command));
        Assert.All(docker.Containers.Where(container => container.Compose), container => Assert.True(container.Running));
    }

    [Fact]
    public async Task ConcurrentConnectionsStartOnceAndShutdownCannotRestartTheStack()
    {
        var docker = new FakeDocker();
        using var service = Create(docker);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.EnsureStartedAsync(docker.Profile.GatewayUri)));
        Assert.Equal(3, docker.Mutations.Count);
        await Task.WhenAll(service.StopAsync(docker.Profile.GatewayUri), service.StopAsync(docker.Profile.GatewayUri),
            service.EnsureStartedAsync(docker.Profile.GatewayUri));
        Assert.Single(docker.Mutations, command => command[0] == "stop");
        Assert.All(docker.Containers, container => Assert.False(container.Running));
    }

    [Fact]
    public async Task StopsOnlyOwnedProjectAndMatchingProfileRunner()
    {
        var docker = new FakeDocker(running: true);
        docker.AddRunner(running: true);
        var foreign = docker.AddForeign();
        using var service = Create(docker);
        await service.StopAsync(docker.Profile.GatewayUri);
        var stop = Assert.Single(docker.Mutations);
        Assert.Equal(["stop", "--time", "15"], stop[..3]);
        Assert.Equal(7, stop.Length - 3);
        Assert.Equal(docker.Proxy.Id, stop[3]);
        Assert.Contains(docker.Runner!.Id, stop);
        Assert.DoesNotContain(foreign.Id, stop);
        Assert.True(foreign.Running);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DifferentStackDataRootIsNeverMutated(bool starting)
    {
        var docker = new FakeDocker(running: true) { GatewayDataRoot = @"C:\another-project\data" };
        using var service = Create(docker);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => starting
            ? service.EnsureStartedAsync(docker.Profile.GatewayUri) : service.StopAsync(docker.Profile.GatewayUri));
        Assert.Contains("anderen Missum-Datenverzeichnis", exception.Message, StringComparison.Ordinal);
        Assert.Empty(docker.Mutations);
    }

    [Fact]
    public async Task DifferentGatewayPortIsNeverMutated()
    {
        var docker = new FakeDocker { PublishedPort = 9090 };
        using var service = Create(docker);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureStartedAsync(docker.Profile.GatewayUri));
        Assert.Contains("Gateway-Port", exception.Message, StringComparison.Ordinal);
        Assert.Empty(docker.Mutations);
    }

    [Fact]
    public async Task RemoteGatewayAndSmokeDoNotEvenProbeDocker()
    {
        var docker = new FakeDocker();
        using (var service = Create(docker))
        {
            await service.EnsureStartedAsync(new Uri("https://external.example:8080"));
            await service.StopAsync(new Uri("https://external.example:8080"));
        }
        using (var service = new MissumAiStackLifecycleService(docker.Profile, docker.RunAsync,
                   disabled: () => true, environment: _ => null))
        {
            await service.EnsureStartedAsync(docker.Profile.GatewayUri);
            await service.StopAsync(docker.Profile.GatewayUri);
        }
        Assert.Empty(docker.Commands);
    }

    [Fact]
    public async Task RemoteDockerContextIsNeverManagedEvenForALocalGateway()
    {
        var docker = new FakeDocker { Endpoint = "ssh://research-server" };
        using var service = Create(docker);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureStartedAsync(docker.Profile.GatewayUri));
        Assert.Contains("lokale Docker-Engine", exception.Message, StringComparison.Ordinal);
        Assert.Single(docker.Commands);
        Assert.Empty(docker.Mutations);
    }

    [Fact]
    public async Task ExplicitDockerHostIsPinnedForAllContainerCommands()
    {
        var docker = new FakeDocker();
        const string endpoint = "tcp://127.0.0.1:2375";
        using var service = new MissumAiStackLifecycleService(docker.Profile, docker.RunAsync,
            environment: name => name == "DOCKER_HOST" ? endpoint : null);
        await service.EnsureStartedAsync(docker.Profile.GatewayUri);
        Assert.All(docker.Commands, command => Assert.Equal(["--host", endpoint], command[..2]));
    }

    [Fact]
    public async Task MissingInstallHasAnActionableStartupErrorAndIdempotentShutdown()
    {
        var docker = new FakeDocker();
        docker.Containers.Clear();
        using var service = Create(docker);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureStartedAsync(docker.Profile.GatewayUri));
        Assert.Contains("deploy-ai-stack.ps1", exception.Message, StringComparison.Ordinal);
        await service.StopAsync(docker.Profile.GatewayUri);
        await service.StopAsync(docker.Profile.GatewayUri);
        Assert.Empty(docker.Mutations);
    }

    [Fact]
    public async Task ShutdownStillStopsWorkersAfterAPartialStartupFailure()
    {
        var docker = new FakeDocker { FailGatewayStart = true };
        using var service = Create(docker);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureStartedAsync(docker.Profile.GatewayUri));
        Assert.Equal(4, docker.Containers.Count(container => container.Running));
        await service.StopAsync(docker.Profile.GatewayUri);
        Assert.All(docker.Containers, container => Assert.False(container.Running));
    }

    [Fact]
    public async Task DesktopStartsOnlyOnStartupWhenTheLocalDaemonIsUnavailable()
    {
        var docker = new FakeDocker { DaemonReady = false };
        var desktopStarts = 0;
        using var service = new MissumAiStackLifecycleService(docker.Profile, docker.RunAsync,
            environment: _ => null, startDesktop: _ => { desktopStarts++; docker.DaemonReady = true; return Task.CompletedTask; },
            delay: (_, _) => Task.CompletedTask);
        await service.EnsureStartedAsync(docker.Profile.GatewayUri);
        Assert.Equal(1, desktopStarts);
        docker.DaemonReady = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StopAsync(docker.Profile.GatewayUri));
        Assert.Equal(1, desktopStarts);
    }

    [Fact]
    public async Task AutoRemovedRunnerDoesNotMakeSuccessfulShutdownFail()
    {
        var docker = new FakeDocker(running: true) { RemoveRunnerOnStop = true };
        docker.AddRunner(running: true);
        using var service = Create(docker);
        await service.StopAsync(docker.Profile.GatewayUri);
        Assert.All(docker.Containers, container => Assert.False(container.Running));
    }

    [Fact]
    public async Task RunnerEndingBetweenListingAndInspectionDoesNotPreventWorkerShutdown()
    {
        var docker = new FakeDocker(running: true) { RemoveRunnerOnInspect = true };
        docker.AddRunner(running: true);
        using var service = Create(docker);
        await service.StopAsync(docker.Profile.GatewayUri);
        Assert.All(docker.Containers, container => Assert.False(container.Running));
    }

    [Fact]
    public async Task ShutdownWaitsForAnInFlightStartThenStopsIt()
    {
        var docker = new FakeDocker();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new MissumAiStackLifecycleService(docker.Profile, async (arguments, token) =>
        {
            if (arguments.Contains("start", StringComparer.Ordinal) && !entered.Task.IsCompleted)
            { entered.SetResult(); await release.Task.WaitAsync(token); }
            return await docker.RunAsync(arguments, token);
        }, environment: _ => null);
        var start = service.EnsureStartedAsync(docker.Profile.GatewayUri);
        await entered.Task;
        var stop = service.StopAsync(docker.Profile.GatewayUri);
        Assert.False(stop.IsCompleted);
        release.SetResult();
        await Task.WhenAll(start, stop);
        Assert.All(docker.Containers, container => Assert.False(container.Running));
    }

    [Theory]
    [InlineData("npipe:////./pipe/dockerDesktopLinuxEngine", true)]
    [InlineData("npipe:////research-server/pipe/docker_engine", false)]
    [InlineData("unix:///var/run/docker.sock", true)]
    [InlineData("tcp://127.0.0.1:2375", true)]
    [InlineData("tcp://203.0.113.45:2375", false)]
    [InlineData("ssh://research-server", false)]
    public void DockerOwnershipDoesNotCrossComputers(string endpoint, bool local) =>
        Assert.Equal(local, MissumAiStackLifecycleService.IsLocalDockerEndpoint(endpoint));

    [Theory]
    [InlineData("C:/ProgramData/Missum-AI-Stack/data", true)]
    [InlineData("/run/desktop/mnt/host/c/ProgramData/Missum-AI-Stack/data", true)]
    [InlineData("C:/ProgramData/another-stack/data", false)]
    public void WindowsAndDockerDesktopBindPathsAreComparedExactly(string actual, bool expected) =>
        Assert.Equal(expected, MissumAiStackLifecycleService.PathsMatch(actual, @"C:\ProgramData\Missum-AI-Stack\data"));

    private static MissumAiStackLifecycleService Create(FakeDocker docker) => new(docker.Profile, docker.RunAsync,
        environment: _ => null);

    private sealed class ArtifactGatewayHandler(FakeDocker docker) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            Assert.Equal("/v1/health/live", request.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.True(docker.Gateway.Running);
            Assert.True(docker.Proxy.Running);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new HealthSnapshot("live", MissumAiProtocol.Version, DateTimeOffset.UtcNow),
                    MissumAiProtocol.CreateJsonOptions()), Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FakeDocker
    {
        public AssistantRuntimeProfile Profile { get; } = new("stable", "Missum", @"C:\profile", new Uri("http://127.0.0.1:8080"),
            8081, 8082, @"C:\state", @"C:\bin\llama-server.exe", @"C:\ProgramData\Missum-AI-Stack", "Missum.stable");
        public List<TestContainer> Containers { get; } = [];
        public List<string[]> Commands { get; } = [];
        public List<string[]> Mutations { get; } = [];
        public string Endpoint { get; init; } = "npipe:////./pipe/dockerDesktopLinuxEngine";
        public string GatewayDataRoot { get; init; } = @"C:\ProgramData\Missum-AI-Stack\data";
        public int PublishedPort { get; init; } = 8080;
        public bool FailGatewayStart { get; init; }
        public bool RemoveRunnerOnStop { get; init; }
        public bool RemoveRunnerOnInspect { get; init; }
        public bool DaemonReady { get; set; } = true;
        public TestContainer Gateway => Containers.Single(container => container.Service == "gateway");
        public TestContainer Proxy => Containers.Single(container => container.Service == "caddy");
        public TestContainer? Runner { get; private set; }

        public FakeDocker(bool running = false)
        {
            foreach (var service in new[] { "caddy", "gateway", "speech", "image", "media", "searxng" })
                Containers.Add(new(new string((char)('a' + Containers.Count), 64), service, compose: true, running));
        }

        public void AddRunner(bool running)
        {
            Runner = new(new string('7', 64), "research-runner", compose: false, running);
            Runner.Labels[MissumAiStackLifecycleService.ProfileLabel] = Profile.Name;
            Runner.Labels[MissumAiStackLifecycleService.StackDataRootLabel] = Profile.StackDataRoot;
            Containers.Add(Runner);
        }

        public TestContainer AddForeign()
        {
            var container = new TestContainer(new string('8', 64), "other-project", compose: false, running: true);
            container.Labels["com.docker.compose.project"] = "other-project";
            Containers.Add(container);
            return container;
        }

        public Task<MissumAiStackLifecycleService.DockerCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(arguments.ToArray());
            if (arguments[0] == "context") return Success(JsonSerializer.Serialize(Endpoint));
            var command = arguments.Skip(2).ToArray();
            if (!DaemonReady) return Task.FromResult(new MissumAiStackLifecycleService.DockerCommandResult(1, "", "Docker engine is unavailable"));
            if (command[0] == "info") return Success("28.0.0");
            if (command[0] == "ps")
            {
                var filters = command.Select((value, index) => (value, index)).Where(item => item.value == "--filter")
                    .Select(item => command[item.index + 1][6..]).Select(value => value.Split('=', 2)).ToArray();
                return Success(string.Join('\n', Containers.Where(container => filters.All(filter =>
                    container.Labels.TryGetValue(filter[0], out var value) && value == filter[1])).Select(container => container.Id)));
            }
            if (command[0] == "inspect")
            {
                var runnerRemoved = RemoveRunnerOnInspect && Runner is not null && Containers.Remove(Runner);
                var json = JsonSerializer.Serialize(Containers.Where(container => command.Contains(container.Id, StringComparer.Ordinal)).Select(container => new
                {
                Id = container.Id,
                Config = new { Labels = container.Labels },
                State = new { Running = container.Running },
                Mounts = new[] { new { Type = "bind", Source = GatewayDataRoot, Destination = "/data" } },
                HostConfig = new { PortBindings = new Dictionary<string, object> { ["8080/tcp"] = new[] { new { HostPort = PublishedPort.ToString(System.Globalization.CultureInfo.InvariantCulture), HostIp = "" } } } },
                }));
                return runnerRemoved ? Task.FromResult(new MissumAiStackLifecycleService.DockerCommandResult(1, json, "Error: No such object: " + Runner!.Id)) : Success(json);
            }
            Mutations.Add(command);
            if (command[0] == "start" && FailGatewayStart && command.Contains(Gateway.Id, StringComparer.Ordinal))
                return Task.FromResult(new MissumAiStackLifecycleService.DockerCommandResult(1, "", "Gateway could not start"));
            foreach (var container in Containers.Where(container => command.Contains(container.Id, StringComparer.Ordinal)))
                container.Running = command[0] == "start";
            if (command[0] == "stop" && RemoveRunnerOnStop && Runner is not null)
            {
                Containers.Remove(Runner);
                return Task.FromResult(new MissumAiStackLifecycleService.DockerCommandResult(1, "", "No such container"));
            }
            return Success("");
        }

        private static Task<MissumAiStackLifecycleService.DockerCommandResult> Success(string text) =>
            Task.FromResult(new MissumAiStackLifecycleService.DockerCommandResult(0, text, ""));
    }

    private sealed class TestContainer
    {
        public string Id { get; }
        public string Service { get; }
        public bool Compose { get; }
        public bool Running { get; set; }
        public Dictionary<string, string> Labels { get; } = [];
        public TestContainer(string id, string service, bool compose, bool running)
        {
            Id = id; Service = service; Compose = compose; Running = running;
            if (compose)
            {
                Labels["com.docker.compose.project"] = MissumAiStackLifecycleService.ComposeProject;
                Labels["com.docker.compose.service"] = service;
            }
        }
    }
}
