using System.Text.Json;
using Missum.App.Services;
using Missum.Ai.Contracts;
using Missum.Core.Contracts;
using Missum.Core.Extensions;
using Missum.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private static readonly JsonSerializerOptions ImmediateResultJson = new() { WriteIndented = true };
    private bool _immediateActionBusy;

    /// <summary>Native host entry point for catalog actions; no browser bridge is involved.</summary>
    private async Task InvokeImmediateActionAsync(string actionId)
    {
        if (_immediateActionBusy || _disposed) return;
        _immediateActionBusy = true;
        try
        {
            var descriptor = Items(_snapshot, "actionDescriptors").FirstOrDefault(item => S(item, "actionId") == actionId);
            if (descriptor.ValueKind != JsonValueKind.Object || S(descriptor, "actionKind") != "immediate")
                throw new InvalidOperationException("Die Aktion ist für diesen Chat nicht verfügbar.");
            if (S(descriptor, "disabledReason") is { Length: > 0 } disabledReason)
                throw new InvalidOperationException(disabledReason);
            if (_session == Guid.Empty) throw new InvalidOperationException("Öffne zuerst einen Chat.");
            var sessionId = _session;
            switch (actionId)
            {
                case BuiltInActionIds.AttachFilesAndFolders:
                    ShowFileChoices();
                    break;
                case BuiltInActionIds.ExportChatPdf:
                    await ExportNativeChatPdfAsync(sessionId);
                    break;
                case BuiltInActionIds.LiveCaptions:
                    await ShowNativeLiveCaptionsAsync(sessionId);
                    break;
                default:
                    if (actionId.StartsWith("builtin.", StringComparison.Ordinal))
                        throw new InvalidOperationException("Für diese integrierte Aktion fehlt der native Handler.");
                    var responses = await RunImmediateCommandAsync("action.invoke", new { actionId, sessionId, arguments = new { } });
                    if (!responses.TryGetValue("action.completed", out var completed))
                        throw new InvalidOperationException("Die Erweiterung hat keinen Abschluss bestätigt.");
                    var result = completed.TryGetProperty("result", out var value) ? value : completed;
                    await AppendNativeExtensionResultAsync(sessionId, S(descriptor, "displayName", actionId), result);
                    break;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowImmediateActionError(exception.Message); }
        finally { _immediateActionBusy = false; }
    }

    private void ShowImmediateActionError(string message)
    {
        if (_disposed) return;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
        if (!_running) StatusText.Text = message;
    }

    private async Task<Dictionary<string, JsonElement>> RunImmediateCommandAsync(string type, object payload)
    {
        var responses = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var snapshotGeneration = _navigationState.Generation;
        await _coordinator.HandleAsync(new(1, type, Guid.NewGuid().ToString("N"), JsonSerializer.SerializeToElement(payload, JsonOptions)),
            async (eventType, data, requestId) =>
            {
                var json = JsonSerializer.SerializeToElement(data, JsonOptions);
                if (eventType == "host.error") throw new InvalidOperationException(S(json, "message", "Die Aktion ist fehlgeschlagen."));
                responses[eventType] = json;
                if (eventType is "state.snapshot" or "session.changed" or "document.changed")
                    await EmitAsync(eventType, data, requestId, snapshotGeneration);
            }, _lifetime.Token);
        return responses;
    }

    private static InfoBar ImmediateErrorBar() => new()
    {
        Severity = InfoBarSeverity.Error,
        IsClosable = true,
        IsOpen = false,
    };

    private static TextBlock ImmediateText(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
    };

    private static Button ImmediateButton(string text, Action action)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => action();
        return button;
    }

    private async Task ExportNativeChatPdfAsync(Guid sessionId)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"Missum-Chat-{DateTime.Now:yyyy-MM-dd-HHmm}",
            DefaultFileExtension = ".pdf",
        };
        picker.FileTypeChoices.Add("PDF-Dokument", new List<string> { ".pdf" });
        var window = App.Current.MainWindow ?? throw new InvalidOperationException("Das Anwendungsfenster ist nicht verfügbar.");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        var exporter = new NativeChatPdfExportService(App.Current.GetService<IConversationSnapshotRepository>(),
            App.Current.GetService<IChatRepository>(), App.Current.GetService<IChatArtifactRepository>(),
            App.Current.GetService<DocumentPdfExporter>());
        if (!_running) StatusText.Text = "Chat-PDF wird erstellt …";
        try
        {
            var receipt = await exporter.ExportAsync(sessionId, file.Path, _lifetime.Token);
            if (!_disposed && !_running) StatusText.Text = $"PDF gespeichert: {Path.GetFileName(receipt.SavedPath)}";
        }
        finally
        {
            if (!_disposed) await RefreshForExternalActivationAsync();
        }
    }

    private async Task AppendNativeExtensionResultAsync(Guid sessionId, string title, JsonElement result)
    {
        var text = result.ValueKind == JsonValueKind.String
            ? result.GetString() ?? ""
            : JsonSerializer.Serialize(result, ImmediateResultJson);
        await App.Current.GetService<IChatRepository>().AddMessageAsync(sessionId, ChatRole.Assistant,
            $"**{title}**\n\n{text}", MessageStatus.Completed, cancellationToken: _lifetime.Token);
        if (!_disposed && _session == sessionId)
        {
            StatusText.Text = $"{title} abgeschlossen";
            await RefreshForExternalActivationAsync();
            DispatcherQueue.TryEnqueue(() => OnScrollToBottom(this, new RoutedEventArgs()));
        }
    }

    private readonly SemaphoreSlim _captionUpdateGate = new(1, 1);
    private Guid? _captionSessionId, _captionMessageId;
    private bool _captionSubscribed;
    private bool _captionActive;
    private JsonElement _captionMessage;
    private DateTimeOffset _captionStartedAt;

    private async Task ShowNativeLiveCaptionsAsync(Guid sessionId)
    {
        var service = App.Current.GetService<SystemAudioCaptionService>();
        if (service.IsRunning) { await service.StopAsync(CancellationToken.None); return; }
        if (!_captionSubscribed)
        {
            service.Changed += OnNativeCaptionChanged;
            _captionSubscribed = true;
        }
        var message = await App.Current.GetService<IChatRepository>().AddMessageAsync(
            sessionId, ChatRole.Assistant, "", MessageStatus.Streaming, cancellationToken: _lifetime.Token);
        _captionSessionId = sessionId;
        _captionMessageId = message.Id;
        _captionStartedAt = DateTimeOffset.UtcNow;
        _captionActive = true;
        await PresentNativeCaptionAsync(new(true, "transcribe", "Live-Untertitel werden gestartet", "", null, null, null));
        try { await service.StartAsync(LiveCaptionMode.Transcribe, _lifetime.Token); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await PresentNativeCaptionAsync(service.Current with { IsActive = false, Error = exception.Message });
        }
    }

    private void OnNativeCaptionChanged(object? sender, SystemAudioCaptionSnapshot snapshot)
    {
        var owner = _captionMessageId;
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (owner != _captionMessageId || _disposed) return;
            try { await PresentNativeCaptionAsync(snapshot); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { ShowError(exception.Message); }
        });
    }

    private async Task PresentNativeCaptionAsync(SystemAudioCaptionSnapshot snapshot)
    {
        var sessionId = _captionSessionId;
        var messageId = _captionMessageId;
        if (sessionId is null || messageId is null) return;
        await _captionUpdateGate.WaitAsync();
        try
        {
            _captionActive = snapshot.IsActive;

            var text = snapshot.Transcript.Trim();
            if (!snapshot.IsActive && text.Length == 0 && string.IsNullOrWhiteSpace(snapshot.Error))
                text = "Es wurde kein Sprachinhalt erkannt.";
            var status = snapshot.IsActive ? MessageStatus.Streaming : snapshot.Error is { Length: > 0 } ? MessageStatus.Failed : MessageStatus.Completed;
            await App.Current.GetService<IChatRepository>().UpdateMessageAsync(messageId.Value, text, status, snapshot.Error, CancellationToken.None);
            _captionMessage = JsonSerializer.SerializeToElement(new
            {
                id = messageId, sessionId, role = "assistant", content = text,
                status = status.ToString().ToLowerInvariant(), error = snapshot.Error,
                liveStatus = snapshot.IsActive ? snapshot.Status : "", model = "Live-Untertitel",
                createdAt = _captionStartedAt, updatedAt = DateTimeOffset.UtcNow,
            }, JsonOptions);
            if (_disposed || _session != sessionId) return;
            _messages[messageId.Value.ToString()] = _captionMessage;
            UpdateCaptionChip();
            SetRunning();
            RenderMessages();
        }
        finally { _captionUpdateGate.Release(); }
    }

    private void UpdateCaptionChip()
    {
        CaptionChip.Visibility = _captionActive && _captionSessionId == _session ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnStopCaptions(object sender, RoutedEventArgs e)
    {
        try { await App.Current.GetService<SystemAudioCaptionService>().StopAsync(CancellationToken.None); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowError(exception.Message); }
    }
}
