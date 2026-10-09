using System.Globalization;
using Missum.Core.Coding;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace Missum.App.Controls;

/// <summary>A selectable, native unified diff with independent old and new line numbers.</summary>
public sealed partial class NativeDiffView : UserControl, IDisposable
{
    private readonly StackPanel _rows = new();
    private readonly bool _wrapLines;
    private readonly SolidColorBrush _neutralForeground = NativeThemeBrushes.Text;
    private readonly SolidColorBrush _mutedForeground = NativeThemeBrushes.MutedText;
    private readonly SolidColorBrush _neutralBackground = NativeThemeBrushes.Resource("MissumLayerBrush", 25);
    private readonly SolidColorBrush _addedForeground = NativeThemeBrushes.Resource("MissumSuccessBrush", Color.FromArgb(255, 0x31, 0xC7, 0x7D));
    private readonly SolidColorBrush _addedBackground = NativeThemeBrushes.Resource("MissumDiffAddedBackgroundBrush", Color.FromArgb(255, 0x13, 0x33, 0x25));
    private readonly SolidColorBrush _removedForeground = NativeThemeBrushes.Resource("MissumDangerBrush", Color.FromArgb(255, 0xFF, 0x62, 0x5A));
    private readonly SolidColorBrush _removedBackground = NativeThemeBrushes.Resource("MissumDiffRemovedBackgroundBrush", Color.FromArgb(255, 0x3B, 0x1D, 0x1A));
    private readonly SolidColorBrush _metaBackground = NativeThemeBrushes.Resource("MissumHoverBrush", 36);
    private string _diff = "";
    private CodingDiffDocument? _document;
    private readonly StackPanel _pager = new() { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(12, 8, 12, 8) };
    private readonly Button _previous = new() { Content = "Zurück" };
    private readonly Button _next = new() { Content = "Weiter" };
    private readonly TextBlock _pageStatus = new() { VerticalAlignment = VerticalAlignment.Center };
    private long _preparation;
    private CancellationTokenSource? _prepareCancellation;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private bool _disposed, _resumePreparation;
    public int CurrentPage { get; private set; }
    public int PageCount => _document?.PageCount ?? 0;
    public int RenderedLineCount { get; private set; }
    public bool IsPreparing { get; private set; }


