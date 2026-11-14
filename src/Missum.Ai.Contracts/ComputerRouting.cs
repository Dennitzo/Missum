namespace Missum.Ai.Contracts;

public sealed record ComputerRoutingRequest(string ModelId, string Prompt);

public sealed record ComputerRoutingResponse(bool UseComputer);
