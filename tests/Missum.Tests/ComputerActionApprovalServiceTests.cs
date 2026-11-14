using Missum.App.Services;
using Missum.Core.Extensions;

namespace Missum.Tests;

public sealed class ComputerActionApprovalServiceTests
{
    private static readonly string[] ComputerToolNames = ["computer.act", "computer.observe"];
    [Fact]
    public void ComputerActionIsAvailableOnlyInCodingAndDeclaresBothTools()
    {
        var catalog = ExtensionActionCatalog.CreateWithBuiltIns();
        Assert.Contains(catalog.GetActions(ExtensionChatMode.Coding), action => action.ActionId == BuiltInActionIds.ComputerUse);
        Assert.DoesNotContain(catalog.GetActions(ExtensionChatMode.General), action => action.ActionId == BuiltInActionIds.ComputerUse);
        Assert.True(catalog.TryGetTools(BuiltInActionIds.ComputerUse, out var tools));
        Assert.Equal(ComputerToolNames, tools!.Select(tool => tool.ModelToolName).Order().ToArray());
    }

    [Fact]
    public async Task ActivationAndDecisionAreBoundToTheSessionRunAndRequest()
    {
        var approvals = new ComputerActionApprovalService();
        var session = Guid.NewGuid();
        Assert.True(await approvals.ActivateAsync(session, "run-a", CancellationToken.None));
        Assert.Equal("run-a", approvals.Snapshot().ActiveRun);
        Assert.Null(approvals.Snapshot().Pending);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await approvals.ActivateAsync(Guid.NewGuid(), "run-a", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await approvals.ActivateAsync(session, "run-b", CancellationToken.None));

        var request = new ComputerApprovalRequest(session, "run-a", "frame-1", "click", "Editor", 20, 30, null);
        var decision = approvals.RequestAsync(request, CancellationToken.None);
        var pending = Assert.IsType<PendingComputerApproval>(approvals.Snapshot().Pending);
        Assert.False(pending.IsActivation);
        Assert.False(approvals.Decide(pending.Id, Guid.NewGuid(), "run-a", ComputerActionDecision.Execute));
        Assert.False(approvals.Decide(pending.Id, session, "run-b", ComputerActionDecision.Execute));
        Assert.False(approvals.Decide(Guid.NewGuid(), session, "run-a", ComputerActionDecision.Execute));
        Assert.True(approvals.Decide(pending.Id, session, "run-a", ComputerActionDecision.ManualDone));
        Assert.Equal(ComputerActionDecision.ManualDone, await decision);
        approvals.EndRun("run-a");
        Assert.Null(approvals.Snapshot().ActiveRun);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await approvals.RequestAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationOrRunEndCompletesPendingRequestWithoutAuthorizingInput()
    {
        var approvals = new ComputerActionApprovalService();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => approvals.ActivateAsync(Guid.NewGuid(), "run-a", cancel.Token));
        Assert.Null(approvals.Snapshot().Pending);

        var session = Guid.NewGuid();
        Assert.True(await approvals.ActivateAsync(session, "run-b", CancellationToken.None));
        var request = new ComputerApprovalRequest(session, "run-b", "frame-1", "click", "Editor", 20, 30, null);
        var decision = approvals.RequestAsync(request, CancellationToken.None);
        var pending = approvals.Snapshot().Pending!;
        approvals.EndRun("run-b");
        Assert.Equal(ComputerActionDecision.Cancel, await decision);
        Assert.False(approvals.Decide(pending.Id, session, "run-b", ComputerActionDecision.Execute));
        Assert.Null(approvals.Snapshot().ActiveRun);
        Assert.False(await approvals.ActivateAsync(session, "run-b", CancellationToken.None));
    }
}
