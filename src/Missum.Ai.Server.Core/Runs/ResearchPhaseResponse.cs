using System.Text.Json;
using System.Text.RegularExpressions;
using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Core.Runs;

/// <summary>Validates the closed, first-party research phase schemas before any fields are consumed.</summary>
internal static class ResearchPhaseResponse
{
    internal static JsonElement Parse(string? content, JsonElement schema)
    {
        var text = (content ?? "").Trim();
        // A single complete Markdown envelope is transport formatting, not research data.
        // Never search for a convenient object inside prose or accept a partial JSON prefix.
        if (text.StartsWith("```json\n", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("```json\r\n", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("```\n", StringComparison.Ordinal)
            || text.StartsWith("```\r\n", StringComparison.Ordinal))
        {
            var start = text.IndexOf('\n') + 1;
            if (text.EndsWith("\n```", StringComparison.Ordinal)) text = text[start..^3].Trim();
        }
        try
        {
            using var document = JsonDocument.Parse(text);
            Validate(document.RootElement, schema, "$", 0);
            return document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"Die Recherche-Teilphase lieferte kein gültiges JSON-Ergebnis (Zeile {error.LineNumber}, Position {error.BytePositionInLine}).", error);
        }
    }

    private static void Validate(JsonElement value, JsonElement schema, string path, int depth)
    {
        if (depth > 32) throw Invalid(path, "zu tief verschachtelt");
        if (schema.TryGetProperty("type", out var type) && !Matches(value, type.GetString()))
            throw Invalid(path, "erwartet " + type.GetString());
        if (schema.TryGetProperty("enum", out var allowed) && !allowed.EnumerateArray().Any(item => JsonElement.DeepEquals(item, value)))
            throw Invalid(path, "Wert ist nicht im Antwortschema erlaubt");
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var field in required.EnumerateArray())
                    if (!value.TryGetProperty(field.GetString()!, out _)) throw Invalid(path + "." + field.GetString(), "Pflichtfeld fehlt");
            schema.TryGetProperty("properties", out var properties);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid(path + "." + property.Name, "Feld ist doppelt vorhanden");
                if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var child))
                    Validate(property.Value, child, path + "." + property.Name, depth + 1);
                else if (schema.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind == JsonValueKind.False)
                    throw Invalid(path + "." + property.Name, "unbekanntes Feld");
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            CheckRange(value.GetArrayLength(), schema, "minItems", "maxItems", path);
            if (schema.TryGetProperty("uniqueItems", out var unique) && unique.ValueKind == JsonValueKind.True)
            {
                var items = value.EnumerateArray().ToArray();
                for (var index = 0; index < items.Length; index++)
                    if (items.Take(index).Any(item => JsonElement.DeepEquals(item, items[index])))
                        throw Invalid(path, "Arrayeinträge müssen eindeutig sein");
            }
            if (schema.TryGetProperty("items", out var itemSchema))
            {
                var index = 0;
                foreach (var item in value.EnumerateArray()) Validate(item, itemSchema, path + "[" + index++ + "]", depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            CheckRange(text.Length, schema, "minLength", "maxLength", path);
            if (schema.TryGetProperty("pattern", out var pattern)
                && !Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                throw Invalid(path, "Text entspricht nicht dem Antwortschema");
        }
        else if (value.ValueKind == JsonValueKind.Number)
            CheckRange(value.GetDouble(), schema, "minimum", "maximum", path);
    }

    private static bool Matches(JsonElement value, string? type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number && double.IsFinite(value.GetDouble()),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => throw new InvalidOperationException("Nicht unterstütztes internes Recherche-Antwortschema: " + type),
    };

    private static void CheckRange(double value, JsonElement schema, string minimum, string maximum, string path)
    {
        if (schema.TryGetProperty(minimum, out var lower) && value < lower.GetDouble()
            || schema.TryGetProperty(maximum, out var upper) && value > upper.GetDouble())
            throw Invalid(path, "Wert außerhalb des Antwortschemas");
    }

    private static InvalidDataException Invalid(string path, string reason) => new("Ungültiges Recherchefeld " + path + ": " + reason + ".");
}

internal sealed class ResearchPhaseResponseException(string message, LmChatResult response, Exception inner)
    : Exception(message, inner)
{
    internal LmChatResult Response { get; } = response;
}