    public NativeDiffView(string diff, bool wrapLines = false)
    {
        _wrapLines = wrapLines;
        _dispatcher = DispatcherQueue;
        Loaded += OnDiffLoaded;
        Unloaded += OnDiffUnloaded;
        _pager.Children.Add(_previous); _pager.Children.Add(_pageStatus); _pager.Children.Add(_next);
        _previous.Click += (_, _) => ShowPage(CurrentPage - 1);
        _next.Click += (_, _) => ShowPage(CurrentPage + 1);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        if (wrapLines)
        {
            // An enclosing review page owns scrolling. No nested viewer can
            // redirect wheel input or make the user scroll each file separately.
            Content = _rows;
            AutomationProperties.SetName(_rows, "Dateiänderungen als Git-Diff");
        }
        else
        {
            var scroll = new ScrollViewer
            {
                Content = _rows,
                MaxHeight = 420,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Enabled,
                VerticalScrollMode = ScrollMode.Enabled,
                Background = _neutralBackground,
            };
            AutomationProperties.SetName(scroll, "Dateiänderungen als Git-Diff");
            Content = scroll;
        }

        var copy = new MenuFlyoutItem { Text = "Gesamten Diff kopieren" };
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(_diff);
            Clipboard.SetContent(package);
        };
        var menu = new MenuFlyout();
        menu.Items.Add(copy);
        ContextFlyout = menu;
        UpdateDiff(diff);
    }

    /// <summary>Replaces the unified diff. Empty and incomplete diff fragments remain displayable.</summary>
    public void UpdateDiff(string diff)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(diff);
        if (_diff == diff && _rows.Children.Count > 0) return;
        _diff = diff; _document = null; CurrentPage = 0; RenderedLineCount = 0;
        var preparation = ++_preparation;
        _prepareCancellation?.Cancel(); _prepareCancellation?.Dispose(); _prepareCancellation = null;
        _rows.Children.Clear();
        if (diff.Length == 0) { IsPreparing = false; AddMeta("Keine Textänderungen verfügbar."); return; }
        if (diff.Length <= 64 * 1024)
        {
            _document = CodingDiffDocument.Parse(diff); IsPreparing = false; ShowPage(0); return;
        }
        // A large receipt is parsed on a worker. Only the requested page creates
        // XAML rows, so a 82,000-line diff cannot block the UI dispatcher.
        IsPreparing = true;
        AddMeta("Dateivergleich wird vorbereitet …");
        _prepareCancellation = new();
        _ = PrepareAsync(diff, preparation, _prepareCancellation.Token);
    }

    public void Deactivate()
    {
        _preparation++; _prepareCancellation?.Cancel(); _prepareCancellation?.Dispose(); _prepareCancellation = null;
        IsPreparing = false;
    }

    private void OnDiffUnloaded(object sender, RoutedEventArgs e)
    {
        // A temporarily detached view may be shown again. Release only this
        // display parser's token, and resume its current source on Loaded.
        _resumePreparation = IsPreparing;
        Deactivate();
    }

    private void OnDiffLoaded(object sender, RoutedEventArgs e)
    {
        if (_disposed || !_resumePreparation) return;
        _resumePreparation = false;
        _rows.Children.Clear();
        UpdateDiff(_diff);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _resumePreparation = false;
        Deactivate();
        Loaded -= OnDiffLoaded; Unloaded -= OnDiffUnloaded;
        _document = null;
        GC.SuppressFinalize(this);
    }

    private async Task PrepareAsync(string diff, long preparation, CancellationToken cancellationToken)
    {
        try
        {
            var document = await Task.Run(() => CodingDiffDocument.Parse(diff, cancellationToken), cancellationToken).ConfigureAwait(false);
            _dispatcher.TryEnqueue(() =>
            {
                if (preparation != _preparation) return;
                _prepareCancellation?.Dispose(); _prepareCancellation = null;
                _document = document; IsPreparing = false; ShowPage(0);
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (preparation != _preparation) return;
                _prepareCancellation?.Dispose(); _prepareCancellation = null;
                IsPreparing = false; _rows.Children.Clear(); AddMeta("Der Dateivergleich konnte nicht dargestellt werden: " + exception.Message);
            });
        }
    }

    public void ShowPage(int page)
    {
        if (_disposed) return;
        if (_document is null) return;
        CurrentPage = Math.Clamp(page, 0, _document.PageCount - 1);
        _rows.Children.Clear(); RenderedLineCount = 0;
        if (_document.PageCount > 1)
        {
            _pageStatus.Text = $"Seite {CurrentPage + 1:N0} / {_document.PageCount:N0} · {_document.LineCount:N0} Diffzeilen";
            _previous.IsEnabled = CurrentPage > 0; _next.IsEnabled = CurrentPage + 1 < _document.PageCount;
            _rows.Children.Add(_pager);
        }
        foreach (var line in _document.Page(CurrentPage))
        {
            RenderedLineCount++;
            var text = line.Text.Length <= 8192 ? line.Text : line.Text[..8192] + "\n[Sehr lange Zeile gekürzt; der vollständige Diff ist über das Kontextmenü kopierbar.]";
            var language = NativeCodeHighlighter.LanguageForPath(line.Path);
            switch (line.Kind)
            {
                case CodingDiffLineKind.Metadata: AddMeta(text); break;
                case CodingDiffLineKind.Added: AddLine(text, "+", line.OldLine, line.NewLine, _addedForeground, _addedBackground, language); break;
                case CodingDiffLineKind.Removed: AddLine(text, "−", line.OldLine, line.NewLine, _removedForeground, _removedBackground, language); break;
                default: AddLine(text, "", line.OldLine, line.NewLine, _neutralForeground, _neutralBackground, language); break;
            }
        }
        if (_document.LineCount == 0) AddMeta("Keine Textänderungen verfügbar.");
    }

    private void AddMeta(string text)
    {
        _rows.Children.Add(new Border
        {
            Background = _metaBackground,
            Padding = new Thickness(12, 4, 12, 4),
            Child = CodeText(text, _mutedForeground, _wrapLines),
        });
    }

    private void AddLine(string text, string marker, int? oldLine, int? newLine,
        SolidColorBrush foreground, SolidColorBrush background, string language)
    {
        var row = new Grid { Background = background, MinHeight = 24, Padding = new Thickness(0, 2, 12, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = _wrapLines ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

        AddCell(FormatLineNumber(oldLine), 0, _mutedForeground, TextAlignment.Right);
        AddCell(FormatLineNumber(newLine), 1, _mutedForeground, TextAlignment.Right);
        AddCell(marker, 2, foreground, TextAlignment.Center);
        AddCell(text, 3, foreground, TextAlignment.Left);
        _rows.Children.Add(row);

        void AddCell(string value, int column, SolidColorBrush brush, TextAlignment alignment)
        {
            var block = CodeText(value, brush, _wrapLines && column == 3);
            block.VerticalAlignment = VerticalAlignment.Top;
            block.TextAlignment = alignment;
            block.Margin = column < 2 ? new Thickness(2, 0, 9, 0) : new Thickness(0);
            block.IsTextSelectionEnabled = column == 3;
            if (column == 3) new NativeCodeHighlighter(block).UpdateText(value, language);
            Grid.SetColumn(block, column);
            row.Children.Add(block);
        }
    }

    private static TextBlock CodeText(string text, SolidColorBrush foreground, bool wrap = false) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Cascadia Mono"),
        FontSize = 13,
        LineHeight = 20,
        Foreground = foreground,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        IsTextSelectionEnabled = true,
    };

    private static string FormatLineNumber(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
}
