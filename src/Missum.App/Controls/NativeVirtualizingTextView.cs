using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Controls;

/// <summary>Bounded, selectable original-source paragraphs for a large expanded detail.</summary>
internal sealed class NativeVirtualizingTextView : ContentControl
{
    internal sealed record SourceRange(int Start, string Source);
    private sealed class PreparationState
    {
        internal NativeTextBlockIndex Index { get; } = new();
        internal bool Preparing { get; set; }
    }
    private PreparationState? _state;
    private string _source = "";
    private readonly NativeVirtualizingBlockPanel _panel;
    internal int LogicalCount => _panel.LogicalCount;
    internal int RealizedCount => _panel.RealizedCount;
    internal bool IsPreparing => _state?.Preparing == true;

    internal NativeVirtualizingTextView(ScrollViewer scroll, Func<bool>? pinSelection, Action<Action>? project)
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _panel = new NativeVirtualizingBlockPanel { ScrollOwner = scroll, PinSelection = pinSelection, Project = project };
        Content = _panel;
        Loaded += (_, _) => PrepareLatest();
        Unloaded += (_, _) => _state = null;
    }

    internal void UpdateText(string source)
    {
        if (_source == source && _state is not null) { _panel.QueueRefresh(); return; }
        _source = source;
        PrepareLatest();
    }

    private void PrepareLatest()
    {
        _state ??= new();
        if (_state.Preparing) return;
        _state.Preparing = true;
        _ = PrepareAsync(_state, _source);
    }

    private async Task PrepareAsync(PreparationState state, string source)
    {
        var blocks = await Task.Run(() =>
        {
            state.Index.Update(source);
            return state.Index.Blocks.ToArray();
        }).ConfigureAwait(false);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            state.Preparing = false;
            if (!ReferenceEquals(state, _state)) return;
            if (_source != source) { PrepareLatest(); return; }
            var rows = blocks.Select(block => new NativeVirtualizingBlockPanel.Row(
                "source:" + block.Start, block.Source, Math.Max(38, block.Source.Length / 85d * 26 + 12),
                () => new ContentControl { Content = CreateSourceBlock(block.Source, new SourceRange(block.Start, block.Source)),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch },
                view => UpdateSourceBlock((ContentControl)view, block.Source, new SourceRange(block.Start, block.Source)))).ToArray();
            if (_panel.Project is { } project) project(() => _panel.SetRows(rows));
            else _panel.SetRows(rows);
        });
    }

    internal static FrameworkElement CreateSourceBlock(string source, object anchor, NativeStreamingMarkdown.PreparedSource? prepared = null)
    {
        if (source.Length <= 16_384) return prepared is not null
            ? new NativeStreamingMarkdown(prepared) { Tag = anchor } : new NativeStreamingMarkdown(source) { Tag = anchor };
        // A fenced program or malformed/unclosed formula can be one atomic
        // source block. Page that exceptional block rather than instantiate an
        // unbounded native TextBlock; the original source remains copyable.
        var fenced = source.TrimStart().StartsWith("```", StringComparison.Ordinal)
            || source.TrimStart().StartsWith("~~~", StringComparison.Ordinal);
        var card = NativeToolResultView.CodeCard(fenced ? "Quelltext" : "Langer Textabschnitt", source, fenced ? "plain" : "text");
        card.Tag = anchor;
        return card;
    }

    internal static void UpdateSourceBlock(ContentControl host, string source, object anchor, NativeStreamingMarkdown.PreparedSource? prepared = null)
    {
        if (host.Content is NativeStreamingMarkdown markdown && source.Length <= 16_384)
        { markdown.Tag = anchor; if (prepared is not null) markdown.UpdatePreparedSource(prepared); else markdown.UpdateText(source); }
        else host.Content = CreateSourceBlock(source, anchor, prepared);
    }
}
