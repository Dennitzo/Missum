using System.Net;
using System.Text.Json;
using Missum.Ai.Client;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Gateway;
using Missum.Ai.Server.Core.Runs;
using Missum.Ai.Server.Core.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Missum.Ai.Server.Tests;

public sealed class RunCancellationByKeyTests
{
    private const string Session = "lost-create-ack-session";
    private static RunRequest Request() => new(MissumAiProtocol.Version, RunMode.Coding,
        [new("user", [new("text", Text: "Bearbeite die offene Forschung weiter.")])], SessionId: Session,
        ClientCapabilities: ["coding", "workspace"], SpecializedAgent: new(SpecializedAgentProfiles.Loop));

    [Fact]
    public async Task CancelBeforeCreatePersistsOnlyATombstoneAndLaterCreateIsAlreadyCancelled()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var cancel = new RunCancelByKeyRequest(Session, "ack-was-lost");

        Assert.Null(await repository.CancelByIdempotencyKeyAsync(cancel));
        Assert.Null(await repository.CancelByIdempotencyKeyAsync(cancel));
        await using (var connection = await context.Database.OpenConnectionAsync())
        {
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM runs;";
            Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        }
        // A new repository object must see the durable cancellation as well.
        repository = new(context.Database, new RunEventNotifier());
        var created = await repository.CreateAsync(Request(), cancel.IdempotencyKey);
        var repeated = await repository.CreateAsync(Request(), cancel.IdempotencyKey);

