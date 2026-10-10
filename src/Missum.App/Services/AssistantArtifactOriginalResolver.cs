using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.App.Services;

/// <summary>Recovers a legacy analysis thumbnail only from its recorded upload, retaining the thumbnail unchanged.</summary>
public sealed partial class AssistantArtifactOriginalResolver : IDisposable
{
    private const long MaximumOriginalBytes = 32L * 1024 * 1024;
    private readonly IChatArtifactRepository _artifacts;
    private readonly IChatRepository _chats;
    private readonly Func<string, CancellationToken, Task<(ArtifactDescriptor Descriptor, byte[] Content)>> _fetch;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AssistantArtifactOriginalResolver(IChatArtifactRepository artifacts, IChatRepository chats, MissumAiConnectionService connection)
        : this(artifacts, chats, async (uploadId, token) =>
        {
            using var client = await connection.CreateArtifactClientAsync(token).ConfigureAwait(false);
            var descriptor = await client.ExportOriginalUploadAsync(uploadId, token).ConfigureAwait(false);
            if (!descriptor.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || descriptor.Length is <= 0 or > MaximumOriginalBytes)
                throw new InvalidDataException("Die Originalbild-Metadaten sind ungültig.");
            var path = Path.Combine(Path.GetTempPath(), "missum-original-" + Guid.NewGuid().ToString("N"));
            try
            {
                await client.DownloadArtifactAsync(descriptor.ArtifactId, path, cancellationToken: token).ConfigureAwait(false);
                if (new FileInfo(path).Length != descriptor.Length) throw new InvalidDataException("Das Originalbild ist unvollständig.");
                return (descriptor, await File.ReadAllBytesAsync(path, token).ConfigureAwait(false));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }) { }

    internal AssistantArtifactOriginalResolver(IChatArtifactRepository artifacts, IChatRepository chats,
        Func<string, CancellationToken, Task<(ArtifactDescriptor Descriptor, byte[] Content)>> fetch)
    { _artifacts = artifacts; _chats = chats; _fetch = fetch; }

    public async Task<ChatArtifact> ResolveAsync(ChatArtifact artifact, CancellationToken token)
    {
        if (!IsThumbnail(artifact)) return artifact;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var peers = await _artifacts.ListForMessageAsync(artifact.MessageId, token).ConfigureAwait(false);
            var existing = peers.FirstOrDefault(item => item.Id != artifact.Id && item.MessageId == artifact.MessageId
                && item.Metadata?.GetValueOrDefault("role") == "original"
                && (item.Metadata?.GetValueOrDefault("replacesArtifactId") == artifact.Id.ToString("D")
                    || artifact.Metadata?.GetValueOrDefault("originalArtifactId") == item.ServerArtifactId));
            if (existing is not null && existing.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return existing;
            var message = await _chats.GetMessageAsync(artifact.MessageId, token).ConfigureAwait(false);
            var step = message?.ToolSteps?.FirstOrDefault(item => item.Id == artifact.StepId
                && item.Tool is "media.analyze" or "media.inspect");
            if (JsonString(step?.OutputJson, "kind", unwrapResult: true) is { } kind && kind != "image") return artifact;
            var uploadId = JsonString(step?.OutputJson, "resolvedUploadId", unwrapResult: true)
                ?? artifact.Metadata?.GetValueOrDefault("sourceUploadId")
                ?? JsonString(step?.InputJson, "uploadId", unwrapResult: false);
            if (step is null || string.IsNullOrWhiteSpace(uploadId))
                throw new InvalidOperationException("Das Originalbild ist für diese ältere Bildvorschau nicht mehr zugeordnet. Die Miniatur ist kein Originalbild.");
            var sameUpload = peers.FirstOrDefault(item => item.MessageId == artifact.MessageId
                && item.Metadata?.GetValueOrDefault("role") == "original"
                && item.Metadata.GetValueOrDefault("sourceUploadId") == uploadId);
            if (sameUpload is not null) return sameUpload;
            if (message is not null && await RecoverVerifiedWorkspaceImageAsync(artifact, message, uploadId, token).ConfigureAwait(false) is { } recovered)
                return recovered;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            (ArtifactDescriptor descriptor, byte[] content) = await _fetch(uploadId, timeout.Token).ConfigureAwait(false);
            if (!descriptor.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || descriptor.Length is <= 0 or > MaximumOriginalBytes || content.LongLength != descriptor.Length
                || descriptor.Metadata?.GetValueOrDefault("sourceUploadId") != uploadId)
                throw new InvalidDataException("Das Originalbild gehört nicht zum gespeicherten Bildaufruf.");
            var metadata = new Dictionary<string, string>(descriptor.Metadata!, StringComparer.Ordinal)
            { ["role"] = "original", ["replacesArtifactId"] = artifact.Id.ToString("D") };
            await using var input = new MemoryStream(content, writable: false);
            return await _artifacts.ImportAsync(artifact.MessageId, descriptor.ArtifactId, descriptor.FileName,
                descriptor.MediaType, descriptor.Sha256, descriptor.Length, artifact.Provider, artifact.StepId,
                metadata, input, token).ConfigureAwait(false);
        }
        catch (Missum.Ai.Client.MissumAiApiException exception) when (exception.Problem?.Status == 404)
        { throw new InvalidOperationException("Das Originalbild ist nicht mehr auf dem Server verfügbar. Die gespeicherte Miniatur kann die Originalauflösung nicht wiederherstellen.", exception); }
        finally { _gate.Release(); }
    }

    internal static IReadOnlyList<ChatArtifact> DisplayArtifacts(IReadOnlyList<ChatArtifact> artifacts,
        IReadOnlyList<AssistantToolStep>? steps = null)
    {
        var originals = artifacts.Where(item => item.Metadata?.GetValueOrDefault("role") == "original").ToArray();
        return artifacts.Where(item => !originals.Any(original => original.MessageId == item.MessageId && original.Id != item.Id
            && (original.Metadata?.GetValueOrDefault("replacesArtifactId") == item.Id.ToString("D")
                || IsThumbnail(item) && (item.Metadata?.GetValueOrDefault("originalArtifactId") == original.ServerArtifactId
                    || RecordedUpload(item, steps) is { } upload && original.Metadata?.GetValueOrDefault("sourceUploadId") == upload)))).ToArray();
    }

    private static string? RecordedUpload(ChatArtifact artifact, IReadOnlyList<AssistantToolStep>? steps)
    {
        var step = steps?.FirstOrDefault(item => item.Id == artifact.StepId && item.Tool is "media.analyze" or "media.inspect");
        return JsonString(step?.OutputJson, "resolvedUploadId", true) ?? artifact.Metadata?.GetValueOrDefault("sourceUploadId")
            ?? JsonString(step?.InputJson, "uploadId", false);
    }

    private static bool IsThumbnail(ChatArtifact artifact) => artifact.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        && string.Equals(artifact.Metadata?.GetValueOrDefault("role"), "thumbnail", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _gate.Dispose();

    private static string? JsonString(string? json, string name, bool unwrapResult)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (unwrapResult && root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var result)) root = result;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
