using Missum.Ai.Client;
using Missum.App.Services;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Missum.Tests;

public sealed class GatewayStartupWaitTests
{
    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task LocalStartupRetriesReverseProxyErrorsUntilGatewayIsAlive(HttpStatusCode status)
    {
        using var handler = new StartupHandler(status);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8080/") };
        using var client = new MissumAiClient(http);

        await WaitAsync(client);

        Assert.Equal(2, handler.Attempts);
        Assert.All(handler.Paths, path => Assert.Equal("/v1/health/live", path));
    }

    [Fact]
    public async Task LocalStartupRetriesConnectionRefused()
    {
        using var handler = new StartupHandler(HttpStatusCode.OK, connectionRefused: true);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8080/") };
        using var client = new MissumAiClient(http);

        await WaitAsync(client);

        Assert.Equal(2, handler.Attempts);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.BadRequest, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, false)]
    public async Task GenuineErrorsAndRemoteFailuresAreNotHidden(HttpStatusCode status, bool local)
    {
        using var handler = new StartupHandler(status);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8080/") };
        using var client = new MissumAiClient(http);

        var error = await Assert.ThrowsAsync<MissumAiApiException>(() => WaitAsync(client, local));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task CallerCanStopWhileServicesAreStarting()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new StartupHandler(HttpStatusCode.BadGateway, alwaysFail: true,
            onAttempt: () => cancellation.Cancel());
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8080/") };
        using var client = new MissumAiClient(http);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaitAsync(client, token: cancellation.Token));

        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task ADeadGatewayDoesNotWaitForever()
    {
        using var handler = new StartupHandler(HttpStatusCode.BadGateway, alwaysFail: true);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8080/") };
        using var client = new MissumAiClient(http);

        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            MissumAiConnectionService.WaitForGatewayAsync(client, "1.0", true,
                TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(5), CancellationToken.None));

        Assert.IsType<MissumAiApiException>(error.InnerException);
    }

    [Fact]
    public async Task ProtocolMismatchIsNotReportedAsModelLoading()
    {
        using var handler = new StartupHandler(HttpStatusCode.OK, protocol: "wrong");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8080/") };
        using var client = new MissumAiClient(http);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => WaitAsync(client));

        Assert.Contains("Protokollabweichung", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task InvalidHealthPayloadIsNotRetried()
    {
        using var handler = new StartupHandler(HttpStatusCode.OK, payload: "not json");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8080/") };
        using var client = new MissumAiClient(http);

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => WaitAsync(client));

        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task HangingHealthRequestHonorsTheStartupBudget()
    {
        using var handler = new HangingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8080/") };
        using var client = new MissumAiClient(http);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            MissumAiConnectionService.WaitForGatewayAsync(client, "1.0", true,
                TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(5), CancellationToken.None));

        Assert.True(handler.CancellationObserved);
    }

    [Fact]
    public async Task HangingHealthRequestCanBeStoppedByTheCaller()
    {
        using var handler = new HangingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8080/") };
        using var client = new MissumAiClient(http);
        using var cancellation = new CancellationTokenSource();
        var waiting = WaitAsync(client, token: cancellation.Token);
        await handler.RequestReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        Assert.True(handler.CancellationObserved);
    }

    private static Task WaitAsync(MissumAiClient client, bool local = true, CancellationToken token = default) =>
        MissumAiConnectionService.WaitForGatewayAsync(client, "1.0", local,
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1), token);

    private sealed class StartupHandler(
        HttpStatusCode status,
        bool connectionRefused = false,
        bool alwaysFail = false,
        string protocol = "1.0",
        string? payload = null,
        Action? onAttempt = null) : HttpMessageHandler
    {
        public int Attempts { get; private set; }
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            Paths.Add(request.RequestUri!.AbsolutePath);
            onAttempt?.Invoke();
            if (connectionRefused && Attempts == 1)
                throw new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused));
            var code = Attempts == 1 || alwaysFail ? status : HttpStatusCode.OK;
            var body = code == HttpStatusCode.OK
                ? payload ?? $$"""{"status":"live","protocolVersion":"{{protocol}}","timestamp":"2026-10-04T08:27:59Z"}"""
                : "starting";
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        public TaskCompletionSource RequestReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestReached.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { CancellationObserved = true; throw; }
            throw new InvalidOperationException("A hanging health request cannot finish without cancellation.");
        }
    }
}
