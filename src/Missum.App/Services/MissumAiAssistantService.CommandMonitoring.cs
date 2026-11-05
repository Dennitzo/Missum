using System.Text.Json;
using Missum.Ai.Client;
using Missum.Ai.Contracts;
using Missum.Core.Coding;
using Missum.Core.Models;

namespace Missum.App.Services;

public sealed partial class MissumAiAssistantService
{
    private async Task<ClientToolResult> ExecuteMonitoredCommandAsync(MissumAiClient client, MissumAiRunRecord localRun,
        RunEvent item, ToolProposal proposal, Func<CodingCommandProgress, Task>? commandProgress,
        CodingRunEvidenceStore? evidenceStore, string? researchActorAgentId, CancellationToken cancellationToken)
    {
        // This cancellation source owns only this local command. A model decision
        // to stop a process must never cancel the conversation or its SSE stream.
        using var commandStop = new CancellationTokenSource();
        using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, commandStop.Token);
        CodingCommandProgress? latest = null;
        string? stopReason = null;
        long revision = 0;
        long nextUpload = 0;
        async Task ObserveAsync(CodingCommandProgress progress)
        {
            latest = progress;
            if (commandProgress is not null) await commandProgress(progress).ConfigureAwait(false);
            var now = Environment.TickCount64;
            if (now < nextUpload || commandStop.IsCancellationRequested) return;
            nextUpload = now + 5000;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                const int maximum = 16_384;
                var stdout = progress.Stdout.Length > maximum ? progress.Stdout[^maximum..] : progress.Stdout;
                var stderr = progress.Stderr.Length > maximum ? progress.Stderr[^maximum..] : progress.Stderr;
                var receipt = await client.SubmitClientToolProgressAsync(item.RunId,
                    new(proposal.ProposalId, ++revision, progress.ElapsedMilliseconds, stdout, stderr,
                        progress.Truncated || stdout.Length != progress.Stdout.Length || stderr.Length != progress.Stderr.Length,
                        progress.ProcessId, progress.CpuMilliseconds, progress.WorkingSetBytes,
                        progress.LastOutputElapsedMilliseconds, progress.OutputCharacters), timeout.Token).ConfigureAwait(false);
                if (receipt.StopRequested)
                {
                    stopReason = receipt.Reason ?? "Die Prozessüberwachung hat diesen Befehl beendet.";
                    await commandStop.CancelAsync().ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                && exception is HttpRequestException or IOException or JsonException or OperationCanceledException)
            {
                // A temporary monitoring outage is not a process failure. Keep
                // collecting output and retry the next bounded heartbeat.
                RunDiagnostic(logger, item.RunId, "command progress upload deferred", exception);
            }
        }
        try
        {
            return await toolBroker.ExecuteForAgentAsync(proposal, localRun.SessionId, localRun.AssistantMessageId,
                codingWorkspacePath: _codingWorkspaces.GetValueOrDefault(item.RunId) ?? localRun.WorkspacePath,
                commandProgress: ObserveAsync, evidenceStore: evidenceStore, runAction: localRun.Action,
                gatewayOverride: client.BaseAddress, cancellationToken: commandCancellation.Token,
                researchActorAgentId: researchActorAgentId).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (commandStop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // LocalCodingToolExecutor has already closed the owned process job
            // and waited for exit before propagating this command-only cancel.
            return new(proposal.ProposalId, "failed", JsonSerializer.SerializeToElement(new
            {
                success = false, cancelled = true, supervisionStopped = true, processExitVerified = true,
                partialOutput = true, outputComplete = false, exitCode = (int?)null, reason = stopReason,
                stdout = latest?.Stdout ?? string.Empty, stderr = latest?.Stderr ?? string.Empty,
                truncated = latest?.Truncated ?? false, elapsedMilliseconds = latest?.ElapsedMilliseconds ?? 0,
                processId = latest?.ProcessId, cpuMilliseconds = latest?.CpuMilliseconds,
                workingSetBytes = latest?.WorkingSetBytes, outputCharacters = latest?.OutputCharacters,
            }, JsonOptions), "client.command_stopped_by_monitor", stopReason);
        }
    }
}
