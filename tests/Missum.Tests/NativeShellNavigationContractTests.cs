namespace Missum.Tests;

public sealed class NativeShellNavigationContractTests
{
    [Fact]
    public void SettingsAndFileMenuExposeTheAssistantRoute()
    {
        var root = FindRepositoryRoot();
        var settingsXaml = File.ReadAllText(Path.Combine(
            root, "src", "Missum.App", "Pages", "SettingsPage.xaml"));
        var settingsCode = File.ReadAllText(Path.Combine(
            root, "src", "Missum.App", "Pages", "SettingsPage.xaml.cs"));
        var mainXaml = File.ReadAllText(Path.Combine(
            root, "src", "Missum.App", "MainWindow.xaml"));
        var mainCode = File.ReadAllText(Path.Combine(
            root, "src", "Missum.App", "MainWindow.xaml.cs"));

        Assert.Contains("NavigationBackButtonNormalStyle", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnBackToAssistant\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("mainWindow.OpenAssistantSessionAsync(refreshSession: false)", settingsCode, StringComparison.Ordinal);
        Assert.Contains("Text=\"AI Assistent\" Click=\"OnMenuAssistant\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("await OpenAssistantSessionAsync(refreshSession: false)", mainCode, StringComparison.Ordinal);
        Assert.Contains("await NavigateToAsync(\"assistant\")", mainCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsDoNotExposeAnAiConnectionDisableSwitch()
    {
        var root = FindRepositoryRoot();
        var settingsXaml = File.ReadAllText(Path.Combine(
            root, "src", "Missum.App", "Pages", "SettingsPage.xaml"));
        var settingsCode = File.ReadAllText(Path.Combine(
            root, "src", "Missum.App", "Pages", "SettingsPage.xaml.cs"));
        var settingsViewModel = File.ReadAllText(Path.Combine(
            root, "src", "Missum.App", "ViewModels", "SettingsViewModel.cs"));

        Assert.DoesNotContain("AI-Verbindungen aktivieren", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsAiConnectionEnabled", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OnAiConnectionModeToggled", settingsCode, StringComparison.Ordinal);
        Assert.DoesNotContain("SetAiConnectionEnabledAsync", settingsViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("IsAiConnectionEnabled", settingsViewModel, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Missum.sln"))
                || File.Exists(Path.Combine(directory.FullName, "Missum.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Missum repository root was not found.");
    }
}
