using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Configuration;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Workers;

public sealed class WorkerApiClient
{
    private readonly HttpClient _httpClient;
    private readonly MissumAiServerOptions _options;
    private readonly JsonSerializerOptions _jsonOptions = MissumAiProtocol.CreateJsonOptions();

    public WorkerApiClient(
        HttpClient httpClient,
        IOptions<MissumAiServerOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _httpClient.Timeout = TimeSpan.FromHours(2);
    }

    public Task<TranscriptionResponse> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<TranscriptionResponse>(
            "speech",
            _options.SpeechWorkerUri,
            "/transcriptions",
            request,
            cancellationToken);

    public async Task<TranscriptionResponse> TranscribeLiveCaptionAsync(
        ReadOnlyMemory<byte> waveAudio,
        string? language,
        LiveCaptionMode mode,
        LiveCaptionProfile profile,
        bool isFinal,
        string sessionId,
        string? previousContext,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(_options.SpeechWorkerUri, "/live-captions"));
        if (!string.IsNullOrWhiteSpace(language))
        {
            request.Headers.TryAddWithoutValidation("X-Missum-AI-Caption-Language", language);
        }
        request.Headers.TryAddWithoutValidation(
            "X-Missum-AI-Caption-Task",
            mode == LiveCaptionMode.TranslateToEnglish ? "translate" : "transcribe");
        request.Headers.TryAddWithoutValidation(
            MissumAiHeaders.CaptionProfile,
            profile == LiveCaptionProfile.Dictation ? "dictation" : "captions");
        request.Headers.TryAddWithoutValidation(MissumAiHeaders.CaptionFinal, isFinal ? "true" : "false");
        request.Headers.TryAddWithoutValidation("X-Missum-AI-Caption-Session", sessionId);
        if (!string.IsNullOrWhiteSpace(previousContext))
        {
            var boundedContext = previousContext.Length <= 1_000
                ? previousContext
                : previousContext[^1_000..];
            request.Headers.TryAddWithoutValidation(
                "X-Missum-AI-Caption-Context-B64",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(boundedContext)));
        }
        request.Content = new ReadOnlyMemoryContent(waveAudio);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Worker speech returned HTTP {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<TranscriptionResponse>(
            _jsonOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("Worker speech returned an empty live-caption response.");
    }

    public Task<WorkerSpeechResult> SynthesizeAsync(
        SpeechRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<WorkerSpeechResult>(
            "speech",
            _options.SpeechWorkerUri,
            "/speech",
            request,
            cancellationToken);

    public Task<WorkerSpeechSessionSnapshot> BeginSpeechSessionAsync(
        string sessionId,
        SpeechContentProfile profile,
        CancellationToken cancellationToken = default) =>
        SendAsync<WorkerSpeechSessionSnapshot>(
            "speech",
            _options.SpeechWorkerUri,
            "/speech/sessions",
            new
            {
                sessionId,
                profile = profile.ToString().ToLowerInvariant(),
            },
            cancellationToken);

    public Task<WorkerSpeechResult> SynthesizeParagraphAsync(
        string sessionId,
        SpeechParagraphRequest request,
        bool forceSegmentSynthesis = false,
        CancellationToken cancellationToken = default) =>
        SendAsync<WorkerSpeechResult>(
            "speech",
            _options.SpeechWorkerUri,
            $"/speech/sessions/{Uri.EscapeDataString(sessionId)}/paragraphs",
            new
            {
                sessionId,
                request.ParagraphIndex,
                request.Text,
                request.Speed,
                request.Parts,
                forceSegmentSynthesis,
            },
            cancellationToken);

    public Task<WorkerSpeechSessionSnapshot> EndSpeechSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        SendAsync<WorkerSpeechSessionSnapshot>(
            "speech",
            _options.SpeechWorkerUri,
            $"/speech/sessions/{Uri.EscapeDataString(sessionId)}/end",
            body: null,
            cancellationToken: cancellationToken);

    public Task<JsonElement> LoadSpeechComponentAsync(
        string component,
        CancellationToken cancellationToken = default)
    {
        if (component is not ("stt" or "tts" or "speaker"))
        {
            throw new ArgumentOutOfRangeException(nameof(component));
        }

        return SendAsync<JsonElement>(
            "speech",
            _options.SpeechWorkerUri,
            "/load",
            new { component },
            cancellationToken);
    }

    public Task<JsonElement> LoadWorkerAsync(
        string workerName,
        CancellationToken cancellationToken = default) =>
        SendAsync<JsonElement>(
            workerName,
            ResolveWorkerUri(workerName),
            "/load",
            body: null,
            cancellationToken);

    public async Task<JsonElement> GetStatusAsync(
        string workerName,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        return await SendGetAsync<JsonElement>(
            workerName,
            ResolveWorkerUri(workerName),
            "/status",
            timeout.Token).ConfigureAwait(false);
    }

    public Task<WorkerImageResult> GenerateImageAsync(
        ImageGenerationRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<WorkerImageResult>(
            "image",
            _options.ImageWorkerUri,
            "/generate",
            request,
            cancellationToken);

    public Task<WorkerMediaResult> InspectMediaAsync(
        WorkerMediaRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<WorkerMediaResult>(
            "media",
            _options.MediaWorkerUri,
            "/inspect",
            request,
            cancellationToken);

    public async Task ReleaseAllAsync(string? exceptWorker, CancellationToken cancellationToken = default)
    {
        var definitions = new[]
        {
            (Name: "speech", Uri: _options.SpeechWorkerUri),
            (Name: "media", Uri: _options.MediaWorkerUri),
            (Name: "image", Uri: _options.ImageWorkerUri),
        };
        foreach (var definition in definitions)
        {
            if (string.Equals(definition.Name, exceptWorker, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                _ = await SendAsync<JsonElement>(
                    definition.Name,
                    definition.Uri,
                    "/release",
                    body: null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                // A stopped optional worker has no resources to release.
            }
        }
    }

    public async Task ReleaseAsync(string workerName, CancellationToken cancellationToken = default)
    {
        var uri = ResolveWorkerUri(workerName);
        _ = await SendAsync<JsonElement>(workerName, uri, "/release", null, cancellationToken).ConfigureAwait(false);
    }

    private Uri ResolveWorkerUri(string workerName) => workerName switch
    {
        "speech" => _options.SpeechWorkerUri,
        "media" => _options.MediaWorkerUri,
        "image" => _options.ImageWorkerUri,
        _ => throw new ArgumentOutOfRangeException(nameof(workerName)),
    };

    private async Task<T> SendGetAsync<T>(
        string workerName,
        Uri baseUri,
        string path,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, path));
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateWorkerFailureAsync(workerName, path, response, cancellationToken).ConfigureAwait(false);
        }

        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException($"Worker {workerName} returned an empty response.");
    }

    private async Task<T> SendAsync<T>(
        string workerName,
        Uri baseUri,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, path));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: _jsonOptions);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateWorkerFailureAsync(workerName, path, response, cancellationToken).ConfigureAwait(false);
        }

        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException($"Worker {workerName} returned an empty response.");
    }

    private static async Task<WorkerRequestException> CreateWorkerFailureAsync(
        string workerName, string path, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Worker errors are small protocol objects. Never retain an arbitrary HTML
        // response, uploaded media, or an unbounded provider response in the run journal.
        string? errorCode = null;
        string? detail = null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var bytes = new byte[4097];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            if (count <= 4096)
            {
                using var document = JsonDocument.Parse(bytes.AsMemory(0, count));
                var value = document.RootElement;
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("detail", out var nested))
                    value = nested;
                if (value.ValueKind == JsonValueKind.Object)
                {
                    if (value.TryGetProperty("errorCode", out var code) && code.ValueKind == JsonValueKind.String)
                    {
                        var candidate = code.GetString();
                        if (candidate is { Length: > 0 and <= 128 }
                            && candidate.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
                            errorCode = candidate;
                    }
                    if (value.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                        detail = message.GetString();
                }
                else if (value.ValueKind == JsonValueKind.String) detail = value.GetString();
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            // A malformed error body must not hide the worker's HTTP status.
        }
        return new WorkerRequestException(workerName, path, response.StatusCode, errorCode, detail);
    }
}

