using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;
using Xunit.Abstractions;

namespace Missum.Tests;

public sealed class NativeChatPdfExportTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task NativeChatExportRendersTheCompleteConversationAndStoresTheExactPdfArtifact()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_NATIVE_PDF_EXPORT_LIVE") != "1") return;
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var artifacts = environment.Get<IChatArtifactRepository>();
        var blobs = environment.Get<IBinaryObjectStore>();
        var settings = environment.Get<ISettingsStore>();
        var session = await chats.CreateSessionAsync("Vollstaendiger nativer PDF-Export", ChatMode.Coding);
        var other = await chats.CreateSessionAsync("Anderer geoeffneter Chat", ChatMode.General);
        await chats.SaveDraftAsync(session.Id, "Unveraenderter Entwurf des exportierten Chats");
        await chats.SaveDraftAsync(other.Id, "Unveraenderter Entwurf des aktiven Tabs");
        await settings.SaveAsync(new AppSettings { ActiveSessionId = other.Id });
        _ = await chats.AddMessageAsync(session.Id, ChatRole.User,
            "USERPDFMARKER Bitte erklaere die Aenderungen vollstaendig.", MessageStatus.Completed);
        var paragraphs = Enumerable.Range(1, 60).Select(index =>
            $"Abschnitt {index:D2}. " + string.Join(' ', Enumerable.Repeat(
                "Die native Oberflaeche zeigt den Verlauf, die Werkzeugschritte und die pruefbaren Ergebnisse dieser Sitzung.", 5)));
        var response = await chats.AddMessageAsync(session.Id, ChatRole.Assistant,
            "ASSISTANTPDFMARKER\n\n" + string.Join("\n\n", paragraphs) + "\n\nLASTPDFMARKER", MessageStatus.Completed);
        await chats.SaveToolStepAsync(response.Id, new AssistantToolStep("pdf-diff", "coding.edit", "completed",
            Detail: "DIFFPDFMARKER\n\n```diff\n--- a/example.cs\n+++ b/example.cs\n@@ -1 +1 @@\n-oldValue\n+newValue\n```",
            InputJson: "{\"path\":\"example.cs\"}", OutputJson: "{\"changed\":true}", ContentOffset: 0));
        var snapshot = await environment.Get<IConversationSnapshotRepository>().GetAsync(session.Id);
        Assert.NotNull(snapshot);
        var otherBefore = await chats.GetSessionAsync(other.Id);
        var sessionIdsBefore = (await chats.ListSessionsAsync()).Select(item => item.Id).Order().ToArray();
        var settingsBefore = JsonSerializer.Serialize(await settings.LoadAsync());
        var target = Path.Combine(environment.Directory, "Chat Export vollstaendig.pdf");
        await File.WriteAllTextAsync(target, "Previously selected file must be replaced with the complete PDF.");
        using var renderer = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
        var service = new NativeChatPdfExportService(environment.Get<IConversationSnapshotRepository>(), chats, artifacts, renderer);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));

        var receipt = await service.ExportAsync(session.Id, target, deadline.Token);

        var savedBytes = await File.ReadAllBytesAsync(target, deadline.Token);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(savedBytes, 0, 5));
        var digest = Convert.ToHexString(SHA256.HashData(savedBytes)).ToLowerInvariant();
        Assert.Equal(target, receipt.SavedPath);
        Assert.Equal(2, receipt.MessageCount);
        Assert.Equal(session.Id, receipt.SessionId);
        Assert.Equal(digest, receipt.Artifact.Sha256);
        Assert.Equal(savedBytes.LongLength, receipt.Artifact.Length);
        Assert.Equal("application/pdf", receipt.Artifact.ContentType);
        Assert.Equal(Path.GetFileName(target), receipt.Artifact.FileName);
        Assert.Equal(receipt.Artifact.Id, Assert.Single(await artifacts.ListForMessageAsync(receipt.MessageId)).Id);
        await using (var stored = await blobs.OpenReadAsync(receipt.Artifact.BlobId, deadline.Token))
        {
            using var copy = new MemoryStream();
            await stored.CopyToAsync(copy, deadline.Token);
            Assert.Equal(savedBytes, copy.ToArray());
        }
        Assert.True(await blobs.VerifyAsync(receipt.Artifact.BlobId, deadline.Token));
        using var pdf = PdfDocument.Open(savedBytes);
        Assert.True(pdf.NumberOfPages >= 3, $"Expected a multipage chat export, got {pdf.NumberOfPages} pages.");
        var extracted = string.Join('\n', pdf.GetPages().Select(page => page.Text));
        foreach (var marker in new[] { "USERPDFMARKER", "ASSISTANTPDFMARKER", "LASTPDFMARKER", "DIFFPDFMARKER", "oldValue", "newValue" })
            Assert.Contains(marker, extracted, StringComparison.Ordinal);
        var messages = await chats.ListMessagesAsync(session.Id, deadline.Token);
        Assert.Equal(3, messages.Count);
        Assert.Equal(JsonSerializer.Serialize(snapshot.Messages), JsonSerializer.Serialize(messages.Take(2)));
        Assert.Equal(receipt.MessageId, messages[2].Id);
        Assert.Equal(MessageStatus.Completed, messages[2].Status);
        Assert.Contains(target, messages[2].Content, StringComparison.Ordinal);
        Assert.Contains(digest, messages[2].Content, StringComparison.Ordinal);
        Assert.Equal(otherBefore, await chats.GetSessionAsync(other.Id, deadline.Token));
        Assert.Empty(await chats.ListMessagesAsync(other.Id, deadline.Token));
        Assert.Equal(snapshot.Session.Draft, (await chats.GetSessionAsync(session.Id, deadline.Token))!.Draft);
        Assert.Equal(sessionIdsBefore, (await chats.ListSessionsAsync(cancellationToken: deadline.Token)).Select(item => item.Id).Order().ToArray());
        Assert.Equal(settingsBefore, JsonSerializer.Serialize(await settings.LoadAsync(deadline.Token)));
        output.WriteLine($"Real renderer: {pdf.NumberOfPages} pages, {savedBytes.LongLength} bytes, SHA-256 {digest}, artifact {receipt.Artifact.Id}.");

        var evidenceRoot = Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_EVIDENCE_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(evidenceRoot))
        {
            var evidence = Path.Combine(Path.GetFullPath(evidenceRoot), "native-chat-pdf-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(evidence);
            File.Copy(target, Path.Combine(evidence, Path.GetFileName(target)));
            await File.WriteAllTextAsync(Path.Combine(evidence, "receipt.json"), JsonSerializer.Serialize(new
            {
                receipt.SessionId, receipt.MessageId, receipt.Artifact, receipt.MessageCount,
                pages = pdf.NumberOfPages, verifiedSha256 = digest, exactBlobMatch = true,
                activeSessionUnchanged = true, otherSessionsUnchanged = true,
            }), deadline.Token);
            output.WriteLine("Evidence: " + evidence);
        }
    }
}
