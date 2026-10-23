using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Core.Policies;

/// <summary>Read compatibility for the retired automatic README/.gitignore feature. Never adds instructions.</summary>
internal static class RetiredRepositoryInstructions
{
    internal static void RemoveFrom(List<LmChatMessage> messages, IReadOnlyCollection<string>? originalUserTexts = null)
    {
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (message.Content is not { } content) continue;
            var cleaned = message.Role switch
            {
                "system" => RemoveBlock(content,
                    "Projektpflege: Bei erlaubter Projektbearbeitung fehlende .gitignore und README.md im Projektwurzelordner",
                    "mitteilen. Keine zusätzlichen Modellläufe für Projektpflege starten.",
                    "Missums verwalteten .gitignore-Block erhalten;", userSuffix: false),
                "user" when originalUserTexts is not null => RemoveUserSuffixes(content, originalUserTexts),
                _ => content,
            };
            if (cleaned != content) messages[index] = message with { Content = cleaned };
        }
    }

    private static string RemoveUserSuffixes(string content, IReadOnlyCollection<string> originalUserTexts)
    {
        // The old transport did not mark its appended text. Its wording alone is
        // therefore insufficient provenance: a user may quote it verbatim. Never
        // change any current original request text, including exact quotations.
        if (originalUserTexts.Contains(content, StringComparer.Ordinal)) return content;
        content = RemoveBlock(content,
            "Bei erlaubten Projektänderungen auch .gitignore und die GitHub-README vollständig und korrekt pflegen; fehlende Dateien anlegen, ",
            "Vorhandene Inhalte erhalten; reine Leseaufträge bleiben lesend.",
            "README-Vorlagen mit tatsächlichen Projektinformationen ausarbeiten und Missums lokalen Publikationsrenderer samt Hilfsskript nicht versionieren. ",
            userSuffix: true, originalUserTexts);
        return RemoveBlock(content,
            "Projektpflege bei erlaubten Dateiänderungen: Lege fehlende .gitignore und README.md im Projektstamm an ",
            "Bei reinen Lese- oder Analyseaufträgen keine Dateien verändern; unveränderte Dokumentation nicht neu schreiben.",
            "Keine zusätzlichen Modellläufe und kein eigenständiges Entfernen bereits versionierter Dateien aus dem Git-Index. ",
            userSuffix: true, originalUserTexts);
    }

    private static string RemoveBlock(string content, string begin, string end, string signature, bool userSuffix,
        IReadOnlyCollection<string>? originalUserTexts = null)
    {
        // Only the final complete generated block is eligible. A matching phrase in
        // source/code, an incomplete block, or subsequent user prose is preserved.
        var start = content.LastIndexOf(begin, StringComparison.Ordinal);
        if (start < 0) return content;
        var separator = start >= 2 && content.AsSpan(start - 2, 2).SequenceEqual("\n\n") ? 2
            : start >= 4 && content.AsSpan(start - 4, 4).SequenceEqual("\r\n\r\n") ? 4 : 0;
        if (userSuffix ? separator == 0 : start > 0 && separator == 0) return content;
        var finish = content.IndexOf(end, start + begin.Length, StringComparison.Ordinal);
        if (finish < 0 || finish - start > 2500) return content;
        finish += end.Length;
        var block = content[start..finish];
        if (!block.Contains(signature, StringComparison.Ordinal)
            || userSuffix && (block.Contains('\n') || block.Contains('\r'))) return content;
        if (userSuffix && finish < content.Length
            && !content.AsSpan(finish).StartsWith("\n\nPublikation dieses Projekts: ", StringComparison.Ordinal)
            && !content.AsSpan(finish).StartsWith("\n\nMISSUM_DOCUMENT_ATTACHMENTS\n", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(content[finish..])) return content;
        var cleaned = content.Remove(start - separator, finish - start + separator);
        if (userSuffix && (originalUserTexts is null || start == separator
            || !originalUserTexts.Contains(content[..(start - separator)], StringComparer.Ordinal)
                && !originalUserTexts.Contains(cleaned, StringComparer.Ordinal))) return content;
        return cleaned;
    }

}
