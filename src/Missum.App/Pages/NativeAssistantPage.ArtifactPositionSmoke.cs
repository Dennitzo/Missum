using System.Security.Cryptography;
using System.Text.Json;
using Missum.App.Controls;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics.Imaging;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifyArtifactPositionSmokeAsync(JsonElement original)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MISSUM_SMOKE_INSTANCE_KEY")))
            throw new InvalidOperationException("Der Bildpositions-Smoke ist ausschließlich im isolierten Portable-Smoke erlaubt.");
        var chats = App.Current.GetService<IChatRepository>();
        var artifacts = App.Current.GetService<IChatArtifactRepository>();
        var session = await chats.CreateSessionAsync("Feste Bildpositionen-Smoke", _lifetime.Token);
        const string firstStep = "image-position-first";
        const string secondStep = "image-position-second";
        const string firstText = "Der erste Bildauftrag steht an dieser Stelle.\n\n";
        const string betweenText = "Zwischen beiden Bildern folgt ein weiterer Abschnitt.\n\n";
        const string tail = "Nach den Bildern wächst die Antwort weiter.\n\n";
        var initialContent = firstText + betweenText;
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, initialContent, MessageStatus.Streaming,
            cancellationToken: _lifetime.Token);
        var key = message.Id.ToString();
        var steps = new[]
        {
            new AssistantToolStep(firstStep, "media.analyze", "completed", "Erstes Bild",
                ContentOffset: firstText.Length, StartedAt: message.CreatedAt, CompletedAt: message.CreatedAt),
            new AssistantToolStep(secondStep, "media.analyze", "completed", "Zweites Bild",
                ContentOffset: initialContent.Length, StartedAt: message.CreatedAt.AddSeconds(1), CompletedAt: message.CreatedAt.AddSeconds(1)),
        };
        try
        {
            await chats.UpdateMessageWithToolStepsAsync(message.Id, initialContent, MessageStatus.Streaming, steps,
                cancellationToken: _lifetime.Token);
            var thumbnail = await ImportArtifactPositionImageAsync(artifacts, message.Id, firstStep, "thumbnail.jpg",
                new Dictionary<string, string> { ["role"] = "thumbnail" }, blue: 40, red: 220);
            var recovered = await ImportArtifactPositionImageAsync(artifacts, message.Id, firstStep, "ErstesOriginal.png",
                new Dictionary<string, string> { ["role"] = "original", ["replacesArtifactId"] = thumbnail.Id.ToString("D") },
                blue: 40, red: 220);
            var second = await ImportArtifactPositionImageAsync(artifacts, message.Id, secondStep, "ZweitesOriginal.png",
                new Dictionary<string, string> { ["role"] = "original" }, blue: 220, red: 40);
            var pdfBytes = System.Text.Encoding.ASCII.GetBytes("%PDF-1.7\n% Native footer placement fixture\n%%EOF\n");
            await using var pdfStream = new MemoryStream(pdfBytes, writable: false);
            var document = await artifacts.ImportAsync(message.Id, "position-pdf-" + Guid.NewGuid().ToString("N"),
                "Ergebnis.pdf", "application/pdf", Convert.ToHexStringLower(SHA256.HashData(pdfBytes)), pdfBytes.Length,
                "native-position-smoke", null, null, pdfStream, _lifetime.Token);
            JsonElement Dto(ChatArtifact artifact) => JsonSerializer.SerializeToElement(new
            {
                id = artifact.Id, fileName = artifact.FileName, contentType = artifact.ContentType,
                length = artifact.Length, sha256 = artifact.Sha256, stepId = artifact.StepId, metadata = artifact.Metadata,
            });
            JsonElement Snapshot(string content, string status, IReadOnlyList<AssistantToolStep> toolSteps, ChatArtifact[] shown)
            {
                var state = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
                state["activeSessionId"] = JsonSerializer.SerializeToElement(session.Id);
                state["chatMode"] = JsonSerializer.SerializeToElement("general");
                state["isRunning"] = JsonSerializer.SerializeToElement(false);
                state["isAiBusy"] = JsonSerializer.SerializeToElement(false);
                state["messages"] = JsonSerializer.SerializeToElement(new[]
                {
                    new { id = message.Id, sessionId = session.Id, role = "assistant", content, status,
                        createdAt = message.CreatedAt, updatedAt = DateTimeOffset.UtcNow, toolSteps, artifacts = shown.Select(Dto).ToArray() },
                }, JsonOptions);
                return JsonSerializer.SerializeToElement(state);
            }
            void Show(JsonElement snapshot) { ApplyEvent("state.snapshot", snapshot); RenderMessagesNow(); UpdateLayout(); }
            Show(Snapshot(initialContent, "streaming", steps, [thumbnail, second, document]));
            var firstContainer = (NativeArtifactLinks)_messageBlocks[key]["artifacts:step:" + firstStep];
            var secondContainer = (NativeArtifactLinks)_messageBlocks[key]["artifacts:step:" + secondStep];
            void AssertOrder(bool hasTail)
            {
                var body = MessageBody(_messageViews[key].View);
                var blocks = _messageBlocks[key];
                var firstImage = blocks["artifacts:step:" + firstStep];
                var secondImage = blocks["artifacts:step:" + secondStep];
                if (body.Children.IndexOf(firstImage) != body.Children.IndexOf(blocks["tool:" + firstStep]) + 1
                    || body.Children.IndexOf(secondImage) != body.Children.IndexOf(blocks["tool:" + secondStep]) + 1
                    || body.Children.IndexOf(blocks["text:" + firstText.Length]) <= body.Children.IndexOf(firstImage)
                    || body.Children.IndexOf(blocks["text:" + firstText.Length]) >= body.Children.IndexOf(secondImage)
                    || body.Children.IndexOf(blocks["artifacts"]) <= body.Children.IndexOf(secondImage)
                    || body.Children.OfType<NativeArtifactLinks>().Count() != 3)
                    throw new InvalidOperationException("Die beiden Bildkarten sind nicht einmalig an ihren gespeicherten Werkzeugpositionen verankert.");
                if (hasTail && body.Children.IndexOf(blocks["text:" + initialContent.Length]) <= body.Children.IndexOf(secondImage))
                    throw new InvalidOperationException("Späterer Antworttext wurde vor die bereits erstellten Bildkarten geschoben.");
                var footerIds = Descendants(blocks["artifacts"]).OfType<Button>().Select(AutomationProperties.GetAutomationId).ToArray();
                if (footerIds.Contains("artifact-" + thumbnail.Id.ToString("N"))
                    || footerIds.Contains("artifact-" + recovered.Id.ToString("N"))
                    || footerIds.Contains("artifact-" + second.Id.ToString("N"))
                    || !footerIds.Contains("artifact-" + document.Id.ToString("N")))
                    throw new InvalidOperationException("Das Nachrichtenende enthält doppelte Bilder oder verliert das unabhängige PDF-Artefakt.");
            }
            AssertOrder(false);
            await WaitArtifactImageSmokeAsync(() => Descendants(firstContainer).OfType<NativeArtifactImagePreview>().Any(view => view.HasImage)
                && Descendants(secondContainer).OfType<NativeArtifactImagePreview>().Any(view => view.HasImage));
            // The live first receipt was not persisted yet and is appended
            // after the saved second receipt. Its earlier offset still owns
            // the first image, independent of the incoming list order.
            var earlierLiveStep = steps[0] with { Status = "running", CompletedAt = null, UpdatedAt = DateTimeOffset.UtcNow };
            Show(Snapshot(initialContent, "streaming", [steps[1], earlierLiveStep], [thumbnail, second, document]));
            AssertOrder(false);
            var unknown = new AssistantToolStep("image-position-unknown", "assistant.progress", "completed", "Ohne Position");
            var tied = new AssistantToolStep("image-position-tie", "web.search", "completed", "Gleiche Position", ContentOffset: firstText.Length);
            Show(Snapshot(initialContent, "streaming", [steps[1], unknown, earlierLiveStep, tied], [thumbnail, second, document]));
            AssertOrder(false);
            var positionBlocks = _messageBlocks[key];
            var positionBody = MessageBody(_messageViews[key].View);
            if (positionBody.Children.IndexOf(positionBlocks["tool:" + firstStep])
                    >= positionBody.Children.IndexOf(positionBlocks["tool:image-position-unknown"])
                || positionBody.Children.IndexOf(positionBlocks["tool:image-position-unknown"])
                    >= positionBody.Children.IndexOf(positionBlocks["tool:image-position-tie"])
                || positionBody.Children.IndexOf(positionBlocks["tool:image-position-tie"])
                    >= positionBody.Children.IndexOf(positionBlocks["tool:" + secondStep])
                || !ReferenceEquals(firstContainer, positionBlocks["artifacts:step:" + firstStep])
                || !ReferenceEquals(secondContainer, positionBlocks["artifacts:step:" + secondStep]))
                throw new InvalidOperationException("Unpositionierte Werkzeugschritte oder gleiche Offsets wurden beim Bildanker-Sortieren instabil verschoben.");
            // Restore ordinary receipts before testing the subsequent text
            // stream; the image containers themselves remain unchanged.
            Show(Snapshot(initialContent, "streaming", steps, [thumbnail, second, document]));
            AssertOrder(false);
            foreach (var appended in new[] { tail, tail + "Ein zusätzlicher Absatz.\n\n", tail + "Ein zusätzlicher Absatz.\n\nNoch mehr Antworttext." })
            {
                ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new
                { sessionId = session.Id, messageId = message.Id, content = initialContent + appended }));
                await Task.Delay(180, _lifetime.Token);
                AssertOrder(true);
                if (!ReferenceEquals(firstContainer, _messageBlocks[key]["artifacts:step:" + firstStep])
                    || !ReferenceEquals(secondContainer, _messageBlocks[key]["artifacts:step:" + secondStep]))
                    throw new InvalidOperationException("Ein Markdown-Delta hat einen bestehenden Bildanker neu aufgebaut.");
            }
            var completedContent = initialContent + tail + "Ein zusätzlicher Absatz.\n\nNoch mehr Antworttext.";
            // Simulate the snapshot that arrives after legacy original recovery,
            // including a duplicate thumbnail notification in the same update.
            Show(Snapshot(completedContent, "completed", steps, [thumbnail, recovered, second, document, second]));
            AssertOrder(true);
            if (!ReferenceEquals(firstContainer, _messageBlocks[key]["artifacts:step:" + firstStep])
                || !ReferenceEquals(secondContainer, _messageBlocks[key]["artifacts:step:" + secondStep]))
                throw new InvalidOperationException("Originalwiederherstellung hat die Bildkarte an das Nachrichtenende versetzt.");
            await WaitArtifactImageSmokeAsync(() => Descendants(firstContainer).OfType<Button>()
                .Any(button => AutomationProperties.GetAutomationId(button) == "artifact-" + recovered.Id.ToString("N"))
                && Descendants(firstContainer).OfType<NativeArtifactImagePreview>().Any(view => view.HasImage));
            var imageCardIds = Descendants(MessageBody(_messageViews[key].View)).OfType<Button>()
                .Select(AutomationProperties.GetAutomationId).Where(id => id.StartsWith("artifact-", StringComparison.Ordinal)).ToArray();
            if (imageCardIds.Length != 3 || imageCardIds.Contains("artifact-" + thumbnail.Id.ToString("N")))
                throw new InvalidOperationException("Thumbnail oder doppelte Bildbenachrichtigungen sind als zusätzliche Karten stehen geblieben.");
            await SaveMathPreviewAsync(ConversationScroll, "native-artifact-position-preview.png");
            await chats.UpdateMessageWithToolStepsAsync(message.Id, completedContent, MessageStatus.Completed, [steps[1], steps[0]],
                cancellationToken: _lifetime.Token);
            var persisted = await chats.GetMessageAsync(message.Id, _lifetime.Token)
                ?? throw new InvalidOperationException("Die Bildpositions-Fixture konnte nicht wieder geladen werden.");
            var persistedArtifacts = AssistantArtifactOriginalResolver.DisplayArtifacts(
                await artifacts.ListForMessageAsync(message.Id, _lifetime.Token), persisted.ToolSteps);
            // Drop this fixture's native caches to exercise cold reconstruction,
            // using only its persisted message, step offsets and artifact IDs.
            MessagesPanel.Children.Remove(_messageViews[key].View);
            _messageViews.Remove(key); _messageBlocks.Remove(key); _messageActionViews.Remove(key);
            Show(Snapshot(persisted.Content, "completed", persisted.ToolSteps ?? [], persistedArtifacts.ToArray()));
            AssertOrder(true);
            if (ReferenceEquals(firstContainer, _messageBlocks[key]["artifacts:step:" + firstStep])
                || ReferenceEquals(secondContainer, _messageBlocks[key]["artifacts:step:" + secondStep]))
                throw new InvalidOperationException("Der Reload-Test hat vorhandene UI-Bildanker wiederverwendet.");
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-artifact-position-validation.json"),
                JsonSerializer.Serialize(new
                {
                    passed = true, processId = Environment.ProcessId, renderer = "WinUI3", images = 2, differentOffsets = new[] { firstText.Length, initialContent.Length },
                    afterOwningToolStep = true, appendedDeltas = 3, anchorsRetainedWhileStreaming = true,
                    originalRecoveryRetainsAnchor = true, thumbnailsNotDuplicated = true,
                    duplicateArtifactNotificationsCoalesced = true, unanchoredPdfRemainsAtFooter = true,
                    unorderedOffsetsSorted = true, earlierLiveReceiptRetainsPosition = true,
                    unpositionedSlotsPreserved = true, equalOffsetsStable = true,
                    persistedReloadRetainsPositions = true, aiRequests = 0,
                }), _lifetime.Token);
        }
        finally
        {
            ApplyEvent("state.snapshot", original); RenderMessagesNow();
            await chats.DeleteSessionAsync(session.Id, CancellationToken.None);
        }
    }

    private async Task<ChatArtifact> ImportArtifactPositionImageAsync(IChatArtifactRepository artifacts, Guid messageId,
        string stepId, string name, IReadOnlyDictionary<string, string> metadata, byte blue, byte red)
    {
        const uint width = 64, height = 32;
        var pixels = new byte[checked((int)(width * height * 4))];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        { pixels[offset] = blue; pixels[offset + 1] = 130; pixels[offset + 2] = red; pixels[offset + 3] = 255; }
        using var encoded = new MemoryStream();
        using (var stream = encoded.AsRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask(_lifetime.Token);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, width, height, 96, 96, pixels);
            await encoder.FlushAsync().AsTask(_lifetime.Token);
        }
        var bytes = encoded.ToArray();
        await using var content = new MemoryStream(bytes, writable: false);
        return await artifacts.ImportAsync(messageId, "position-image-" + Guid.NewGuid().ToString("N"), name,
            "image/png", Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length,
            "native-position-smoke", stepId, metadata, content, _lifetime.Token);
    }
}
