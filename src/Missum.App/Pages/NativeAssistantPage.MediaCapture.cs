using Missum.App.Services;
using Missum.Core.Extensions;
using Missum.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private static DesktopScreenshotService Screenshots => App.Current.GetService<DesktopScreenshotService>();
    private static ScreenClipCaptureService ScreenClips => App.Current.GetService<ScreenClipCaptureService>();
    private static SystemAudioAnalysisCaptureService AudioCapture => App.Current.GetService<SystemAudioAnalysisCaptureService>();

    private async Task<bool> BeginSelectedMediaCaptureAsync(string? actionId, Guid sessionId) => actionId switch
    {
        BuiltInActionIds.ImageAnalysis => await CaptureScreenshotForAnalysisAsync(sessionId),
        BuiltInActionIds.VideoAnalysis => await CaptureVideoForAnalysisAsync(sessionId),
        BuiltInActionIds.AudioAnalysis => await CaptureSystemAudioForAnalysisAsync(sessionId),
        _ => true,
    };

    private async Task<bool> EnsureRequiredMediaCaptureAsync(Guid sessionId, string prompt, string? actionId)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            sessionId,
            prompt,
            extensionActionId = actionId,
        });
        var required = await _coordinator.GetRequiredMediaCaptureAsync(payload, _lifetime.Token);
        var requiredActionId = required switch
        {
            PromptTriggerAction.ImageAnalysis => BuiltInActionIds.ImageAnalysis,
            PromptTriggerAction.VideoAnalysis => BuiltInActionIds.VideoAnalysis,
            PromptTriggerAction.AudioAnalysis => BuiltInActionIds.AudioAnalysis,
            _ => null,
        };
        return requiredActionId is null || await BeginSelectedMediaCaptureAsync(requiredActionId, sessionId);
    }

    private async Task<bool> CaptureScreenshotForAnalysisAsync(Guid sessionId)
    {
        var target = await SelectCaptureTargetAsync(
            "Bild für die Analyse aufnehmen",
            "Wähle ein Fenster, einen Monitor oder den gesamten Desktop. Missum erstellt genau einen lokalen Screenshot und hängt ihn an diese Sitzung an.");
        if (target is null) return false;

        var screenshot = await Screenshots.CaptureAsync(target, _lifetime.Token);
        await using var stream = new MemoryStream(screenshot.Content, writable: false);
        await _coordinator.ImportAttachmentAsync(sessionId, screenshot.FileName, screenshot.ContentType, stream, _lifetime.Token);
        await FinishNativeMediaImportAsync(sessionId);
        return true;
    }

    private async Task<bool> CaptureVideoForAnalysisAsync(Guid sessionId)
    {
        var target = await SelectCaptureTargetAsync(
            "Video für die Analyse aufnehmen",
            "Wähle ein Fenster, einen Monitor oder den gesamten Desktop. Missum nimmt die Quelle lokal mit zwei Bildern pro Sekunde und höchstens 30 Sekunden ohne Ton auf.");
        if (target is null) return false;

        var capture = ScreenClips;
        await capture.StartAsync(sessionId, target, _lifetime.Token);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void Render(ScreenClipSnapshot value) => status.Text = $"{value.Status}\nQuelle: {value.SourceLabel}\nDauer: {value.ElapsedSeconds:00} / {value.MaximumSeconds:00} Sekunden";
        Render(capture.Current);
        EventHandler<ScreenClipSnapshot>? changed = (_, value) => DispatcherQueue.TryEnqueue(() => Render(value));
        capture.Changed += changed;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Bildschirmclip aufnehmen",
            Content = status,
            PrimaryButtonText = "Clip übernehmen",
            CloseButtonText = "Verwerfen",
            DefaultButton = ContentDialogButton.Primary,
        };
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                await capture.CancelAsync(CancellationToken.None);
                return false;
            }

            var clip = await capture.StopAsync(CancellationToken.None);
            try
            {
                await using var stream = await OpenCapturedMediaAsync(clip.Path, _lifetime.Token);
                await _coordinator.ImportAttachmentAsync(sessionId, clip.FileName, clip.ContentType, stream, _lifetime.Token);
            }
            finally { TryDeleteCapturedMedia(clip.Path); }
            await FinishNativeMediaImportAsync(sessionId);
            return true;
        }
        catch
        {
            await capture.CancelAsync(CancellationToken.None);
            throw;
        }
        finally { capture.Changed -= changed; }
    }

    private async Task<bool> CaptureSystemAudioForAnalysisAsync(Guid sessionId)
    {
        var capture = AudioCapture;
        await capture.StartAsync(sessionId, _lifetime.Token);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void Render(SystemAudioCaptureSnapshot value) => status.Text = $"{value.Status}\nQuelle: {value.SourceLabel}\nDauer: {value.ElapsedSeconds:00} / {value.MaximumSeconds:00} Sekunden";
        Render(capture.Current);
        EventHandler<SystemAudioCaptureSnapshot>? changed = (_, value) => DispatcherQueue.TryEnqueue(() => Render(value));
        capture.Changed += changed;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Systemaudio aufnehmen",
            Content = status,
            PrimaryButtonText = "Audio übernehmen",
            CloseButtonText = "Verwerfen",
            DefaultButton = ContentDialogButton.Primary,
        };
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                await capture.CancelAsync(CancellationToken.None);
                return false;
            }

            var audio = await capture.StopAsync(CancellationToken.None);
            await using var stream = new MemoryStream(audio.Content, writable: false);
            await _coordinator.ImportAttachmentAsync(sessionId, audio.FileName, audio.ContentType, stream, _lifetime.Token);
            await FinishNativeMediaImportAsync(sessionId);
            return true;
        }
        catch
        {
            await capture.CancelAsync(CancellationToken.None);
            throw;
        }
        finally { capture.Changed -= changed; }
    }

    private async Task<DesktopCaptureTarget?> SelectCaptureTargetAsync(string title, string description)
    {
        var targets = Screenshots.ListTargets();
        if (targets.Count == 0)
            throw new InvalidOperationException("Windows meldet keinen aufnehmbaren Bildschirm und kein aufnehmbares Fenster.");

        var selector = new ComboBox
        {
            Header = "Aufnahmequelle",
            ItemsSource = targets,
            DisplayMemberPath = nameof(DesktopCaptureTarget.DisplayName),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(selector);
        var result = await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = "Aufnehmen",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Primary,
        }.ShowAsync();
        return result == ContentDialogResult.Primary ? selector.SelectedItem as DesktopCaptureTarget : null;
    }

    private async Task FinishNativeMediaImportAsync(Guid sessionId)
    {
        if (_disposed || _session != sessionId) return;
        await SetInspectorVisibleAsync(true);
        await RefreshForExternalActivationAsync();
    }

    private static async Task<FileStream> OpenCapturedMediaAsync(string path, CancellationToken cancellationToken)
    {
        IOException? lastError = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1_048_576,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (IOException exception)
            {
                lastError = exception;
                await Task.Delay(100, cancellationToken);
            }
        }
        throw new IOException("Der fertiggestellte Bildschirmclip konnte nicht für die lokale Speicherung geöffnet werden.", lastError);
    }

    private static void TryDeleteCapturedMedia(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task CancelNativeMediaCaptureAsync()
    {
        try { await ScreenClips.CancelAsync(CancellationToken.None); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        try { await AudioCapture.CancelAsync(CancellationToken.None); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }
}
