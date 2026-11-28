using Missum.Ai.Contracts;
using Missum.Core.Models;

namespace Missum.App.Services;

public sealed partial class LocalToolBroker
{
    private static readonly TimeSpan WebdesignActivationWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan WebdesignSurfaceWait = TimeSpan.FromSeconds(8);

    /// <summary>
    /// The local AI may use webdesign.serve/webdesign.preview in any Codex run that offers them, also when
    /// the session's Webdesign tool is still off (web project or web prompt detected). The first call
    /// switches the tool on for the session, which shows the chip and the "Webseite" tab. Page operations
    /// (open, capture, console, inspect) wait, bounded, until the tab has registered its preview surface
    /// for this project, so <see cref="Webdesign.WebdesignPreviewRegistry.For"/> finds it.
    /// </summary>
    private async Task EnsureWebdesignToolAsync(ToolProposal proposal, Guid sessionId, string? workspace,
        CancellationToken cancellationToken)
    {
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var activated = false;
        if (session is { ChatMode: ChatMode.Coding, WebdesignEnabled: false })
        {
            await chats.SetWebdesignEnabledAsync(sessionId, true, cancellationToken).ConfigureAwait(false);
            Webdesign.WebdesignToolActivation.Raise(sessionId);
            activated = true;
        }
        if (string.IsNullOrWhiteSpace(workspace)) return;
        // Only page operations need the tab itself; server, annotation and resolve calls never wait.
        var operation = proposal.Arguments.TryGetProperty("operation", out var value) ? value.GetString() : null;
        if (proposal.Name != WorkspaceTools.WebdesignPreview || operation is not ("open" or "capture" or "console" or "inspect")) return;
        // A tab that just appeared still starts its server and browser; otherwise the session may simply not be shown.
        _ = await Webdesign.WebdesignPreviewRegistry.WaitForAsync(workspace, ready: true,
            activated ? WebdesignActivationWait : WebdesignSurfaceWait, cancellationToken).ConfigureAwait(false);
    }
}
