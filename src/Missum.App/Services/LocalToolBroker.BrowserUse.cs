using Missum.Ai.Contracts;
using Missum.Core.Extensions;

namespace Missum.App.Services;

public sealed partial class LocalToolBroker
{
    /// <summary>
    /// Runs <c>browser.use</c>. In a Codex session with the Webdesign tool on, a Missum-native agent
    /// works in the project's "Webseite" tab (<see cref="WebViewBrowserAgent"/>); otherwise the
    /// browser-use agent drives a real Chrome or Edge. Both use the model loaded in the local Missum
    /// AI runtime and return the same result shape to the run.
    /// </summary>
    private async Task<ClientToolResult> ExecuteBrowserUseAsync(ToolProposal proposal, Guid sessionId, string? codingWorkspacePath,
        string? researchActorAgentId, CancellationToken cancellationToken)
    {
        if (researchActorAgentId is not null)
            throw new UnauthorizedAccessException("Browser-Use ist für Recherche-Teilagenten nicht freigegeben.");
        ValidateProposal(proposal, extensionTool: BuiltInExtensionCatalog.Tools.Single(tool => tool.ModelToolName == proposal.Name));
        var request = BrowserUseRequest.Parse(proposal.Arguments);
        var surface = await FindBrowserUseSurfaceAsync(request, sessionId, codingWorkspacePath, cancellationToken).ConfigureAwait(false);
        var outcome = surface is not null
            ? await RunWebViewBrowserUseAsync(request, surface, cancellationToken).ConfigureAwait(false)
            : await BrowserUseTool.ExecuteAsync(request, connection.SelectedModel, cancellationToken).ConfigureAwait(false);
        var payload = new
        {
            success = outcome.Success,
            status = outcome.Status,
            result = outcome.Result,
            error = outcome.Error,
            steps = outcome.Steps,
            urls = outcome.Urls,
            model = outcome.Model,
            browser = outcome.Browser,
            mode = outcome.Mode,
            finalUrl = outcome.FinalUrl,
            actions = outcome.Actions,
        };
        return outcome.Success
            ? Result(proposal, "completed", payload)
            : Result(proposal, "failed", payload, "client.browser_use_failed", outcome.Error);
    }

    /// <summary>
    /// The "Webseite" tab of the session's project when browser.use should work there: the Webdesign
    /// tool is on and the tab (possibly just activated, bounded wait) shows the project's server.
    /// A start address on another site keeps the external browser.
    /// </summary>
    private async Task<Webdesign.IWebdesignPreviewSurface?> FindBrowserUseSurfaceAsync(BrowserUseRequest request, Guid sessionId,
        string? codingWorkspacePath, CancellationToken cancellationToken)
    {
        if (!BrowserUseDispatch.StartUrlMayBeLocal(request.StartUrl)) return null;
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var workspace = !string.IsNullOrWhiteSpace(codingWorkspacePath) ? codingWorkspacePath : session?.CodingWorkspacePath;
        if (!BrowserUseDispatch.WantsWebView(session, workspace)) return null;
        if (Webdesign.WebdesignPreviewRegistry.TryGet(workspace) is not { IsReady: true })
        {
            if (Webdesign.WebdesignServeService.Instance is { } serve)
            {
                try { await serve.EnsureAsync(workspace!, cancellationToken).ConfigureAwait(false); }
                // The tab reports a server that does not start; browser.use then keeps the external browser.
                catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException)) { }
            }
            var registered = Webdesign.WebdesignPreviewRegistry.TryGet(workspace) is not null;
            // A session whose tab is not registered yet refreshes so chip and tab appear (as webdesign.preview does).
            if (!registered) Webdesign.WebdesignToolActivation.Raise(sessionId);
            _ = await Webdesign.WebdesignPreviewRegistry.WaitForAsync(workspace!, ready: true,
                registered ? WebdesignSurfaceWait : WebdesignActivationWait, cancellationToken).ConfigureAwait(false);
        }
        var surface = Webdesign.WebdesignPreviewRegistry.TryGet(workspace);
        return BrowserUseDispatch.UseWebView(session, workspace, surface, request.StartUrl) ? surface : null;
    }

    private async Task<BrowserUseOutcome> RunWebViewBrowserUseAsync(BrowserUseRequest request, Webdesign.IWebdesignPreviewSurface surface,
        CancellationToken cancellationToken)
    {
        var profile = AssistantRuntimeProfile.Resolve();
        var model = await BrowserUseTool.DiscoverModelAsync(profile.NativePort, connection.SelectedModel, cancellationToken).ConfigureAwait(false);
        // One browser agent at a time: it shares the single local model slot with the run.
        await BrowserUseTool.RunGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var client = new OpenAiBrowserAgentModel(model);
            var agent = new WebViewBrowserAgent(surface, client, new WebViewBrowserAgentOptions { MaxSteps = WebViewBrowserAgent.StepsFor(request) });
            return await agent.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally { BrowserUseTool.RunGate.Release(); }
    }
}
