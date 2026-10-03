namespace Missum.Core.Research;

/// <summary>The same immutable publication used by the reader and all research exports.</summary>
public sealed record ScientificPublicationExportSnapshot(string ProjectId, long PublicationRevision,
    string MarkdownPath, string PdfPath, string ContentHash, bool IsDraft);

public interface IScientificPublicationProvider
{
    Task<ScientificPublicationExportSnapshot?> EnsurePublicationAsync(string projectId,
        CancellationToken cancellationToken = default);
}
