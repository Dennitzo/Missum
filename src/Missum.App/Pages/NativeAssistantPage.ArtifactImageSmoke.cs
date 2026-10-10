using System.Security.Cryptography;
using System.Net;
using System.Text.Json;
using Missum.App.Controls;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    // Real repository, original blobs, Windows decoder and WinUI layout in the
    // empty smoke profile. Only launching an external app and a delayed load
    // are intercepted; no AI request or original user database is involved.
    private async Task VerifyArtifactImageSmokeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MISSUM_SMOKE_INSTANCE_KEY")))
            throw new InvalidOperationException("Der Bildkarten-Smoke ist ausschließlich im isolierten Portable-Smoke erlaubt.");
        var chats = App.Current.GetService<IChatRepository>();
        var artifacts = App.Current.GetService<IChatArtifactRepository>();
        var session = await chats.CreateSessionAsync("Originalbild-Vorschau-Smoke", _lifetime.Token);
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Bildvorschau", MessageStatus.Completed,
            cancellationToken: _lifetime.Token);
        var host = new Grid { Width = 720, HorizontalAlignment = HorizontalAlignment.Left };
        var releaseOldLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeArtifactLinks? links = null;
        try
        {
            var landscape = await ImportArtifactImageSmokeAsync(artifacts, message.Id, 4096, 1024, "Originalbild.png");
            var portrait = await ImportArtifactImageSmokeAsync(artifacts, message.Id, 1024, 4096, "Hochformat.png");
            JsonElement[] Payload(ChatArtifact artifact, string? name = null) =>
                [JsonSerializer.SerializeToElement(new { id = artifact.Id, fileName = name ?? artifact.FileName,
                    contentType = artifact.ContentType, length = artifact.Length, sha256 = artifact.Sha256 })];
            var opened = new List<(string Path, string Hash, uint Width, uint Height)>();
            links = new NativeArtifactLinks(Payload(landscape), message.Id)
            {
                OpenFileSmokeAdapter = async file =>
                {
                    var bytes = await File.ReadAllBytesAsync(file.Path, _lifetime.Token);
                    using var stream = await file.OpenAsync(FileAccessMode.Read).AsTask(_lifetime.Token);
                    var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(_lifetime.Token);
                    opened.Add((file.Path, Convert.ToHexStringLower(SHA256.HashData(bytes)),
                        decoder.OrientedPixelWidth, decoder.OrientedPixelHeight));
                    return true;
                },
            };
            host.Children.Add(links);
            MessagesPanel.Children.Add(host);
            await WaitArtifactImageSmokeAsync(() => Descendants(links).OfType<NativeArtifactImagePreview>().Any(view => view.HasImage));
            var preview = Descendants(links).OfType<NativeArtifactImagePreview>().Single();
            var originalSource = (BitmapImage)preview.Image.Source;
            Button CardButton(NativeArtifactLinks control, Guid id) => Descendants(control).OfType<Button>()
                .Single(button => AutomationProperties.GetAutomationId(button) == "artifact-" + id.ToString("N"));
            var button = CardButton(links, landscape.Id);
            // WinUI reports original image metadata through PixelWidth/Height;
            // DecodePixelWidth/Height independently bound its display decoder.
            if (originalSource.PixelWidth != 4096 || originalSource.PixelHeight != 1024
                || originalSource.DecodePixelWidth != 2048 || originalSource.DecodePixelHeight != 512
                || originalSource.DecodePixelType != DecodePixelType.Physical || preview.Image.Stretch != Stretch.Uniform)
                throw new InvalidOperationException($"Das vollständige Originalbild wurde nicht mit begrenzter Decodergröße angezeigt: Pixel={originalSource.PixelWidth}x{originalSource.PixelHeight}, Decode={originalSource.DecodePixelWidth}x{originalSource.DecodePixelHeight} ({originalSource.DecodePixelType}), Stretch={preview.Image.Stretch}, Layout={preview.ActualWidth}x{preview.ActualHeight}.");
            void AssertLandscapeFit()
            {
                host.UpdateLayout();
                if (preview.ActualWidth < 100 || preview.ActualWidth > host.ActualWidth
                    || preview.ActualHeight > 560 || Math.Abs(preview.ActualHeight * 4 - preview.ActualWidth) > 2)
                    throw new InvalidOperationException("Die Bildvorschau passt sich nicht mit unverändertem Seitenverhältnis an die Chatbreite an.");
            }
            AssertLandscapeFit();
            var wideWidth = preview.ActualWidth;
            await SaveMathPreviewAsync(host, "native-artifact-image-preview.png");
            host.Width = 360;
            await Task.Delay(100, _lifetime.Token); host.UpdateLayout();
            AssertLandscapeFit();
            var narrowWidth = preview.ActualWidth;
            if (narrowWidth >= wideWidth - 100)
                throw new InvalidOperationException("Die Bildvorschau hat auf eine schmalere Chatbreite nicht reagiert.");
            await SaveMathPreviewAsync(host, "native-artifact-image-narrow-preview.png");
            links.UpdateArtifacts(Payload(landscape));
            if (!ReferenceEquals(button, CardButton(links, landscape.Id))
                || !ReferenceEquals(originalSource, preview.Image.Source))
                throw new InvalidOperationException("Ein unverändertes Streaming-Update hat die Bildkarte oder ihre Quelle neu erzeugt.");
            var peer = new ButtonAutomationPeer(button);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await WaitArtifactImageSmokeAsync(() => opened.Count == 1);
            var openedImage = opened.Single();
            if (openedImage.Hash != landscape.Sha256 || openedImage.Width != 4096 || openedImage.Height != 1024
                || Path.GetFileName(openedImage.Path).StartsWith("thumbnail", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Der Öffnen-Pfad hat eine verkleinerte Vorschau statt des bytegleichen Originalbilds gewählt.");
            MessagesPanel.Children.Remove(host);
            await Task.Delay(80, _lifetime.Token);
            if (links.IsLoaded) throw new InvalidOperationException("Die Bildkarte wurde beim Sitzungswechsel nicht entladen.");
            MessagesPanel.Children.Add(host);
            await WaitArtifactImageSmokeAsync(() => links.IsLoaded);
            if (!ReferenceEquals(originalSource, preview.Image.Source))
                throw new InvalidOperationException("Ein Sitzungswechsel hat eine bereits geladene Vorschau verloren.");
            host.Width = 720;
            var oldLoadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var portraitLoads = 0;
            links.BeforePreviewSmokeAdapter = (id, _) =>
            {
                if (id == portrait.Id && ++portraitLoads == 1)
                {
                    oldLoadEntered.TrySetResult();
                    // Deliberately ignore cancellation here to prove that a
                    // late asynchronous completion cannot overwrite a new load.
                    return releaseOldLoad.Task;
                }
                return Task.CompletedTask;
            };
            links.UpdateArtifacts(Payload(portrait));
            await oldLoadEntered.Task.WaitAsync(TimeSpan.FromSeconds(15), _lifetime.Token);
            var portraitButton = CardButton(links, portrait.Id);
            links.UpdateArtifacts(Payload(portrait, "Aktualisiertes Hochformat.png"));
            await WaitArtifactImageSmokeAsync(() => Descendants(links).OfType<NativeArtifactImagePreview>().Any(view => view.HasImage));
            preview = Descendants(links).OfType<NativeArtifactImagePreview>().Single();
            var portraitSource = (BitmapImage)preview.Image.Source;
            if (portraitSource.PixelWidth != 1024 || portraitSource.PixelHeight != 4096
                || portraitSource.DecodePixelWidth != 512 || portraitSource.DecodePixelHeight != 2048
                || portraitSource.DecodePixelType != DecodePixelType.Physical
                || Math.Abs(preview.ActualHeight - 560) > 1 || preview.Image.Stretch != Stretch.Uniform
                || !ReferenceEquals(portraitButton, CardButton(links, portrait.Id)))
                throw new InvalidOperationException("Die Hochformat-Vorschau ist nicht vollständig, begrenzt oder stabil dargestellt.");
            releaseOldLoad.TrySetResult();
            await Task.Delay(150, _lifetime.Token); host.UpdateLayout();
            if (!ReferenceEquals(portraitSource, preview.Image.Source))
                throw new InvalidOperationException("Ein veralteter Bildladevorgang hat die aktuelle Vorschau überschrieben.");
            await SaveMathPreviewAsync(host, "native-artifact-image-portrait-preview.png");
            var denied = new NativeArtifactLinks(Payload(landscape), Guid.NewGuid())
            {
                OpenFileSmokeAdapter = _ => throw new InvalidOperationException("Ein fremdes Nachrichtenbild wurde zum Öffnen freigegeben."),
            };
            host.Children.Clear(); host.Children.Add(denied);
            await WaitArtifactImageSmokeAsync(() => Descendants(denied).OfType<TextBlock>()
                .Any(text => text.Text.Contains("gehört nicht zu dieser Nachricht", StringComparison.Ordinal)));
            var deniedButton = CardButton(denied, landscape.Id);
            ((IInvokeProvider)new ButtonAutomationPeer(deniedButton).GetPattern(PatternInterface.Invoke)).Invoke();
            await WaitArtifactImageSmokeAsync(() => Descendants(denied).OfType<InfoBar>().Any(bar => bar.IsOpen));
            if (opened.Count != 1 || Descendants(denied).OfType<NativeArtifactImagePreview>().Any(view => view.HasImage))
                throw new InvalidOperationException("Die Bildkarte hat die Zuordnung zur ursprünglichen Nachricht nicht durchgesetzt.");
            var recovery = await VerifyArtifactImageRecoverySmokeAsync(landscape, message.Id, host);
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-artifact-image-validation.json"),
                JsonSerializer.Serialize(new
                {
                    passed = true, processId = Environment.ProcessId, renderer = "WinUI3", originalWidth = 4096, originalHeight = 1024,
                    sourcePixelWidth = originalSource.PixelWidth, sourcePixelHeight = originalSource.PixelHeight,
                    decodeWidth = originalSource.DecodePixelWidth, decodeHeight = originalSource.DecodePixelHeight,
                    originalHash = landscape.Sha256, openedOriginalHash = openedImage.Hash,
                    openedOriginalWidth = openedImage.Width, openedOriginalHeight = openedImage.Height,
                    wideWidth, narrowWidth, maximumHeight = 560, preserveAspectRatio = true,
                    fullImageWithoutCropping = true, retainsViewDuringStreaming = true,
                    unloadReloadPreservesDecodedImage = true, staleCompletionIgnored = true,
                    owningMessageEnforced = true, externalApplicationStarted = false,
                    recovery,
                }), _lifetime.Token);
        }
        finally
        {
            releaseOldLoad.TrySetResult();
            MessagesPanel.Children.Remove(host);
            if (links is not null)
            {
                links.BeforePreviewSmokeAdapter = null;
                links.OpenFileSmokeAdapter = null;
            }
            await chats.DeleteSessionAsync(session.Id, CancellationToken.None);
        }
    }

    private async Task<object> VerifyArtifactImageRecoverySmokeAsync(ChatArtifact artifact, Guid messageId, Grid host)
    {
        JsonElement[] Payload(ChatArtifact item) => [JsonSerializer.SerializeToElement(new
        { id = item.Id, fileName = item.FileName, contentType = item.ContentType, length = item.Length, sha256 = item.Sha256 })];
        bool HasImage(NativeArtifactLinks card) => Descendants(card).OfType<NativeArtifactImagePreview>().Any(view => view.HasImage);
        bool HasFailure(NativeArtifactLinks card, string text) => Descendants(card).OfType<TextBlock>()
            .Any(label => label.Visibility == Visibility.Visible && label.Text.Contains(text, StringComparison.Ordinal));
        Button CardButton(NativeArtifactLinks card, Guid id) => Descendants(card).OfType<Button>()
            .Single(button => AutomationProperties.GetAutomationId(button) == "artifact-" + id.ToString("N"));
        void Mount(NativeArtifactLinks card) { host.Children.Clear(); host.Children.Add(card); }

        var automaticAttempts = 0;
        var automatic = new NativeArtifactLinks(Payload(artifact), messageId)
        {
            BeforePreviewSmokeAdapter = (_, _) =>
            {
                if (++automaticAttempts == 1)
                    throw new HttpRequestException("An error occurred while sending the request", null, HttpStatusCode.BadGateway);
                return Task.CompletedTask;
            },
        };
        Mount(automatic);
        await WaitArtifactImageSmokeAsync(() => HasFailure(automatic, "vorübergehend nicht erreichbar (HTTP 502)"));
        var retainedButton = CardButton(automatic, artifact.Id);
        automatic.UpdateArtifacts(Payload(artifact));
        await WaitArtifactImageSmokeAsync(() => HasImage(automatic));
        if (automaticAttempts != 2 || !ReferenceEquals(retainedButton, CardButton(automatic, artifact.Id)))
            throw new InvalidOperationException("Ein vorübergehender Bilddienstfehler wurde nicht automatisch in derselben Karte behoben.");

        var exhaustedAttempts = 0;
        var allowRecovery = false;
        var recoveryOpens = 0;
        var exhausted = new NativeArtifactLinks(Payload(artifact), messageId)
        {
            BeforePreviewSmokeAdapter = (_, _) =>
            {
                exhaustedAttempts++;
                if (!allowRecovery) throw new HttpRequestException("HTTP 502", null, HttpStatusCode.BadGateway);
                return Task.CompletedTask;
            },
            OpenFileSmokeAdapter = async file =>
            {
                var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(file.Path, _lifetime.Token)));
                if (hash != artifact.Sha256) throw new InvalidOperationException("Die Wiederherstellung hat nicht das Originalbild geöffnet.");
                recoveryOpens++;
                return true;
            },
        };
        Mount(exhausted);
        await WaitArtifactImageSmokeAsync(() => HasFailure(exhausted, "Bitte „Öffnen“ erneut versuchen."));
        if (exhaustedAttempts != 3 || HasImage(exhausted))
            throw new InvalidOperationException("Bildvorschau-Wiederholungen sind nicht auf einen Erstversuch und zwei weitere Versuche begrenzt.");
        for (var refresh = 0; refresh < 5; refresh++) exhausted.UpdateArtifacts(Payload(artifact));
        await Task.Delay(150, _lifetime.Token);
        if (exhaustedAttempts != 3) throw new InvalidOperationException("Ein Snapshot-Refresh hat ein erschöpftes Bildladebudget zurückgesetzt.");
        allowRecovery = true;
        var open = CardButton(exhausted, artifact.Id);
        ((IInvokeProvider)new ButtonAutomationPeer(open).GetPattern(PatternInterface.Invoke)).Invoke();
        await WaitArtifactImageSmokeAsync(() => HasImage(exhausted));
        if (recoveryOpens != 1 || exhaustedAttempts != 4 || !ReferenceEquals(open, CardButton(exhausted, artifact.Id)))
            throw new InvalidOperationException("Erfolgreiches Öffnen des Originals hat die zuvor fehlgeschlagene Vorschau nicht wiederhergestellt.");

        var missingAttempts = 0;
        var missing = new NativeArtifactLinks(Payload(artifact), messageId)
        {
            BeforePreviewSmokeAdapter = (_, _) =>
            {
                missingAttempts++;
                throw new HttpRequestException("HTTP 404", null, HttpStatusCode.NotFound);
            },
        };
        Mount(missing);
        await WaitArtifactImageSmokeAsync(() => HasFailure(missing, "nicht mehr auf dem Server verfügbar"));
        missing.UpdateArtifacts(Payload(artifact));
        await Task.Delay(1200, _lifetime.Token);
        if (missingAttempts != 1 || HasImage(missing))
            throw new InvalidOperationException("Ein dauerhaft fehlendes Originalbild wurde automatisch wiederholt.");

        var foreignAttempts = 0;
        var foreign = new NativeArtifactLinks(Payload(artifact), Guid.NewGuid())
        {
            BeforePreviewSmokeAdapter = (_, _) => { foreignAttempts++; return Task.CompletedTask; },
        };
        Mount(foreign);
        await WaitArtifactImageSmokeAsync(() => HasFailure(foreign, "gehört nicht zu dieser Nachricht"));
        foreign.UpdateArtifacts(Payload(artifact));
        if (foreignAttempts != 0 || HasImage(foreign))
            throw new InvalidOperationException("Die Retry-Logik hat eine fremde Nachricht bis zur Originalauflösung gelangen lassen.");

        var corruptBytes = System.Text.Encoding.UTF8.GetBytes("Native image smoke: invalid PNG payload.");
        await using var corruptContent = new MemoryStream(corruptBytes, writable: false);
        var corruptArtifact = await App.Current.GetService<IChatArtifactRepository>().ImportAsync(messageId,
            "native-corrupt-image-smoke-" + Guid.NewGuid().ToString("N"), "DefektesBild.png", "image/png",
            Convert.ToHexStringLower(SHA256.HashData(corruptBytes)), corruptBytes.Length,
            "native-image-smoke", null, null, corruptContent, _lifetime.Token);
        var corruptAttempts = 0;
        var corrupt = new NativeArtifactLinks(Payload(corruptArtifact), messageId)
        {
            BeforePreviewSmokeAdapter = (_, _) => { corruptAttempts++; return Task.CompletedTask; },
        };
        Mount(corrupt);
        await WaitArtifactImageSmokeAsync(() => HasFailure(corrupt, "Bildvorschau nicht verfügbar:"));
        corrupt.UpdateArtifacts(Payload(corruptArtifact));
        await Task.Delay(1200, _lifetime.Token);
        if (corruptAttempts != 1 || HasImage(corrupt))
            throw new InvalidOperationException("Eine beschädigte Bilddatei wurde automatisch erneut decodiert.");

        var cancelledAttempts = 0;
        var cancelled = new NativeArtifactLinks(Payload(artifact), messageId)
        {
            BeforePreviewSmokeAdapter = (_, _) =>
            {
                cancelledAttempts++;
                throw new HttpRequestException("An error occurred while sending the request");
            },
        };
        Mount(cancelled);
        await WaitArtifactImageSmokeAsync(() => HasFailure(cancelled, "Verbindung zum Missum-Bilddienst"));
        host.Children.Clear();
        await Task.Delay(1200, _lifetime.Token);
        if (cancelledAttempts != 1 || cancelled.IsLoaded || HasImage(cancelled))
            throw new InvalidOperationException("Ein entladener Bildkarten-Client hat seinen geplanten Netzwerk-Retry weiter ausgeführt.");
        return new
        {
            passed = true, networkFailureLocalized = true, automaticRetryRecovered = true, automaticAttempts,
            maximumAutomaticAttempts = 3, retryBudgetSurvivesSnapshots = true,
            openingRecoveredPreview = true, recoveryOpens, attemptsAfterSuccessfulOpen = exhaustedAttempts,
            permanent404Attempts = missingAttempts, foreignMessageOriginalAttempts = foreignAttempts,
            corruptImageAttempts = corruptAttempts, unloadedRetryAttempts = cancelledAttempts,
            externalApplicationStarted = false,
        };
    }

    private async Task WaitArtifactImageSmokeAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new InvalidOperationException("Die native Originalbild-Vorschau konnte im Smoke nicht rechtzeitig geladen werden.");
            await Task.Delay(30, _lifetime.Token);
            UpdateLayout();
        }
        UpdateLayout();
        await Task.Delay(50, _lifetime.Token);
        UpdateLayout();
    }

    private async Task<ChatArtifact> ImportArtifactImageSmokeAsync(IChatArtifactRepository artifacts, Guid messageId,
        uint width, uint height, string name)
    {
        var pixels = new byte[checked((int)(width * height * 4))];
        for (var y = 0u; y < height; y++)
        for (var x = 0u; x < width; x++)
        {
            var offset = checked((int)((y * width + x) * 4));
            pixels[offset] = (byte)(x * 255 / width);
            pixels[offset + 1] = (byte)(y * 255 / height);
            pixels[offset + 2] = x < width / 2 ? (byte)220 : (byte)40;
            pixels[offset + 3] = 255;
        }
        using var encoded = new MemoryStream();
        using (var stream = encoded.AsRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask(_lifetime.Token);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, width, height, 96, 96, pixels);
            await encoder.FlushAsync().AsTask(_lifetime.Token);
        }
        var bytes = encoded.ToArray();
        await using var content = new MemoryStream(bytes, writable: false);
        return await artifacts.ImportAsync(messageId, "native-image-smoke-" + Guid.NewGuid().ToString("N"), name,
            "image/png", Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, "native-image-smoke", null, null,
            content, _lifetime.Token);
    }
}
