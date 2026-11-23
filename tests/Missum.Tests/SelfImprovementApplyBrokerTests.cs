using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.App.Services;

namespace Missum.Tests;

/// <summary>
/// Runs the real broker script (self-improvement-apply.ps1) against isolated installs, data, state and a
/// loopback fake gateway. Every test has its own temp root; nothing touches the user's installation.
/// </summary>
[Collection(SelfImprovementApplyTestGroup.Name)]
public sealed class SelfImprovementApplyBrokerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task AppOnlyCandidateIsSwappedVerifiedAndReattachedOnce()
    {
        await using var rig = SelfImprovementApplyRig.Create();
        var result = await rig.ApplyAsync("apply-1");

        Assert.True(result.Status == "completed", result.Message + Environment.NewLine + rig.Diagnostics());
        Assert.Equal("applied", result.Result.GetProperty("status").GetString());
        Assert.True(result.Result.GetProperty("changeApp").GetBoolean());
        Assert.False(result.Result.GetProperty("changeStack").GetBoolean());
        Assert.Equal(["v1", "v2"], await rig.StartsAfterAsync(2));
        Assert.Equal("core v2", File.ReadAllText(Path.Combine(rig.Install, "lib", "core.txt")));
        Assert.True(File.Exists(Path.Combine(rig.Install, "lib", "added.txt")));
        Assert.False(File.Exists(Path.Combine(rig.Install, "lib", "removed.txt")));
        // The old version stays as backup next to the operation, including the databases with their WAL.
        var backup = Path.Combine(rig.OpDirectory("apply-1"), "backup");
        Assert.Equal("core v1", File.ReadAllText(Path.Combine(backup, "app", "lib", "core.txt")));
        Assert.True(File.Exists(Path.Combine(backup, "db", "app-Missum.db-wal")));
        rig.AssertDataPreserved();
        using var reattach = JsonDocument.Parse(File.ReadAllText(Path.Combine(rig.OpDirectory("apply-1"), "reattach.json")));
        Assert.Equal(rig.ServerRunId, reattach.RootElement.GetProperty("serverRunId").GetString());
        Assert.Equal("attached", reattach.RootElement.GetProperty("status").GetString());
        Assert.Null(SelfImprovementApply.HoldReason());
        Assert.Null(SelfImprovementApply.ActiveOperation());

        // A lost acknowledgement retries the same proposal: it returns the recorded outcome and changes nothing.
        var retried = await rig.ExecuteAsync("apply-1", CancellationToken.None);
        Assert.Equal(result.Status, retried.Status);
        Assert.Equal(result.Result.GetRawText(), retried.Result.GetRawText());
        var recovered = await SelfImprovementApply.ResolveRecoveredAsync("apply-1", rig.SessionId, rig.ServerRunId, rig.ReadRunAsync, CancellationToken.None);
        Assert.Equal(result.Result.GetRawText(), recovered!.Result.GetRawText());
        Assert.Equal(["v1", "v2"], await rig.StartsAfterAsync(2));
        Assert.Equal(1, rig.BrokerStarts("apply-1"));
    }

    [Fact]
    public async Task DuplicateCallJoinsTheOperationAndAnotherProposalIsRefused()
    {
        await using var rig = SelfImprovementApplyRig.Create();
        SelfImprovementApplyRig.SetFaults("hold:revalidate");
        var apply = rig.ApplyAsync("apply-1");
        await rig.WaitForFileAsync(rig.FaultMarker("apply-1", "hold", "revalidate"), Wait);

        Assert.NotNull(SelfImprovementApply.HoldReason());
        var other = await rig.ExecuteAsync("apply-2", CancellationToken.None);
        Assert.Equal("rejected", other.Status);
        Assert.Equal("selfImprovement.apply_in_progress", other.ErrorCode);
        Assert.False(File.Exists(Path.Combine(rig.OpDirectory("apply-2"), "operation.json")));

        var duplicate = rig.ExecuteAsync("apply-1", rig.OldAppCancellation.Token);
        rig.Release("apply-1", "revalidate");
        var result = await apply;
        Assert.True(result.Status == "completed", result.Message + Environment.NewLine + rig.Diagnostics());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => duplicate);
        Assert.Equal(["v1", "v2"], await rig.StartsAfterAsync(2));
        Assert.Equal(1, rig.BrokerStarts("apply-1"));
    }

    [Fact]
    public async Task CancellingBeforeTheHandoffRejectsWithoutChangingAnything()
    {
        await using var rig = SelfImprovementApplyRig.Create();
        SelfImprovementApplyRig.SetFaults("hold:revalidate");
        rig.StartOldApp();
        using var cancel = new CancellationTokenSource();
        var call = rig.ExecuteAsync("apply-1", cancel.Token);
        await rig.WaitForFileAsync(rig.FaultMarker("apply-1", "hold", "revalidate"), Wait);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.False(SelfImprovementApply.IsPlannedRestart);
        Assert.True(File.Exists(Path.Combine(rig.OpDirectory("apply-1"), "abort.json")));

        rig.Release("apply-1", "revalidate");
        await rig.WaitForStatusAsync("apply-1", Wait);
        Assert.Equal("rejected", rig.Status("apply-1"));
        Assert.Equal("selfImprovement.apply_cancelled", rig.Code("apply-1"));
        Assert.True(rig.OldAppAlive);
        Assert.Equal(["v1"], await rig.StartsAfterAsync(1));
        rig.AssertInstallIsV1();
        Assert.Null(SelfImprovementApply.HoldReason());
        var retried = await rig.ExecuteAsync("apply-1", CancellationToken.None);
        Assert.Equal("selfImprovement.apply_cancelled", retried.ErrorCode);
    }

    public static TheoryData<string> AllPhases =>
        ["revalidate", "await-app-exit", "drain", "ownership", "backup", "swap-app", "swap-stack", "verify-stack", "start-app", "await-reattach"];

    /// <summary>The whole broker (supervisor and worker) dies at a phase; a new broker continues from the recorded phase.</summary>
    [Theory]
    [MemberData(nameof(AllPhases))]
    public async Task BrokerKilledAtPhaseIsResumedAndAppliesOnce(string phase)
    {
        await using var rig = SelfImprovementApplyRig.Create();
        SelfImprovementApplyRig.SetFaults("hold:" + phase);
        async Task KillAsync(bool restart)
        {
            await rig.WaitForFileAsync(rig.FaultMarker("apply-1", "hold", phase), Wait);
            SelfImprovementApplyRig.KillProcesses(rig.OpId("apply-1"));
            Assert.False(SelfImprovementApply.BrokerIsRunning(rig.OpDirectory("apply-1")));
            // After the handoff no app runs; the next app start restarts the broker of the active operation.
            if (restart) SelfImprovementApply.EnsureActiveBrokerRunning();
        }

        // Before the handoff the old app's tool call restarts the broker by itself.
        var beforeHandoff = phase == "revalidate" ? KillAsync(restart: false) : null;
        var result = await rig.ApplyAsync("apply-1", whileBrokerAlone: beforeHandoff is null ? () => KillAsync(restart: true) : null);
        if (beforeHandoff is not null) await beforeHandoff;

        Assert.True(result.Status == "completed", result.Message + Environment.NewLine + rig.Diagnostics());
        Assert.True(rig.BrokerStarts("apply-1") >= 2, rig.Diagnostics());
        Assert.Equal(["v1", "v2"], await rig.StartsAfterAsync(2));
        Assert.Equal("core v2", File.ReadAllText(Path.Combine(rig.Install, "lib", "core.txt")));
        rig.AssertDataPreserved();
    }

    /// <summary>The worker crashes in or right after a phase; the supervisor restarts it, nothing runs twice.</summary>
    [Theory]
    [InlineData("crash:revalidate")]
    [InlineData("crash:backup")]
    [InlineData("crash:after-backup")]
    [InlineData("crash:swap-app")]
    [InlineData("crash:after-swap-app")]
    [InlineData("crash:verify-stack")]
    [InlineData("crash:after-start-app")]
    [InlineData("crash:after-await-reattach")]
    public async Task WorkerCrashIsRestartedBySupervisor(string fault)
    {
        await using var rig = SelfImprovementApplyRig.Create();
        SelfImprovementApplyRig.SetFaults(fault);
        var result = await rig.ApplyAsync("apply-1");

        Assert.True(result.Status == "completed", result.Message + Environment.NewLine + rig.Diagnostics());
        Assert.True(File.Exists(rig.FaultMarker("apply-1", "crash", fault["crash:".Length..])));
        Assert.True(rig.BrokerStarts("apply-1") >= 2, rig.Diagnostics());
        Assert.Equal(["v1", "v2"], await rig.StartsAfterAsync(2));
        rig.AssertDataPreserved();
    }

    [Theory]
    [InlineData("exit", "selfImprovement.app_exited")]
    [InlineData("migrate-exit", "selfImprovement.app_exited")]
    public async Task FailedStartOrMigrationRollsBackAppAndDatabases(string mode, string cause)
    {
        await using var rig = SelfImprovementApplyRig.Create();
        rig.SetMode("v2", mode);
        var result = await rig.ApplyAsync("apply-1", newAppResumes: false);

        Assert.True(result.Status == "failed", result.Message + Environment.NewLine + rig.Diagnostics());
        Assert.Equal("selfImprovement.rolled_back", result.ErrorCode);
        Assert.Equal("rolledBack", result.Result.GetProperty("status").GetString());
        Assert.Equal(cause, result.Result.GetProperty("cause").GetString());
        Assert.Contains("zurückgerollt", result.Message);
        Assert.Equal(["v1", "v2", "v1"], await rig.StartsAfterAsync(3));
        rig.AssertInstallIsV1();
        rig.AssertDataPreserved();
        Assert.Null(SelfImprovementApply.HoldReason());
    }

    [Fact]
    public async Task CrashDuringRollbackStillCompletesTheRollback()
    {
        await using var rig = SelfImprovementApplyRig.Create();
        rig.SetMode("v2", "migrate-exit");
        SelfImprovementApplyRig.SetFaults("crash:rollback");
        var result = await rig.ApplyAsync("apply-1", newAppResumes: false);

        Assert.True(result.Status == "failed" && result.ErrorCode == "selfImprovement.rolled_back", result.Message + Environment.NewLine + rig.Diagnostics());
        Assert.True(File.Exists(rig.FaultMarker("apply-1", "crash", "rollback")));
        Assert.Equal(["v1", "v2", "v1"], await rig.StartsAfterAsync(3));
        rig.AssertInstallIsV1();
        rig.AssertDataPreserved();
    }

    [Fact]
    public async Task RunThatIsNoLongerResumableRollsBackBeforeTheNewAppStarts()
    {
        await using var rig = SelfImprovementApplyRig.Create();
        SelfImprovementApplyRig.SetFaults("hold:verify-stack");
        var result = await rig.ApplyAsync("apply-1", newAppResumes: false, whileBrokerAlone: async () =>
        {
            await rig.WaitForFileAsync(rig.FaultMarker("apply-1", "hold", "verify-stack"), Wait);
            rig.Gateway.RunState = "cancelled";
            rig.Release("apply-1", "verify-stack");
        });

        Assert.True(result.Status == "failed", result.Message + Environment.NewLine + rig.Diagnostics());
        Assert.Equal("selfImprovement.rolled_back", result.ErrorCode);
        Assert.Equal("selfImprovement.run_not_resumable", result.Result.GetProperty("cause").GetString());
        Assert.Contains(rig.ServerRunId, result.Message);
        Assert.Equal(["v1", "v1"], await rig.StartsAfterAsync(2));
        rig.AssertInstallIsV1();
        rig.AssertDataPreserved();
    }

    [Fact]
    public async Task ReattachFailureRollsBack()
    {
        await using var rig = SelfImprovementApplyRig.Create();
        var result = await rig.ApplyAsync("apply-1", readRun: _ => Task.FromResult(new RunSnapshot(
            rig.ServerRunId, RunState.Cancelled, RunMode.Coding, null, null, 8, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));

        Assert.True(result.Status == "failed", result.Message + Environment.NewLine + rig.Diagnostics());
        Assert.Equal("selfImprovement.reattach_failed", result.Result.GetProperty("cause").GetString());
        Assert.Contains("Cancelled", result.Message);
        Assert.Equal(["v1", "v2", "v1"], await rig.StartsAfterAsync(3));
        rig.AssertInstallIsV1();
        rig.AssertDataPreserved();
    }

    [Fact]
    public async Task FailedRollbackKeepsChatAndCheckpointAndSaysSo()
    {
        await using var rig = SelfImprovementApplyRig.Create();
        SelfImprovementApplyRig.SetFaults("fail:verify-stack,fail:rollback");
        var result = await rig.ApplyAsync("apply-1", newAppResumes: false);

        Assert.True(result.Status == "failed", result.Message + Environment.NewLine + rig.Diagnostics());
        Assert.Equal("selfImprovement.rollback_failed", result.ErrorCode);
        Assert.Contains("Chat und Checkpoint bleiben erhalten", result.Message);
        var problems = result.Result.GetProperty("problems").EnumerateArray().Select(p => p.GetProperty("code").GetString() ?? "").ToArray();
        Assert.Equal(["selfImprovement.test_fault", "selfImprovement.rollback_failed"], problems);
        // The backup with the databases and the old app stays for a manual restore.
        Assert.True(File.Exists(Path.Combine(rig.OpDirectory("apply-1"), "backup", "manifest.json")));
        Assert.True(File.Exists(Path.Combine(rig.OpDirectory("apply-1"), "backup", "db", "app-Missum.db-wal")));
        Assert.Null(SelfImprovementApply.HoldReason());
    }

    [Fact]
    public async Task PortableFileChangedAfterVerificationIsRejectedBeforeAnyChange()
    {
        await using var rig = SelfImprovementApplyRig.Create();
        File.WriteAllText(Path.Combine(rig.Portable, "lib", "core.txt"), "tampered");
        var result = await rig.ApplyAsync("apply-1");

        Assert.Equal("rejected", result.Status);
        Assert.Equal("selfImprovement.portable_modified", result.ErrorCode);
        Assert.Contains("lib\\core.txt", result.Result.GetProperty("problems")[0].GetProperty("paths").EnumerateArray().Select(p => p.GetString()));
        Assert.True(rig.OldAppAlive);
        Assert.Equal(["v1"], await rig.StartsAfterAsync(1));
        Assert.False(SelfImprovementApply.IsPlannedRestart);
        rig.AssertInstallIsV1();
    }

    [Fact]
    public async Task UnknownImageDigestIsRejectedBeforeAnyChange()
    {
        await using var rig = SelfImprovementApplyRig.Create();
        rig.Images["missum-ai/gateway:candidate-" + rig.CandidateId] = "sha256:" + new string('0', 64);
        var result = await rig.ApplyAsync("apply-1");

        Assert.Equal("rejected", result.Status);
        Assert.Equal("selfImprovement.image_digest_mismatch", result.ErrorCode);
        Assert.True(rig.OldAppAlive);
        Assert.Equal(["v1"], await rig.StartsAfterAsync(1));
        rig.AssertInstallIsV1();
        Assert.Contains("MISSUM_AI_IMAGE_VERSION=2.0.0", File.ReadAllText(rig.EnvFile));
    }

    [Fact]
    public async Task CandidateEqualToTheInstallationIsRejected()
    {
        await using var rig = SelfImprovementApplyRig.Create();
        Directory.Delete(rig.Portable, true);
        SelfImprovementApplyRig.CopyTree(rig.Install, rig.Portable);
        rig.WritePortableManifest();
        var result = await rig.ApplyAsync("apply-1");

        Assert.Equal("rejected", result.Status);
        Assert.Equal("selfImprovement.no_changes", result.ErrorCode);
        Assert.Equal(["v1"], await rig.StartsAfterAsync(1));
    }

    [Fact]
    public async Task OtherActiveRunsBlockTheApplyBeforeAnythingIsWritten()
    {
        await using var rig = SelfImprovementApplyRig.Create();
        rig.Gateway.ActiveRuns = ["run-other"];
        rig.StartOldApp();
        var result = await rig.ExecuteAsync("apply-1", CancellationToken.None);

        Assert.Equal("rejected", result.Status);
        Assert.Equal("selfImprovement.drain_pending", result.ErrorCode);
        Assert.Contains("run-other", result.Message);
        Assert.False(File.Exists(Path.Combine(rig.OpDirectory("apply-1"), "operation.json")));
        Assert.Null(SelfImprovementApply.HoldReason());
        Assert.True(rig.OldAppAlive);
    }

    [Fact]
    public async Task RunStartedDuringTheHandoffIsReportedPendingAndTheAppRestarted()
    {
        await using var rig = SelfImprovementApplyRig.Create();
        SelfImprovementApplyRig.SetFaults("hold:drain");
        var result = await rig.ApplyAsync("apply-1", newAppResumes: false, whileBrokerAlone: async () =>
        {
            await rig.WaitForFileAsync(rig.FaultMarker("apply-1", "hold", "drain"), Wait);
            rig.Gateway.ActiveRuns = ["run-worker-job"];
            rig.Release("apply-1", "drain");
        });

        Assert.Equal("rejected", result.Status);
        Assert.Equal("selfImprovement.drain_pending", result.ErrorCode);
        Assert.Contains("run-worker-job", result.Message);
        // Nothing was changed; the app that ended for the handoff is started again.
        Assert.Equal(["v1", "v1"], await rig.StartsAfterAsync(2));
        rig.AssertInstallIsV1();
        rig.AssertDataPreserved();
        Assert.False(Directory.Exists(Path.Combine(rig.OpDirectory("apply-1"), "backup")));
    }

    [Fact]
    public async Task ExecutableOutsideTheInstallationIsRejected()
    {
        await using var rig = SelfImprovementApplyRig.Create();
        rig.Executable = Path.Combine(rig.Root, "elsewhere", "app.cmd");
        rig.WriteApp(Path.GetDirectoryName(rig.Executable)!, "v1");
        var result = await rig.ApplyAsync("apply-1", newAppResumes: false);

        Assert.Equal("rejected", result.Status);
        Assert.Equal("selfImprovement.ownership_mismatch", result.ErrorCode);
        Assert.Equal(["v1", "v1"], await rig.StartsAfterAsync(2));
        rig.AssertInstallIsV1();
    }
}
