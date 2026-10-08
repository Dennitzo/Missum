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
    public void ExportErrorKeepsTheWrappedHumanDiagnosticInsteadOfPowerShellMetadata()
    {
        const string error = "\r\nDie PDF wurde nicht erzeugt, weil 1 mathematische Ausdrücke\r\n"
            + "nicht KaTeX-kompatibel sind.\r\nIn C:\\Assets\\export-document.ps1:440 Zeichen:9\r\n"
            + "+ throw ...\r\n+ CategoryInfo : OperationStopped\r\n"
            + "+ FullyQualifiedErrorId : Die PDF wurde nicht erzeugt\r\n   .\r\n";

        Assert.Equal("Die PDF wurde nicht erzeugt, weil 1 mathematische Ausdrücke nicht KaTeX-kompatibel sind.",
            DocumentPdfExporter.ExportErrorDetail(error));
    }

    [Theory]
    [InlineData("Microsoft Edge hat keine PDF-Datei erzeugt.\nAt C:\\Assets\\export-document.ps1:444 char:9\n+ throw ...",
        "Microsoft Edge hat keine PDF-Datei erzeugt.")]
    [InlineData("Das Ziel ist schreibgeschützt.\n\nWeitere PowerShell-Metadaten.", "Das Ziel ist schreibgeschützt.")]
    [InlineData("  \r\n  ", null)]
    public void ExportErrorReportsOnlyTheActualDiagnostic(string text, string? expected)
    {
        Assert.Equal(expected, DocumentPdfExporter.ExportErrorDetail(text));
    }

    [Fact]
    public void ExportErrorBoundsTheDiagnosticLength()
    {
        Assert.Equal(4096, DocumentPdfExporter.ExportErrorDetail(new string('x', 8192))!.Length);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task GeomagneticMatrixPublicationRendersWithoutRewritingItsTeXSource()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_NATIVE_PDF_EXPORT_LIVE") != "1") return;
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Directory, "GeomagneticMatrix.md");
        const string markdown = """
            # GEOMAGNETICMATRIXCHECK

            ## Reduziertes Modell

            ### 3.3 Reduzierte α-Ω-Gleichungen

            $$\frac{d}{dt}\begin{pmatrix}B_p\\B_\phi\end{pmatrix} = \begin{pmatrix}-\eta_p & \alpha\\\omega_\Omega & -\eta_\phi\end{pmatrix}\begin{pmatrix}B_p\\B_\phi\end{pmatrix} \tag{3.9}$$
            """;
        await File.WriteAllTextAsync(source, markdown);
        using var renderer = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        var target = await renderer.EnsureCurrentAsync(source, sourceChanged: true,
            scientificPublication: true, cancellationToken: deadline.Token);

        Assert.NotNull(target);
        using var pdf = PdfDocument.Open(target);
        var content = string.Join('\n', pdf.GetPages().Select(page => page.Text));
        Assert.Contains("GEOMAGNETICMATRIXCHECK", content, StringComparison.Ordinal);
        Assert.Contains("(3.9)", content, StringComparison.Ordinal);
        Assert.DoesNotContain("\\begin", content, StringComparison.Ordinal);
        Assert.Equal(markdown, await File.ReadAllTextAsync(source, deadline.Token));

        var published = await File.ReadAllBytesAsync(target, deadline.Token);
        await File.WriteAllTextAsync(source, markdown + "\n\n$$\\MissumUnknownCommand{1}$$", deadline.Token);
        var invalid = await Assert.ThrowsAsync<InvalidOperationException>(() => renderer.EnsureCurrentAsync(
            source, sourceChanged: true, scientificPublication: true, cancellationToken: deadline.Token));
        Assert.Contains("3.3 Reduzierte α-Ω-Gleichungen", invalid.Message, StringComparison.Ordinal);
        Assert.Contains("mathematische Ausdrücke", invalid.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\uFFFD', invalid.Message);
        Assert.Contains("Undefined control sequence", invalid.Message, StringComparison.Ordinal);
        Assert.Equal(published, await File.ReadAllBytesAsync(target, deadline.Token));
        output.WriteLine($"Geomagnetic matrix PDF: {pdf.NumberOfPages} page(s), {published.Length} bytes; invalid math preserves the prior PDF and reports the real parser error.");
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task DocumentExportPublishesLongPathsAtomicallyAndReportsTheActualFailure()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_NATIVE_PDF_EXPORT_LIVE") != "1") return;
        await using var environment = await TestEnvironment.CreateAsync();
        var directory = environment.Directory;
        while (directory.Length < 280)
            directory = Path.Combine(directory, "publication-" + new string('p', 32));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "Publikation.md");
        var target = Path.ChangeExtension(source, ".pdf");
        await File.WriteAllTextAsync(source, "# LONGPATHPDFMARKER\n\nDie Gleichung lautet $x^2 + y^2 = z^2$.\n");
        await File.WriteAllTextAsync(target, "Previously published PDF must be replaced atomically.");
        using var renderer = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        Assert.Equal(target, await renderer.EnsureCurrentAsync(source, sourceChanged: true,
            scientificPublication: true, cancellationToken: deadline.Token));
        var published = await File.ReadAllBytesAsync(target, deadline.Token);
        using (var pdf = PdfDocument.Open(published))
            Assert.Contains("LONGPATHPDFMARKER", string.Join('\n', pdf.GetPages().Select(page => page.Text)), StringComparison.Ordinal);
        Assert.True(target.Length > 260);

        await File.WriteAllTextAsync(source, "# Invalid mathematics\n\n$$\\MissumUnknownCommand{1}$$\n", deadline.Token);
        var mathFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => renderer.EnsureCurrentAsync(
            source, sourceChanged: true, scientificPublication: true, cancellationToken: deadline.Token));
        Assert.Contains("KaTeX", mathFailure.Message, StringComparison.Ordinal);
        Assert.Equal(published, await File.ReadAllBytesAsync(target, deadline.Token));
        Assert.Empty(Directory.EnumerateFiles(directory, ".*.tmp.pdf"));
        Assert.Empty(Directory.EnumerateFiles(directory, ".*.bak.pdf"));

        // A filesystem publish error after valid mathematics is not a KaTeX error.
        var blockedSource = Path.Combine(directory, "blocked.md");
        Directory.CreateDirectory(Path.ChangeExtension(blockedSource, ".pdf"));
        await File.WriteAllTextAsync(blockedSource, "# Valid mathematics\n\n$x = 1$\n", deadline.Token);
        var publishFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => renderer.EnsureCurrentAsync(
            blockedSource, sourceChanged: true, scientificPublication: true, cancellationToken: deadline.Token));
        Assert.DoesNotContain("KaTeX", publishFailure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(published, await File.ReadAllBytesAsync(target, deadline.Token));
        Assert.Empty(Directory.EnumerateFiles(directory, ".*.tmp.pdf"));
        Assert.Empty(Directory.EnumerateFiles(directory, ".*.bak.pdf"));
        output.WriteLine($"Long-path PDF: {target.Length} characters, {published.Length} bytes; failed math and filesystem publication preserved the prior PDF.");
    }

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
