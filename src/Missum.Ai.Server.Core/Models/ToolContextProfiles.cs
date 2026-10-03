using System.Security.Cryptography;
using System.Text;

namespace Missum.Ai.Server.Core.Models;

/// <summary>Versioned native tool prefixes; a run and its children must pin the same profile.</summary>
public static class ToolContextProfiles
{
    public const string Current = "compact-v1";

    public static string Signature(IReadOnlyList<LmToolDefinition> tools, string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var transport = ModelRuntimeClient.PrepareTransportTools(modelId ?? string.Empty, tools);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(transport.GetRawText())));
    }
}
