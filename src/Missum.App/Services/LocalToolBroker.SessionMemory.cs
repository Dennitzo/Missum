using Missum.Ai.Contracts;
using Missum.Core.Models;

namespace Missum.App.Services;

public sealed partial class LocalToolBroker
{
    /// <summary>
    /// Runs <c>memory.sessions.search</c>: a read-only search over the user's other chats. Only
    /// ChatGPT sessions receive the tool, so a proposal from any other session is refused.
    /// </summary>
    private async Task<ClientToolResult> ExecuteSessionMemorySearchAsync(ToolProposal proposal, Guid sessionId,
        CancellationToken cancellationToken)
    {
        ValidateProposal(proposal, extensionTool: SessionMemorySearchTool.Descriptor);
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Sitzung der Gedächtnissuche wurde nicht gefunden.");
        if (session.ChatMode != ChatMode.General)
            throw new UnauthorizedAccessException("Die chatübergreifende Gedächtnissuche ist nur im ChatGPT-Modus freigegeben.");
        var payload = await SessionMemorySearchTool.ExecuteAsync(chats, sessionId, proposal.Arguments, cancellationToken)
            .ConfigureAwait(false);
        return Result(proposal, "completed", payload);
    }
}
