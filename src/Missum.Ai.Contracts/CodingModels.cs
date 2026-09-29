namespace Missum.Ai.Contracts;

public sealed record CodingModelCatalogResponse(
    IReadOnlyList<ModelRuntimeStatus> Models,
    string ModelRoot,
    bool RuntimeReachable,
    string? Message,
    DateTimeOffset UpdatedAt);
