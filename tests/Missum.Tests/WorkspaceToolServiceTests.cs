using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Missum.Tests;

public sealed class WorkspaceToolServiceTests
{
    [Fact]
    public async Task FileUploadUsesFrozenGatewayAndReturnsExactSourceIdentity()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Path.Combine(environment.Directory, "workspace");
        Directory.CreateDirectory(workspace);
        var path = Path.Combine(workspace, "plot.png");
        byte[] bytes = [137, 80, 78, 71, 13, 10, 26, 10];
        await File.WriteAllBytesAsync(path, bytes);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var observed = new List<Uri>();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(value => value with { MissumAiServerUrl = "http://changed-gateway.invalid:1111" });
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance,
            () => new UploadHandler(observed, sha, bytes.Length));
        var proposal = new ToolProposal("image-source", "science-run", WorkspaceTools.ImageInput,
            JsonSerializer.SerializeToElement(new { operation = "file", path = "plot.png" }),
            ToolRiskClass.ReadOnly, "Plot laden", DateTimeOffset.UtcNow.AddMinutes(1));
        var result = JsonSerializer.SerializeToElement(await new WorkspaceToolService(connection).ExecuteAsync(proposal,
            workspace, null, gatewayOverride: new("http://frozen-gateway.invalid:2222")));
        Assert.NotEmpty(observed);
        Assert.All(observed, uri => Assert.Equal("frozen-gateway.invalid:2222", uri.Authority));
        Assert.Equal(sha, result.GetProperty("sha256").GetString());
        Assert.Equal(Path.GetFullPath(path), result.GetProperty("sourcePath").GetString());
        Assert.Equal(Path.GetFullPath(workspace), result.GetProperty("workspaceRoot").GetString());
        Assert.True(result.GetProperty("fromWorkspace").GetBoolean());
    }

    [Fact]
    public async Task OutsideFileAndWindowCaptureAreNotClaimedAsProjectPlots()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var root = Path.Combine(environment.Directory, "workspace");
        Directory.CreateDirectory(root);
        var outside = Path.Combine(environment.Directory, "outside.png");
        Assert.False(WorkspaceToolService.ImageSourceIdentity(outside, root, temporary: false).FromWorkspace);
        var captured = WorkspaceToolService.ImageSourceIdentity(Path.Combine(root, "capture.png"), root, temporary: true);
        Assert.False(captured.FromWorkspace);
        Assert.Null(captured.SourcePath);
    }

    private sealed class UploadHandler(List<Uri> observed, string sha, int length) : HttpMessageHandler
    {
        private const string Id = "upload-0123456789abcdef0123456789abcdef";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            observed.Add(request.RequestUri!);
            object result;
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/v1/uploads")
            {
                var manifest = JsonSerializer.Deserialize<UploadManifest>(await request.Content!.ReadAsStringAsync(cancellationToken), MissumAiProtocol.CreateJsonOptions())!;
                Assert.Equal(sha, manifest.Sha256);
                result = new UploadCreated(Id, MissumAiProtocol.UploadChunkSize, 1, [], DateTimeOffset.UtcNow.AddHours(1));
            }
            else if (request.Method == HttpMethod.Put && path == $"/v1/uploads/{Id}/chunks/0")
            {
                var chunk = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                Assert.Equal(sha, Convert.ToHexStringLower(SHA256.HashData(chunk)));
                result = new UploadChunkReceipt(Id, 0, sha, length, true);
            }
            else if (request.Method == HttpMethod.Post && path == $"/v1/uploads/{Id}/complete")
                result = new UploadCompleted(Id, "plot.png", "image/png", length, sha, DateTimeOffset.UtcNow.AddHours(1));
            else throw new InvalidOperationException("Unexpected isolated upload request: " + path);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(result, MissumAiProtocol.CreateJsonOptions()), Encoding.UTF8, "application/json"),
            };
        }
    }
}
