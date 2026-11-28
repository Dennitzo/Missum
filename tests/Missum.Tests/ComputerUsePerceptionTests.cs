using Missum.App.Services;
using Missum.App.Services.Extensions;
using Missum.Core.Extensions;

namespace Missum.Tests;

public sealed class ComputerUsePerceptionTests
{
    [Theory]
    [InlineData("ctrl+s", new ushort[] { 0x11, 0x53 })]
    [InlineData("alt+f4", new ushort[] { 0x12, 0x73 })]
    [InlineData("win+r", new ushort[] { 0x5B, 0x52 })]
    [InlineData("ctrl+shift+esc", new ushort[] { 0x11, 0x10, 0x1B })]
    [InlineData("F24", new ushort[] { 0x87 })]
    [InlineData("Strg+Entf", new ushort[] { 0x11, 0x2E })]
    [InlineData("ctrl++", new ushort[] { 0x11, 0xBB })]
    public void KeyCombinationsCoverFunctionWindowsAndNavigationKeys(string combination, ushort[] expected) =>
        Assert.Equal(expected, ComputerKeys.Parse(combination, _ => -1).Select(key => key.VirtualKey));

    [Fact]
    public void NavigationKeysCarryTheExtendedFlagAndPunctuationUsesTheLayout()
    {
        Assert.True(ComputerKeys.Parse("left", _ => -1)[0].Extended);
        Assert.True(ComputerKeys.Parse("win", _ => -1)[0].Extended);
        Assert.False(ComputerKeys.Parse("a", _ => -1)[0].Extended);
        // Layout scan: '?' on a German layout is Shift + 0xDB.
        var question = ComputerKeys.Parse("?", character => character == '?' ? (short)0x1DB : (short)-1)[0];
        Assert.Equal(0xDB, question.VirtualKey);
        Assert.True(question.Shift);
        Assert.Throws<ArgumentException>(() => ComputerKeys.Parse("hyperdrive", _ => -1));
        Assert.Throws<ArgumentException>(() => ComputerKeys.Parse("a+b+c+d+e+f", _ => -1));
    }

    [Fact]
    public void DownscalingAveragesPixelsSoTextStaysReadable()
    {
        // A 2x2 checkerboard of black and white becomes one mid-grey pixel instead of a random sample.
        byte[] source = [0, 0, 0, 255, 255, 255, 255, 255, 255, 255, 255, 255, 0, 0, 0, 255];
        var scaled = ComputerMarks.Resample(source, 2, 2, 1, 1);
        Assert.Equal([127, 127, 127, 255], scaled);
        Assert.Equal(source, ComputerMarks.Resample(source, 2, 2, 2, 2));
    }

    [Fact]
    public void MarksDrawAnOutlineAndANumberLabel()
    {
        var image = new byte[100 * 60 * 4];
        ComputerMarks.Draw(image, 100, 60, [new ComputerMark(12, 30, 30, 40, 20)]);
        // Outline pixel on the left edge of the box.
        Assert.Equal(255, image[(40 * 100 + 30) * 4 + 3]);
        Assert.NotEqual(0, image[(40 * 100 + 30) * 4] + image[(40 * 100 + 30) * 4 + 1] + image[(40 * 100 + 30) * 4 + 2]);
        // The label sits above the box (rows 12..29) and is filled.
        Assert.Equal(255, image[(14 * 100 + 31) * 4 + 3]);
        // Nothing outside box and label is touched.
        Assert.Equal(0, image[(55 * 100 + 5) * 4 + 3]);
    }

    [Fact]
    public void GridMarksTenthsWithNormalizedLabels()
    {
        var image = new byte[200 * 100 * 4];
        ComputerMarks.DrawGrid(image, 200, 100);
        // Dotted vertical line at x = 20 (100 normalized) and horizontal line at y = 50 (500 normalized).
        Assert.Equal(255, image[(30 * 200 + 20) * 4 + 3]);
        Assert.Equal(255, image[(50 * 200 + 99) * 4 + 3]);
        // Between grid lines and labels the image stays untouched.
        Assert.Equal(0, image[(75 * 200 + 110) * 4 + 3]);
    }

    [Fact]
    public void TargetsArePopupFirstReadingOrderedDistinctAndMappedIntoTheImage()
    {
        var region = new ComputerImageRegion(0, 0, 2000, 1000);
        ComputerUiCandidate Candidate(string type, string name, int x, int y, ComputerUiSource source = ComputerUiSource.Window) =>
            new(type, name, null, true, x, y, 100, 40, source);
        var targets = ComputerUiAutomation.SelectTargets(
        [
            Candidate("Button", "Speichern", 800, 100),
            Candidate("Button", "Datei", 10, 10),
            Candidate("MenuItem", "Öffnen", 20, 60, ComputerUiSource.Popup),
            Candidate("Button", "Start", 0, 960, ComputerUiSource.Taskbar),
            Candidate("Button", "Datei", 10, 10), // duplicate
            Candidate("Pane", "", 500, 500), // unnamed container
            new("Button", "Winzig", null, true, 50, 50, 2, 2, ComputerUiSource.Window),
            Candidate("Button", "Außerhalb", 2100, 50),
        ], region, 1000, 500);
        Assert.Equal(["Öffnen", "Datei", "Speichern", "Start"], targets.Select(target => target.Candidate.Name));
        Assert.Equal([1, 2, 3, 4], targets.Select(target => target.Index));
        var save = targets[2];
        Assert.Equal((400, 50, 50, 20), (save.ImageX, save.ImageY, save.ImageWidth, save.ImageHeight));
        Assert.Equal((850, 120), (save.CenterX, save.CenterY));
    }

