using Missum.App.Services;

namespace Missum.Tests;

/// <summary>
/// Stack swaps against real Docker: an isolated compose project with its own image tags and data bind mount.
/// These tests fail (not skip) when Docker is unavailable, so a missing mandatory check never looks like success.
/// </summary>
[Collection(SelfImprovementApplyTestGroup.Name)]
public sealed class SelfImprovementApplyStackTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task GatewayOnlyCandidateSwapsTheStackAndKeepsTheApp()
    {
        await using var rig = SelfImprovementApplyRig.Create();
        rig.UseStack(changeApp: false);
        var result = await rig.ApplyAsync("apply-1");

        Assert.True(result.Status == "completed", result.Message + Environment.NewLine + rig.Diagnostics());
        Assert.False(result.Result.GetProperty("changeApp").GetBoolean());
        Assert.True(result.Result.GetProperty("changeStack").GetBoolean());
        Assert.Equal(rig.CandidateImageId, rig.GatewayContainerImage());
        var env = File.ReadAllText(rig.EnvFile);
        Assert.Contains("MISSUM_AI_IMAGE_VERSION=candidate-" + rig.CandidateId.ToLowerInvariant(), env);
        Assert.Contains("MISSUM_AI_OTHER=kept", env);
        // The same (unchanged) app is restarted and reattaches the run.
        Assert.Equal(["v1", "v1"], await rig.StartsAfterAsync(2));
        rig.AssertInstallIsV1();
        Assert.Equal(5, SelfImprovementApplyRig.ReadRows(rig.GatewayDatabase).Length);
        Assert.True(File.Exists(Path.Combine(rig.OpDirectory("apply-1"), "backup", "db", "gateway-wal")));
        Assert.Equal(rig.OriginalEnv, File.ReadAllText(Path.Combine(rig.OpDirectory("apply-1"), "backup", "stack.env")));
    }

    [Fact]
    public async Task AppAndGatewayAreSwappedTogether()
    {
        await using var rig = SelfImprovementApplyRig.Create();
        rig.UseStack(changeApp: true);
        var result = await rig.ApplyAsync("apply-1");

        Assert.True(result.Status == "completed", result.Message + Environment.NewLine + rig.Diagnostics());
        Assert.True(result.Result.GetProperty("changeApp").GetBoolean());
        Assert.True(result.Result.GetProperty("changeStack").GetBoolean());
        Assert.Equal(rig.CandidateImageId, rig.GatewayContainerImage());
        Assert.Equal(["v1", "v2"], await rig.StartsAfterAsync(2));
        Assert.Equal("core v2", File.ReadAllText(Path.Combine(rig.Install, "lib", "core.txt")));
        rig.AssertDataPreserved();
    }

    /// <summary>
    /// The candidate gateway half-migrates its database and then reports a fault; the rollback restores the
    /// image version, the container, the gateway database with its WAL, the app and the app databases.
    /// </summary>
    [Theory]
    [InlineData("protocol", "selfImprovement.protocol_mismatch")]
    [InlineData("down", "selfImprovement.gateway_start_failed")]
    [InlineData("run", "selfImprovement.run_not_resumable")]
    public async Task FailingCandidateGatewayRollsBackEverything(string fault, string cause)
    {
        await using var rig = SelfImprovementApplyRig.Create();
        rig.UseStack(changeApp: true);
        switch (fault)
        {
            case "protocol": rig.Gateway.CandidateProtocol = "2.0"; break;
            case "down": rig.Gateway.CandidateDown = true; break;
            default: rig.Gateway.CandidateRunState = "failed"; break;
        }
        SelfImprovementApply.BrokerTimeouts!["gatewaySeconds"] = 6;
        SelfImprovementApplyRig.SetFaults("hold:verify-stack");
        var result = await rig.ApplyAsync("apply-1", newAppResumes: false, whileBrokerAlone: async () =>
        {
            await rig.WaitForFileAsync(rig.FaultMarker("apply-1", "hold", "verify-stack"), Wait);
            Assert.Equal(rig.CandidateImageId, rig.GatewayContainerImage());
            // What a failed migration of the candidate gateway leaves behind.
            File.WriteAllText(rig.GatewayDatabase, "half-migrated");
            File.Delete(rig.GatewayDatabase + "-wal");
            rig.Release("apply-1", "verify-stack");
        });

        Assert.True(result.Status == "failed", result.Message + Environment.NewLine + rig.Diagnostics());
        Assert.Equal("selfImprovement.rolled_back", result.ErrorCode);
        Assert.Equal(cause, result.Result.GetProperty("cause").GetString());
        Assert.Equal(rig.OriginalEnv, File.ReadAllText(rig.EnvFile));
        Assert.Equal(rig.LiveImageId, rig.GatewayContainerImage());
        Assert.Equal(["gateway-0", "gateway-1", "gateway-2", "gateway-3", "gateway-4"], SelfImprovementApplyRig.ReadRows(rig.GatewayDatabase));
        Assert.Equal(["v1", "v1"], await rig.StartsAfterAsync(2));
        rig.AssertInstallIsV1();
        rig.AssertDataPreserved();
    }

    [Fact]
    public async Task ContainerOfAnotherDataDirectoryIsNotTouched()
    {
        await using var rig = SelfImprovementApplyRig.Create();
        rig.UseStack(changeApp: false);
        // The installation claims a different stack data root than the one the container mounts.
        var foreign = Path.Combine(rig.Root, "foreign-stack");
        Directory.CreateDirectory(Path.Combine(foreign, "data"));
        SelfImprovementApply.InstallationOverride = (_, _) => Task.FromResult<(SelfImprovementApply.Installation?, string?)>(
            (new(rig.Executable, rig.Install, rig.Data, foreign, rig.EnvFile, rig.ComposeFiles, rig.Project, rig.CurrentImageVersion), null));
        var result = await rig.ApplyAsync("apply-1", newAppResumes: false);

        Assert.Equal("rejected", result.Status);
        Assert.Equal("selfImprovement.ownership_mismatch", result.ErrorCode);
        Assert.Equal(rig.LiveImageId, rig.GatewayContainerImage());
        Assert.Equal(rig.OriginalEnv, File.ReadAllText(rig.EnvFile));
        Assert.Equal(["v1", "v1"], await rig.StartsAfterAsync(2));
    }
}
