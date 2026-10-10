using System.Security.Cryptography;
using System.Text.Json;
using Missum.Core.Coding;
using Missum.Core.Models;

namespace Missum.App.Services;

public sealed partial class AssistantArtifactOriginalResolver
{
    // A file left in a project is not automatically the original. Recover it
    // only when the exact image.input upload and an earlier successful execution
    // receipt bind its workspace path and SHA. Changed files never replace it.
    private async Task<ChatArtifact?> RecoverVerifiedWorkspaceImageAsync(ChatArtifact thumbnail, ChatMessage message,
        string uploadId, CancellationToken token)
    {
        var steps = message.ToolSteps ?? [];
        var inputIndex = -1;
        for (var index = 0; index < steps.Count; index++)
            if (steps[index].Tool == "image.input" && JsonString(steps[index].OutputJson, "uploadId", true) == uploadId)
                inputIndex = index;
        if (inputIndex < 0) return null;
        var input = steps[inputIndex];
        var relative = JsonString(input.InputJson, "path", false);
        var mediaType = JsonString(input.OutputJson, "mediaType", true);
        if (JsonString(input.InputJson, "operation", false) != "file" || string.IsNullOrWhiteSpace(relative)
            || mediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true) return null;
        var session = await _chats.GetSessionAsync(message.SessionId, token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(session?.CodingWorkspacePath)) return null;
        string path;
        try { path = WorkspaceFilePath.Resolve(session.CodingWorkspacePath, relative); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { return null; }
        if (!File.Exists(path)) return null;
        var normalized = Path.GetRelativePath(session.CodingWorkspacePath, path).Replace('\\', '/');
        foreach (var receipt in steps.Take(inputIndex).Reverse().Where(step => step.Tool == "research.code.execute"))
        {
            if (string.IsNullOrWhiteSpace(receipt.OutputJson)) continue;
            try
            {
                using var document = JsonDocument.Parse(receipt.OutputJson);
                var result = document.RootElement;
                if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("result", out var wrapped)) result = wrapped;
                if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True
                    || !result.TryGetProperty("projectId", out var project) || project.ValueKind != JsonValueKind.String
                    || !result.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array) continue;
                foreach (var run in runs.EnumerateArray())
                {
                    if (run.ValueKind != JsonValueKind.Object || !run.TryGetProperty("exitCode", out var exit)
                        || exit.ValueKind != JsonValueKind.Number || !exit.TryGetInt32(out var code) || code != 0
                        || !run.TryGetProperty("timedOut", out var timedOut) || timedOut.ValueKind != JsonValueKind.False
                        || !run.TryGetProperty("outputHashes", out var hashes) || hashes.ValueKind != JsonValueKind.Object) continue;
                    foreach (var output in hashes.EnumerateObject())
                    {
                        if (!string.Equals("Science/" + project.GetString() + "/" + output.Name.Replace('\\', '/'), normalized,
                                StringComparison.OrdinalIgnoreCase) || output.Value.ValueKind != JsonValueKind.String) continue;
                        var expected = output.Value.GetString();
                        if (expected is not { Length: 64 } || !expected.All(char.IsAsciiHexDigit)) continue;
                        await using var stream = File.OpenRead(path);
                        if (stream.Length is <= 0 or > MaximumOriginalBytes) return null;
                        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
                        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) continue;
                        stream.Position = 0;
                        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["role"] = "original", ["sourceUploadId"] = uploadId,
                            ["replacesArtifactId"] = thumbnail.Id.ToString("D"),
                            ["verifiedByStepId"] = receipt.Id, ["verifiedSourceSha256"] = actual,
                            ["recoveredFrom"] = "verifiedWorkspaceExecution",
                        };
                        return await _artifacts.ImportAsync(thumbnail.MessageId, "workspace-original-" + actual,
                            Path.GetFileName(path), mediaType, actual, stream.Length, thumbnail.Provider, thumbnail.StepId,
                            metadata, stream, token).ConfigureAwait(false);
                    }
                }
            }
            catch (JsonException) { }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
        return null;
    }
}
