using Missum.Ai.Client;
using Missum.Ai.Contracts;
using System.Net;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class SessionContextCompactionClientTests
{
    [Fact]
    public async Task ExplicitCompactionUsesItsLongRunningChannelAndTheSameClientIdentity()
    {
        using var controlHandler = new CompactionHandler();
        using var longHandler = new CompactionHandler { Delay = TimeSpan.FromMilliseconds(120) };
        using var control = new HttpClient(controlHandler) { BaseAddress = new("http://gateway.test/"), Timeout = TimeSpan.FromMilliseconds(30) };
        using var longChannel = new HttpClient(longHandler) { BaseAddress = control.BaseAddress, Timeout = Timeout.InfiniteTimeSpan };
        using var client = new MissumAiClient(control, "compaction-client", longOperationHttpClient: longChannel);

        var result = await client.CompactSessionContextAsync(new("session-123", "coding/model", 32768));

        Assert.True(result.Changed);
        Assert.Equal(150, result.AfterTokenEstimate);
        Assert.Equal(0, controlHandler.Calls);
        Assert.Equal(1, longHandler.Calls);
        Assert.Equal("compaction-client", longHandler.ClientId);
        Assert.Equal("session-123", longHandler.Request!.SessionId);
        Assert.Equal(32768, longHandler.Request.MaximumContextTokens);
    }

    [Fact]
    public async Task StopCancelsTheLongRunningCompactionRequest()
    {
        using var handler = new CompactionHandler { Delay = Timeout.InfiniteTimeSpan };
        using var http = new HttpClient(handler) { BaseAddress = new("http://gateway.test/"), Timeout = Timeout.InfiniteTimeSpan };
        using var client = new MissumAiClient(http);
        using var stop = new CancellationTokenSource();
        var pending = client.CompactSessionContextAsync(new("session-123"), stop.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, handler.Calls);
    }

    private sealed class CompactionHandler : HttpMessageHandler
    {
        internal TimeSpan Delay { get; init; }
        internal int Calls { get; private set; }
        internal string? ClientId { get; private set; }
        internal SessionContextCompactionRequest? Request { get; private set; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/sessions/context/compact", request.RequestUri!.AbsolutePath);
            ClientId = request.Headers.GetValues(MissumAiHeaders.ClientId).Single();
            Request = JsonSerializer.Deserialize<SessionContextCompactionRequest>(await request.Content!.ReadAsStringAsync(cancellationToken), MissumAiProtocol.CreateJsonOptions());
            Started.TrySetResult();
            if (Delay != TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new SessionContextCompactionResponse("run-123", true, 1000, 150, 1, "Kontext komprimiert."),
                    MissumAiProtocol.CreateJsonOptions()), System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
