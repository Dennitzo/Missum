using System.Security.Cryptography;
using System.Text.Json;
using Missum.App.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifyPlanetPaletteSmokeAsync()
    {
        const int sampleCount = 128;
        var preview = new Grid { Width = 768, Background = new SolidColorBrush(Color.FromArgb(255, 30, 30, 30)) };
        preview.RowDefinitions.Add(new() { Height = new(36) });
        for (var row = 0; row < 8; row++) preview.RowDefinitions.Add(new() { Height = new(62) });
        for (var column = 0; column < 16; column++) preview.ColumnDefinitions.Add(new() { Width = new(48) });
        var heading = new TextBlock { Text = "Planetenpalette · 14 / 28 DIP · 128 Beispiele", FontSize = 14,
            Margin = new(12, 8, 0, 0), Foreground = new SolidColorBrush(Color.FromArgb(255, 220, 220, 220)) };
        Grid.SetColumnSpan(heading, 16); preview.Children.Add(heading);
        var smallPlanets = new List<NativeSubagentAvatar>();
        for (var index = 0; index < sampleCount; index++)
        {
            var tile = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
            var small = SubagentIcon("palette-preview-" + index, 14, index);
            smallPlanets.Add(small); tile.Children.Add(small);
            tile.Children.Add(SubagentIcon("palette-preview-" + index, 28, index));
            tile.Children.Add(new TextBlock { Text = index.ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center, Opacity = .65,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 220, 220, 220)) });
            Grid.SetColumn(tile, index % 16); Grid.SetRow(tile, 1 + index / 16); preview.Children.Add(tile);
        }
        var originalBody = BodyGrid.Visibility;
        MessagesPanel.Children.Add(preview);
        try
        {
            BodyGrid.Visibility = Visibility.Visible;
            UpdateLayout(); await Task.Delay(100);
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(preview);
            if (bitmap.PixelWidth < 700 || bitmap.PixelHeight < 500)
                throw new InvalidOperationException("The native planet palette must have a visible contact sheet.");
            var buffer = await bitmap.GetPixelsAsync();
            var pixels = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(buffer);
            var scaleX = bitmap.PixelWidth / preview.ActualWidth;
            var scaleY = bitmap.PixelHeight / preview.ActualHeight;
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var avatar in smallPlanets)
            {
                var position = avatar.TransformToVisual(preview).TransformPoint(new(0, 0));
                var left = (int)Math.Round(position.X * scaleX);
                var top = (int)Math.Round(position.Y * scaleY);
                var width = (int)Math.Round(avatar.ActualWidth * scaleX);
                var height = (int)Math.Round(avatar.ActualHeight * scaleY);
                if (width < 14 || height < 14 || left < 0 || top < 0 || left + width > bitmap.PixelWidth || top + height > bitmap.PixelHeight)
                    throw new InvalidOperationException("A native planet was clipped or has no visible layout.");
                var crop = new byte[width * height * 4];
                for (var row = 0; row < height; row++)
                    Array.Copy(pixels, ((top + row) * bitmap.PixelWidth + left) * 4, crop, row * width * 4, width * 4);
                hashes.Add(Convert.ToHexString(SHA256.HashData(crop)));
            }
            if (hashes.Count != sampleCount)
                throw new InvalidOperationException("Distinct planets must remain visually distinct at their actual 14-DIP overlay size.");
            using (var file = File.Create(Path.Combine(App.Current.DataDirectory, "native-planet-palette-preview.png")))
            using (var stream = file.AsRandomAccessStream())
            {
                var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                    (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
                await encoder.FlushAsync();
            }
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-planet-palette-validation.json"),
                JsonSerializer.Serialize(new { renderer = "WinUI3", passed = true, paletteVersion = NativePlanetPalette.PaletteVersion,
                    paletteCount = NativePlanetPalette.PaletteCount, renderedSamples = sampleCount, distinctSmallIcons = hashes.Count,
                    smallIconDip = 14, nativeVectors = true }));
        }
        finally { MessagesPanel.Children.Remove(preview); BodyGrid.Visibility = originalBody; UpdateLayout(); }
    }
}
