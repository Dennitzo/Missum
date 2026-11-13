using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Missum.BlenderAdapter;

/// <summary>
/// The adapter process lives only for a single tool call, so everything that has to survive
/// between calls is kept in one small JSON file: which Blender instance and which scene the run is
/// bound to, and which mutations were already sent. The same directory also holds the status file
/// the composer reads, and the capture images that vision looks at.
/// </summary>
internal static class IntegrationStateStore
{
    /// <summary>Shared with the composer: <c>%TEMP%\missum-program-integrations</c>.</summary>
    public const string DirectoryName = "missum-program-integrations";

    private const string BindingFileName = "blender.json";
    private const string StatusFileName = "status.json";
    private const string CaptureFolderName = "captures";
    private const int RememberedOperations = 40;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string RootDirectory => Path.Combine(Path.GetTempPath(), DirectoryName);

    internal static string WorkspaceRoot => Path.TrimEndingDirectorySeparator(Path.GetFullPath(
        Environment.GetEnvironmentVariable("ASSISTANT_EXTENSION_WORKSPACE_ROOT")
        ?? throw new BlenderToolException("blender.workspace_missing", "Blender benötigt einen geöffneten Projektordner.")));

    private static string BindingFilePath
    {
        get
        {
            var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(WorkspaceRoot.ToUpperInvariant())));
            return Path.Combine(RootDirectory, "blender-workspaces", key, BindingFileName);
        }
    }

    private static string StatusFilePath => Path.Combine(RootDirectory, StatusFileName);

    public static JsonObject Load()
    {
        try
        {
            if (!File.Exists(BindingFilePath)) return new JsonObject();
            var text = File.ReadAllText(BindingFilePath);
            if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
            return JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new JsonObject();
        }
    }

    public static void Save(JsonObject state)
    {
        state["updatedAt"] = DateTimeOffset.Now.ToString("O");
        WriteAtomically(BindingFilePath, state.ToJsonString(Json));
    }

    public static JsonObject? Binding(JsonObject state) => state["binding"] as JsonObject;

    public static void SetBinding(JsonObject state, BlenderInstance instance)
    {
        if (!BindingMatches(state, instance))
        {
            state["operations"] = null;
            state["visionPending"] = null;
            state["visionReviews"] = null;
        }
        ClearRequiresRebind(state);
        state["binding"] = new JsonObject
        {
            ["instanceId"] = instance.InstanceId,
            ["sessionStamp"] = instance.SessionStamp,
            ["port"] = instance.Port,
            ["projectPath"] = instance.BlendPath,
            ["sceneName"] = instance.SceneName,
            ["boundAt"] = DateTimeOffset.Now.ToString("O"),
        };
    }

    public static bool BindingMatches(JsonObject state, BlenderInstance instance) =>
        Binding(state) is { } binding
        && string.Equals(Field(binding, "instanceId"), instance.InstanceId, StringComparison.Ordinal)
        && string.Equals(Field(binding, "sessionStamp"), instance.SessionStamp, StringComparison.Ordinal)
        && string.Equals(Field(binding, "sceneName"), instance.SceneName, StringComparison.Ordinal)
        && string.Equals(Field(binding, "projectPath"), instance.BlendPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Refreshes the bound scene after a save moved or gave the project its file path.</summary>
    public static void UpdateBindingScene(JsonObject state, string projectPath, string sceneName)
    {
        if (Binding(state) is not { } binding) return;
        binding["projectPath"] = projectPath;
        binding["sceneName"] = sceneName;
    }

    public static void ClearBinding(JsonObject state) => state["binding"] = null;

    /// <summary>
    /// A binding that was lost (instance gone, file reloaded or scene switched) must not be replaced
    /// implicitly: an edit could then land in another scene. Only a status call that names the target
    /// instance explicitly lifts this requirement again.
    /// </summary>
    public static void SetRequiresRebind(JsonObject state, string reason)
    {
        state["requiresRebind"] = true;
        state["rebindReason"] = reason;
        ClearBinding(state);
    }

    public static bool RequiresRebind(JsonObject state) => Wire.ReadBool(state, "requiresRebind", false);

    public static string RebindReason(JsonObject state) => Wire.ReadString(state, "rebindReason");

    public static void ClearRequiresRebind(JsonObject state)
    {
        state["requiresRebind"] = false;
        state["rebindReason"] = null;
    }

    public static JsonObject? OperationEntry(JsonObject state, string operationId) =>
        state["operations"] is JsonObject operations
            ? operations[operationId] as JsonObject
            : null;

    public static void RememberOperation(JsonObject state, string operationId, string outcome, string summary)
    {
        if (string.IsNullOrWhiteSpace(operationId)) return;
        if (state["operations"] is not JsonObject operations)
        {
            operations = new JsonObject();
            state["operations"] = operations;
        }

        operations[operationId] = new JsonObject
        {
            ["outcome"] = outcome,
            ["summary"] = summary,
            ["at"] = DateTimeOffset.Now.ToString("O"),
        };

        // Keep the ledger small: drop the oldest entries when it grows over the limit.
        while (operations.Count > RememberedOperations)
        {
            string? oldest = null;
            foreach (var pair in operations)
            {
                oldest = pair.Key;
                break;
            }

            if (oldest is null) break;
            operations.Remove(oldest);
        }
    }

    /// <summary>
    /// A picture is not yet a reviewed result. A capture therefore stays open until the run reports
    /// back what vision actually looked at, which is what lets the composer say
    /// „Visuelle Prüfung ausstehend“ instead of pretending the change was checked.
    /// </summary>
    public static void MarkVisionPending(JsonObject state, string? operationId, string capturePath, string sha256,
        string source = "scene")
    {
        state["visionPending"] = new JsonObject
        {
            ["operationId"] = operationId,
            ["capturePath"] = capturePath,
            ["sha256"] = sha256,
            ["source"] = source,
            ["since"] = DateTimeOffset.Now.ToString("O"),
        };
    }

    public static JsonObject? VisionPending(JsonObject state) => state["visionPending"] as JsonObject;

    /// <summary>
    /// <c>scene</c> for a picture taken inside a controlled Blender, <c>window</c> for a picture of a
    /// window that was only found. A window picture has no scene binding, so its verdict is closed by
    /// the picture alone instead of being refused for a missing connection.
    /// </summary>
    public static string CaptureSource(JsonObject state) =>
        VisionPending(state) is { } pending ? Wire.ReadString(pending, "source", "scene") : "scene";

    /// <summary>
    /// Closes an open capture with the verdict the run got out of <c>media.analyze</c>. When the run
    /// also names the hash of the image it analysed, the recorded capture decides whether that verdict
    /// really belongs to this picture; a mismatch keeps the review open instead of accepting it.
    /// </summary>
    public static JsonObject CompleteVisionReview(JsonObject state, string? operationId, string? analyzedSha256,
        string verdict, string summary)
    {
        var pending = VisionPending(state);
        var expected = Field(pending, "sha256");
        var expectedOperation = Field(pending, "operationId");
        var reportedOperation = (operationId ?? string.Empty).Trim();
        var reported = (analyzedSha256 ?? string.Empty).Trim().ToLowerInvariant();
        var matchesOperation = string.Equals(expectedOperation, reportedOperation, StringComparison.Ordinal);
        var matchesImage = expected.Length > 0 && string.Equals(expected, reported, StringComparison.Ordinal);
        var belongsToCapture = pending is not null && matchesOperation
            && (matchesImage || verdict == "unavailable" && reported.Length == 0);

        var record = new JsonObject
        {
            ["operationId"] = reportedOperation,
            ["capturePath"] = Field(pending, "capturePath"),
            ["source"] = Field(pending, "source"),
            ["sha256"] = expected,
            ["analyzedSha256"] = reported,
            ["matchesCapture"] = belongsToCapture,
            ["verdict"] = verdict,
            ["summary"] = summary,
            ["at"] = DateTimeOffset.Now.ToString("O"),
        };

        // Keep the existing verdict and the open review when a response lacks the matching identity.
        if (!belongsToCapture) return record;
        state["visionPending"] = null;

        if (state["visionReviews"] is not JsonArray reviews)
        {
            reviews = new JsonArray();
            state["visionReviews"] = reviews;
        }

        reviews.Add(record.DeepClone());
        while (reviews.Count > 12) reviews.RemoveAt(0);
        return record;
    }

    /// <summary>
    /// Vision state for the composer: <c>pending</c> while a capture was never looked at,
    /// <c>deviation</c> when vision found a mismatch, otherwise the last verdict or nothing at all.
    /// </summary>
    public static (string State, string Detail) VisionSummary(JsonObject state)
    {
        if (VisionPending(state) is { } pending)
        {
            var path = Field(pending, "capturePath");
            return ("pending", path.Length == 0
                ? "Visuelle Prüfung ausstehend."
                : $"Visuelle Prüfung ausstehend: {Path.GetFileName(path)}");
        }

        if (state["visionReviews"] is JsonArray reviews && reviews.Count > 0 && reviews[^1] is JsonObject last)
        {
            var verdict = Field(last, "verdict");
            var summary = Field(last, "summary");
            return verdict switch
            {
                "verified" => ("verified", summary.Length == 0 ? "Sichtprüfung abgeschlossen." : summary),
                "deviation" => ("deviation", summary.Length == 0 ? "Sichtprüfung meldet eine Abweichung." : summary),
                "unavailable" => ("unavailable", "Visuelle Prüfung ausstehend: Vision war nicht verfügbar."),
                _ => (verdict, summary),
            };
        }

        return (string.Empty, string.Empty);
    }

    private static string Field(JsonObject? node, string name) =>
        node is null ? string.Empty : Wire.ReadString(node, name);

    /// <summary>
    /// Writes the connection summary the composer chip shows. Failures are ignored: the chip is a
    /// convenience, and a missing file simply reads as "no integration connected".
    /// </summary>
    public static void WriteStatus(string actionId, string extensionId, JsonObject? integrationState,
        bool connected, string? sceneName, string? projectName, string? detail,
        int observedCount = 0, string? observedDetail = null)
    {
        var entry = new JsonObject
        {
            ["actionId"] = actionId,
            ["extensionId"] = extensionId,
            ["connected"] = connected,
            ["sceneName"] = sceneName,
            ["projectName"] = projectName,
            ["detail"] = detail,
            ["observedCount"] = observedCount,
            ["observedDetail"] = observedDetail,
            ["updatedAt"] = DateTimeOffset.Now.ToString("O"),
        };

        if (integrationState is not null)
        {
            var (visionState, visionDetail) = VisionSummary(integrationState);
            if (visionState.Length > 0)
            {
                entry["visionState"] = visionState;
                entry["visionDetail"] = visionDetail;
            }
        }

        var statuses = new JsonArray { entry };
        try
        {
            // Keep other integrations in the same file so Revit or Unreal can add their own entry.
            if (File.Exists(StatusFilePath))
            {
                var text = File.ReadAllText(StatusFilePath);
                if (!string.IsNullOrWhiteSpace(text)
                    && JsonNode.Parse(text) is JsonArray existing)
                {
                    foreach (var item in existing)
                    {
                        if (item is JsonObject other
                            && !string.Equals(Wire.ReadString(other, "extensionId"), extensionId, StringComparison.Ordinal))
                            statuses.Add(other.DeepClone());
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // Rebuild the file from scratch when the previous content cannot be read.
        }

        WriteAtomically(StatusFilePath, statuses.ToJsonString(Json));
    }

    /// <summary>Artifact path for one preview image; previews are tool output, never project files.</summary>
    public static string NewCapturePath(string kind)
    {
        var directory = Path.Combine(RootDirectory, CaptureFolderName);
        Directory.CreateDirectory(directory);
        var name = string.Concat(kind.Where(static character => char.IsAsciiLetterOrDigit(character) || character is '-'))
            .ToLowerInvariant();
        if (name.Length == 0) name = "ansicht";
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        return Path.Combine(directory, string.Concat("blender-", name, "-", stamp, "-", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8], ".png"));
    }

    public static string Sha256Of(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void WriteAtomically(string target, string content)
    {
        // The binding lives in a per-workspace folder, so the folder of the target is created and not
        // only the root: otherwise every save fails quietly and an open visual review disappears.
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? RootDirectory);
        var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                File.WriteAllText(temporary, content);
                File.Move(temporary, target, overwrite: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                TryDelete(temporary);
                Thread.Sleep(40 * (attempt + 1));
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is harmless.
        }
    }
}
