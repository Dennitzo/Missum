using System.Text.Json;
using System.Text.Json.Serialization;

namespace Missum.Ai.Contracts;

public static class MissumAiProtocol
{
    public const string Version = "1.0";
    public const string ApiPrefix = "/v1";
    public const int UploadChunkSize = 8 * 1024 * 1024;
    public const long MaximumJsonBytes = 2L * 1024 * 1024;
    public const long MaximumToolResultTextBytes = 4L * 1024 * 1024;
    public const int MaximumLiveCaptionChunkBytes = 512 * 1024;
    public const int LiveCaptionSampleRate = 16_000;

    public static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public static class MissumAiHeaders
{
    public const string ClientId = "X-Missum-Client-ID";
    public const string LastEventId = "Last-Event-ID";
    public const string IdempotencyKey = "Idempotency-Key";
    public const string CaptionProfile = "X-Missum-AI-Caption-Profile";
    public const string CaptionTurnId = "X-Missum-AI-Caption-Turn-Id";
    public const string CaptionRevision = "X-Missum-AI-Caption-Revision";
    public const string CaptionWindowStartMilliseconds = "X-Missum-AI-Caption-Window-Start-Ms";
    public const string CaptionFinal = "X-Missum-AI-Caption-Final";
}

public sealed record MissumAiProblem(
    string Type,
    string Title,
    int Status,
    string Detail,
    string? ErrorCode = null,
    string? TraceId = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);
