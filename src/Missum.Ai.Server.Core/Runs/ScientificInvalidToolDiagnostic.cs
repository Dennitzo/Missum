using System.Text.Json;
using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Core.Runs;

internal static class ScientificInvalidToolDiagnostic
{
    internal static string LastFailure(IReadOnlyList<LmChatMessage> messages)
    {
        foreach (var message in messages.Reverse())
        {
            if (message.Role != "tool" || message.Content is null) continue;
            try
            {
                using var document = JsonDocument.Parse(message.Content);
                var data = document.RootElement;
                if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("errorCode", out var code)
                    || code.ValueKind != JsonValueKind.String || code.GetString() != "agent.invalid_tool_call"
                    || !data.TryGetProperty("message", out var reason) || reason.ValueKind != JsonValueKind.String) continue;
                var tool = messages.SelectMany(item => item.ToolCalls ?? [])
                    .LastOrDefault(call => call.Id == message.ToolCallId)?.Name ?? "Werkzeug";
                var detail = reason.GetString()!;
                return $" Letzte Ablehnung ({tool}): " + detail[..Math.Min(detail.Length, 1600)];
            }
            catch (JsonException) { }
        }
        return "";
    }
}
