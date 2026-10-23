using System.IO.Pipes;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Missum.App.Services;

public sealed partial class MissumAiStackLifecycleService
{
    private async Task<List<StackContainer>> RefreshStoppedGatewayAsync(string endpoint, List<StackContainer> containers,
        CancellationToken cancellationToken)
    {
        var gateway = containers.Single(static container => container.IsCompose && container.Service == "gateway");
        // Never restart an online service: it may belong to another open client
        // or still be running a persisted inference or background operation.
        if (gateway.Running) return containers;
        var installedImage = ReadString(gateway.Details, "Image");
        var imageReference = ReadString(gateway.Details.GetProperty("Config"), "Image");
        if (string.IsNullOrEmpty(installedImage) || string.IsNullOrEmpty(imageReference)) return containers;

        if (!await HasGatewayImageUpdateAsync(endpoint, gateway.Details, installedImage, imageReference, cancellationToken).ConfigureAwait(false))
            return containers;

        var originalName = ReadString(gateway.Details, "Name")?.TrimStart('/');
        if (string.IsNullOrWhiteSpace(originalName))
            throw new InvalidOperationException("Der installierte Gateway-Container besitzt keinen gültigen Namen.");
        var suffix = "-missum-update-" + Guid.NewGuid().ToString("N")[..12];
        var temporaryName = originalName + suffix;
        var backupName = originalName + suffix + "-previous";
        var configuration = BuildGatewayCreateConfiguration(gateway.Details, imageReference);
        string? replacementId = null;
        var originalRenamed = false;
        var replacementRenamed = false;
        var replacementStarted = false;
        var originalRemoved = false;
        try
        {
            replacementId = await _createContainer(endpoint, temporaryName, configuration, cancellationToken).ConfigureAwait(false);
            if (!ContainerIdPattern.IsMatch(replacementId))
                throw new InvalidOperationException("Docker lieferte keine gültige Kennung für den aktualisierten Gateway.");
            var current = await DockerAsync(endpoint, ["inspect", gateway.Id], cancellationToken).ConfigureAwait(false);
            EnsureSuccess(current, "Der installierte Gateway konnte vor der Aktualisierung nicht überprüft werden");
            using var currentDocument = JsonDocument.Parse(current.StandardOutput);
            if (currentDocument.RootElement[0].GetProperty("State").GetProperty("Running").GetBoolean())
            {
                EnsureSuccess(await DockerAsync(endpoint, ["rm", replacementId], cancellationToken).ConfigureAwait(false),
                    "Der ungenutzte Gateway-Updatecontainer konnte nicht entfernt werden");
                replacementId = null;
                return await ReadContainersAsync(endpoint, includeRunners: false, cancellationToken).ConfigureAwait(false);
            }

            EnsureSuccess(await DockerAsync(endpoint, ["rename", gateway.Id, backupName], cancellationToken).ConfigureAwait(false),
                "Der bisherige Gateway konnte nicht für die Aktualisierung vorbereitet werden");
            originalRenamed = true;
            EnsureSuccess(await DockerAsync(endpoint, ["rename", replacementId, originalName], cancellationToken).ConfigureAwait(false),
                "Der aktualisierte Gateway konnte nicht aktiviert werden");
            replacementRenamed = true;
            EnsureSuccess(await DockerAsync(endpoint, ["start", replacementId], cancellationToken).ConfigureAwait(false),
                "Der aktualisierte Gateway konnte nicht gestartet werden; der bisherige Container bleibt erhalten");
            replacementStarted = true;
            // No --force and no stop: a concurrent external start cannot lose
            // its active job. In that case removal fails and names roll back.
            EnsureSuccess(await DockerAsync(endpoint, ["rm", gateway.Id], cancellationToken).ConfigureAwait(false),
                "Der bisherige Gateway ist inzwischen aktiv; die Aktualisierung wurde zurückgenommen");
            originalRemoved = true;
            return await ReadContainersAsync(endpoint, includeRunners: false, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Rollback must also work when the connection request was cancelled.
            using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (!originalRemoved && !replacementStarted)
            {
                if (replacementRenamed && replacementId is not null)
                    await DockerAsync(endpoint, ["rename", replacementId, temporaryName], rollback.Token).ConfigureAwait(false);
                if (originalRenamed)
                    await DockerAsync(endpoint, ["rename", gateway.Id, originalName], rollback.Token).ConfigureAwait(false);
                if (replacementId is not null)
                    await DockerAsync(endpoint, ["rm", replacementId], rollback.Token).ConfigureAwait(false);
            }
            throw;
        }
    }

    private async Task<bool> HasGatewayImageUpdateAsync(string endpoint, JsonElement container, string installedImage,
        string imageReference, CancellationToken cancellationToken)
    {
        // The containerd image store records a config ID in container.Image,
        // which cannot necessarily be looked up as an image. Its immutable
        // platform manifest descriptor can be compared directly instead.
        if (container.TryGetProperty("ImageManifestDescriptor", out var descriptor)
            && descriptor.ValueKind == JsonValueKind.Object
            && ReadString(descriptor, "digest") is { Length: > 0 } installedDigest
            && descriptor.TryGetProperty("platform", out var platform)
            && ReadString(platform, "os") is { Length: > 0 } os
            && ReadString(platform, "architecture") is { Length: > 0 } architecture)
        {
            var variant = ReadString(platform, "variant");
            var platformName = os + "/" + architecture + (string.IsNullOrEmpty(variant) ? string.Empty : "/" + variant);
            var result = await DockerAsync(endpoint, ["image", "inspect", "--platform", platformName, imageReference], cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                using var document = JsonDocument.Parse(result.StandardOutput);
                var available = document.RootElement[0];
                if (available.TryGetProperty("Descriptor", out var availableDescriptor)
                    && ReadString(availableDescriptor, "mediaType") is "application/vnd.oci.image.manifest.v1+json" or "application/vnd.docker.distribution.manifest.v2+json"
                    && ReadString(availableDescriptor, "digest") is { Length: > 0 } availableDigest)
                    return !string.Equals(installedDigest, availableDigest, StringComparison.Ordinal);
            }
        }
        // Legacy stores expose installed image IDs directly. Compare image
        // configuration and layers, not OCI index IDs versus platform IDs.
        // No path pulls a missing tag from a registry.
        var images = await DockerAsync(endpoint, ["image", "inspect", installedImage, imageReference], cancellationToken).ConfigureAwait(false);
        if (images.ExitCode != 0) return false;
        using var imageDocument = JsonDocument.Parse(images.StandardOutput);
        var inspected = imageDocument.RootElement;
        if (inspected.ValueKind != JsonValueKind.Array || inspected.GetArrayLength() != 2)
            throw new InvalidOperationException("Docker lieferte keine eindeutigen Gateway-Imageinformationen.");
        var installed = inspected[0];
        var availableImage = inspected[1];
        if (ReadString(installed, "Os") != ReadString(availableImage, "Os")
            || ReadString(installed, "Architecture") != ReadString(availableImage, "Architecture"))
            throw new InvalidOperationException("Das neue Gateway-Image passt nicht zur installierten Plattform.");
        return !JsonElement.DeepEquals(installed.GetProperty("Config"), availableImage.GetProperty("Config"))
            || !JsonElement.DeepEquals(installed.GetProperty("RootFS"), availableImage.GetProperty("RootFS"));
    }

    internal static JsonElement BuildGatewayCreateConfiguration(JsonElement container, string imageReference)
    {
        var configuration = JsonNode.Parse(container.GetProperty("Config").GetRawText())!.AsObject();
        configuration["Image"] = imageReference;
        configuration["HostConfig"] = JsonNode.Parse(container.GetProperty("HostConfig").GetRawText());
        var endpoints = new JsonObject();
        foreach (var network in container.GetProperty("NetworkSettings").GetProperty("Networks").EnumerateObject())
        {
            var endpoint = new JsonObject();
            foreach (var field in network.Value.EnumerateObject())
            {
                // Assigned addresses and engine-generated endpoint IDs must be
                // allocated afresh; explicit IPAM settings and aliases survive.
                if (field.Name is "IPAMConfig" or "Links" or "Aliases" or "DriverOpts" or "GwPriority")
                    endpoint[field.Name] = JsonNode.Parse(field.Value.GetRawText());
            }
            endpoints[network.Name] = endpoint;
        }
        configuration["NetworkingConfig"] = new JsonObject { ["EndpointsConfig"] = endpoints };
        return JsonSerializer.SerializeToElement(configuration);
    }

    private static async Task<string> CreateContainerAsync(string endpoint, string name, JsonElement configuration,
        CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false };
        var baseAddress = new Uri("http://docker/");
        if (endpoint.StartsWith("npipe:////./pipe/", StringComparison.OrdinalIgnoreCase))
        {
            var pipeName = endpoint["npipe:////./pipe/".Length..];
            handler.ConnectCallback = async (_, token) =>
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try { await pipe.ConnectAsync(token).ConfigureAwait(false); return pipe; }
                catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
            };
        }
        else if (endpoint.StartsWith("unix:///", StringComparison.Ordinal))
        {
            var socketPath = endpoint["unix://".Length..];
            handler.ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            };
        }
        else
        {
            baseAddress = new Uri(endpoint.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)
                ? "http://" + endpoint["tcp://".Length..] : endpoint);
        }
        using var client = new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(60) };
        using var response = await client.PostAsJsonAsync("/containers/create?name=" + Uri.EscapeDataString(name),
            configuration, cancellationToken).ConfigureAwait(false);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Der aktualisierte Gateway konnte nicht angelegt werden: "
                + (ReadString(result.RootElement, "message") ?? response.StatusCode.ToString()));
        return result.RootElement.GetProperty("Id").GetString() ?? string.Empty;
    }
}
