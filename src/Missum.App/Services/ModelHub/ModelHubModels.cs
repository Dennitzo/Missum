namespace Missum.App.Services.ModelHub;

/// <summary>One unsloth GGUF repository as listed by Hugging Face.</summary>
public sealed record HubModel(string Id, DateTimeOffset LastModified, string? PipelineTag, bool Gated)
{
    /// <summary>"unsloth/Inkling-2-Mini-GGUF" → "Inkling-2-Mini".</summary>
    public string Name
    {
        get
        {
            var name = Id.Contains('/', StringComparison.Ordinal) ? Id[(Id.IndexOf('/', StringComparison.Ordinal) + 1)..] : Id;
            return name.EndsWith("-GGUF", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
        }
    }
}

public sealed record HubModelPage(IReadOnlyList<HubModel> Models, string? NextCursor);

/// <summary>A file in a repository at one commit.</summary>
public sealed record HubFile(string Path, long Size, string? Sha256);

public sealed record HubModelDetails(
    string Id,
    string Commit,
    DateTimeOffset LastModified,
    string? BaseModel,
    string? PipelineTag,
    long? Parameters,
    int? ContextLength,
    IReadOnlyList<HubFile> Files);

/// <summary>A downloadable GGUF quantization: the model file(s) plus companions it needs.</summary>
public sealed record HubQuantization(string Label, IReadOnlyList<HubFile> Parts, IReadOnlyList<HubFile> Companions)
{
    public long ModelBytes => Parts.Sum(p => p.Size);
    public long TotalBytes => ModelBytes + Companions.Sum(c => c.Size);
    /// <summary>The file llama.cpp loads; for split models the -00001- part.</summary>
    public HubFile Primary => Parts[0];
    public IEnumerable<HubFile> AllFiles => Parts.Concat(Companions);
}

internal static partial class ModelHubLog
{
    [Microsoft.Extensions.Logging.LoggerMessage(EventId = 7101, Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Model download {Repo} {Label} failed")]
    public static partial void DownloadFailed(Microsoft.Extensions.Logging.ILogger logger, Exception exception, string repo, string label);

    [Microsoft.Extensions.Logging.LoggerMessage(EventId = 7102, Level = Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Model hub: {Context}")]
    public static partial void Minor(Microsoft.Extensions.Logging.ILogger logger, Exception exception, string context);

    [Microsoft.Extensions.Logging.LoggerMessage(EventId = 7103, Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Model download state: {Context}")]
    public static partial void StateFailed(Microsoft.Extensions.Logging.ILogger logger, Exception exception, string context);
}
