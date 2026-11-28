using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace Missum.App.Services;

/// <summary>Where a UI element was found; popups (menus, dropdowns) are listed before the window behind them.</summary>
internal enum ComputerUiSource { Popup = 0, Window = 1, Taskbar = 2 }

/// <summary>A UI Automation element found during an observation (physical screen coordinates).</summary>
internal sealed record ComputerUiCandidate(string Type, string Name, string? Value, bool Enabled,
    int X, int Y, int Width, int Height, ComputerUiSource Source, object? Element = null);

/// <summary>A numbered element of an observation: what the model sees and how Missum reaches it again.</summary>
internal sealed record ComputerUiTarget(int Index, ComputerUiCandidate Candidate, int ImageX, int ImageY, int ImageWidth, int ImageHeight)
{
    public int CenterX => Candidate.X + Candidate.Width / 2;
    public int CenterY => Candidate.Y + Candidate.Height / 2;
}

/// <summary>
/// UI Automation (UIA3 via FlaUI) for Computer-Use: collects actionable controls of the observed
/// windows in one cached request, and uses patterns (Value, Focus) for robust input. Calls run on
/// thread-pool (MTA) threads so the WinUI thread is never blocked by another process.
/// </summary>
internal static class ComputerUiAutomation
{
    public const int MaximumTargets = 160;

