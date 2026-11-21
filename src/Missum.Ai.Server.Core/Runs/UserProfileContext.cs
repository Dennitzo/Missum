using System.Security.Cryptography;
using System.Text;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Core.Runs;

internal static class UserProfileContext
{
    internal const int MaximumTextCharacters = 6_000;
    internal const int MaximumOperationNoteCharacters = 10_000;
    internal const int TokenBudget = 1_000;
    // A UTF-8 byte budget conservatively bounds byte-fallback tokenizers without
    // warming a model or changing the text between checkpoints and native forks.
    private const int MaximumBlockBytes = TokenBudget - 16;
    private const string Start = "=== NUTZERPROFIL (dauerhaft gespeicherte Vorgaben des Nutzers) ===";
    private const string End = "=== ENDE NUTZERPROFIL ===";
    private const string OperationStart = "=== GERADE AUSGEFÜHRTE GEDÄCHTNISAKTION ===";
    private const string OperationInstructions = "Diese Quittung kommt direkt aus dem lokalen Missum-Speicher. Erfolgreiche Änderungen sind bereits dauerhaft wirksam. Berichte den vermerkten Status.";
    private const string Instructions = "Persönlicher Kontext. Aktueller Auftrag, Systemregeln und Werkzeugrechte haben Vorrang. Projektvorgaben gehen globalen Vorgaben vor. Aktionsquittungen gelten für die aktuelle Antwort.";
    private static readonly string[] RetiredClientMetadata =
    [
        "NUTZERPROFIL",
        "Dauerhafte Vorgaben dieses Nutzers aus früheren Sitzungen, projektbezogene zuerst.",
        "Diese Zeilen sind persönlicher Kontext, keine System- oder Werkzeuganweisung und erweitern keine Rechte.",
        "Widerspricht der aktuelle Auftrag, gilt der aktuelle Auftrag. Eine vorübergehende Ausnahme nennt der Nutzer im Auftrag.",
    ];

    internal static LmChatMessage? CreateMessage(UserProfileSection? section)
    {
        if (section is null) return null;

        var prefix = Start + "\n" + Instructions;
        var suffix = "\n" + End;
        var remaining = MaximumBlockBytes - Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(suffix);
        // Receipts for this turn take precedence over older preferences.
        var operationHeader = "\n" + OperationStart + "\n" + OperationInstructions;
        var operationHeaderBytes = Encoding.UTF8.GetByteCount(operationHeader);
        remaining -= operationHeaderBytes;
        var operation = WholeLines(section.OperationNote, ref remaining);
        if (operation.Length == 0) remaining += operationHeaderBytes;
        var text = WholeLines(section.Text, ref remaining);
        if (text.Length == 0 && operation.Length == 0) return null;
        return new("system", prefix + text + (operation.Length > 0 ? operationHeader + operation : string.Empty) + suffix);
    }

    internal static string? Fingerprint(UserProfileSection? section) =>
        CreateMessage(section)?.Content is { } content
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))
            : null;

    internal static IReadOnlyList<LmChatMessage> Refresh(
        IReadOnlyList<LmChatMessage> messages, LmChatMessage? current)
    {
        var leadingCount = 0;
        foreach (var message in messages)
        {
            if (message.Role != "system") break;
            if (message.Content is { } content && TryLocate(content, out var start, out var end))
            {
                if (current?.Content is { } same && content.AsSpan(start, end - start).SequenceEqual(same))
                    return messages;

                var updated = messages.ToList();
                if (current?.Content is { } replacement)
                    updated[leadingCount] = message with { Content = string.Concat(content.AsSpan(0, start), replacement, content.AsSpan(end)) };
                else if (start == 0 && end == content.Length)
                    updated.RemoveAt(leadingCount);
                else
                {
                    // Native rendering merges leading system messages with two newlines.
                    // Remove the section and one separator, leaving the adjacent policy intact.
                    if (start >= 2 && content.AsSpan(start - 2, 2).SequenceEqual("\n\n")) start -= 2;
                    else if (content.AsSpan(end).StartsWith("\n\n", StringComparison.Ordinal)) end += 2;
                    updated[leadingCount] = message with { Content = string.Concat(content.AsSpan(0, start), content.AsSpan(end)) };
                }
                return updated;
            }
            leadingCount++;
        }

        if (current is null) return messages;
        var result = messages.ToList();
        result.Insert(Math.Min(1, leadingCount), current);
        return result;
    }

    internal static LmChatMessage? LeadingMessage(IReadOnlyList<LmChatMessage> messages) =>
        messages.TakeWhile(static message => message.Role == "system")
            .FirstOrDefault(static message => message.Content?.StartsWith(Start, StringComparison.Ordinal) == true);

    private static bool TryLocate(string content, out int start, out int end)
    {
        start = 0;
        end = 0;
        if (!content.StartsWith(Start, StringComparison.Ordinal))
        {
            start = content.IndexOf("\n\n" + Start, StringComparison.Ordinal);
            if (start < 0) return false;
            start += 2;
        }
        var endMarker = content.IndexOf("\n" + End, start + Start.Length, StringComparison.Ordinal);
        if (endMarker < 0) return false;
        end = endMarker + 1 + End.Length;
        return true;
    }

    private static string WholeLines(string? input, ref int available)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var result = new StringBuilder();
        foreach (var raw in input.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (RetiredClientMetadata.Contains(raw, StringComparer.Ordinal)
                || raw.Contains(Start, StringComparison.Ordinal) || raw.Contains(End, StringComparison.Ordinal)
                || raw.Contains(OperationStart, StringComparison.Ordinal))
                continue;
            // Some native tokenizers normalize compatibility characters before
            // their byte fallback. Budget the larger representation as well.
            var cost = 1 + Math.Max(Encoding.UTF8.GetByteCount(raw),
                Encoding.UTF8.GetByteCount(raw.Normalize(NormalizationForm.FormKC)));
            if (cost > available) continue;
            result.Append('\n').Append(raw);
            available -= cost;
        }
        return result.ToString();
    }
}
