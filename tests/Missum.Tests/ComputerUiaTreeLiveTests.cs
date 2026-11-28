using FlaUI.UIA3;

namespace Missum.Tests;

/// <summary>
/// Diagnostic breadth-first dump of a window's UI Automation tree within a time budget
/// (MISSUM_COMPUTER_UIA_DUMP=&lt;process name&gt;|&lt;output file&gt;).
/// </summary>
public sealed class ComputerUiaTreeLiveTests
{
    [Fact]
    [Trait("Category", "Live")]
    public void DumpAutomationTreeBreadthFirst()
    {
        var setting = Environment.GetEnvironmentVariable("MISSUM_COMPUTER_UIA_DUMP");
        if (string.IsNullOrWhiteSpace(setting) || setting.Split('|') is not [var processName, var output]) return;
        var process = System.Diagnostics.Process.GetProcessesByName(processName).First(item => item.MainWindowHandle != 0);
        using var automation = new UIA3Automation();
        var root = automation.FromHandle(process.MainWindowHandle);
        var walker = automation.TreeWalkerFactory.GetRawViewWalker();
        _ = walker.GetFirstChild(root); // Activates Slate's accessibility; it builds the tree over the next frames.
        Thread.Sleep(4000);
        var lines = new List<string>();
        var queue = new Queue<(FlaUI.Core.AutomationElements.AutomationElement Element, int Depth)>();
        queue.Enqueue((root, 0));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var visited = 0;
        while (queue.Count > 0 && watch.Elapsed < TimeSpan.FromSeconds(8))
        {
            var (element, depth) = queue.Dequeue();
            visited++;
            lines.Add($"{watch.ElapsedMilliseconds,6} d{depth} {element.Properties.ControlType.ValueOrDefault,-12} '{element.Properties.Name.ValueOrDefault}' {element.Properties.BoundingRectangle.ValueOrDefault}");
            if (depth >= 12) continue;
            for (var child = walker.GetFirstChild(element); child is not null; child = walker.GetNextSibling(child))
                queue.Enqueue((child, depth + 1));
        }
        lines.Insert(0, $"visited {visited} in {watch.ElapsedMilliseconds} ms, queued {queue.Count}");
        // Hit-test a few points of the window (menu bar, toolbar, center).
        var bounds = root.Properties.BoundingRectangle.ValueOrDefault;
        foreach (var (dx, dy) in new[] { (30, 40), (80, 40), (200, 80), (bounds.Width / 2, bounds.Height / 2) })
        {
            var hit = automation.FromPoint(new System.Drawing.Point(bounds.X + dx, bounds.Y + dy));
            lines.Add($"hit {dx},{dy}: {hit?.Properties.ControlType.ValueOrDefault} '{hit?.Properties.Name.ValueOrDefault}' {hit?.Properties.FrameworkId.ValueOrDefault}");
        }
        File.WriteAllLines(output, lines);
    }
}
