using System.Security.Cryptography;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Gateway;
using Missum.Ai.Server.Core.Runtime;
using Missum.Ai.Server.Core.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Missum.Ai.Server.Tests;

/// <summary>Exercises the public original export route and upload integrity checks with isolated storage.</summary>
public sealed class OriginalUploadGatewayTests
{
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==");
    private static readonly byte[] EmptyRequest = "{}"u8.ToArray();
    private static readonly byte[] MutatedBytes = "The verified original upload was altered after completion"u8.ToArray();

    [Fact]
    public async Task CompletedImageExportsItsExactOriginalBytesHashAndProvenance()
    {
        await using var gateway = new GatewayFixture();
        var upload = await gateway.UploadAsync("capture.png", "image/png", complete: true);

        var response = await gateway.SubmitAsync(upload.UploadId);

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        var descriptor = response.Body.Deserialize<ArtifactDescriptor>(MissumAiProtocol.CreateJsonOptions())!;
        Assert.Equal("capture.png", descriptor.FileName);
        Assert.Equal("image/png", descriptor.MediaType);
        Assert.Equal(PngBytes.Length, descriptor.Length);
        Assert.Equal(Hash(PngBytes), descriptor.Sha256);
        Assert.Equal("original", descriptor.Metadata!["role"]);
        Assert.Equal(upload.UploadId, descriptor.Metadata["sourceUploadId"]);
        var file = await gateway.Artifacts.ResolveAsync(descriptor.ArtifactId);
        Assert.NotNull(file);
        Assert.Equal(PngBytes, await File.ReadAllBytesAsync(file.Path));
        Assert.Equal(descriptor.ArtifactId, file.Descriptor.ArtifactId);
        Assert.Equal(descriptor.Sha256, file.Descriptor.Sha256);
        Assert.Equal(upload.UploadId, file.Descriptor.Metadata!["sourceUploadId"]);
    }

    [Theory]
    [InlineData("missing", StatusCodes.Status404NotFound, "resource.not_found")]
    [InlineData("pending", StatusCodes.Status404NotFound, "resource.not_found")]
    [InlineData("missing-payload", StatusCodes.Status404NotFound, "resource.not_found")]
    [InlineData("audio", StatusCodes.Status400BadRequest, "request.invalid_argument")]
    public async Task NonexistentIncompleteAndNonImageUploadsAreRejected(string scenario, int expectedStatus, string expectedCode)
    {
        await using var gateway = new GatewayFixture();
        var uploadId = "upload-" + new string('0', 32);
        if (scenario != "missing")
        {
            var upload = await gateway.UploadAsync(scenario == "audio" ? "speech.wav" : "capture.png",
                scenario == "audio" ? "audio/wav" : "image/png", complete: scenario != "pending");
            uploadId = upload.UploadId;
            if (scenario == "missing-payload")
            {
                var path = await gateway.Uploads.ResolveCompletedPathAsync(uploadId);
                Assert.NotNull(path);
                File.Delete(path);
            }
        }

        var response = await gateway.SubmitAsync(uploadId);

        Assert.Equal(expectedStatus, response.Status);
        var problem = response.Body.Deserialize<MissumAiProblem>(MissumAiProtocol.CreateJsonOptions())!;
        Assert.Equal(expectedCode, problem.ErrorCode);
    }

    [Fact]
    public async Task AlteredCompletedPayloadCannotBeExportedAsTheVerifiedOriginal()
    {
        await using var gateway = new GatewayFixture();
        var upload = await gateway.UploadAsync("capture.png", "image/png", complete: true);
        var path = await gateway.Uploads.ResolveCompletedPathAsync(upload.UploadId);
        Assert.NotNull(path);
        await File.WriteAllBytesAsync(path, MutatedBytes);

        var response = await gateway.SubmitAsync(upload.UploadId);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, response.Status);
        var problem = response.Body.Deserialize<MissumAiProblem>(MissumAiProtocol.CreateJsonOptions())!;
        Assert.Equal("upload.integrity_failed", problem.ErrorCode);
        Assert.Equal(0L, await gateway.StoredArtifactCountAsync());
        Assert.Empty(gateway.StoredArtifactPaths());
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class GatewayFixture : IAsyncDisposable
    {
        private readonly TestServerContext _context = new();
        private readonly ServiceProvider _services;
        private readonly RequestDelegate _route;
        public UploadService Uploads { get; }
        public ArtifactService Artifacts { get; }

        public GatewayFixture()
        {
            Uploads = new UploadService(_context.Database, _context.WrappedOptions);
            Artifacts = new ArtifactService(_context.Database, _context.WrappedOptions);
            _services = new ServiceCollection().AddLogging().AddRouting()
                .AddSingleton(Uploads).AddSingleton(Artifacts)
                .AddSingleton(new ServerRuntimeState(_context.WrappedOptions)).BuildServiceProvider();
            var routes = new RouteBuilder(_services);
            GatewayEndpoints.Map(routes);
            var endpoint = Assert.Single(routes.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
                endpoint => endpoint.RoutePattern.RawText == "/v1/uploads/{uploadId}/original-artifact"
                    && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST"));
            _route = endpoint.RequestDelegate!;
        }

        public async Task<UploadCreated> UploadAsync(string fileName, string mediaType, bool complete)
        {
            var upload = await Uploads.CreateAsync(new(fileName, mediaType, PngBytes.Length, Hash(PngBytes)));
            using var stream = new MemoryStream(PngBytes, writable: false);
            await Uploads.PutChunkAsync(upload.UploadId, 0, stream, Hash(PngBytes));
            if (complete) await Uploads.CompleteAsync(upload.UploadId);
            return upload;
        }

        public async Task<(int Status, JsonElement Body)> SubmitAsync(string uploadId)
        {
            var http = new DefaultHttpContext { RequestServices = _services };
            http.Request.Method = "POST";
            http.Request.Path = $"/v1/uploads/{uploadId}/original-artifact";
            http.Request.RouteValues["uploadId"] = uploadId;
            http.Request.ContentType = "application/json";
            http.Request.ContentLength = EmptyRequest.Length;
            await using var request = new MemoryStream(EmptyRequest, writable: false);
            await using var response = new MemoryStream();
            http.Request.Body = request;
            http.Response.Body = response;
            await new ProblemDetailsMiddleware(_route).InvokeAsync(http, _services.GetRequiredService<ServerRuntimeState>());
            return (http.Response.StatusCode, JsonSerializer.Deserialize<JsonElement>(response.ToArray()));
        }

        public async Task<long> StoredArtifactCountAsync()
        {
            await using var connection = await _context.Database.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM artifacts;";
            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public IEnumerable<string> StoredArtifactPaths() => Directory.Exists(_context.Options.ArtifactDirectory)
            ? Directory.EnumerateFiles(_context.Options.ArtifactDirectory)
            : Enumerable.Empty<string>();

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            _context.Dispose();
        }
    }

    private sealed class RouteBuilder(IServiceProvider services) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = services;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }
}
