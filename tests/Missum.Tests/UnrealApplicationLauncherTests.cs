using Missum.App.Services.Extensions;

namespace Missum.Tests;

public sealed class UnrealApplicationLauncherTests
{
    [Fact]
    public void SelectsProjectEngineWithoutImplicitUpgrade()
    {
        UnrealEngineInstallation[] installations =
        [
            new("5.4", @"C:\UE_5.4", new Version(5, 4, 4)),
            new("5.6", @"C:\UE_5.6", new Version(5, 6, 0)),
        ];
        Assert.Equal("5.4", UnrealApplicationLauncher.SelectEngine(installations, "5.4", null).Association);
        Assert.Throws<InvalidOperationException>(() => UnrealApplicationLauncher.SelectEngine(installations, "5.3", null));
        Assert.Throws<InvalidOperationException>(() => UnrealApplicationLauncher.SelectEngine(installations, "5.4", "5.6"));
        Assert.Throws<InvalidOperationException>(() => UnrealApplicationLauncher.SelectEngine(installations, null, null));
    }

    [Fact]
    public void FindsOnlyAnUnambiguousProjectInsideTheWorkspace()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "missum-unreal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            Assert.Null(UnrealApplicationLauncher.FindProject(workspace, null));
            var first = Path.Combine(workspace, "One.uproject");
            File.WriteAllText(first, """{"EngineAssociation":"5.4"}""");
            Assert.Equal(first, UnrealApplicationLauncher.FindProject(workspace, null));
            Assert.Equal("5.4", UnrealApplicationLauncher.ReadAssociation(first));
            File.WriteAllText(Path.Combine(workspace, "Two.uproject"), "{}");
            Assert.Throws<InvalidOperationException>(() => UnrealApplicationLauncher.FindProject(workspace, null));
            Assert.Equal(first, UnrealApplicationLauncher.FindProject(workspace, "One.uproject"));
            Assert.Throws<UnauthorizedAccessException>(() => UnrealApplicationLauncher.FindProject(workspace, "../outside.uproject"));
        }
        finally { Directory.Delete(workspace, recursive: true); }
    }
}
