using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Missum.App.Services.Webdesign;

public sealed record WebdesignBox(double X, double Y, double Width, double Height);

public sealed record WebdesignStyleChange(string Property, string From, string To);

/// <summary>
/// One wish of the user on the website: a marked element (mark), a comment pin (pin), a drawn
/// frame (region) or a live fine-tuning (tune). The number is what the user and the AI say (#3).
/// </summary>
public sealed record WebdesignAnnotation
{
    public int Number { get; init; }
    public string Kind { get; init; } = "mark";
    public string Status { get; init; } = "open";
    public string Page { get; init; } = "/";
    public string Device { get; init; } = "laptop";
    public int ViewportWidth { get; init; }
    public string? Selector { get; init; }
    public string? Tag { get; init; }
    public string? Text { get; init; }
    public WebdesignBox? Box { get; init; }
    public string? Comment { get; init; }
    public IReadOnlyList<WebdesignStyleChange>? Styles { get; init; }
    public string? SourceHint { get; init; }
    public string? ResolutionNote { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; init; }
}

/// <summary>
/// The user's open wishes per Webdesign project, stored in <c>.missum/annotations.json</c> so they
/// survive restarts and reach every run of the project. Page content never creates entries on its
/// own: only the user's explicit marking in the tab does.
/// </summary>
public sealed class WebdesignAnnotationStore
{
    public static WebdesignAnnotationStore Shared { get; } = new();
    private const int MaximumOpen = 100;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly ConcurrentDictionary<string, List<WebdesignAnnotation>> _projects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public event Action<string>? Changed;

    public static string PathFor(string workspace) => Path.Combine(workspace, WebdesignProjectConfig.FolderName, "annotations.json");

    public IReadOnlyList<WebdesignAnnotation> List(string workspace)
    {
        lock (_lock) return [.. Load(workspace)];
    }

    public IReadOnlyList<WebdesignAnnotation> Open(string workspace) =>
        List(workspace).Where(static item => item.Status == "open").ToArray();

    public WebdesignAnnotation Add(string workspace, WebdesignAnnotation annotation)
    {
        WebdesignAnnotation added;
        lock (_lock)
        {
            var items = Load(workspace);
            if (items.Count(static item => item.Status == "open") >= MaximumOpen)
                throw new InvalidOperationException("Es sind bereits 100 offene Markierungen vorhanden. Sende oder entferne zuerst einige.");
            added = annotation with { Number = items.Count == 0 ? 1 : items.Max(static item => item.Number) + 1, Status = "open",
                CreatedAt = DateTimeOffset.UtcNow, ResolvedAt = null, ResolutionNote = null };
            items.Add(added);
            Save(workspace, items);
        }
        Notify(workspace);
        return added;
    }

    public void Update(string workspace, int number, Func<WebdesignAnnotation, WebdesignAnnotation> change)
    {
        lock (_lock)
        {
            var items = Load(workspace);
            var index = items.FindIndex(item => item.Number == number);
            if (index < 0) return;
            items[index] = change(items[index]) with { Number = number };
            Save(workspace, items);
        }
        Notify(workspace);
    }

    public void Remove(string workspace, int number)
    {
        lock (_lock)
        {
            var items = Load(workspace);
            if (items.RemoveAll(item => item.Number == number) == 0) return;
            Save(workspace, items);
        }
        Notify(workspace);
    }

    /// <summary>Removes all entries that are done, keeps open ones.</summary>
    public void ClearDone(string workspace)
    {
        lock (_lock)
        {
            var items = Load(workspace);
            if (items.RemoveAll(static item => item.Status == "done") == 0) return;
            Save(workspace, items);
        }
        Notify(workspace);
    }

    /// <summary>Marks the given numbers as done. Returns the numbers that were open and are now done.</summary>
    public IReadOnlyList<int> Resolve(string workspace, IEnumerable<int> numbers, string? note)
    {
        var wanted = numbers.ToHashSet();
        var resolved = new List<int>();
        lock (_lock)
        {
            var items = Load(workspace);
            for (var index = 0; index < items.Count; index++)
            {
                if (!wanted.Contains(items[index].Number) || items[index].Status != "open") continue;
                items[index] = items[index] with
                {
                    Status = "done", ResolvedAt = DateTimeOffset.UtcNow,
                    ResolutionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 2000)],
                };
                resolved.Add(items[index].Number);
            }
            if (resolved.Count > 0) Save(workspace, items);
        }
        if (resolved.Count > 0) Notify(workspace);
        return resolved;
    }

    /// <summary>The open wishes as compact text for the model, one numbered line each.</summary>
    public string Describe(string workspace, IReadOnlyCollection<int>? only = null)
    {
        var builder = new StringBuilder();
        foreach (var item in Open(workspace).Where(item => only is null || only.Contains(item.Number)))
            builder.AppendLine(Describe(item));
        return builder.ToString().TrimEnd();
    }

    public static string Describe(WebdesignAnnotation item)
    {
        var parts = new List<string>
        {
            $"#{item.Number} {KindLabel(item.Kind)} auf {item.Page} ({item.Device}, {item.ViewportWidth.ToString(CultureInfo.InvariantCulture)} px breit)",
        };
        if (!string.IsNullOrWhiteSpace(item.Selector)) parts.Add($"Selektor {item.Selector}");
        if (!string.IsNullOrWhiteSpace(item.Text)) parts.Add($"Text „{Trim(item.Text, 120)}“");
        if (item.Box is { } box)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"Box x{box.X:0} y{box.Y:0} {box.Width:0}×{box.Height:0}"));
        if (!string.IsNullOrWhiteSpace(item.SourceHint)) parts.Add($"vermutlich {item.SourceHint}");
        if (item.Styles is { Count: > 0 } styles)
            parts.Add("Feintuning: " + string.Join(", ", styles.Select(static change => $"{change.Property} {change.From} → {change.To}")));
        if (!string.IsNullOrWhiteSpace(item.Comment)) parts.Add($"Wunsch: {Trim(item.Comment, 600)}");
        return string.Join("; ", parts);
    }

    private static string KindLabel(string kind) => kind switch
    {
        "pin" => "Kommentar",
        "region" => "Bereich",
        "tune" => "Feintuning",
        _ => "Element",
    };

    private static string Trim(string value, int maximum)
    {
        var text = value.ReplaceLineEndings(" ").Trim();
        return text.Length <= maximum ? text : text[..maximum] + " …";
    }

    private List<WebdesignAnnotation> Load(string workspace)
    {
        var key = WebdesignServeService.Normalize(workspace);
        return _projects.GetOrAdd(key, static path =>
        {
            try
            {
                var file = PathFor(path);
                if (File.Exists(file))
                    return JsonSerializer.Deserialize<List<WebdesignAnnotation>>(File.ReadAllText(file), Json) ?? [];
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { }
            return [];
        });
    }

    private static void Save(string workspace, List<WebdesignAnnotation> items)
    {
        try
        {
            var path = PathFor(WebdesignServeService.Normalize(workspace));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(items, Json));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private void Notify(string workspace)
    {
        try { Changed?.Invoke(WebdesignServeService.Normalize(workspace)); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }
}