    /// <summary>The last UI Automation failure (diagnostics; observations continue without marks).</summary>
    internal static Exception? LastError { get; private set; }
    private static readonly Lazy<UIA3Automation> Automation = new(() => new UIA3Automation(), LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly ControlType[] InteractiveTypes =
    [
        ControlType.Button, ControlType.CheckBox, ControlType.ComboBox, ControlType.Edit, ControlType.Hyperlink,
        ControlType.ListItem, ControlType.MenuItem, ControlType.RadioButton, ControlType.Slider, ControlType.Spinner,
        ControlType.SplitButton, ControlType.TabItem, ControlType.TreeItem, ControlType.DataItem, ControlType.Document,
        ControlType.HeaderItem,
    ];

    /// <summary>Collects candidates from the given windows within a time budget; returns what was found in time.</summary>
    public static async Task<IReadOnlyList<ComputerUiCandidate>> CollectAsync(
        IReadOnlyList<(nint Handle, ComputerUiSource Source)> windows, TimeSpan budget, CancellationToken cancellationToken)
    {
        var results = new List<ComputerUiCandidate>();
        var deadline = DateTimeOffset.UtcNow + budget;
        foreach (var (handle, source) in windows)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero || handle == 0) break;
            var collect = Task.Run(() => CollectWindow(handle, source), cancellationToken);
            try { results.AddRange(await collect.WaitAsync(remaining, cancellationToken).ConfigureAwait(false)); }
            catch (TimeoutException exception) { LastError = exception; break; } // A slow provider (huge trees) must not stall the observation.
            catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException
                or ArgumentException or UnauthorizedAccessException or NotSupportedException or InvalidCastException)
            { LastError = exception; /* Window closed or provider unavailable. */ }
        }
        return results;
    }

    private static List<ComputerUiCandidate> CollectWindow(nint handle, ComputerUiSource source)
    {
        var automation = Automation.Value;
        var library = automation.PropertyLibrary;
        var factory = automation.ConditionFactory;
        var conditions = InteractiveTypes.Select(type => (ConditionBase)factory.ByControlType(type)).ToList();
        conditions.Add(new PropertyCondition(library.Element.IsKeyboardFocusable, true));
        var condition = new AndCondition(new OrCondition(conditions), new PropertyCondition(library.Element.IsOffscreen, false));
        var cache = new CacheRequest { TreeScope = TreeScope.Element };
        cache.Add(library.Element.Name);
        cache.Add(library.Element.ControlType);
        cache.Add(library.Element.BoundingRectangle);
        cache.Add(library.Element.IsEnabled);
        cache.Add(library.Element.IsOffscreen);
        cache.Add(library.Element.IsPassword);
        var found = new List<(ComputerUiCandidate Candidate, AutomationElement Element, bool Password)>();
        AutomationElement[] elements;
        var activation = cache.Activate();
        try
        {
            var root = automation.FromHandle(handle);
            try { elements = root.FindAllDescendants(condition); }
            catch (System.Runtime.InteropServices.COMException exception)
            {
                // Some providers reject cached queries (E_FAIL); a plain query still works.
                LastError = exception;
                activation.Dispose();
                activation = null;
                elements = automation.FromHandle(handle).FindAllDescendants(condition);
            }
            foreach (var element in elements)
            {
                var rectangle = element.Properties.BoundingRectangle.ValueOrDefault;
                if (rectangle.Width < 4 || rectangle.Height < 4) continue;
                var type = element.Properties.ControlType.ValueOrDefault.ToString();
                var name = Clean(element.Properties.Name.ValueOrDefault);
                found.Add((new(type, name, null, element.Properties.IsEnabled.ValueOrDefault,
                    rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height, source, element), element,
                    element.Properties.IsPassword.ValueOrDefault));
                if (found.Count >= 600) break;
            }
        }
        finally { activation?.Dispose(); }
        // Current values only for fields (one extra call each); a provider error never drops the element.
        return found.Select(item =>
        {
            if (item.Password || item.Candidate.Type is not ("Edit" or "ComboBox" or "Spinner" or "Slider")) return item.Candidate;
            try
            {
                var value = Clean(item.Element.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault);
                return value.Length == 0 ? item.Candidate : item.Candidate with { Value = value };
            }
            catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException) { return item.Candidate; }
        }).ToList();
    }

    /// <summary>
    /// Keeps visible, distinct, actionable candidates inside the image region, orders them popup
    /// first and then by reading order, numbers them and maps them into image pixels.
    /// </summary>
    internal static IReadOnlyList<ComputerUiTarget> SelectTargets(IEnumerable<ComputerUiCandidate> candidates,
        ComputerImageRegion region, int imageWidth, int imageHeight, Func<int, int, bool>? excluded = null, int maximum = MaximumTargets)
    {
        var seen = new HashSet<(int, int, int, int)>();
        var ordered = candidates
            .Where(candidate => candidate.Width >= 4 && candidate.Height >= 4)
            .Where(candidate => !(candidate.Type is "Pane" or "Window" or "Group" or "Custom" or "Text" or "Image" or "TitleBar")
                || candidate.Name.Length > 0)
            .Select(candidate => Clip(candidate, region))
            .Where(candidate => candidate is not null).Select(candidate => candidate!)
            .Where(candidate => excluded is null || !excluded(candidate.X + candidate.Width / 2, candidate.Y + candidate.Height / 2))
            .OrderBy(candidate => candidate.Source)
            .ThenBy(candidate => candidate.Y / 8)
            .ThenBy(candidate => candidate.X)
            .Where(candidate => seen.Add((candidate.X / 3, candidate.Y / 3, candidate.Width / 3, candidate.Height / 3)))
            .Take(maximum)
            .ToList();
        var targets = new List<ComputerUiTarget>(ordered.Count);
        for (var index = 0; index < ordered.Count; index++)
        {
            var candidate = ordered[index];
            var x = (int)Math.Floor((double)(candidate.X - region.X) * imageWidth / region.Width);
            var y = (int)Math.Floor((double)(candidate.Y - region.Y) * imageHeight / region.Height);
            var width = Math.Max(2, (int)Math.Round((double)candidate.Width * imageWidth / region.Width));
            var height = Math.Max(2, (int)Math.Round((double)candidate.Height * imageHeight / region.Height));
            targets.Add(new(index + 1, candidate, x, y, width, height));
        }
        return targets;
    }

    private static ComputerUiCandidate? Clip(ComputerUiCandidate candidate, ComputerImageRegion region)
    {
        var left = Math.Max(candidate.X, region.X);
        var top = Math.Max(candidate.Y, region.Y);
        var right = Math.Min(candidate.X + candidate.Width, region.X + region.Width);
        var bottom = Math.Min(candidate.Y + candidate.Height, region.Y + region.Height);
        return right - left < 4 || bottom - top < 4 ? null
            : candidate with { X = left, Y = top, Width = right - left, Height = bottom - top };
    }

    /// <summary>Sets an edit's value through the UIA Value pattern; false when the control has none.</summary>
    public static Task<bool> TrySetValueAsync(object? element, string text) => Task.Run(() =>
    {
        if (element is not AutomationElement automationElement) return false;
        var pattern = automationElement.Patterns.Value.PatternOrDefault;
        if (pattern is null || pattern.IsReadOnly.ValueOrDefault) return false;
        try { automationElement.Focus(); } catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
        pattern.SetValue(text);
        return true;
    });

    /// <summary>Gives an element keyboard focus; false if the provider refuses.</summary>
    public static Task<bool> TryFocusAsync(object? element) => Task.Run(() =>
    {
        if (element is not AutomationElement automationElement) return false;
        try { automationElement.Focus(); return true; }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException) { return false; }
    });

    /// <summary>The element that currently has keyboard focus, for action feedback.</summary>
    public static Task<string?> DescribeFocusAsync() => Task.Run(() =>
    {
        try
        {
            var focused = Automation.Value.FocusedElement();
            if (focused is null) return null;
            var name = Clean(focused.Properties.Name.ValueOrDefault);
            return $"{focused.Properties.ControlType.ValueOrDefault}{(name.Length > 0 ? " \"" + name + "\"" : "")}";
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException) { return null; }
    });

    private static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var single = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return single.Length <= 80 ? single : single[..80] + "…";
    }
}
