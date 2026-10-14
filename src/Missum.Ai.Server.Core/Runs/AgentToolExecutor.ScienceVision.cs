using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Missum.Ai.Contracts;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class AgentToolExecutor
{
    internal const string VisualReviewProtocol = "missum-visual-review-v1";

    // Produced exclusively from completed upload identity and actual model output.
    // App persistence additionally matches it to its image.input receipt and current plot.
    internal static JsonElement? CreateVisualReviewReceipt(UploadCompleted upload, string requestedUploadId,
        string modelId, string analysis)
    {
        if (!upload.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(upload.UploadId, requestedUploadId, StringComparison.Ordinal)
            || upload.Sha256.Length != 64 || !upload.Sha256.All(char.IsAsciiHexDigit)
            || string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(analysis)) return null;
        return JsonSerializer.SerializeToElement(new
        {
            protocol = VisualReviewProtocol, kind = "image", status = "analyzed", exactIdentity = true,
            sourceUploadId = upload.UploadId, sourceSha256 = upload.Sha256.ToLowerInvariant(), modelId,
            analysis, analysisSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(analysis))),
            completedAt = DateTimeOffset.UtcNow, quantitativeValidation = false,
            interpretation = "Sichtbefund und Fehlerdiagnose; kein mathematischer Beweis oder quantitativer Abnahmenachweis.",
        });
    }
}
