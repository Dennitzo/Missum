using Missum.App.Services;

namespace Missum.Tests;

public sealed partial class MissumAiStackLifecycleServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContainerdPlatformManifestIdentifiesUpdateEvenWhenContainerConfigIdCannotBeInspected(bool sameImage)
    {
        var docker = new FakeDocker { ImageUpdateAvailable = true, ContainerdImageDescriptors = true, EquivalentImageWithDifferentIds = sameImage };
        var originalId = docker.Gateway.Id;
        using var service = Create(docker);
        await service.EnsureGatewayStartedAsync(docker.Profile.GatewayUri);
        Assert.Equal(sameImage, docker.Gateway.Id == originalId);
        Assert.Equal(sameImage ? 0 : 1, docker.CreatedConfigurations.Count);
        Assert.DoesNotContain(docker.Commands, command => command.Contains("image", StringComparer.Ordinal)
            && !command.Contains("--platform", StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartUpdatesAnOwnedStoppedGatewayToTheLocallyBuiltImage(bool artifactsOnly)
    {
        var docker = new FakeDocker { ImageUpdateAvailable = true };
        var originalId = docker.Gateway.Id;
        using var service = Create(docker);

        if (artifactsOnly) await service.EnsureGatewayStartedAsync(docker.Profile.GatewayUri);
        else await service.EnsureStartedAsync(docker.Profile.GatewayUri);

        Assert.Single(docker.CreatedConfigurations);
        Assert.DoesNotContain(docker.Containers, container => container.Id == originalId);
        Assert.Equal("sha256:new", docker.Gateway.Image);
        Assert.Equal("missum-ai-gateway", docker.Gateway.Name);
        Assert.True(docker.Gateway.Running);
        Assert.True(docker.Proxy.Running);
        Assert.DoesNotContain(docker.Mutations, command => command[0] is "stop" or "compose" || command.Contains("--force", StringComparer.Ordinal));
        Assert.True(docker.Mutations.FindIndex(command => command[0] == "start" && command.Contains(docker.Gateway.Id, StringComparer.Ordinal))
            < docker.Mutations.FindIndex(command => command[0] == "rm" && command.Contains(originalId, StringComparer.Ordinal)));
        if (artifactsOnly)
            Assert.All(docker.Containers.Where(container => container.Service is not ("gateway" or "caddy")), container => Assert.False(container.Running));
    }

    [Fact]
    public async Task UpdatePreservesInstalledEnvironmentVolumesSecurityPortsAndConfiguredNetworksWithoutRepositoryFiles()
    {
        var docker = new FakeDocker { ImageUpdateAvailable = true };
        using var service = Create(docker);
        await service.EnsureGatewayStartedAsync(docker.Profile.GatewayUri);

        var configuration = Assert.Single(docker.CreatedConfigurations);
        Assert.Equal("missum-ai/gateway:2.0.0", configuration.GetProperty("Image").GetString());
        Assert.Contains("CUSTOM_SETTING=preserved", configuration.GetProperty("Env").EnumerateArray().Select(value => value.GetString()));
        var host = configuration.GetProperty("HostConfig");
        Assert.True(host.GetProperty("ReadonlyRootfs").GetBoolean());
        Assert.Equal(docker.GatewayDataRoot + ":/data:rw", host.GetProperty("Binds")[0].GetString());
        Assert.Equal("no-new-privileges:true", host.GetProperty("SecurityOpt")[0].GetString());
        Assert.Equal("8080", host.GetProperty("PortBindings").GetProperty("8080/tcp")[0].GetProperty("HostPort").GetString());
        var networks = configuration.GetProperty("NetworkingConfig").GetProperty("EndpointsConfig");
        Assert.Equal(2, networks.EnumerateObject().Count());
        var backend = networks.GetProperty("missum-ai-stack_backend");
        Assert.Equal("gateway", backend.GetProperty("Aliases")[1].GetString());
        Assert.Equal("172.20.0.42", backend.GetProperty("IPAMConfig").GetProperty("IPv4Address").GetString());
        Assert.False(backend.TryGetProperty("EndpointID", out _));
        Assert.False(backend.TryGetProperty("NetworkID", out _));
        Assert.False(backend.TryGetProperty("IPAddress", out _));
        Assert.Equal(MissumAiStackLifecycleService.ComposeProject,
            configuration.GetProperty("Labels").GetProperty("com.docker.compose.project").GetString());
    }

    [Fact]
    public async Task RunningGatewayIsNeverRecreatedEvenWhenANewerImageWasBuilt()
    {
        var docker = new FakeDocker(running: true) { ImageUpdateAvailable = true };
        using var service = Create(docker);
        await service.EnsureStartedAsync(docker.Profile.GatewayUri);
        Assert.Empty(docker.CreatedConfigurations);
        Assert.Empty(docker.Mutations);
        Assert.DoesNotContain(docker.Commands, command => command.Contains("image", StringComparer.Ordinal));
    }

    [Fact]
    public async Task OciIndexAndPlatformConfigIdDifferenceDoesNotRecreateEquivalentImageContent()
    {
        var docker = new FakeDocker { ImageUpdateAvailable = true, EquivalentImageWithDifferentIds = true };
        var originalId = docker.Gateway.Id;
        using var service = Create(docker);
        await service.EnsureGatewayStartedAsync(docker.Profile.GatewayUri);
        Assert.Empty(docker.CreatedConfigurations);
        Assert.Equal(originalId, docker.Gateway.Id);
        Assert.Equal(2, docker.Mutations.Count);
    }

    [Fact]
    public async Task MissingLocalImageTagStartsExistingGatewayWithoutPullingOrChangingInstallation()
    {
        var docker = new FakeDocker { ImageUpdateAvailable = true, ImageMissing = true };
        using var service = Create(docker);
        await service.EnsureGatewayStartedAsync(docker.Profile.GatewayUri);
        Assert.Empty(docker.CreatedConfigurations);
        Assert.True(docker.Gateway.Running);
        Assert.DoesNotContain(docker.Commands.SelectMany(command => command), argument => argument == "pull");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedGatewayReplacementRestoresOriginalNameAndKeepsOriginalContainer(bool failStart)
    {
        var docker = new FakeDocker { ImageUpdateAvailable = true, FailGatewayStart = failStart, FailReplacementRename = !failStart };
        var originalId = docker.Gateway.Id;
        using var service = Create(docker);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureGatewayStartedAsync(docker.Profile.GatewayUri));
        Assert.Equal(originalId, docker.Gateway.Id);
        Assert.Equal("missum-ai-gateway", docker.Gateway.Name);
        Assert.False(docker.Gateway.Running);
        Assert.DoesNotContain(docker.Mutations, command => command[0] == "stop" || command.Contains("--force", StringComparer.Ordinal));
    }

    [Fact]
    public async Task ExternalStartDuringReplacementIsPreservedAndUnusedReplacementIsRemoved()
    {
        var docker = new FakeDocker { ImageUpdateAvailable = true, StartOriginalDuringUpdate = true };
        var originalId = docker.Gateway.Id;
        using var service = Create(docker);
        await service.EnsureGatewayStartedAsync(docker.Profile.GatewayUri);
        Assert.Equal(originalId, docker.Gateway.Id);
        Assert.True(docker.Gateway.Running);
        Assert.DoesNotContain(docker.Mutations, command => command[0] == "stop" || (command[0] == "rm" && command.Contains(originalId, StringComparer.Ordinal)));
    }

    [Fact]
    public async Task RepeatedConnectionsDoNotRepeatedlyRecreateTheUpdatedImage()
    {
        var docker = new FakeDocker { ImageUpdateAvailable = true };
        using var service = Create(docker);
        await service.EnsureGatewayStartedAsync(docker.Profile.GatewayUri);
        docker.Gateway.Running = false;
        await service.EnsureGatewayStartedAsync(docker.Profile.GatewayUri);
        Assert.Single(docker.CreatedConfigurations);
    }

    [Fact]
    public async Task CancellationAfterCreatingReplacementRemovesOnlyTheUnusedCandidate()
    {
        var docker = new FakeDocker { ImageUpdateAvailable = true };
        var originalId = docker.Gateway.Id;
        using var cancellation = new CancellationTokenSource();
        using var service = new MissumAiStackLifecycleService(docker.Profile, docker.RunAsync, environment: _ => null,
            createContainer: async (endpoint, name, configuration, token) =>
            {
                var id = await docker.CreateContainerAsync(endpoint, name, configuration, token);
                cancellation.Cancel();
                return id;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EnsureGatewayStartedAsync(docker.Profile.GatewayUri, cancellation.Token));
        Assert.Equal(originalId, docker.Gateway.Id);
        Assert.Equal("missum-ai-gateway", docker.Gateway.Name);
        Assert.False(docker.Gateway.Running);
        Assert.DoesNotContain(docker.Mutations, command => command[0] == "stop" || command.Contains(originalId, StringComparer.Ordinal));
    }

    [Fact]
    public async Task CreateFailureLeavesTheOriginalInstallationUntouched()
    {
        var docker = new FakeDocker { ImageUpdateAvailable = true };
        var originalId = docker.Gateway.Id;
        using var service = new MissumAiStackLifecycleService(docker.Profile, docker.RunAsync, environment: _ => null,
            createContainer: (_, _, _, _) => throw new InvalidOperationException("Engine create rejected"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureGatewayStartedAsync(docker.Profile.GatewayUri));
        Assert.Equal(originalId, docker.Gateway.Id);
        Assert.Equal("missum-ai-gateway", docker.Gateway.Name);
        Assert.Empty(docker.Mutations);
    }
}
