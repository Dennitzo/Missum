using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Missum.Core.Research;

namespace Missum.App.Services;

/// <summary>Bind an actual vision response to the exact uploaded project image.</summary>
internal static class ScientificVisualInspection
{
    internal const string Method = "vision-inspection";
    internal const string UploadMethod = "vision-upload";
    internal const string Protocol = "science-plot-inspection-v1";

    internal static ResearchVerification? UploadBinding(string projectId, string projectRoot, JsonElement result, DateTimeOffset now)
    {
        if (Text(result, "uploadId").Length == 0 || !Hash(Text(result, "sha256"))
            || !Boolean(result, "fromWorkspace") || Text(result, "sourcePath").Length == 0) return null;
        var source = Path.GetFullPath(Text(result, "sourcePath"));
        var relative = Path.GetRelativePath(Path.GetFullPath(projectRoot), source).Replace('\\', '/');
        if (!ImagePath(relative)) return null;
        var payload = JsonSerializer.Serialize(new
        {
            protocol = Protocol, projectId, artifactPath = relative, sha256 = Text(result, "sha256"), uploadId = Text(result, "uploadId"),
        });
        return new("vision-upload-" + Digest(projectId + Text(result, "uploadId")), projectId, "artifact", relative,
            "visual", UploadMethod, "uploaded", payload, now);
    }

    internal static ResearchVerification? Review(string projectId, IReadOnlyList<ResearchVerification> bindings,
        JsonElement result, DateTimeOffset now)
    {
        if (!result.TryGetProperty("visualReview", out var review) || review.ValueKind != JsonValueKind.Object
            || Text(review, "protocol") != "missum-visual-review-v1" || !Boolean(review, "exactIdentity")
            || Text(review, "kind") != "image" || Text(review, "status") != "analyzed"
            || !Hash(Text(review, "sourceSha256")) || Text(review, "modelId").Length == 0
            || Text(review, "analysis").Length == 0 || Digest(Text(review, "analysis")) != Text(review, "analysisSha256")) return null;
        foreach (var binding in bindings.Where(item => item.ProjectId == projectId && item.Method == UploadMethod))
        {
            using var document = JsonDocument.Parse(binding.EvidenceJson);
            var upload = document.RootElement;
            if (Text(upload, "uploadId") != Text(review, "sourceUploadId")
                || Text(upload, "sha256") != Text(review, "sourceSha256") || !ImagePath(Text(upload, "artifactPath"))) continue;
            var artifactPath = Text(upload, "artifactPath");
            var evidence = JsonSerializer.Serialize(new
            {
                protocol = Protocol, projectId, artifactPath, sha256 = Text(review, "sourceSha256"),
                uploadId = Text(review, "sourceUploadId"), modelId = Text(review, "modelId"),
                analysis = Text(review, "analysis"), analysisSha256 = Text(review, "analysisSha256"),
                completedAt = Text(review, "completedAt"), quantitativeValidation = false,
            });
            return new("vision-review-" + Digest(projectId + artifactPath + Text(review, "sourceSha256")), projectId,
                "artifact", artifactPath, "visual", Method, "observed", evidence, now);
        }
        return null;
    }

    internal static bool HasCurrentReview(string projectId, string artifactPath, string sha256,
        IReadOnlyList<ResearchVerification> trustedChecks)
    {
        foreach (var check in trustedChecks.Where(item => item.ProjectId == projectId && item.Method == Method && item.Status == "observed"))
        {
            try
            {
                using var document = JsonDocument.Parse(check.EvidenceJson);
                var evidence = document.RootElement;
                if (Text(evidence, "protocol") == Protocol && Text(evidence, "artifactPath") == artifactPath
                    && Text(evidence, "sha256") == sha256 && Text(evidence, "analysis").Length > 0
                    && Digest(Text(evidence, "analysis")) == Text(evidence, "analysisSha256")) return true;
            }
            catch (JsonException) { }
        }
        return false;
    }

    private static bool ImagePath(string path) => (path.StartsWith("work/", StringComparison.Ordinal) || path.StartsWith("artifacts/", StringComparison.Ordinal))
        && !path.Contains(':') && !path.Contains('\\') && !path.Any(char.IsControl)
        && path.Split('/').All(part => part.Length > 0 && part is not ("." or ".."))
        && (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));
    private static string Text(JsonElement data, string property) => ScientificPublicationService.Text(data, property);
    private static bool Boolean(JsonElement data, string property) => data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
    private static bool Hash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    internal static string Digest(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
