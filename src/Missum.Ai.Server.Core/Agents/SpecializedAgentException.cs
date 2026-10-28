namespace Missum.Ai.Server.Core.Agents;

public sealed class SpecializedAgentException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
