using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async void OnComposerPaste(object sender, TextControlPasteEventArgs e)
    {
        var ownerSession = _session;
        if (_disposed || ownerSession == Guid.Empty) return;
        try
        {
            var data = Clipboard.GetContent();
            var files = data.Contains(StandardDataFormats.StorageItems);
            var image = data.Contains(StandardDataFormats.Bitmap);
            if (!files && !image) return; // Text uses the native editor, including during steering.
            e.Handled = true;
            await ImportComposerClipboardAsync(data, ownerSession);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowError(exception.Message); }
    }
    private async Task ImportComposerClipboardAsync(DataPackageView data, Guid ownerSession)
    {
        var files = data.Contains(StandardDataFormats.StorageItems);
        SetComposerAttachmentImportBusy(ownerSession, true);
        try
        {
            if (files)
            {
                var items = (await data.GetStorageItemsAsync()).OfType<StorageFile>().ToArray();
                if (items.Length == 0) throw new InvalidOperationException("Bitte Dateien oder ein Bild einfügen; Ordner werden hier nicht als Anhang übernommen.");
                foreach (var file in items)
                {
                    using var stream = await file.OpenStreamForReadAsync();
                    if (_coordinator.SupportedDocumentExtensions.Contains(Path.GetExtension(file.Name)))
                        await _coordinator.ImportDocumentAsync(ownerSession, file.Name, stream, _lifetime.Token);
                    else await _coordinator.ImportAttachmentAsync(ownerSession, file.Name, file.ContentType, stream, _lifetime.Token);
                }
            }
            else
            {
                var bitmap = await data.GetBitmapAsync();
                using var input = await bitmap.OpenReadAsync();
                var decoder = await BitmapDecoder.CreateAsync(input);
                using var pixels = await decoder.GetSoftwareBitmapAsync();
                using var encoded = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
                encoder.SetSoftwareBitmap(pixels);
                await encoder.FlushAsync();
                encoded.Seek(0);
                using var stream = encoded.AsStreamForRead();
                await _coordinator.ImportAttachmentAsync(ownerSession, $"Eingefügtes Bild {DateTime.Now:HH-mm-ss}.png", "image/png", stream, _lifetime.Token);
            }
        }
        finally
        {
            SetComposerAttachmentImportBusy(ownerSession, false);
            await RefreshComposerAfterImportAsync(ownerSession);
        }
    }

}
