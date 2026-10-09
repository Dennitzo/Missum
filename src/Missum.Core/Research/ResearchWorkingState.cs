using System.Text.Json;
using System.Text.Json.Serialization;

namespace Missum.Core.Research;

/// <summary>The durable scientific working set. Revision is independent of chat/run checkpoints.</summary>
public sealed record ResearchWorkingState(
    string ProjectId,
    long Revision,
    long PublicationRevision,
    string Title,
    IReadOnlyList<ResearchWorkingItem> Items,
    DateTimeOffset UpdatedAt);

public sealed record ResearchWorkingItem(
    string Id,
    string Kind,
    long Revision,
    string? OwnerAgentId,
    JsonElement Data,
    DateTimeOffset UpdatedAt);

/// <summary>Data replaces one object; Patch merges only explicitly supplied fields into an existing object.</summary>
public sealed record ResearchWorkingChange(string Id, string Kind, long ExpectedRevision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] JsonElement Data = default,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Patch = null);

public sealed record ResearchWorkingConflict(string Id, string Code, string Message, long? ActualRevision = null);

public sealed record ResearchWorkingUpdateResult(
    bool Success,
    ResearchWorkingState State,
    IReadOnlyList<string> ChangedIds,
    IReadOnlyList<ResearchWorkingConflict> Conflicts,
    bool Replayed = false);

/// <summary>A single database snapshot for resumable, stamped research reads.</summary>
public sealed record ResearchWorkingReadSnapshot(
    ScientificResearchProject Project,
    ResearchWorkingState State,
    IReadOnlyList<ResearchLiteratureEntry> Sources,
    IReadOnlyList<ResearchEvidenceRecord> Evidence,
    IReadOnlyList<ResearchExperiment> Experiments,
    IReadOnlyList<ResearchVerification> Checks);

/// <summary>
/// Model-authored statements and manuscript sections, separate from trusted execution/source receipts.
/// actorAgentId is taken from the authenticated run context, never from tool arguments.
/// </summary>
public interface IScientificResearchStateRepository
{
    Task<ResearchWorkingState> LoadWorkingStateAsync(string projectId, CancellationToken cancellationToken = default);

    Task<ResearchWorkingReadSnapshot> LoadWorkingReadSnapshotAsync(string projectId,
        CancellationToken cancellationToken = default);

    Task<ResearchWorkingUpdateResult> ApplyWorkingUpdateAsync(string projectId, string operationId,
        string? actorAgentId, string? title, IReadOnlyList<ResearchWorkingChange> changes,
        CancellationToken cancellationToken = default);

    /// <summary>Recovers an already committed operation without executing a mutation again.</summary>
    Task<ResearchWorkingUpdateResult?> ReadWorkingOperationAsync(string projectId, string operationId,
        string? actorAgentId, CancellationToken cancellationToken = default);

    /// <summary>Stores a receipt produced by a real local tool; never exposed as a model write operation.</summary>
    Task SaveExecutionVerificationAsync(ResearchVerification verification, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ResearchVerification>> LoadExecutionVerificationsAsync(string projectId,
        CancellationToken cancellationToken = default);
}
