using System.Text.Json;
using System.Text.Json.Serialization;

namespace Missum.App.Services.Webdesign;

/// <summary>
/// Serve settings of one Webdesign project, stored in <c>.missum/webdesign.json</c>. This is the
/// only file Missum writes into a project on its own: an empty folder is served from memory.
/// </summary>
public sealed record WebdesignProjectConfig
{
    public const string FolderName = ".missum";
    public const string FileName = "webdesign.json";

    /// <summary>Served folder relative to the project, "." for the project itself.</summary>
    public string Root { get; init; } = ".";
    public string StartPage { get; init; } = "index.html";
    /// <summary>auto (detect), static (built-in server) or command (project dev server).</summary>
    public string Kind { get; init; } = "auto";
    /// <summary>Own dev-server command; {port} and {host} are replaced.</summary>
    public string? Command { get; init; }
    public int? PreferredPort { get; init; }
    public bool Lan { get; init; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathFor(string workspace) => Path.Combine(workspace, FolderName, FileName);

    public static WebdesignProjectConfig Load(string workspace)
    {
        var path = PathFor(workspace);
        try
        {
            if (File.Exists(path))
                return Normalize(JsonSerializer.Deserialize<WebdesignProjectConfig>(File.ReadAllText(path), Json) ?? new());
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    public void Save(string workspace)
    {
        var path = PathFor(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(Normalize(this), Json));
        File.Move(temporary, path, overwrite: true);
    }

    internal static WebdesignProjectConfig Normalize(WebdesignProjectConfig value) => value with
    {
        Root = NormalizeRelative(value.Root, "."),
        StartPage = NormalizeRelative(value.StartPage, "index.html"),
        Kind = value.Kind is "static" or "command" ? value.Kind : "auto",
        Command = string.IsNullOrWhiteSpace(value.Command) ? null : value.Command.Trim(),
        PreferredPort = value.PreferredPort is > 0 and < 65536 ? value.PreferredPort : null,
    };

    /// <summary>Keeps a configured path inside the project: no rooted paths, no "..".</summary>
    internal static string NormalizeRelative(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var text = value.Trim().Replace('\\', '/').Trim('/');
        if (text.Length == 0 || text == ".") return fallback == "." ? "." : fallback;
        if (Path.IsPathRooted(text) || text.Contains(':') || text.Split('/').Any(part => part is ".." or ""))
            return fallback;
        return text;
    }
}
