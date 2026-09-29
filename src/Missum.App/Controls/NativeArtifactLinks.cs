using System.Globalization;
using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.System;
using Windows.UI;

namespace Missum.App.Controls;

/// <summary>Native links to locally stored output files. Payload URLs are never opened.</summary>
public sealed partial class NativeArtifactLinks : StackPanel
{
    private readonly IChatArtifactRepository _artifacts;
    private readonly AssistantArtifactPreviewService _previews;
    private readonly Guid? _messageId;
    private readonly StackPanel _links = new() { Spacing = 6 };
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsClosable = true, IsOpen = false };
    private readonly Dictionary<Guid, ArtifactLinkView> _views = [];
    private readonly HashSet<Guid> _opening = [];

    /// <param name="artifactsArray">The message's artifacts JSON array, or an absent/null value.</param>
    /// <param name="messageId">Owning message, checked against the repository before opening a file.</param>
    public NativeArtifactLinks(JsonElement artifactsArray, Guid? messageId = null)
        : this(ReadArray(artifactsArray), messageId) { }

    public NativeArtifactLinks(JsonElement[] artifacts, Guid? messageId = null)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        _artifacts = App.Current.GetService<IChatArtifactRepository>();
        _previews = App.Current.GetService<AssistantArtifactPreviewService>();
        _messageId = messageId;
        Spacing = 8;
        Children.Add(_links);
        Children.Add(_error);
        UpdateArtifacts(artifacts);
    }

    public void UpdateArtifacts(JsonElement artifactsArray) => UpdateArtifacts(ReadArray(artifactsArray));

    /// <summary>Updates labels while preserving existing buttons during response streaming.</summary>
    public void UpdateArtifacts(JsonElement[] artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        var desired = new List<Button>();
        var seen = new HashSet<Guid>();
        foreach (var artifact in artifacts)
        {
            if (!Guid.TryParse(ReadString(artifact, "id"), out var id) || id == Guid.Empty || !seen.Add(id)) continue;
            if (!_views.TryGetValue(id, out var view))
            {
                view = new ArtifactLinkView();
                _views.Add(id, view);
                var currentView = view;
                // OpenArtifactAsync handles expected failures in the inline InfoBar.
                view.Button.Click += (_, _) => { _ = OpenArtifactAsync(id, currentView); };
            }
            long? length = artifact.TryGetProperty("length", out var size) && size.ValueKind == JsonValueKind.Number
                && size.TryGetInt64(out var count) && count >= 0 ? count : null;
            view.Update(ReadString(artifact, "fileName", "Erzeugte Datei"), ReadString(artifact, "contentType"), length);
            desired.Add(view.Button);
        }
        foreach (var stale in _views.Keys.Where(id => !seen.Contains(id)).ToArray())
        {
            _links.Children.Remove(_views[stale].Button);
            _views.Remove(stale);
        }
        for (var index = 0; index < desired.Count; index++)
        {
            if (index < _links.Children.Count && ReferenceEquals(_links.Children[index], desired[index])) continue;
            _links.Children.Remove(desired[index]);
            _links.Children.Insert(index, desired[index]);
        }
        Visibility = desired.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task OpenArtifactAsync(Guid id, ArtifactLinkView view)
    {
        if (!_opening.Add(id)) return;
        view.Button.IsEnabled = false;
        _error.IsOpen = false;
        try
        {
            // Resolve the current record: names, paths and URLs supplied by a model
            // are not authority for filesystem access or the owning message.
            var artifact = await _artifacts.GetAsync(id)
                ?? throw new InvalidOperationException("Die erzeugte Datei ist nicht mehr lokal vorhanden.");
            if (_messageId is { } expectedMessage && artifact.MessageId != expectedMessage)
                throw new InvalidOperationException("Die erzeugte Datei gehört nicht zu dieser Nachricht.");
            if (!IsCurrent(id, view)) return;
            view.Update(artifact.FileName, artifact.ContentType, artifact.Length);
            var path = await _previews.MaterializeOriginalAsync(artifact.Id, CancellationToken.None);
            var file = await StorageFile.GetFileFromPathAsync(path);
            if (!IsCurrent(id, view)) return;
            if (!await Launcher.LaunchFileAsync(file))
                throw new InvalidOperationException("Die Datei konnte nicht geöffnet werden. Prüfe, ob ein Standardprogramm für dieses Dateiformat eingerichtet ist.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _error.Title = "Datei konnte nicht geöffnet werden";
            _error.Message = exception.Message;
            _error.IsOpen = true;
        }
        finally
        {
            _opening.Remove(id);
            view.Button.IsEnabled = true;
        }
    }

    private bool IsCurrent(Guid id, ArtifactLinkView view) => IsLoaded
        && _views.TryGetValue(id, out var current) && ReferenceEquals(current, view);

    private static JsonElement[] ReadArray(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];

    private static string ReadString(JsonElement value, string property, string fallback = "") =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString() ?? fallback : fallback;

    private static SolidColorBrush Gray(byte value) => new(Color.FromArgb(255, value, value, value));

    private sealed class ArtifactLinkView
    {
        private readonly TextBlock _name = new() { FontSize = 14, Foreground = Gray(235), TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _details = new() { FontSize = 12, Foreground = Gray(160), TextTrimming = TextTrimming.CharacterEllipsis };

        public ArtifactLinkView()
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(new FontIcon { Glyph = "\uE8A5", FontSize = 20, Foreground = Gray(195), VerticalAlignment = VerticalAlignment.Center });
            var labels = new StackPanel { Spacing = 3 };
            labels.Children.Add(_name); labels.Children.Add(_details);
            Grid.SetColumn(labels, 1); row.Children.Add(labels);
            var open = new TextBlock { Text = "Öffnen", FontSize = 12, Foreground = Gray(190), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(open, 2); row.Children.Add(open);
            Button = new Button
            {
                Content = row,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                BorderBrush = Gray(55),
                Background = Gray(33),
            };
        }

        public Button Button { get; }

        public void Update(string name, string contentType, long? length)
        {
            _name.Text = string.IsNullOrWhiteSpace(name) ? "Erzeugte Datei" : name;
            var extension = Path.GetExtension(name).TrimStart('.');
            var format = extension.Length is > 0 and <= 12 && extension.All(char.IsLetterOrDigit)
                ? extension.ToUpperInvariant() : string.IsNullOrWhiteSpace(contentType) ? "Datei" : contentType;
            _details.Text = length is { } bytes ? format + " · " + FormatSize(bytes) : format;
            AutomationProperties.SetName(Button, $"{_name.Text}, {_details.Text}, öffnen");
            ToolTipService.SetToolTip(Button, _name.Text + "\n" + _details.Text);
        }

        private static string FormatSize(long bytes) => bytes switch
        {
            < 1024 => bytes.ToString("N0", CultureInfo.CurrentCulture) + " B",
            < 1024 * 1024 => (bytes / 1024d).ToString("N1", CultureInfo.CurrentCulture) + " KB",
            < 1024L * 1024 * 1024 => (bytes / (1024d * 1024)).ToString("N1", CultureInfo.CurrentCulture) + " MB",
            _ => (bytes / (1024d * 1024 * 1024)).ToString("N1", CultureInfo.CurrentCulture) + " GB",
        };
    }
}

