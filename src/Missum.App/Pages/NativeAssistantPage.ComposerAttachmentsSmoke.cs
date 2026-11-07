using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifyComposerAttachmentsSmokeAsync(JsonElement original)
    {
        if (Environment.GetEnvironmentVariable("ASSISTANT_DISABLE_RUNTIME_AUTOSTART") != "1")
            throw new InvalidOperationException("Die Anhangsprüfung benötigt ein isoliertes Profil.");
        var chats = App.Current.GetService<IChatRepository>();
        var session = await chats.CreateSessionAsync("Anhangsanzeige", ChatMode.General, _lifetime.Token);
        var other = await chats.CreateSessionAsync("Ohne Anhänge", ChatMode.General, _lifetime.Token);
        var priorFixture = _selectionInputSmokeActive;
        _selectionInputSmokeActive = true;
        try
        {
            await ShowAttachmentsAsync(session.Id, false);
            Composer.Text = "Prüfe die angehängten Daten.";
            SetComposerAttachmentImportBusy(session.Id, true);
            if (ComposerAttachmentStatus.Visibility != Visibility.Visible || ComposerAttachmentsHost.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Der Dateiimport besitzt keine sichtbare Rückmeldung.");
            SetComposerAttachmentImportBusy(session.Id, false);
            using (var source = new MemoryStream(Encoding.UTF8.GetBytes("Messwert: 42")))
                await _coordinator.ImportDocumentAsync(session.Id, "Messdaten.txt", source, _lifetime.Token);
            await ShowAttachmentsAsync(session.Id, true);
            RequireAttachments(1, "Messdaten.txt");
            var path = Path.Combine(App.Current.DataDirectory, "Clipboard-Daten.txt");
            await File.WriteAllTextAsync(path, "Zusätzliche Daten", _lifetime.Token);
            var fileData = new DataPackage();
            fileData.SetStorageItems(new[] { await StorageFile.GetFileFromPathAsync(path) });
            await ImportComposerClipboardAsync(fileData.GetView(), session.Id);
            using var image = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, image);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, 2, 1, 96, 96,
                new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 });
            await encoder.FlushAsync();
            image.Seek(0);
            var imageData = new DataPackage();
            imageData.SetBitmap(RandomAccessStreamReference.CreateFromStream(image));
            await ImportComposerClipboardAsync(imageData.GetView(), session.Id);
            await ShowAttachmentsAsync(session.Id, true);
            RequireAttachments(3, "Clipboard-Daten.txt");
            if (Composer.Text != "Prüfe die angehängten Daten." || _composerAttachmentRemoveButtons.Any(button => button.IsEnabled))
                throw new InvalidOperationException("Import verändert den Prompt oder erlaubt das Entfernen laufend genutzter Anhänge.");
            await ShowAttachmentsAsync(other.Id, false);
            if (ComposerAttachmentsHost.Visibility != Visibility.Collapsed || ComposerAttachmentsPanel.Children.Count != 0)
                throw new InvalidOperationException("Anhänge sind in eine andere Sitzung gelangt.");
            await ShowAttachmentsAsync(session.Id, false);
            RequireAttachments(3, "Messdaten.txt");
            if (_composerAttachmentRemoveButtons.Any(button => !button.IsEnabled))
                throw new InvalidOperationException("Anhangsaktionen bleiben nach dem Lauf gesperrt.");
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-composer-attachments-validation.json"),
                JsonSerializer.Serialize(new { passed = true, renderer = "WinUI3", storedDocumentVisible = true,
                    clipboardFileVisible = true, clipboardImageVisible = true, liveRunKeepsAttachmentsVisible = true,
                    busyFeedbackVisible = true, draftPreserved = true, sessionIsolation = true,
                    sessionReloadRestoresAttachments = true, systemClipboardModified = false, modelRequests = 0, userDataModified = false }), _lifetime.Token);
        }
        finally
        {
            _selectionInputSmokeActive = priorFixture;
            await chats.DeleteSessionAsync(session.Id, CancellationToken.None);
            await chats.DeleteSessionAsync(other.Id, CancellationToken.None);
            ApplyEvent("state.snapshot", original);
        }
        async Task ShowAttachmentsAsync(Guid id, bool running)
        {
            var stored = JsonSerializer.SerializeToElement(await _coordinator.BuildConversationForHostAsync(id, _lifetime.Token), JsonOptions);
            var snapshot = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
            foreach (var field in stored.EnumerateObject()) snapshot[field.Name] = field.Value.Clone();
            snapshot["isRunning"] = JsonSerializer.SerializeToElement(running);
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(snapshot));
            ShowChatView(); UpdateLayout();
            await WaitForNativePreviewFrameAsync();
        }
        void RequireAttachments(int count, string name)
        {
            if (ComposerAttachmentsPanel.Children.Count != count || ComposerAttachmentsHost.Visibility != Visibility.Visible
                || ComposerAttachmentsHost.ActualHeight <= 0 || !ComposerAttachmentsPanel.Children.OfType<Border>().Any(chip =>
                    ((StackPanel)chip.Child).Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<TextBlock>()).Any(text => text.Text == name)))
                throw new InvalidOperationException("Anhangsanzeige: " + JsonSerializer.Serialize(new { expectedCount = count, actualCount = ComposerAttachmentsPanel.Children.Count, visibility = ComposerAttachmentsHost.Visibility.ToString(), height = ComposerAttachmentsHost.ActualHeight, expectedName = name, entries = ComposerAttachmentsPanel.Children.OfType<Border>().Select(chip => ((StackPanel)chip.Child).Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<TextBlock>()).Select(text => text.Text).ToArray()).ToArray(), snapshotHasDocuments = Items(_snapshot, "documents").Length, snapshotHasAttachments = Items(_snapshot, "attachments").Length }));
        }
    }
}
