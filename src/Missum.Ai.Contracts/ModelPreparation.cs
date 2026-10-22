namespace Missum.Ai.Contracts;

public sealed record ModelPreparationRequest(string ModelId, int ContextLength = 0);
public sealed record ModelPreparationResponse(string ModelId, int ContextLength, bool AlreadyLoaded);
