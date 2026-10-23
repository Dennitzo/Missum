using System.Net;
using System.Text;
using System.Text.Json;
using Missum.Ai.Server.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Missum.Ai.Server.Tests;

public sealed class NativeInferenceCancellationTests
{
    private static readonly string[] Roles = ["general", "coding", "subagent", "vision"];
    private static readonly string[] Phases = ["headers", "text/event-stream", "application/json"];

    public static IEnumerable<object[]> SilentInferenceCases =>
        from role in Roles
        from phase in Phases
        select new object[] { role, phase };

    [Theory]
    [MemberData(nameof(SilentInferenceCases))]
    public async Task SilentNativeInferenceWaitsForTheOwnerAndStopReleasesItsSlot(string role, string phase)
    {
        using var context = new TestServerContext();
        context.Options.ModelRuntimeUri = new("http://native.test:19090");
        using var handler = new SilentInferenceHandler(phase);
        // The runtime must override a transport-wide timeout. Control requests
        // keep their own deadlines; inference itself has no inactivity timer.
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(30) };
        using var runtime = new ModelRuntimeClient(http, context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
        using var stop = new CancellationTokenSource();
        Directory.CreateDirectory(context.Root);
        var imagePath = Path.Combine(context.Root, "vision.png");
        await File.WriteAllBytesAsync(imagePath, [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

        Task InvokeAsync(CancellationToken token) => role == "vision"
            ? runtime.AnalyzeImagesAsync(SilentInferenceHandler.ModelId, "Prüfe das Bild", [imagePath], cancellationToken: token)
            : runtime.CompleteChatAsync(SilentInferenceHandler.ModelId, [new("user", "Untersuche die Aufgabe")], [],
                modelRole: role == "coding" ? "coding" : "general",
                runtimeInstanceId: role == "subagent" ? SilentInferenceHandler.ChildId : null,
                sessionCacheKey: "silent-native-session", cancellationToken: token);

        var inference = InvokeAsync(stop.Token);
        try
        {
            await handler.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(120);
            Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
            Assert.False(inference.IsCompleted);
            Assert.Equal(1, handler.InferenceCalls);
            Assert.False(stop.IsCancellationRequested);
        }
        finally { await stop.CancelAsync(); }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inference.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, handler.InferenceCalls); // A deliberate stop never retries.
        Assert.Equal(role == "vision" ? 0 : 1, handler.InterruptedSaves);

        handler.WaitForStop = false;
        await InvokeAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, handler.InferenceCalls); // Both native execution gates were released.
    }

    private sealed class SilentInferenceHandler(string phase) : HttpMessageHandler
    {
        internal const string ModelId = "coding/qwen3.8-27b~unbounded-native-test";
        internal const string ChildId = ModelId + "@subagent";
        internal TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool WaitForStop { get; set; } = true;
        internal int InferenceCalls { get; private set; }
        internal int InterruptedSaves { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/v1/models":
                    return Json(new { data = new[]
                    {
                        new { id = ModelId, status = new { value = "loaded" }, tags = Array.Empty<string>() },
                        new { id = ChildId, status = new { value = "loaded" },
                            tags = new[] { "missum-agent-instance:subagent", "missum-base-model:" + ModelId } },
                    } });
                case "/props": return Json(new { default_generation_settings = new { n_ctx = 32_768 } });
                case "/v1/chat/completions/input_tokens": return Json(new { input_tokens = 100 });
                case "/sessions/prepare": return Json(new { status = "resident" });
                case "/sessions/save":
                    using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)))
                        if (body.RootElement.TryGetProperty("promptTokens", out var tokens) && tokens.ValueKind == JsonValueKind.Number)
                            InterruptedSaves++;
                    return Json(new { status = "saved" });
                case "/v1/chat/completions":
                    InferenceCalls++;
                    if (!WaitForStop) return Json(new { choices = new[] { new { message = new { content = "Fertig" }, finish_reason = "stop" } } });
                    if (phase == "headers")
                    {
                        Blocked.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        throw new InvalidOperationException("The native request must be cancelled by its owner.");
                    }
                    var stream = new SilentStream(phase == "application/json" ? "{\"choices\":[" : "", Blocked);
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
                    response.Content.Headers.ContentType = new(phase);
                    return response;
                default: throw new InvalidOperationException("Unexpected endpoint: " + request.RequestUri.AbsolutePath);
            }
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }

    private sealed class SilentStream(string prefix, TaskCompletionSource blocked) : Stream
    {
        private readonly byte[] _prefix = Encoding.UTF8.GetBytes(prefix);
        private int _offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < _prefix.Length)
            {
                var count = Math.Min(buffer.Length, _prefix.Length - _offset);
                _prefix.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }
            blocked.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The native read must be cancelled by its owner.");
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
