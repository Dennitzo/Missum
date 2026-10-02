using System.Globalization;
using System.Text;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Gateway;
using Missum.Ai.Server.Core.Runs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Missum.Ai.Server.Tests;

public sealed class LongRunningScienceSseReplayTests
{
    [Theory]
    [InlineData(RunState.Completed, 0)]
    [InlineData(RunState.Completed, 300)]
    [InlineData(RunState.Failed, 0)]
    [InlineData(RunState.Cancelled, 300)]
    public async Task TerminalRunReplaysEveryPageAfterDurableCursorWithoutWaiting(RunState terminal, int acknowledged)
    {
        await using var gateway = new GatewayFixture();
        var run = await gateway.CreateRunAsync();
        var events = await gateway.AppendTextAsync(run, 770);
        var terminalEvent = await gateway.Repository.AppendEventAsync(run, terminal switch
        {
            RunState.Completed => RunEventTypes.RunCompleted,
            RunState.Failed => RunEventTypes.RunFailed,
            _ => RunEventTypes.RunCancelled,
        }, new { state = terminal.ToString() });
        events.Add(terminalEvent);
        await gateway.Repository.UpdateStateAsync(run, terminal);
        var cursor = acknowledged == 0 ? 0 : events[acknowledged - 1].Id;

        var received = await gateway.ReadAsync(run, cursor);

        Assert.Equal(events.Skip(acknowledged).Select(static item => item.Id), received.Select(static item => item.Id));
        Assert.Equal(terminalEvent.Type, received[^1].Type);
        Assert.Equal(terminalEvent.Id, received[^1].Id);
        Assert.All(received, item => Assert.Equal(run, item.RunId));
        Assert.Equal(received.Count, received.Select(static item => item.Id).Distinct().Count());
    }

    [Fact]
    public async Task EventsCommittedDuringReplayAreDrainedBeforeClosingTerminalStream()
    {
        await using var gateway = new GatewayFixture();
        var run = await gateway.CreateRunAsync();
        var expected = await gateway.AppendTextAsync(run, 300);
        await using var body = new FlushHookStream(async () =>
        {
            expected.AddRange(await gateway.AppendTextAsync(run, 300, 300));
            expected.Add(await gateway.Repository.AppendEventAsync(run, RunEventTypes.RunCompleted, new { completed = true }));
            await gateway.Repository.UpdateStateAsync(run, RunState.Completed);
        });

        var received = await gateway.ReadAsync(run, 0, body);

        Assert.True(body.HookInvoked);
        Assert.Equal(expected.Select(static item => item.Id), received.Select(static item => item.Id));
        Assert.Equal(RunEventTypes.RunCompleted, received[^1].Type);
    }

    [Fact]
    public async Task DisconnectStopsReplayWithoutCancellingResearchRun()
    {
        await using var gateway = new GatewayFixture();
        var run = await gateway.CreateRunAsync();
        await gateway.AppendTextAsync(run, 600);
        using var cancellation = new CancellationTokenSource();
        await using var body = new FlushHookStream(() => { cancellation.Cancel(); return Task.CompletedTask; });

        var received = await gateway.ReadAsync(run, 0, body, cancellation.Token);

        Assert.True(body.HookInvoked);
        Assert.InRange(received.Count, 0, 256);
        Assert.Equal(RunState.Running, (await gateway.Repository.GetAsync(run))!.State);
    }

    private sealed class GatewayFixture : IAsyncDisposable
    {
        private readonly TestServerContext _context = new();
        private readonly ServiceProvider _services;
        private readonly RequestDelegate _route;
        public RunRepository Repository { get; }

        public GatewayFixture()
        {
            var notifier = new RunEventNotifier();
            Repository = new RunRepository(_context.Database, notifier);
            _services = new ServiceCollection().AddLogging().AddRouting()
                .AddSingleton(Repository).AddSingleton(notifier).BuildServiceProvider();
            var routes = new RouteBuilder(_services);
            GatewayEndpoints.Map(routes);
            _route = Assert.Single(routes.DataSources.SelectMany(static source => source.Endpoints).OfType<RouteEndpoint>(),
                static endpoint => endpoint.RoutePattern.RawText == "/v1/runs/{runId}/events").RequestDelegate!;
        }

        public async Task<string> CreateRunAsync()
        {
            var created = await Repository.CreateAsync(new RunRequest(MissumAiProtocol.Version, RunMode.General,
                [new RunMessage("user", [new ContentPart("text", "Lang laufende Forschung")])], DeepResearch: true,
                SessionId: "science-fixture", Limits: new(TimeoutSeconds: 0)), null);
            await Repository.UpdateStateAsync(created.Snapshot.RunId, RunState.Running);
            return created.Snapshot.RunId;
        }

        public async Task<List<RunEvent>> AppendTextAsync(string run, int count, int offset = 0)
        {
            var result = new List<RunEvent>(count);
            for (var index = 0; index < count; index++)
                result.Add(await Repository.AppendEventAsync(run, RunEventTypes.TextDelta, new TextDeltaEvent($"Abschnitt {offset + index}. ")));
            return result;
        }

        public async Task<List<RunEvent>> ReadAsync(string run, long cursor, MemoryStream? output = null,
            CancellationToken cancellationToken = default)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
            await using var owned = output is null ? new MemoryStream() : null;
            var body = output ?? owned!;
            var http = new DefaultHttpContext { RequestServices = _services, RequestAborted = linked.Token };
            http.Request.Method = "GET";
            http.Request.Path = $"/v1/runs/{run}/events";
            http.Request.RouteValues["runId"] = run;
            http.Request.Headers[MissumAiHeaders.LastEventId] = cursor.ToString(CultureInfo.InvariantCulture);
            http.Response.Body = body;
            try { await _route(http); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
            Assert.Equal("text/event-stream", http.Response.ContentType);
            return Encoding.UTF8.GetString(body.ToArray()).Split('\n').Where(static line => line.StartsWith("data: ", StringComparison.Ordinal))
                .Select(static line => JsonSerializer.Deserialize<RunEvent>(line[6..], MissumAiProtocol.CreateJsonOptions())!).ToList();
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            _context.Dispose();
        }
    }

    private sealed class FlushHookStream(Func<Task> hook) : MemoryStream
    {
        public bool HookInvoked { get; private set; }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await base.FlushAsync(cancellationToken);
            if (HookInvoked) return;
            HookInvoked = true;
            await hook();
        }
    }

    private sealed class RouteBuilder(IServiceProvider services) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = services;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }
}
