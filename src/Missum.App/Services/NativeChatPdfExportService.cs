using System.Globalization;
using System.Text;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.App.Services;

/// <summary>Exports one durable conversation snapshot through the existing document renderer.</summary>
public sealed class NativeChatPdfExportService(
    IConversationSnapshotRepository conversations,
    DocumentPdfExporter renderer)
{
    public Task<ChatPdfExportReceipt> ExportAsync(Guid sessionId, string destinationPath,
        CancellationToken cancellationToken = default) => ExportAsync(sessionId, destinationPath, null, cancellationToken);

    public async Task<ChatPdfExportReceipt> ExportAsync(Guid sessionId, string destinationPath,
        Guid? messageId, CancellationToken cancellationToken = default)
    {
        var snapshot = await conversations.GetAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die zu exportierende Sitzung wurde nicht gefunden.");
        if (messageId is { } selectedId)
        {
            var selected = snapshot.Messages.FirstOrDefault(message => message.Id == selectedId)
                ?? throw new InvalidOperationException("Die zu exportierende Nachricht wurde nicht gefunden.");
            snapshot = snapshot with { Messages = [selected] };
        }
        if (snapshot.Messages.Count == 0) throw new InvalidOperationException("Der Chat enthält noch keine Nachrichten.");
        var destination = Path.GetFullPath(destinationPath);
        if (!string.Equals(Path.GetExtension(destination), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Der Export benötigt eine PDF-Zieldatei.");
        var exportId = Guid.NewGuid().ToString("N");
        var exportRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Missum", "ChatPdfExports"));
        var workingDirectory = Path.Combine(exportRoot, exportId);
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var source = Path.Combine(workingDirectory, "chat.md");
            await File.WriteAllTextAsync(source, FormatSnapshot(snapshot), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var rendered = await renderer.EnsureChatTranscriptAsync(source, sourceChanged: true, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Der Chat-PDF-Renderer hat keine Datei erzeugt.");
            await using var pdf = new FileStream(rendered, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true);
            var header = new byte[5];
            await pdf.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            if (!header.AsSpan().SequenceEqual("%PDF-"u8)) throw new InvalidDataException("Die erzeugte Datei ist kein PDF.");
            pdf.Position = 0;

            // A failed copy must not truncate an existing file chosen in the save picker.
            var pendingDestination = destination + "." + exportId + ".tmp";
            try
            {
                await using (var output = new FileStream(pendingDestination, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, bufferSize: 81920, useAsync: true))
                {
                    await pdf.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                File.Move(pendingDestination, destination, overwrite: true);
            }
            finally { if (File.Exists(pendingDestination)) File.Delete(pendingDestination); }

            return new(sessionId, destination, snapshot.Messages.Count);
        }
        finally
        {
            // Only this invocation's internally generated directory can be removed.
            if (string.Equals(Path.GetDirectoryName(workingDirectory), exportRoot, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(workingDirectory)) Directory.Delete(workingDirectory, recursive: true);
        }
    }

    internal static string FormatSnapshot(ConversationSnapshot snapshot)
    {
        var output = new StringBuilder();
        output.Append("# ").AppendLine(snapshot.Session.Title.Replace('\r', ' ').Replace('\n', ' '));
        output.AppendLine().Append("Missum · ").Append(snapshot.Messages.Count).Append(" Nachrichten · Stand ")
            .Append(snapshot.Session.ConversationRevision).AppendLine().AppendLine();
        foreach (var message in snapshot.Messages)
        {
            var role = message.Role switch { ChatRole.User => "Du", ChatRole.Assistant => "Missum", _ => "System" };
            output.Append("## ").Append(role).Append(" · ").AppendLine(message.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture));
            if (message.Status != MessageStatus.Completed) output.Append("Status zum Exportzeitpunkt: ").AppendLine(message.Status.ToString());
            output.AppendLine().AppendLine(message.Content).AppendLine();
            foreach (var step in message.ToolSteps ?? [])
            {
                output.Append("### Werkzeug: ").Append(step.Tool).Append(" · ").AppendLine(step.Status).AppendLine();
                if (!string.IsNullOrWhiteSpace(step.Detail)) output.AppendLine(step.Detail).AppendLine();
                else if (!string.IsNullOrWhiteSpace(step.Explanation)) output.AppendLine(step.Explanation).AppendLine();
                if (!string.IsNullOrWhiteSpace(step.InputJson)) AppendLiteral(output, "Eingabe", step.InputJson);
                if (!string.IsNullOrWhiteSpace(step.OutputJson)) AppendLiteral(output, "Ausgabe", step.OutputJson);
            }
            if (!string.IsNullOrWhiteSpace(message.Error) && message.Error != message.Content)
                output.Append("Fehler: ").AppendLine(message.Error).AppendLine();
            if (snapshot.Artifacts.TryGetValue(message.Id, out var linked))
                foreach (var artifact in linked)
                    output.Append("Anhang: ").Append(artifact.FileName).Append(" · ").Append(artifact.ContentType)
                        .Append(" · ").Append(artifact.Length).Append(" Bytes · SHA-256 ").AppendLine(artifact.Sha256).AppendLine();
            output.AppendLine("---").AppendLine();
        }
        return output.ToString();
    }

    private static void AppendLiteral(StringBuilder output, string label, string content)
    {
        var fence = "~~~~";
        while (content.Contains(fence, StringComparison.Ordinal)) fence += "~";
        output.AppendLine(label).AppendLine().AppendLine(fence).AppendLine(content).AppendLine(fence).AppendLine();
    }
}

public sealed record ChatPdfExportReceipt(Guid SessionId, string SavedPath, int MessageCount);
