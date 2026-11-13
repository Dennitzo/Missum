using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private string? _composerAttachmentsSignature;
    private readonly List<Button> _composerAttachmentRemoveButtons = [];
    private readonly Dictionary<Guid, int> _composerAttachmentImports = [];
    // A submitted prompt owns its attachments from that moment on. The server consumes the rows only
    // when the run starts executing, so the composer hides exactly the submitted chips itself until the
    // next authoritative snapshot agrees.
    private Guid _submittedAttachmentOwner;
    private readonly HashSet<string> _submittedAttachmentIds = new(StringComparer.OrdinalIgnoreCase);

    private void RenderComposerAttachments(JsonElement data)
    {
        if (!_running && !_sendPending) ClearSubmittedAttachmentSuppression();
        var submitted = Items(data, "submittedDocumentIds").Select(item => item.GetString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = Items(data, "documents").Where(item => !submitted.Contains(S(item, "id"))).Select(item => (Item: item, Document: true))
            .Concat(Items(data, "attachments").Select(item => (Item: item, Document: false))).ToArray();
        if (_submittedAttachmentOwner == _session && _submittedAttachmentIds.Count > 0)
            entries = entries.Where(entry => !_submittedAttachmentIds.Contains(S(entry.Item, "id"))).ToArray();
        var signature = _session + "|" + ThemeBrush("MissumTextBrush", 220).Color + "|"
            + string.Join("|", entries.Select(entry => (entry.Document ? "doc:" : "file:") + S(entry.Item, "id")
                + ":" + S(entry.Item, "fileName") + ":" + S(entry.Item, "length") + ":" + S(entry.Item, "preparationStatus")));
        if (_composerAttachmentsSignature != signature)
        {
            _composerAttachmentsSignature = signature;
            ComposerAttachmentsPanel.Children.Clear();
            _composerAttachmentRemoveButtons.Clear();
            var owner = _session;
            foreach (var entry in entries)
            {
                if (!Guid.TryParse(S(entry.Item, "id"), out var id)) continue;
                var name = S(entry.Item, "fileName", "Datei");
                var mediaType = S(entry.Item, "contentType");
                var kind = entry.Document ? "Dokument" : mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "Bild" : "Datei";
                var size = long.TryParse(S(entry.Item, "length"), out var bytes)
                    ? bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.#} MB" : $"{Math.Max(1, bytes / 1024d):0.#} KB" : "";
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(new FontIcon { Glyph = kind == "Bild" ? "\uE91B" : "\uE8A5", FontSize = 16,
                    Foreground = ThemeBrush("MissumTextBrush", 220), VerticalAlignment = VerticalAlignment.Center });
                var description = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                description.Children.Add(new TextBlock { Text = name, FontSize = 12, MaxWidth = 220,
                    TextTrimming = TextTrimming.CharacterEllipsis, Foreground = ThemeBrush("MissumTextBrush", 220) });
                description.Children.Add(new TextBlock { Text = string.Join(" · ", new[] { kind, size }.Where(value => value.Length > 0)),
                    FontSize = 11, Foreground = ThemeBrush("MissumMutedTextBrush", 150) });
                row.Children.Add(description);
                var remove = new Button { Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
                    MinWidth = 24, MinHeight = 24, Padding = new Thickness(4), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(remove, name + " entfernen");
                ToolTipService.SetToolTip(remove, "Anhang entfernen");
                var document = entry.Document;
                remove.Click += async (_, _) =>
                {
                    // Removable during a run: the running prompt already works on its frozen copy.
                    if (_disposed || _session != owner) return;
                    remove.IsEnabled = false;
                    try
                    {
                        if (await CommandAsync(document ? "document.remove" : "attachment.remove",
                            document ? new { documentId = id } : (object)new { attachmentId = id }))
                            await RefreshForExternalActivationAsync();
                    }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
                    catch (Exception exception) when (exception is not OutOfMemoryException) { ShowError(exception.Message); }
                    finally { if (!_disposed) UpdateComposerAttachmentControls(); }
                };
                _composerAttachmentRemoveButtons.Add(remove);
                row.Children.Add(remove);
                var chip = new Border { Child = row, Padding = new Thickness(10, 6, 6, 6), CornerRadius = new CornerRadius(9),
                    Background = ThemeBrush("MissumLayerStrongBrush", 44), Tag = id.ToString("D") };
                AutomationProperties.SetName(chip, name + ", " + kind + ", " + size);
                ToolTipService.SetToolTip(chip, name);
                ComposerAttachmentsPanel.Children.Add(chip);
            }
        }
        UpdateComposerAttachmentControls();
    }

    private void UpdateComposerAttachmentControls()
    {
        if (ComposerAttachmentsHost is null) return;
        var busy = _composerAttachmentImports.GetValueOrDefault(_session) > 0;
        var hasFiles = ComposerAttachmentsPanel.Children.Count > 0;
        ComposerAttachmentStatus.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ComposerAttachmentsScroll.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        ComposerAttachmentsHost.Visibility = ActiveSubagent is null && (busy || hasFiles) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in _composerAttachmentRemoveButtons)
        {
            // An attachment stays withdrawable while the AI works, because that run reads the copy it
            // received at send time. Only a submission in flight or a running import has to wait.
            button.IsEnabled = !_sendPending && !busy;
            ToolTipService.SetToolTip(button, "Anhang entfernen");
        }
    }

    /// <summary>Hides the chips of a prompt or steering prompt the moment it is submitted.</summary>
    private Guid[] ClearComposerAttachmentsAfterSubmit()
    {
        if (_submittedAttachmentOwner != _session) _submittedAttachmentIds.Clear();
        _submittedAttachmentOwner = _session;
        var ids = ComposerAttachmentsPanel.Children.OfType<Border>().Select(chip => Guid.Parse((string)chip.Tag)).ToArray();
        foreach (var id in ids) _submittedAttachmentIds.Add(id.ToString("D"));
        ComposerAttachmentsPanel.Children.Clear();
        _composerAttachmentRemoveButtons.Clear();
        // The next authoritative snapshot has to rebuild the row, including chips a failed send left behind.
        _composerAttachmentsSignature = null;
        UpdateComposerAttachmentControls();
        return ids;
    }

    /// <summary>A submission that never reached the assistant keeps its attachments in the composer.</summary>
    private void RestoreComposerAttachmentsAfterSubmit(Guid[] ids)
    {
        foreach (var id in ids) _submittedAttachmentIds.Remove(id.ToString("D"));
        _composerAttachmentsSignature = null;
    }

    private void ClearSubmittedAttachmentSuppression()
    {
        _submittedAttachmentOwner = Guid.Empty;
        _submittedAttachmentIds.Clear();
    }

    private async Task RefreshComposerAfterImportAsync(Guid ownerSession)
    {
        if (_disposed || _session != ownerSession) return;
        try { await RefreshForExternalActivationAsync(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { if (!_disposed) ShowError(exception.Message); }
    }

    private void SetComposerAttachmentImportBusy(Guid sessionId, bool busy)
    {
        var count = Math.Max(0, _composerAttachmentImports.GetValueOrDefault(sessionId) + (busy ? 1 : -1));
        if (count == 0) _composerAttachmentImports.Remove(sessionId); else _composerAttachmentImports[sessionId] = count;
        if (!_disposed) UpdateComposerAttachmentControls();
    }
}
