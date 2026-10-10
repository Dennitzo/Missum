using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using Missum.Ai.Server.Core.Runtime;
using Missum.Ai.Server.Core.Storage;
using Missum.Ai.Server.Core.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class MediaOriginalArtifactTests
{
    [Fact]
    public void ExactOriginalReplacesItsThumbnailAndKeepsVisionTransportPrivate()
    {
        var original = Artifact("original", "original.png", "image/png", "original",
            new() { ["sourceUploadId"] = "upload-source" });
        var thumbnail = Artifact("thumb", "thumbnail.jpg", "image/jpeg", "thumbnail",
            new() { ["sourceUploadId"] = "upload-source", ["originalArtifactId"] = original.ArtifactId });
        var model = Artifact("vision", "vision-input.jpg", "image/jpeg", "vision_input");

        Assert.Equal(original, Assert.Single(AgentToolExecutor.VisibleMediaArtifacts([original, thumbnail, model])));
    }

    [Fact]
    public void UnrelatedVideoAndImagePreviewsAreNotHiddenByAnOriginal()
    {
        var original = Artifact("original", "original.png", "image/png", "original",
            new() { ["sourceUploadId"] = "upload-source" });
        var frame = Artifact("frame", "frame-001.jpg", "image/jpeg", "frame",
            new() { ["sourceUploadId"] = "upload-video", ["group"] = "overview", ["timecodeSeconds"] = "1.2" });
        var videoThumbnail = Artifact("video-thumb", "thumbnail.jpg", "image/jpeg", "thumbnail",
            new() { ["sourceUploadId"] = "upload-video", ["group"] = "overview" });
        var otherThumbnail = Artifact("other-thumb", "thumbnail.jpg", "image/jpeg", "thumbnail",
            new() { ["originalArtifactId"] = "unavailable-original" });

        Assert.Equal(new[] { original, frame, videoThumbnail, otherThumbnail },
            AgentToolExecutor.VisibleMediaArtifacts([original, frame, videoThumbnail, otherThumbnail]));
    }

    [Fact]
    public void OriginalFromSingleWorkerImageSuppressesLegacyThumbnailWithoutReferences()
    {
        var original = Artifact("original", "original.webp", "image/webp", "original");
        var thumbnail = Artifact("thumb", "thumbnail.jpg", "image/jpeg", "thumbnail");
        var model = Artifact("vision", "vision-input.jpg", "image/jpeg", "vision_input");

        Assert.Equal(original, Assert.Single(AgentToolExecutor.VisibleMediaArtifacts([thumbnail, model, original])));
    }

    [Fact]
    public async Task WorkerImportPreservesOriginalBytesAndLinksDerivativeDescriptors()
    {
        using var context = new TestServerContext();
        var sourceRoot = Path.Combine(context.Options.WorkerArtifactDirectory, "media-original-test");
        Directory.CreateDirectory(sourceRoot);
        var originalBytes = Encoding.UTF8.GetBytes("byte-exact original container, alpha and metadata");
        var visionBytes = Encoding.UTF8.GetBytes("separate normalized model JPEG");
        var thumbnailBytes = Encoding.UTF8.GetBytes("separate small thumbnail JPEG");
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "original.png"), originalBytes);
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "vision-input.jpg"), visionBytes);
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "thumbnail.jpg"), thumbnailBytes);
        using var http = new HttpClient(new WorkerTransport());
        using var model = new ModelRuntimeClient(http, context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
        var runtime = new ServerRuntimeState(context.WrappedOptions);
        using var scheduler = new GpuLeaseScheduler(context.Database, runtime);
        var storage = new ArtifactService(context.Database, context.WrappedOptions);
        using var worker = new WorkerOrchestrator(new WorkerApiClient(http, context.WrappedOptions), model,
            scheduler, storage, context.WrappedOptions, runtime);
        var uploadId = "upload-" + new string('a', 32);

        var result = await worker.InspectMediaAsync(new(uploadId, "image/png"), "original-import-run");

        var original = Assert.Single(result.Artifacts, item => item.Metadata!["role"] == "original");
        var originalFile = await storage.ResolveAsync(original.ArtifactId);
        Assert.NotNull(originalFile);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(originalFile.Path));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(originalBytes)), original.Sha256);
        Assert.Equal("image/png", original.MediaType);
        Assert.Equal("original.png", original.FileName);
        Assert.Equal(uploadId, original.Metadata!["sourceUploadId"]);
        Assert.Equal("original-import-run", original.Metadata["runId"]);
        foreach (var derivative in result.Artifacts.Where(item => item != original))
        {
            Assert.Equal(original.ArtifactId, derivative.Metadata!["originalArtifactId"]);
            Assert.Equal(original.Sha256, derivative.Metadata["originalSha256"]);
            Assert.Equal(original.FileName, derivative.Metadata["originalFileName"]);
            Assert.Equal(original.MediaType, derivative.Metadata["originalMediaType"]);
            Assert.Equal(uploadId, derivative.Metadata["sourceUploadId"]);
        }
        Assert.Equal(original, Assert.Single(AgentToolExecutor.VisibleMediaArtifacts(result.Artifacts)));
    }

    private static ArtifactDescriptor Artifact(string id, string fileName, string mediaType, string role,
        Dictionary<string, string>? metadata = null)
    {
        metadata ??= new();
        metadata["role"] = role;
        var now = DateTimeOffset.UtcNow;
        return new(id, fileName, mediaType, 10, new string('a', 64), now, now.AddHours(24), metadata);
    }

    private sealed class WorkerTransport : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/release") return Json(new { released = true });
            Assert.Equal("/inspect", request.RequestUri.AbsolutePath);
            // Original deliberately follows thumbnail to verify import ordering.
            return Json(new { kind = "image", metadata = new { width = 4800, height = 2400 }, artifacts = new[]
            {
                new { relativePath = "artifacts/worker/media-original-test/thumbnail.jpg", fileName = "thumbnail.jpg", mediaType = "image/jpeg", role = "thumbnail" },
                new { relativePath = "artifacts/worker/media-original-test/vision-input.jpg", fileName = "vision-input.jpg", mediaType = "image/jpeg", role = "vision_input" },
                new { relativePath = "artifacts/worker/media-original-test/original.png", fileName = "original.png", mediaType = "image/png", role = "original" },
            }, frames = Array.Empty<object>() });
        }

        private static Task<HttpResponseMessage> Json(object value) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") });
    }
}
