using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Missum.App.Controls;
using Missum.App.Services.Meetings;
using Windows.System;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private Grid? _meetingChatRoot;
    private StackPanel? _meetingChatList;
    private ScrollViewer? _meetingChatScroll;
    private TextBox? _meetingChatInput;
    private Button? _meetingChatMic;
    private TextBlock? _meetingChatStatus;
    private MentionAutocomplete? _meetingChatMention;

    /// <summary>Who can be mentioned with "@": Missum first, then everyone else in the meeting.</summary>
    private List<MentionCandidate> MeetingMentionCandidates()
    {
        var list = new List<MentionCandidate> { new(MeetingRoom.AgentName, "AI-Assistent – antwortet auf deine Frage", MeetingAgentVisual.AccentBrush, true) };
        var room = _meetings?.Room;
        if (room is null) return list;
        var local = _meetings!.LocalParticipantId;
        foreach (var person in room.Read(s => s.Participants.Where(p => !p.IsAgent && p.Id != local)
                     .OrderByDescending(p => p.Connected).ThenBy(p => p.Name).Select(MeetingJson.Clone).ToList()))
            list.Add(new MentionCandidate(person.Name, person.Connected ? "Im Meeting" : "War im Meeting", ParticipantBrush(person.ColorIndex), false));
        return list;
    }
    private Grid? _meetingModelFooter;
    private NativeContextRing? _meetingContextRing;
    private TextBlock? _meetingModelLabel;
    private TextBlock? _meetingReasoningLabel;
    private Button? _meetingModelButton;

    /// <summary>Mirrors the composer's model display and shows Missum's last context use.</summary>
    private void UpdateMeetingModelFooter()
    {
        if (_meetingModelFooter is null || _meetingModelLabel is null || _meetingReasoningLabel is null) return;
        var host = IsMeetingsMode && _meetings?.View is { Role: "host", NeedsProject: false };
        _meetingModelFooter.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
        if (!host) return;
        _meetingModelLabel.Text = ModelLabel.Text;
        _meetingReasoningLabel.Text = ReasoningLabel.Text;
        var agent = _meetings!.Room?.Read(static s => (s.Agent.ContextUsed, s.Agent.ContextLimit));
        // Same wording as the prompt window's context ring.
        string description;
        if (agent is { } usage && usage.ContextLimit > 0)
        {
            _meetingContextRing!.Value = Math.Clamp(usage.ContextUsed * 100.0 / usage.ContextLimit, 0, 100);
            description = $"Kontext: {usage.ContextUsed:N0} / {usage.ContextLimit:N0} Token ({_meetingContextRing.Value:0.0} %)";
        }
        else description = "Kontext noch nicht verfügbar";
        if (_meetingContextToolTip is not null && !Equals(_meetingContextToolTip.Content, description)) _meetingContextToolTip.Content = description;
        if (_meetingContextRing?.Parent is FrameworkElement ring) Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ring, description);
    }

    private ToolTip? _meetingContextToolTip;
    internal string? MeetingContextDescription => _meetingContextToolTip?.Content as string;
    private string? _meetingChatSignature;

    private Grid MeetingChatRoot()
    {
        if (_meetingChatRoot is not null) return _meetingChatRoot;
        _meetingChatRoot = new Grid { RowSpacing = 10 };
        _meetingChatRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _meetingChatRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _meetingChatRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _meetingChatRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _meetingChatStatus = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("MissumMutedTextBrush", 160), Visibility = Visibility.Collapsed };
        Grid.SetRow(_meetingChatStatus, 1);
        _meetingChatRoot.Children.Add(_meetingChatStatus);

        _meetingChatList = new StackPanel { Spacing = 12, Padding = new Thickness(0, 0, 14, 8) };
        _meetingChatScroll = new ScrollViewer { Content = _meetingChatList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(_meetingChatScroll, 2);
        _meetingChatRoot.Children.Add(_meetingChatScroll);

        _meetingChatInput = PlainTextBox(14, "Nachricht an alle oder @Missum");
        _meetingChatInput.AcceptsReturn = true;
        _meetingChatInput.MaxHeight = 140;
        AutomationProperties.SetName(_meetingChatInput, "Nachricht an alle Teilnehmer");
        _meetingChatMention = new MentionAutocomplete(_meetingChatInput, MeetingMentionCandidates);
        _meetingChatInput.PreviewKeyDown += (_, args) =>
        {
            if (_meetingChatMention.HandleKey(args)) return;
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (args.Key == VirtualKey.Enter && !shift) { args.Handled = true; SendMeetingChat(); }
        };
        _meetingChatMic = new Button
        {
            Width = 32, Height = 32, Padding = new Thickness(0), CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            Content = new FontIcon { Glyph = "", FontSize = 15, Foreground = NativeIconPalette.BrushFor("speech") },
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        ToolTipService.SetToolTip(_meetingChatMic, "Nachricht diktieren");
        AutomationProperties.SetName(_meetingChatMic, "Nachricht diktieren");
        _meetingChatMic.Click += async (_, _) => await ToggleMeetingDictationAsync();
        var send = new Button
        {
            Width = 32, Height = 32, Padding = new Thickness(0), CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(0),
            Background = ThemeBrush("MissumAccentBrush", 170),
            Content = new FontIcon { Glyph = "", FontSize = 14, Foreground = ThemeBrush("MissumAccentTextBrush", 255) },
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        ToolTipService.SetToolTip(send, "Senden");
        AutomationProperties.SetName(send, "Nachricht senden");
        send.Click += (_, _) => SendMeetingChat();
        var inputGrid = new Grid { ColumnSpacing = 4 };
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputGrid.Children.Add(_meetingChatInput);
        Grid.SetColumn(_meetingChatMic, 1);
        Grid.SetColumn(send, 2);
        inputGrid.Children.Add(_meetingChatMic);
        inputGrid.Children.Add(send);
        var inputBorder = new Border
        {
            Background = ThemeBrush("MissumInputBrush", 36),
            BorderBrush = ThemeBrush("MissumStrokeBrush", 48),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(6, 4, 5, 4),
            Margin = new Thickness(0, 0, 14, 0),
            Child = inputGrid,
        };
        _meetingChatInput.GotFocus += (_, _) => inputBorder.BorderBrush = ThemeBrush("MissumAccentBrush", 0xB0);
        _meetingChatInput.LostFocus += (_, _) => inputBorder.BorderBrush = ThemeBrush("MissumStrokeBrush", 48);
        Grid.SetRow(inputBorder, 3);
        _meetingChatRoot.Children.Add(inputBorder);

        // Host only: the same model, reasoning and context picker as in Codex.
        _meetingModelFooter = new Grid { Margin = new Thickness(4, 0, 14, 0), ColumnSpacing = 6, Visibility = Visibility.Collapsed };
        _meetingModelFooter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _meetingModelFooter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _meetingContextRing = new NativeContextRing { Value = 0, Maximum = 100 };
        // A background makes the ring hoverable; the tooltip opens on hover like in the prompt window.
        var ringHost = new Border
        {
            Width = 28, Height = 28, Child = _meetingContextRing, VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        _meetingContextToolTip = new ToolTip { Content = "Kontext noch nicht verfügbar" };
        ToolTipService.SetToolTip(ringHost, _meetingContextToolTip);
        ringHost.PointerEntered += (_, _) => _meetingContextToolTip.IsOpen = true;
        ringHost.PointerExited += (_, _) => _meetingContextToolTip.IsOpen = false;
        _meetingModelLabel = new TextBlock { FontSize = 12, MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = ThemeBrush("MissumTextBrush", 240) };
        _meetingReasoningLabel = new TextBlock { FontSize = 12, Foreground = ThemeBrush("MissumAccentReadableBrush", 190) };
        var labels = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        labels.Children.Add(_meetingModelLabel);
        labels.Children.Add(_meetingReasoningLabel);
        labels.Children.Add(new FontIcon { Glyph = "\uE70D", FontSize = 8, Foreground = ThemeBrush("MissumMutedTextBrush", 160) });
        _meetingModelButton = new Button { Content = labels, HorizontalAlignment = HorizontalAlignment.Right };
        if (Resources.TryGetValue("ComposerModelButtonStyle", out var style) && style is Style buttonStyle) _meetingModelButton.Style = buttonStyle;
        ToolTipService.SetToolTip(_meetingModelButton, "Modell, Denkaufwand und Kontextlimit für Missum");
        AutomationProperties.SetName(_meetingModelButton, "Modell für Missum auswählen");
        _meetingModelButton.Click += OnReasoningClick;
        Grid.SetColumn(_meetingModelButton, 1);
        _meetingModelFooter.Children.Add(ringHost);
        _meetingModelFooter.Children.Add(_meetingModelButton);
        _meetingChatRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_meetingModelFooter, 4);
        _meetingChatRoot.Children.Add(_meetingModelFooter);
        UpdateMeetingModelFooter();
        return _meetingChatRoot;
    }

    private void RenderMeetingChat()
    {
        if (MeetingChatHost.Content is null) MeetingChatHost.Content = MeetingChatRoot();
        var root = MeetingChatRoot();
        _ = root;
        var room = Meetings.Room;
        var view = Meetings.View;
        var status = view.Connection switch
        {
            MeetingConnectionState.Reconnecting => "Verbindung wird wiederhergestellt …",
            MeetingConnectionState.Connecting => "Verbinde mit dem Meeting …",
            _ when view.NeedsProject => "Lege zuerst ein Projekt an, um den Meeting-Chat zu nutzen.",
            _ when room is not null && !Meetings.CanEdit => "Nicht verbunden – nur lesen.",
            _ => null,
        };
        _meetingChatStatus!.Text = status ?? "";
        _meetingChatStatus.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
        _meetingChatInput!.IsEnabled = Meetings.CanEdit;
        UpdateMeetingModelFooter();
        if (room is null) { _meetingChatList!.Children.Clear(); return; }

        var atBottom = _meetingChatScroll!.VerticalOffset >= _meetingChatScroll.ScrollableHeight - 24;
        {
            var (messages, participants) = room.Read(s => (s.Chat.TakeLast(400).ToList(), s.Participants.ToDictionary(p => p.Id, p => p.ColorIndex)));
            var signature = "c" + messages.Count + (messages.LastOrDefault()?.Id.ToString() ?? "");
            if (signature == _meetingChatSignature) return;
            _meetingChatSignature = signature;
            _meetingChatList!.Children.Clear();
            if (messages.Count == 0)
                _meetingChatList.Children.Add(EmptyChatHint("Schreibe allen eine Nachricht oder frag direkt: „@Missum, was ist noch offen?“"));
            Guid? previousAuthor = null;
            foreach (var message in messages)
            {
                var grouped = previousAuthor == message.AuthorId;
                _meetingChatList.Children.Add(ChatMessageRow(message, participants.GetValueOrDefault(message.AuthorId), message.AuthorId == Meetings.LocalParticipantId, grouped));
                previousAuthor = message.AuthorId;
            }
        }
        if (atBottom || _meetingChatScroll.ScrollableHeight <= 0)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _meetingChatScroll.UpdateLayout();
                _meetingChatScroll.ChangeView(null, _meetingChatScroll.ScrollableHeight, null, true);
            });
    }

    private static TextBlock EmptyChatHint(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 13,
        Margin = new Thickness(4, 8, 4, 0),
        Foreground = ThemeBrush("MissumMutedTextBrush", 150),
    };

    private static StackPanel ChatMessageRow(MeetingChatMessage message, int colorIndex, bool own, bool grouped)
    {
        var time = message.At.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
        if (message.IsAgent)
        {
            var agent = new StackPanel { Spacing = 3, Margin = new Thickness(2, grouped ? -6 : 0, 0, 0) };
            if (!grouped)
                agent.Children.Add(new TextBlock
                {
                    Text = $"Missum · {time}",
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeBrush("MissumAccentReadableBrush", 190),
                });
            agent.Children.Add(new TextBlock { Text = message.Text, TextWrapping = TextWrapping.Wrap, FontSize = 14, IsTextSelectionEnabled = true });
            return agent;
        }
        var bubble = new Border
        {
            Background = own ? ThemeBrush("MissumSelectionBrush", 50) : ThemeBrush("MissumLayerStrongBrush", 40),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(11, 7, 11, 8),
            HorizontalAlignment = own ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 250,
            Child = new TextBlock { Text = message.Text, TextWrapping = TextWrapping.Wrap, FontSize = 14, IsTextSelectionEnabled = true },
        };
        var stack = new StackPanel { Spacing = 4, Margin = new Thickness(0, grouped ? -6 : 0, 0, 0), HorizontalAlignment = own ? HorizontalAlignment.Right : HorizontalAlignment.Stretch };
        if (!grouped)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = own ? HorizontalAlignment.Right : HorizontalAlignment.Left };
            header.Children.Add(Avatar(new MeetingParticipant { Name = message.AuthorName, ColorIndex = colorIndex }, 18));
            header.Children.Add(new TextBlock { Text = own ? $"Du · {time}" : $"{message.AuthorName} · {time}", FontSize = 12, Foreground = ThemeBrush("MissumMutedTextBrush", 160), VerticalAlignment = VerticalAlignment.Center });
            stack.Children.Add(header);
        }
        stack.Children.Add(bubble);
        return stack;
    }

    private void SendMeetingChat()
    {
        if (_meetingChatInput is null || !Meetings.CanEdit) return;
        var text = _meetingChatInput.Text.Trim();
        if (text.Length == 0) return;
        Meetings.SendChat(text);
        _meetingChatInput.Text = "";
    }

    /// <summary>Dictation into the meeting chat uses Missum's existing voice control pipeline.</summary>
    private async Task ToggleMeetingDictationAsync()
    {
        try
        {
            if (_microphone.Current.IsRecording && _meetingDictation)
            {
                await StopNativeDictationAsync();
                _dictationSession = null;
                _meetingDictation = false;
            }
            else if (!_microphone.Current.IsRecording)
            {
                _meetingDictation = true;
                _dictationSession = _session;
                _dictationPrefix = _meetingChatInput?.Text ?? "";
                await StartNativeDictationAsync();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _meetingDictation = false;
            ShowMeetingError(exception.Message);
        }
        UpdateMeetingDictationButton();
    }

    private void UpdateMeetingDictationButton()
    {
        if (_meetingChatMic is null) return;
        var active = _meetingDictation && _dictationCapture is not null;
        _meetingChatMic.Background = active ? ThemeBrush("MissumAccentSubtleBrush", 50) : new SolidColorBrush(Colors.Transparent);
        ToolTipService.SetToolTip(_meetingChatMic, active ? "Diktieren beenden" : "Nachricht diktieren");
    }

    /// <summary>The text box that receives dictation: the meeting chat in Claude Meetings, otherwise the composer.</summary>
    private TextBox DictationTarget => _meetingDictation && _meetingChatInput is not null ? _meetingChatInput : Composer;
}