public sealed class WorkerRequestException : HttpRequestException
{
    public WorkerRequestException(string workerName, string operation, HttpStatusCode statusCode,
        string? errorCode, string? detail)
        : base($"Worker {workerName} returned HTTP {(int)statusCode}"
            + (errorCode is null ? "." : $" ({errorCode})."), null, statusCode)
    {
        WorkerName = workerName;
        Operation = operation;
        ErrorCode = errorCode;
        // Detail is sanitized again when exposed to the AI; this bound also
        // protects callers outside media.analyze.
        Detail = detail is null ? null : detail[..Math.Min(detail.Length, 1024)];
    }

    public string WorkerName { get; }
    public string Operation { get; }
    public string? ErrorCode { get; }
    public string? Detail { get; }
}

public sealed record WorkerArtifact(
    string RelativePath,
    string FileName,
    string MediaType,
    string? Role = null,
    double? TimecodeSeconds = null,
    string? Group = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record WorkerSpeechResult(
    string RelativePath,
    string FileName,
    string MediaType,
    string Provider,
    IReadOnlyDictionary<string, string>? Metadata,
    IReadOnlyList<SpeechParagraphTiming>? Timings = null);

public sealed record WorkerSpeechSessionSnapshot(
    string SessionId,
    string State,
    string? Profile,
    string Provider,
    double? CreatedAtUnix = null,
    double? LastUsedUnix = null);

public sealed record WorkerImageResult(
    string Provider,
    string Model,
    long DurationMilliseconds,
    IReadOnlyList<WorkerArtifact> Artifacts);

public sealed record WorkerMediaRequest(
    string UploadId,
    string MediaType,
    IReadOnlyList<WorkerTimeWindow>? DetailWindows = null);

public sealed record WorkerTimeWindow(double Start, double End);

public sealed record WorkerMediaResult(
    string Kind,
    JsonElement Metadata,
    IReadOnlyList<WorkerArtifact> Artifacts,
    IReadOnlyList<WorkerArtifact> Frames);
