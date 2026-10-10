using System.Globalization;
using System.Text.Json;

namespace Missum.App.Services;

/// <summary>Conversation changes determine sidebar position; selection and client drafts do not.</summary>
internal static class AssistantSidebarSessionOrder
{
    internal static IReadOnlyList<JsonElement> Sort(IEnumerable<JsonElement> sessions) => sessions
        .OrderByDescending(session => Date(session, "updatedAt"))
        .ThenByDescending(session => Date(session, "createdAt"))
        .ThenByDescending(session => Text(session, "id"), StringComparer.Ordinal)
        .ToArray();

    private static DateTimeOffset Date(JsonElement session, string name) =>
        DateTimeOffset.TryParse(Text(session, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value : DateTimeOffset.MinValue;

    private static string Text(JsonElement session, string name) =>
        session.ValueKind == JsonValueKind.Object && session.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";
}
