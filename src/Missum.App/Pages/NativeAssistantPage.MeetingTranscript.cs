using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Missum.App.Services.Meetings;

namespace Missum.App.Pages;

/// <summary>
/// The Transcript tab: everything said in the lobby, by participant, as a chat window without a
/// prompt. Your own words sit right in a bubble like your messages in ChatGPT, everyone else on
/// the left with name and time. Consecutive sentences of one speaker form one block; new text is
/// revealed as it arrives, and the view follows the newest line while you are at the bottom.
/// </summary>
public sealed partial class NativeAssistantPage
{
    private Grid? _transcriptRoot;
    private ScrollViewer? _transcriptScroll;
    private StackPanel? _transcriptList;
    private TextBlock? _transcriptTitle;
    private Border? _transcriptEmpty;
    private Guid _transcriptRoom;
    private readonly HashSet<Guid> _transcriptShown = [];
    private (Guid Speaker, StackPanel Lines)? _transcriptLastBlock;
    private readonly Queue<(TextBlock Target, string Text)> _transcriptReveal = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _transcriptRevealTimer;
    private bool _meetingTranscriptDirty = true;

    private Grid MeetingTranscriptRoot()
    {
        if (_transcriptRoot is not null) return _transcriptRoot;
        _transcriptRoot = new Grid();
        _transcriptRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _transcriptRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new StackPanel { Spacing = 4, Padding = new Thickness(40, 22, 40, 10) };
        _transcriptTitle = new TextBlock { FontSize = 30, FontWeight = Microsoft.UI.Text.FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
        header.Children.Add(_transcriptTitle);
        header.Children.Add(new TextBlock { Text = "Live-Transkript der Lobby, nach Teilnehmer", FontSize = 13, Foreground = ThemeBrush("MissumMutedTextBrush", 160) });
        _transcriptRoot.Children.Add(header);

        _transcriptList = new StackPanel { Spacing = 18, MaxWidth = 780, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0, 8, 0, 40) };
        _transcriptEmpty = new Border
        {
            Padding = new Thickness(0, 60, 0, 0),
            Child = new TextBlock
            {
                Text = "Noch nichts gesprochen. Sobald jemand spricht, erscheint es hier live.",
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeBrush("MissumMutedTextBrush", 150),
            },
        };
        var column = new Grid { Padding = new Thickness(40, 0, 40, 0) };
        column.Children.Add(_transcriptEmpty);
        column.Children.Add(_transcriptList);
        _transcriptScroll = new ScrollViewer
        {
            Content = column,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        AutomationProperties.SetName(_transcriptScroll, "Transkript");
        Grid.SetRow(_transcriptScroll, 1);
        _transcriptRoot.Children.Add(_transcriptScroll);
        return _transcriptRoot;
    }

    private void RenderMeetingTranscript()
    {
        if (_meetings?.Room is not { } room || _transcriptList is null || _transcriptScroll is null) return;
        var snapshot = room.Snapshot();
        _transcriptTitle!.Text = MeetingSessionTitle(snapshot);
        // Another meeting (or a fresh snapshot without our lines): start over.
        if (snapshot.Id != _transcriptRoom || snapshot.Transcript.Count < _transcriptShown.Count)
        {
            _transcriptRoom = snapshot.Id;
            _transcriptShown.Clear();
            _transcriptList.Children.Clear();
            _transcriptLastBlock = null;
            _transcriptReveal.Clear();
        }
        var firstFill = _transcriptShown.Count == 0;
        var atBottom = _transcriptScroll.ScrollableHeight - _transcriptScroll.VerticalOffset < 60;
        var added = false;
        foreach (var entry in snapshot.Transcript.OrderBy(t => t.At))
        {
            if (!_transcriptShown.Add(entry.Id)) continue;
            AppendTranscriptLine(entry, snapshot, animate: !firstFill);
            added = true;
        }
        _transcriptEmpty!.Visibility = _transcriptShown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (added && (atBottom || firstFill))
        {
            _transcriptList.UpdateLayout();
            _transcriptScroll.ChangeView(null, _transcriptScroll.ScrollableHeight, null, disableAnimation: firstFill);
        }
    }

    private void AppendTranscriptLine(MeetingTranscriptEntry entry, MeetingRoomState snapshot, bool animate)
    {
        var self = entry.SpeakerId == Meetings.LocalParticipantId;
        var line = new TextBlock
        {
            Text = animate ? "" : entry.Text,
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = ThemeBrush("MissumTextBrush", 240),
        };
        ToolTipService.SetToolTip(line, entry.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        if (animate) QueueTranscriptReveal(line, entry.Text);

        // Consecutive sentences of one speaker continue the same block.
        if (_transcriptLastBlock is { } last && last.Speaker == entry.SpeakerId)
        {
            last.Lines.Children.Add(line);
            return;
        }
        var lines = new StackPanel { Spacing = 6 };
        lines.Children.Add(line);
        _transcriptLastBlock = (entry.SpeakerId, lines);
        var person = snapshot.Participants.FirstOrDefault(p => p.Id == entry.SpeakerId)
            ?? new MeetingParticipant { Id = entry.SpeakerId, Name = entry.SpeakerName, ColorIndex = 0 };
        var time = entry.At.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        if (self)
        {
            var bubble = new Border
            {
                Child = lines,
                Padding = new Thickness(16, 10, 16, 11),
                CornerRadius = new CornerRadius(18),
                Background = ThemeBrush("MissumLayerStrongBrush", 50),
                HorizontalAlignment = HorizontalAlignment.Right,
                MaxWidth = 560,
            };
            var mine = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
            mine.Children.Add(new TextBlock { Text = "Du · " + time, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, Foreground = ThemeBrush("MissumMutedTextBrush", 150) });
            mine.Children.Add(bubble);
            _transcriptList!.Children.Add(mine);
            return;
        }
        var block = new Grid { ColumnSpacing = 12 };
        block.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        block.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var avatar = Avatar(person, 28);
        avatar.VerticalAlignment = VerticalAlignment.Top;
        block.Children.Add(avatar);
        var body = new StackPanel { Spacing = 4 };
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        name.Children.Add(new TextBlock { Text = person.Name, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        name.Children.Add(new TextBlock { Text = time, FontSize = 12, Foreground = ThemeBrush("MissumMutedTextBrush", 150), VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(name);
        body.Children.Add(lines);
        Grid.SetColumn(body, 1);
        block.Children.Add(body);
        _transcriptList!.Children.Add(block);
    }

    /// <summary>Reveals a new line like streamed text: about 1.2 s per line, never slower than 40 characters a second.</summary>
    private void QueueTranscriptReveal(TextBlock target, string text)
    {
        _transcriptReveal.Enqueue((target, text));
        if (_transcriptRevealTimer is null)
        {
            _transcriptRevealTimer = DispatcherQueue.CreateTimer();
            _transcriptRevealTimer.Interval = TimeSpan.FromMilliseconds(30);
            _transcriptRevealTimer.Tick += (_, _) => RunMeetingCallback("Transcript.Reveal", RevealTranscriptStep);
        }
        if (!_transcriptRevealTimer.IsRunning) _transcriptRevealTimer.Start();
    }

    private void RevealTranscriptStep()
    {
        if (_transcriptReveal.Count == 0) { _transcriptRevealTimer?.Stop(); return; }
        var (target, text) = _transcriptReveal.Peek();
        var step = Math.Max(2, text.Length / 40);
        var next = Math.Min(text.Length, target.Text.Length + step);
        target.Text = text[..next];
        if (next >= text.Length) _transcriptReveal.Dequeue();
        // Keep following the newest line while the reader is at the bottom.
        if (_transcriptScroll is { } scroll && scroll.ScrollableHeight - scroll.VerticalOffset < 80)
            scroll.ChangeView(null, scroll.ScrollableHeight, null, disableAnimation: true);
    }

    /// <summary>True when every transcript line is fully shown (smoke tests).</summary>
    internal bool TranscriptRevealDone => _transcriptReveal.Count == 0;
    internal int TranscriptLinesShown => _transcriptShown.Count;
}