    [Fact]
    public void TargetsCoveringMissumAreLeftOut()
    {
        var targets = ComputerUiAutomation.SelectTargets(
            [new("Button", "Missum", null, true, 0, 0, 100, 40, ComputerUiSource.Window), new("Button", "Rechner", null, true, 300, 0, 100, 40, ComputerUiSource.Window)],
            new ComputerImageRegion(0, 0, 1000, 500), 1000, 500, excluded: (x, _) => x < 200);
        Assert.Equal("Rechner", Assert.Single(targets).Candidate.Name);
    }

    [Theory]
    [InlineData("UnrealEditor", "MissumDivided - Unreal Editor", "unreal-editor")]
    [InlineData("UnrealEditor-Cmd", "", "unreal-editor")]
    [InlineData("EpicGamesLauncher", "Epic Games Launcher", "epic-launcher")]
    [InlineData("explorer", "Downloads", "file-explorer")]
    [InlineData("chrome", "Neuer Tab", "browser")]
    [InlineData("CalculatorApp", "Rechner", "calculator")]
    [InlineData("WindowsTerminal", "PowerShell", "terminal")]
    [InlineData("someapp", "Irgendwas", "application")]
    public void ForegroundProgramsAreRecognized(string process, string title, string kind) =>
        Assert.Equal(kind, ComputerAppRecognizer.Recognize(process, title).Kind);

    [Fact]
    public void UnrealEditorGetsOperatingHints()
    {
        var app = ComputerAppRecognizer.Recognize("UnrealEditor", "MissumDivided - Unreal Editor");
        Assert.Contains("Content Browser", app.Hints);
        Assert.Contains("Alt+P", app.Hints);
    }

    [Fact]
    public void LaunchResolvesAliasesUnrealAndProjects()
    {
        var engines = new[] { new UnrealEngineInstallation("5.4", @"D:\UE_5.4", new Version(5, 4)), new UnrealEngineInstallation("5.8", @"E:\UnrealEngine\UE_5.8", new Version(5, 8)) };
        var unreal = ComputerAppRecognizer.ResolveLaunch("unreal", null, _ => true, () => engines);
        Assert.Equal(@"E:\UnrealEngine\UE_5.8\Engine\Binaries\Win64\UnrealEditor.exe", unreal.FileName);
        Assert.False(unreal.UseShellExecute);
        var project = ComputerAppRecognizer.ResolveLaunch(@"C:\Projekte\Mein Spiel\MeinSpiel.uproject", null, _ => true, () => engines);
        Assert.Equal("\"C:\\Projekte\\Mein Spiel\\MeinSpiel.uproject\" " + ComputerAppRecognizer.UnrealAccessibilityArgument, project.Arguments);
        Assert.Equal(ComputerAppRecognizer.UnrealAccessibilityArgument, unreal.Arguments);
        Assert.Equal("calc.exe", ComputerAppRecognizer.ResolveLaunch("Rechner", null).FileName);
        Assert.Equal("notepad.exe", ComputerAppRecognizer.ResolveLaunch("editor", null).FileName);
        var url = ComputerAppRecognizer.ResolveLaunch("https://example.com", null);
        Assert.True(url.UseShellExecute);
        Assert.Equal("https://example.com", url.FileName);
        Assert.Throws<InvalidOperationException>(() => ComputerAppRecognizer.ResolveLaunch("unreal", null, _ => true, () => []));
    }

    [Fact]
    public void ComputerToolsFitTheGatewayContract()
    {
        var tools = BuiltInExtensionCatalog.Tools.Where(tool => tool.ActionId == BuiltInActionIds.ComputerUse).ToArray();
        Assert.Equal(2, tools.Length);
        foreach (var tool in tools)
        {
            Assert.InRange(tool.Description.Length, 1, 300);
            Assert.Equal(tool.Description.Trim(), tool.Description);
            Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
            Assert.InRange(tool.InputSchema.GetProperty("properties").EnumerateObject().Count(), 1, 64);
        }
        var act = tools.Single(tool => tool.ModelToolName == "computer.act");
        var properties = act.InputSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("elementIndex", out _));
        Assert.True(properties.TryGetProperty("target", out _));
        Assert.Equal(ComputerDesktopBroker.Kinds.Order(), properties.GetProperty("kind").GetProperty("enum").EnumerateArray().Select(item => item.GetString()!).Order());
    }

    [Fact]
    public void PixelFontHasCompleteDigits()
    {
        var field = typeof(ComputerMarks).GetField("Digits", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var digits = (string[])field.GetValue(null)!;
        Assert.Equal(10, digits.Length);
        Assert.All(digits, glyph => Assert.Equal(35, glyph.Length));
    }
}
