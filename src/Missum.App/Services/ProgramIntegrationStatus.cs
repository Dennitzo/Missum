using System.Text.Json;

namespace Missum.App.Services;

/// <summary>
/// Connection summary of a program integration (for example a running Blender instance) as it was
/// last reported by the integration adapter. The composer shows it in the tool chip so the user can
/// see which scene is being worked on, or that the connection is still missing.
/// </summary>
public sealed record ProgramIntegrationStatus(
    string ActionId,
    string ExtensionId,
    bool Connected,
    string? SceneName,
    string? ProjectName,
    string? Detail,
    DateTimeOffset? UpdatedAt,
    string? VisionState = null,
    string? VisionDetail = null,
    int ObservedCount = 0,
    string? ObservedDetail = null)
{
    /// <summary>
    /// A program window that Missum found but does not run itself. It can be looked at, it cannot be
    /// commanded, and the interface says so instead of claiming a connection that is not there.
    /// </summary>
    public bool Observed => !Connected && ObservedCount > 0;

    /// <summary>Short text for the composer chip, e.g. „Blender · Werkstatt“ or „Blender · nicht verbunden“.</summary>
    public string ChipSuffix
    {
        get
        {
            if (!Connected) return Observed ? "im Fenster gefunden" : "nicht verbunden";
            var scene = SceneName ?? ProjectName ?? "verbunden";
            return OpenReview is { } review ? $"{scene} · {review}" : scene;
        }
    }

    /// <summary>Longer text for the tool menu note below the integration name.</summary>
    public string MenuNote
    {
        get
        {
            if (!Connected)
            {
                if (!Observed) return Detail ?? "Es ist keine Editorinstanz verbunden.";
                var found = ObservedDetail is { Length: > 0 } title ? title : "offenes Programmfenster";
                return $"Im Fenster gefunden: {found}. Gesehen, aber nicht gesteuert: Änderungen sind nur in "
                    + "einem von Missum geöffneten Arbeitsfenster möglich.";
            }
            var connection = Detail ?? $"Verbunden mit Szene „{SceneName ?? ProjectName ?? "ohne Namen"}“.";
            var review = VisionState switch
            {
                "pending" => VisionDetail ?? "Visuelle Prüfung ausstehend.",
                "unavailable" => VisionDetail ?? "Visuelle Prüfung ausstehend: Vision war nicht verfügbar.",
                "deviation" => VisionDetail ?? "Sichtprüfung meldet eine Abweichung.",
                "verified" => VisionDetail is { Length: > 0 } confirmed ? $"Sichtprüfung: {confirmed}" : null,
                _ => null,
            };
            return string.IsNullOrWhiteSpace(review) ? connection : $"{connection} {review}";
        }
    }

    /// <summary>
    /// A finished change is only shown as checked when vision actually looked at the picture. An open
    /// review stays on the chip, but never triggers another attempt by itself.
    /// </summary>
    private string? OpenReview => VisionState switch
    {
        "pending" => "Sichtprüfung ausstehend",
        "unavailable" => "Sichtprüfung ausstehend",
        "deviation" => "Sichtprüfung: Abweichung",
        _ => null,
    };
}

/// <summary>
/// Reads the small status file that integration adapters write next to their binding state.
/// Adapters run as short lived ExtensionHost entrypoints, so a file is the only place where the
/// current editor connection can survive between tool calls and be shown in the composer again.
/// Reading never throws: an absent or damaged file simply means „no integration connected“.
/// </summary>
public static class ProgramIntegrationStatusReader
{
    private static readonly Lock Gate = new();
    private static readonly List<ProgramIntegrationStatus> Empty = [];

    // Same directory the Blender adapter uses for its binding state. Kept in one place so the
    // adapter and the composer agree without sharing an assembly.
    public const string DirectoryName = "missum-program-integrations";
    public const string StatusFileName = "status.json";

    // A status older than this is treated as stale: the editor was most likely closed.
    private static readonly TimeSpan FreshWindow = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan CacheWindow = TimeSpan.FromSeconds(2);

    private static IReadOnlyList<ProgramIntegrationStatus> _cache = Empty;
    private static DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public static string StatusFilePath => Path.Combine(Path.GetTempPath(), DirectoryName, StatusFileName);

    public static IReadOnlyList<ProgramIntegrationStatus> Read()
    {
        var now = DateTimeOffset.Now;
        lock (Gate)
        {
            if (now - _cachedAt < CacheWindow) return _cache;
        }

        var parsed = ParseFile(now);
        lock (Gate)
        {
            _cache = parsed;
            _cachedAt = now;
            return parsed;
        }
    }

    public static ProgramIntegrationStatus? Find(string? actionId) => string.IsNullOrWhiteSpace(actionId)
        ? null
        : Read().FirstOrDefault(status => string.Equals(status.ActionId, actionId, StringComparison.Ordinal));

    internal static void ResetCacheForTests()
    {
        lock (Gate)
        {
            _cache = Empty;
            _cachedAt = DateTimeOffset.MinValue;
        }
    }

    private static List<ProgramIntegrationStatus> ParseFile(DateTimeOffset now)
    {
        try
        {
            var path = StatusFilePath;
            if (!File.Exists(path)) return Empty;
            var file = new FileInfo(path);
            if (file.Length is <= 0 or > 256 * 1024) return Empty;
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Array) return Empty;

            var statuses = new List<ProgramIntegrationStatus>();
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var actionId = Text(entry, "actionId");
                var extensionId = Text(entry, "extensionId");
                if (string.IsNullOrWhiteSpace(actionId) || string.IsNullOrWhiteSpace(extensionId)) continue;
                var updatedAt = Date(entry, "updatedAt");
                var connected = Flag(entry, "connected");
                var detail = Text(entry, "detail");
                var visionState = Text(entry, "visionState");
                var visionDetail = Text(entry, "visionDetail");
                var observedCount = Number(entry, "observedCount");
                var observedDetail = Text(entry, "observedDetail");
                if (updatedAt is { } stamp && now - stamp > FreshWindow)
                {
                    if (connected)
                    {
                        connected = false;
                        detail = "Die Verbindung wurde seit über drei Minuten nicht mehr bestätigt.";
                        // Without the editor there is nothing left to look at; an open review is unknown again.
                        visionState = null;
                        visionDetail = null;
                    }
                    else
                    {
                        // A window seen earlier can be closed since; repeating it as open would be a guess.
                        observedCount = 0;
                        observedDetail = null;
                    }
                }

                statuses.Add(new ProgramIntegrationStatus(
                    actionId,
                    extensionId,
                    connected,
                    Text(entry, "sceneName"),
                    Text(entry, "projectName"),
                    string.IsNullOrWhiteSpace(detail) ? null : detail,
                    updatedAt,
                    visionState,
                    visionDetail,
                    observedCount,
                    observedDetail));
            }

            return statuses.Count == 0 ? Empty : statuses;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return Empty;
        }
    }

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 and <= 220 } text
            ? text
            : null;

    private static bool Flag(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int Number(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed) && parsed > 0
            ? parsed
            : 0;

    private static DateTimeOffset? Date(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
}
