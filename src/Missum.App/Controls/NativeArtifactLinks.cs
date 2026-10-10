using System.Globalization;
using System.Net;
using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.System;

namespace Missum.App.Controls;

/// <summary>Native image previews and links to locally stored output files. Payload URLs are never opened.</summary>
public sealed partial class NativeArtifactLinks : StackPanel
{
    private const int MaximumPreviewRetries = 2;
    private readonly IChatArtifactRepository _artifacts;
    private readonly AssistantArtifactPreviewService _previews;
    private readonly Guid? _messageId;
    private readonly StackPanel _links = new() { Spacing = 6 };
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsClosable = true, IsOpen = false };
    private readonly Dictionary<Guid, ArtifactLinkView> _views = [];
    private readonly HashSet<Guid> _opening = [];
    private Func<StorageFile, Task<bool>>? _openFileSmokeAdapter;
    private Func<Guid, CancellationToken, Task>? _beforePreviewSmokeAdapter;

    internal Func<StorageFile, Task<bool>>? OpenFileSmokeAdapter
    {
        set { RequireSmokeAdapter(value); _openFileSmokeAdapter = value; }
    }

    internal Func<Guid, CancellationToken, Task>? BeforePreviewSmokeAdapter
    {
        set { RequireSmokeAdapter(value); _beforePreviewSmokeAdapter = value; }
    }

    private static void RequireSmokeAdapter(Delegate? adapter)
    {
        if (adapter is not null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MISSUM_SMOKE_INSTANCE_KEY")))
            throw new InvalidOperationException("Bildkarten-Testadapter sind ausschließlich im isolierten Portable-Smoke erlaubt.");
    }

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
        NativeNotice.Attach(_error);
        Children.Add(_links);
        Children.Add(_error);
        Loaded += (_, _) =>
        {
            if (!IsLoaded) return;
            foreach (var (id, view) in _views)
            {
                view.PreviewFailed = false;
                StartImagePreview(id, view);
            }
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            foreach (var view in _views.Values) view.CancelPreview();
        };
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
                AutomationProperties.SetAutomationId(view.Button, "artifact-" + id.ToString("N"));
                var currentView = view;
                // OpenArtifactAsync handles expected failures in the inline InfoBar.
                view.Button.Click += (_, _) => { _ = OpenArtifactAsync(id, currentView); };
            }
            long? length = artifact.TryGetProperty("length", out var size) && size.ValueKind == JsonValueKind.Number
                && size.TryGetInt64(out var count) && count >= 0 ? count : null;
            view.Update(ReadString(artifact, "fileName", "Erzeugte Datei"), ReadString(artifact, "contentType"), length,
                ReadString(artifact, "sha256"));
            StartImagePreview(id, view);
            desired.Add(view.Button);
        }
        foreach (var stale in _views.Keys.Where(id => !seen.Contains(id)).ToArray())
        {
            _views[stale].Dispose();
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

    private void StartImagePreview(Guid id, ArtifactLinkView view)
    {
        if (!IsLoaded || !view.IsImage || view.Preview.HasImage || view.PreviewLoading || view.PreviewFailed) return;
        var (version, token) = view.BeginPreview();
        _ = LoadImagePreviewAsync(id, view, version, token);
    }

    private async Task LoadImagePreviewAsync(Guid id, ArtifactLinkView view, long version, CancellationToken token)
    {
        try
        {
            for (var retry = 0; ; retry++)
            {
                try
                {
                    await LoadImagePreviewAttemptAsync(id, view, version, token);
                    return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    if (!IsCurrentPreview(id, view, version, token)) return;
                    var transient = IsTransientImageFailure(exception);
                    var message = DescribeImageFailure(exception);
                    if (!transient || retry >= MaximumPreviewRetries)
                    {
                        view.PreviewFailed = true;
                        view.Preview.ShowFailure(message + (transient ? "\nBitte „Öffnen“ erneut versuchen." : ""));
                        return;
                    }
                    var delay = TimeSpan.FromSeconds(1 << retry);
                    var seconds = (int)delay.TotalSeconds;
                    view.Preview.ShowFailure(message + $"\nErneuter Versuch in {seconds} {(seconds == 1 ? "Sekunde" : "Sekunden")} …");
                    await Task.Delay(delay, token);
                    if (!IsCurrentPreview(id, view, version, token)) return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { view.EndPreview(version); }
    }

    private async Task LoadImagePreviewAttemptAsync(Guid id, ArtifactLinkView view, long version, CancellationToken token)
    {
        // Image URLs and paths in a model payload never authorize access.
        // The repository and owning message select the original local blob.
        var sourceArtifact = await _artifacts.GetAsync(id, token)
            ?? throw new InvalidOperationException("Das Originalbild ist nicht mehr lokal vorhanden.");
        if (_messageId is { } expectedSourceMessage && sourceArtifact.MessageId != expectedSourceMessage)
            throw new InvalidOperationException("Das Bild gehört nicht zu dieser Nachricht.");
        if (!IsCurrentPreview(id, view, version, token)) return;
        var artifact = await _previews.ResolveOriginalArtifactAsync(id, token);
        if (_messageId is { } expectedMessage && artifact.MessageId != expectedMessage)
            throw new InvalidOperationException("Das Bild gehört nicht zu dieser Nachricht.");
        if (!IsCurrentPreview(id, view, version, token)) return;
        view.UpdateResolvedLabels(artifact.FileName, artifact.ContentType, artifact.Length);
        if (!artifact.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            view.DisableImagePreview();
            return;
        }
        if (_beforePreviewSmokeAdapter is { } beforePreview) await beforePreview(id, token);
        if (!IsCurrentPreview(id, view, version, token)) return;
        var path = await _previews.MaterializeOriginalAsync(artifact.Id, token);
        var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
        using var stream = await file.OpenAsync(FileAccessMode.Read).AsTask(token);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
        var width = decoder.OrientedPixelWidth;
        var height = decoder.OrientedPixelHeight;
        if (width == 0 || height == 0) throw new InvalidOperationException("Das Bild hat keine gültige Größe.");
        if (!IsCurrentPreview(id, view, version, token)) return;
        // Decode from the original, but bound display memory even for tall
        // or very large images. Opening retains the untouched original.
        var scale = Math.Min(1d, Math.Min(2048d / width, 2048d / height));
        var bitmap = new BitmapImage
        {
            DecodePixelType = DecodePixelType.Physical,
            DecodePixelWidth = Math.Max(1, (int)Math.Round(width * scale)),
            DecodePixelHeight = Math.Max(1, (int)Math.Round(height * scale)),
            CreateOptions = BitmapCreateOptions.IgnoreImageCache,
        };
        stream.Seek(0);
        await bitmap.SetSourceAsync(stream).AsTask(token);
        if (!IsCurrentPreview(id, view, version, token)) return;
        view.Preview.Show(bitmap, width, height, artifact.FileName);
    }

    private static bool IsTransientImageFailure(Exception exception) => exception switch
    {
        TimeoutException or OperationCanceledException => true,
        HttpRequestException http => http.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            || http.StatusCode is { } status && (int)status >= 500,
        _ => false,
    };

    private static string DescribeImageFailure(Exception exception) => exception switch
    {
        TimeoutException or OperationCanceledException => "Das Laden des Originalbilds hat zu lange gedauert. Der Missum-Bilddienst antwortet noch nicht.",
        HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone } =>
            "Das Originalbild ist nicht mehr auf dem Server verfügbar.",
        HttpRequestException { StatusCode: HttpStatusCode.BadGateway } =>
            "Der Missum-Bilddienst ist vorübergehend nicht erreichbar (HTTP 502).",
        HttpRequestException { StatusCode: HttpStatusCode.ServiceUnavailable } =>
            "Der Missum-Bilddienst startet noch oder ist vorübergehend nicht verfügbar (HTTP 503).",
        HttpRequestException { StatusCode: HttpStatusCode.GatewayTimeout or HttpStatusCode.RequestTimeout } =>
            "Der Missum-Bilddienst hat nicht rechtzeitig geantwortet. Bitte gleich noch einmal versuchen.",
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } =>
            "Der Missum-Bilddienst ist gerade ausgelastet. Bitte gleich noch einmal versuchen.",
        HttpRequestException { StatusCode: { } status } =>
            $"Das Originalbild konnte nicht vom Missum-Bilddienst geladen werden (HTTP {(int)status}).",
        HttpRequestException => "Die Verbindung zum Missum-Bilddienst konnte nicht hergestellt werden. Bitte die Verbindung zum Server prüfen.",
        _ => exception.Message,
    };

    private bool IsCurrentPreview(Guid id, ArtifactLinkView view, long version, CancellationToken token) =>
        !token.IsCancellationRequested && IsCurrent(id, view) && view.PreviewVersion == version;

    private async Task OpenArtifactAsync(Guid id, ArtifactLinkView view)
    {
        if (!_opening.Add(id)) return;
        view.Button.IsEnabled = false;
        _error.IsOpen = false;
        try
        {
            // Resolve the current record: names, paths and URLs supplied by a model
            // are not authority for filesystem access or the owning message.
            var sourceArtifact = await _artifacts.GetAsync(id)
                ?? throw new InvalidOperationException("Die erzeugte Datei ist nicht mehr lokal vorhanden.");
            if (_messageId is { } expectedSourceMessage && sourceArtifact.MessageId != expectedSourceMessage)
                throw new InvalidOperationException("Die erzeugte Datei gehört nicht zu dieser Nachricht.");
            if (!IsCurrent(id, view)) return;
            var artifact = await _previews.ResolveOriginalArtifactAsync(id, CancellationToken.None);
            if (_messageId is { } expectedMessage && artifact.MessageId != expectedMessage)
                throw new InvalidOperationException("Die erzeugte Datei gehört nicht zu dieser Nachricht.");
            if (!IsCurrent(id, view)) return;
            view.UpdateResolvedLabels(artifact.FileName, artifact.ContentType, artifact.Length);
            var path = await _previews.MaterializeOriginalAsync(artifact.Id, CancellationToken.None);
            var file = await StorageFile.GetFileFromPathAsync(path);
            if (!IsCurrent(id, view)) return;
            if (!await (_openFileSmokeAdapter is { } openFile ? openFile(file) : Launcher.LaunchFileAsync(file).AsTask()))
                throw new InvalidOperationException("Die Datei konnte nicht geöffnet werden. Prüfe, ob ein Standardprogramm für dieses Dateiformat eingerichtet ist.");
            if (IsCurrent(id, view) && view.IsImage && !view.Preview.HasImage)
            {
                // Resolving/opening the original can have started the gateway
                // or recovered a legacy upload after the preview exhausted its
                // limited retry budget. Reuse this card and the successful file.
                view.CancelPreview();
                view.PreviewFailed = false;
                StartImagePreview(id, view);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (IsCurrent(id, view))
            {
                _error.Title = "Datei konnte nicht geöffnet werden";
                _error.Message = DescribeImageFailure(exception);
                _error.IsOpen = true;
            }
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

    private sealed class ArtifactLinkView : IDisposable
    {
        private readonly FontIcon _icon = new() { Glyph = "\uE8A5", FontSize = 20, Foreground = NativeIconPalette.BrushFor("document"), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _name = new() { FontSize = 14, Foreground = NativeThemeBrushes.Text, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _details = new() { FontSize = 12, Foreground = NativeThemeBrushes.MutedText, TextTrimming = TextTrimming.CharacterEllipsis };
        private string? _previewSignature;
        private CancellationTokenSource? _previewCancellation;
        private bool _resolvedLabels;

        public ArtifactLinkView()
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(_icon);
            var labels = new StackPanel { Spacing = 3 };
            labels.Children.Add(_name); labels.Children.Add(_details);
            Grid.SetColumn(labels, 1); row.Children.Add(labels);
            var open = new TextBlock { Text = "Öffnen", FontSize = 12, Foreground = NativeThemeBrushes.MutedText, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(open, 2); row.Children.Add(open);
            var content = new Grid();
            content.RowDefinitions.Add(new() { Height = GridLength.Auto });
            content.RowDefinitions.Add(new() { Height = GridLength.Auto });
            content.Children.Add(Preview);
            Grid.SetRow(row, 1); content.Children.Add(row);
            Button = new Button
            {
                Content = content,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                BorderBrush = NativeThemeBrushes.Resource("MissumStrokeBrush", 55),
                Background = NativeThemeBrushes.Resource("MissumLayerBrush", 33),
            };
        }

        public Button Button { get; }
        internal NativeArtifactImagePreview Preview { get; } = new();
        internal bool IsImage { get; private set; }
        internal bool PreviewLoading { get; private set; }
        internal bool PreviewFailed { get; set; }
        internal long PreviewVersion { get; private set; }

        public void Update(string name, string contentType, long? length, string sha256)
        {
            var signature = name + "\0" + contentType + "\0" + length + "\0" + sha256;
            if (_previewSignature != signature)
            {
                _previewSignature = signature;
                CancelPreview();
                _resolvedLabels = false;
                IsImage = contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
                PreviewFailed = false;
                Preview.Reset(IsImage);
            }
            if (!_resolvedLabels) UpdateLabels(name, contentType, length);
        }

        internal (long Version, CancellationToken Token) BeginPreview()
        {
            CancelPreview();
            _previewCancellation = new CancellationTokenSource();
            PreviewLoading = true;
            Preview.Reset(IsImage);
            return (PreviewVersion, _previewCancellation.Token);
        }

        public void Dispose() => CancelPreview();

        internal void CancelPreview()
        {
            PreviewVersion++;
            _previewCancellation?.Cancel();
            _previewCancellation?.Dispose();
            _previewCancellation = null;
            PreviewLoading = false;
        }

        internal void EndPreview(long version)
        {
            if (PreviewVersion != version) return;
            _previewCancellation?.Dispose();
            _previewCancellation = null;
            PreviewLoading = false;
        }

        internal void DisableImagePreview()
        {
            IsImage = false;
            Preview.Reset(false);
        }

        internal void UpdateResolvedLabels(string name, string contentType, long? length)
        {
            _resolvedLabels = true;
            UpdateLabels(name, contentType, length);
        }

        public void UpdateLabels(string name, string contentType, long? length)
        {
            _name.Text = string.IsNullOrWhiteSpace(name) ? "Erzeugte Datei" : name;
            var iconKey = NativeIconPalette.FileKey(name);
            _icon.Foreground = NativeIconPalette.BrushFor(iconKey);
            _icon.Glyph = iconKey switch
            {
                "pdf" => "\uEA90", "image" => "\uEB9F", "audio" => "\uE767", "video" => "\uE714",
                "code" => "\uE943", _ => "\uE8A5",
            };
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