        Assert.True(created.Created);
        Assert.False(repeated.Created);
        Assert.Equal(created.Snapshot.RunId, repeated.Snapshot.RunId);
        Assert.Equal(RunState.Cancelled, created.Snapshot.State);
        Assert.Equal("run.cancelled", created.Snapshot.ErrorCode);
        Assert.Single(await repository.GetEventsAfterAsync(created.Snapshot.RunId, 0), item => item.Type == RunEventTypes.RunCancelled);
        Assert.DoesNotContain(created.Snapshot.RunId, await repository.RecoverAsync());
    }

    [Theory]
    [InlineData(RunState.Queued)]
    [InlineData(RunState.Running)]
    [InlineData(RunState.WaitingForClient)]
    [InlineData(RunState.Interrupted)]
    public async Task CancelAfterAcceptanceStopsExistingRunAndPreventsIdempotentRestart(RunState state)
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var original = await repository.CreateAsync(Request(), "existing-request");
        await repository.UpdateStateAsync(original.Snapshot.RunId, state);

        Assert.Equal(original.Snapshot.RunId, await repository.CancelByIdempotencyKeyAsync(new(Session, "existing-request")));
        await repository.CancelByIdempotencyKeyAsync(new(Session, "existing-request"));
        var repeated = await repository.CreateAsync(Request(), "existing-request");

        Assert.False(repeated.Created);
        Assert.Equal(RunState.Cancelled, repeated.Snapshot.State);
        Assert.Single(await repository.GetEventsAfterAsync(original.Snapshot.RunId, 0), item => item.Type == RunEventTypes.RunCancelled);
    }

    [Fact]
    public async Task CancellationAndCreationRaceAlwaysLeavesTheSameRunCancelled()
    {
        using var context = new TestServerContext();
        await context.Database.InitializeAsync();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        for (var index = 0; index < 8; index++)
        {
            var key = "racing-request-" + index;
            var creation = Task.Run(() => repository.CreateAsync(Request(), key));
            var cancellation = Task.Run(() => repository.CancelByIdempotencyKeyAsync(new(Session, key)));
            await Task.WhenAll(creation, cancellation);

            var run = (await creation).Snapshot.RunId;
            Assert.Equal(RunState.Cancelled, (await repository.GetAsync(run))!.State);
            Assert.False((await repository.CreateAsync(Request(), key)).Created);
            Assert.Single(await repository.GetEventsAfterAsync(run, 0), item => item.Type == RunEventTypes.RunCancelled);
        }
    }

    [Fact]
    public async Task KeysAreBoundToTheOriginalSessionAndCannotCancelForeignRuns()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var created = await repository.CreateAsync(Request(), "session-bound-key");
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CancelByIdempotencyKeyAsync(new("foreign", "session-bound-key")));
        Assert.Equal(RunState.Queued, (await repository.GetAsync(created.Snapshot.RunId))!.State);
        await repository.CancelByIdempotencyKeyAsync(new(Session, "not-yet-created"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CancelByIdempotencyKeyAsync(new("foreign", "not-yet-created")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CreateAsync(Request() with { SessionId = "foreign" }, "not-yet-created"));
    }

    [Fact]
    public async Task ClientSendsTheScopedCancellationWithoutCreatingOrRecoveringARun()
    {
        using var handler = new CancellationHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://missum-ai.test/") };
        using var client = new MissumAiClient(http, "goai_123456789abc_test");

        await client.CancelRunByIdempotencyKeyAsync(Session, "unknown-create");

        Assert.Equal("/v1/runs/cancel-by-key", handler.Path);
        Assert.Equal(new RunCancelByKeyRequest(Session, "unknown-create"), handler.Request);
    }

    [Fact]
    public async Task GatewayCancelsUnknownAcceptanceAndNeverQueuesTheLateCreate()
    {
        await using var gateway = new GatewayFixture();
        var cancelled = await gateway.SendAsync("/v1/runs/cancel-by-key", new RunCancelByKeyRequest(Session, "late-create"));
        Assert.Equal(StatusCodes.Status204NoContent, cancelled.Status);
        var created = await gateway.SendAsync("/v1/runs", Request(), "late-create");
        Assert.Equal(StatusCodes.Status202Accepted, created.Status);
        Assert.Equal(RunState.Cancelled, created.Body.Deserialize<RunAccepted>(MissumAiProtocol.CreateJsonOptions())!.State);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await using var queued = gateway.Queue.ReadAllAsync(deadline.Token).GetAsyncEnumerator();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.MoveNextAsync().AsTask());
    }

    [Fact]
    public async Task GatewayCanCancelAnExistingRequestAndRejectsIncorrectScope()
    {
        await using var gateway = new GatewayFixture();
        var existing = await gateway.Repository.CreateAsync(Request(), "known-create");
        var wrong = await gateway.SendAsync("/v1/runs/cancel-by-key", new RunCancelByKeyRequest("foreign", "known-create"));
        Assert.Equal(StatusCodes.Status409Conflict, wrong.Status);
        var cancelled = await gateway.SendAsync("/v1/runs/cancel-by-key", new RunCancelByKeyRequest(Session, "known-create"));
        Assert.Equal(StatusCodes.Status204NoContent, cancelled.Status);
        Assert.Equal(RunState.Cancelled, (await gateway.Repository.GetAsync(existing.Snapshot.RunId))!.State);
    }

    private sealed class CancellationHandler : HttpMessageHandler
    {
        internal string? Path { get; private set; }
        internal RunCancelByKeyRequest? Request { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Path = request.RequestUri!.AbsolutePath;
            Request = JsonSerializer.Deserialize<RunCancelByKeyRequest>(await request.Content!.ReadAsStringAsync(cancellationToken), MissumAiProtocol.CreateJsonOptions());
            return new(HttpStatusCode.NoContent);
        }
    }

    private sealed class GatewayFixture : IAsyncDisposable
    {
        private readonly TestServerContext _context = new();
        private readonly ServiceProvider _services;
        private readonly Dictionary<string, RequestDelegate> _routes;
        internal RunRepository Repository { get; }
        internal RunWorkChannel Queue { get; }
        internal GatewayFixture()
        {
            var services = new ServiceCollection().AddLogging().AddRouting();
            services.AddMissumAiServerServices(_context.Options, includeHostedServices: false);
            services.AddSingleton(_context.Database);
            _services = services.BuildServiceProvider();
            Repository = _services.GetRequiredService<RunRepository>();
            Queue = _services.GetRequiredService<RunWorkChannel>();
            var routes = new RouteBuilder(_services);
            GatewayEndpoints.Map(routes);
            _routes = routes.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
                .Where(endpoint => endpoint.RoutePattern.RawText is "/v1/runs" or "/v1/runs/cancel-by-key"
                    && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST"))
                .ToDictionary(endpoint => endpoint.RoutePattern.RawText!, endpoint => endpoint.RequestDelegate!);
        }
        internal async Task<(int Status, JsonElement Body)> SendAsync<T>(string path, T input, string? key = null)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(input, MissumAiProtocol.CreateJsonOptions());
            var http = new DefaultHttpContext { RequestServices = _services };
            http.Request.Method = "POST"; http.Request.Path = path;
            http.Request.ContentType = "application/json"; http.Request.ContentLength = body.Length;
            if (key is not null) http.Request.Headers[MissumAiHeaders.IdempotencyKey] = key;
            await using var request = new MemoryStream(body);
            await using var response = new MemoryStream();
            http.Request.Body = request; http.Response.Body = response;
            await new ProblemDetailsMiddleware(_routes[path]).InvokeAsync(http, _services.GetRequiredService<ServerRuntimeState>());
            return (http.Response.StatusCode, response.Length == 0 ? default : JsonSerializer.Deserialize<JsonElement>(response.ToArray()));
        }
        public async ValueTask DisposeAsync() { await _services.DisposeAsync(); _context.Dispose(); }
    }

    private sealed class RouteBuilder(IServiceProvider services) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = services;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }
}
