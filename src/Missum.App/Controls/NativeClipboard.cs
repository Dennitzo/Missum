using Missum.App.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Missum.App.Controls;

internal static class NativeClipboard
{
    internal const string UnavailableMessage = "Die Zwischenablage ist momentan nicht verfügbar. Die Auswahl bleibt erhalten; bitte erneut kopieren.";
    private static readonly ClipboardTextWriter Writer = new(text =>
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush(); // Preserve the copied text even if the user closes Missum before pasting.
    });

    internal static Task<bool> WriteTextAsync(string text, CancellationToken cancellationToken = default)
        => Writer.WriteTextAsync(text, cancellationToken);
}
