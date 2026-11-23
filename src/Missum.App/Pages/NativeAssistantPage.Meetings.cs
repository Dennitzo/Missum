using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Missum.App.Controls;
using Missum.App.Services.Meetings;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace Missum.App.Pages;

/// <summary>
/// Claude Meetings mode: three native tabs (Meeting, Notes, Whiteboard), the
/// meeting chat in the right sidebar and a compact camera overlay on the
/// document tabs. All state lives in <see cref="MeetingSessionService"/>; this
/// partial only renders it and turns user input into meeting operations.
/// </summary>
public sealed partial class NativeAssistantPage
{
    private const string MeetingsMode = "claudemeetings";
    private const double MeetingChatWidth = 320;
    private MeetingSessionService? _meetings;
    private MeetingTab _meetingTab = MeetingTab.Meeting;
    private readonly Dictionary<MeetingTab, Button> _meetingTabButtons = [];
    private DispatcherQueueTimer? _meetingRenderTimer;
    private bool _meetingStageDirty = true;
    private bool _meetingNotesDirty = true;
    private bool _meetingBoardDirty = true;
    private bool _meetingChatDirty = true;
    private bool _meetingOverlayDirty = true;
    private bool _meetingChatRequested = true;
    private bool _meetingActivationBusy;
    private Guid _meetingActivatedSession;
    private MeetingLink? _meetingJoinAfterSession;
    private readonly Dictionary<Guid, MeetingVideoSource> _meetingVideo = [];
    private readonly Dictionary<Guid, double> _meetingLevels = [];
    private bool _meetingDictation;

    private enum MeetingTab { Meeting, Notes, Whiteboard }

    private bool IsMeetingsMode => _mode == MeetingsMode;

    private MeetingSessionService Meetings
    {
        get
        {
            if (_meetings is not null) return _meetings;
            _meetings = App.Current.GetService<MeetingSessionService>();
            _meetings.Changed += OnMeetingChanged;
            _meetings.SnapshotChanged += OnMeetingSnapshot;
            _meetings.OperationApplied += OnMeetingOperation;
            _meetings.VideoFrame += OnMeetingVideo;
            _meetings.MicrophoneLevel += OnMeetingMicrophoneLevel;
            _meetings.ParticipantLevel += OnMeetingLevel;
            _meetings.JoinRequested += OnMeetingJoinRequested;
            MeetingsHost.SizeChanged += (_, _) => MarkMeetingsDirty(overlay: true);
            _meetingRenderTimer = DispatcherQueue.CreateTimer();
            _meetingRenderTimer.Interval = TimeSpan.FromMilliseconds(110);
            _meetingRenderTimer.Tick += (_, _) => RunUiCallback("Meetings.Render", RenderMeetingsIfDirty);
            _meetingRenderTimer.Start();
            return _meetings;
        }
    }

    /// <summary>Called after every coordinator snapshot, next to the Science sync.</summary>
    private void SyncMeetingsSession()
    {
        if (!IsMeetingsMode)
        {
            if (MeetingsHost.Visibility == Visibility.Visible) HideMeetingsView();
            var hadMeetingTabs = false;
            foreach (var button in _meetingTabButtons.Values) hadMeetingTabs |= SessionTabsPanel.Children.Remove(button);
            UpdateResponsiveLayout();
            if (hadMeetingTabs) RenderSessionTabs();
            return;
        }
        var service = Meetings;
        if (BodyGrid.Visibility == Visibility.Visible || MeetingsHost.Visibility != Visibility.Visible) ShowMeetingsView();
        if (_session != Guid.Empty && service.SessionId != _session && !_meetingActivationBusy)
            _ = ActivateMeetingSessionAsync(_session);
        if (service.PendingJoin is not null && !_meetingActivationBusy) OnMeetingJoinRequested();
        UpdateResponsiveLayout();
        RenderSessionTabs();
    }

    private async Task ActivateMeetingSessionAsync(Guid sessionId)
    {
        _meetingActivationBusy = true;
        try
        {
            var title = Items(_snapshot, "sessions").Where(s => S(s, "id") == sessionId.ToString())
                .Select(s => S(s, "title")).FirstOrDefault();
            var workspace = S(_snapshot, "workspacePath");
            await Meetings.ActivateAsync(sessionId, title, string.IsNullOrWhiteSpace(workspace) ? null : workspace, _lifetime.Token);
            _meetingActivatedSession = sessionId;
            if (_meetingJoinAfterSession is { } link && sessionId == _session)
            {
                _meetingJoinAfterSession = null;
                await JoinMeetingAsync(link);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowMeetingError(exception.Message); }
        finally
        {
            _meetingActivationBusy = false;
            MarkMeetingsDirty(all: true);
            // The user may have switched sessions while this one was loading.
            if (!_disposed && IsMeetingsMode && Meetings.SessionId != _session && _session != Guid.Empty)
                _ = ActivateMeetingSessionAsync(_session);
        }
    }

    private void OnMeetingJoinRequested()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_disposed || _meetingActivationBusy) return;
            var link = Meetings.TakePendingJoin();
            if (link is null) return;
            try
            {
                if (!IsMeetingsMode)
                {
                    _meetingJoinAfterSession = link;
                    await NavigateAsync("mode.switch", new { chatMode = MeetingsMode });
                    return;
                }
                await JoinMeetingAsync(link);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException) { ShowMeetingError(exception.Message); }
        });
    }

    /// <summary>
    /// Joins inside the current session when it is still an empty meeting;
    /// otherwise a fresh Claude Meetings session (in the same project) is created first.
    /// </summary>
    private async Task JoinMeetingAsync(MeetingLink link)
    {
        var room = Meetings.Room;
        var workspace = S(_snapshot, "workspacePath");
        var hasProject = !string.IsNullOrWhiteSpace(workspace) && Directory.Exists(workspace);
        if (!hasProject)
        {
            // A guest's copy lives in a project folder too: pick one first, then join there.
            _meetingJoinAfterSession = link;
            OnAddProject(this, new RoutedEventArgs());
            return;
        }
        var empty = room?.Read(static s => !s.IsLive && s.Chat.Count == 0 && s.Transcript.Count == 0
            && s.Whiteboard.Notes.Count == 0 && s.Whiteboard.Elements.Count == 0 && s.Documents.Sum(d => d.Blocks.Count) <= 1) ?? true;
        if (!empty || Meetings.SessionId != _session)
        {
            _meetingJoinAfterSession = link;
            await NavigateAsync("session.projectCreate", new { workspacePath = workspace, chatMode = MeetingsMode });
            return;
        }
        _meetingTab = MeetingTab.Meeting;
        var workspacePath = S(_snapshot, "workspacePath");
        await Meetings.JoinAsync(_session, string.IsNullOrWhiteSpace(workspacePath) ? null : workspacePath, link, _lifetime.Token);
        await Meetings.SetMicrophoneAsync(true);
        MarkMeetingsDirty(all: true);
    }

    private void ShowMeetingsView()
    {
        HideWorkingPlanView();
        HidePlanModeTab();
        HideToolView();
        HideSubagentOverview();
        _activeSourcesSession = null; _sourcesHost.Visibility = Visibility.Collapsed;
        HideResearchView();
        SaveReviewScrollOffset();
        _activeReviewRunId = null;
        ReviewScroll.Visibility = Visibility.Collapsed;
        BodyGrid.Visibility = Visibility.Collapsed;
        MeetingsHost.Visibility = Visibility.Visible;
        MarkMeetingsDirty(all: true);
    }

    private void HideMeetingsView()
    {
        MeetingsHost.Visibility = Visibility.Collapsed;
        MeetingsOverlayHost.Visibility = Visibility.Collapsed;
        MeetingChatHost.Visibility = Visibility.Collapsed;
        InspectorScroll.Visibility = Visibility.Visible;
        // Leaving Claude Meetings: bring the chat body back (ShowMeetingsView collapsed it).
        ShowChatView();
    }

    private void SelectMeetingTab(MeetingTab tab)
    {
        if (!IsMeetingsMode) return;
        _meetingTab = tab;
        ShowMeetingsView();
        RenderSessionTabs();
    }

    private int RenderMeetingTab(SessionViewKind view, int position)
    {
        var tab = view switch
        {
            SessionViewKind.Meeting => MeetingTab.Meeting,
            SessionViewKind.Notes => MeetingTab.Notes,
            _ => MeetingTab.Whiteboard,
        };
        if (!_meetingTabButtons.TryGetValue(tab, out var button))
        {
            var (glyph, title, help) = tab switch
            {
                MeetingTab.Meeting => ("", "Meeting", "Kameras, Steuerung und Einladungslink"),
                MeetingTab.Notes => ("", "Notes", "Gemeinsames Dokument mit Missums Live-Notizen"),
                _ => ("", "Whiteboard", "Gemeinsames Whiteboard mit Haftnotizen"),
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            content.Children.Add(CreateTabIcon(glyph));
            content.Children.Add(new TextBlock { Text = title, FontSize = 13 });
            button = TabButton();
            button.Content = content;
            button.Height = 32;
            button.Padding = new Thickness(10, 0, 10, 0);
            button.Click += (_, _) => SelectMeetingTab(tab);
            AutomationProperties.SetName(button, title);
            ToolTipService.SetToolTip(button, help);
            _meetingTabButtons[tab] = button;
        }
        var active = _meetingTab == tab && MeetingsHost.Visibility == Visibility.Visible;
        ApplyTabAppearance(button, active);
        SetTabEnabled(button, !_sessionTabNavigationBusy);
        SetTabHelp(button, active ? "Aktiver Tab" : "Tab öffnen");
        PositionTab(button, position);
        return position + 1;
    }

    private void MarkMeetingsDirty(bool all = false, bool stage = false, bool notes = false, bool board = false, bool chat = false, bool overlay = false)
    {
        _meetingStageDirty |= all || stage;
        _meetingNotesDirty |= all || notes;
        _meetingBoardDirty |= all || board;
        _meetingChatDirty |= all || chat;
        _meetingOverlayDirty |= all || overlay;
    }

    private void OnMeetingChanged() => DispatcherQueue.TryEnqueue(() => MarkMeetingsDirty(stage: true, chat: true, overlay: true, notes: true, board: true));
    private void OnMeetingSnapshot() => DispatcherQueue.TryEnqueue(() => MarkMeetingsDirty(all: true));

    private void OnMeetingOperation(MeetingOp op) => DispatcherQueue.TryEnqueue(() =>
    {
        switch (op.Type)
        {
            case MeetingOpTypes.ChatAdd: MarkMeetingsDirty(chat: true); break;
            case MeetingOpTypes.TranscriptAdd: MarkMeetingsDirty(chat: true); break;
            case MeetingOpTypes.AgentStatus: MarkMeetingsDirty(stage: true, overlay: true, chat: true); break;
            case MeetingOpTypes.Presence: MarkMeetingsDirty(notes: true, board: true); break;
            case MeetingOpTypes.ParticipantJoin or MeetingOpTypes.ParticipantLeave or MeetingOpTypes.ParticipantMedia:
                MarkMeetingsDirty(stage: true, overlay: true, chat: true); break;
            case MeetingOpTypes.RoomLive or MeetingOpTypes.RoomTitle: MarkMeetingsDirty(all: true); break;
            default:
                if (op.Type.StartsWith("wb.", StringComparison.Ordinal)) MarkMeetingsDirty(board: true);
                else MarkMeetingsDirty(notes: true);
                break;
        }
    });

    private void RenderMeetingsIfDirty()
    {
        if (_disposed || !IsMeetingsMode || _meetings is null) return;
        DecayMeetingLevels();
        if (MeetingsHost.Visibility != Visibility.Visible) return;
        if (_meetings.View.NeedsProject)
        {
            if (_meetingStageDirty || _meetingNotesDirty || _meetingBoardDirty || _meetingOverlayDirty)
            {
                _meetingStageDirty = _meetingNotesDirty = _meetingBoardDirty = _meetingOverlayDirty = false;
                MeetingsContent.Content = ProjectRequiredCard();
                MeetingsOverlayHost.Visibility = Visibility.Collapsed;
            }
            if (_meetingChatDirty && MeetingChatHost.Visibility == Visibility.Visible) { _meetingChatDirty = false; RenderMeetingChat(); }
            return;
        }
        if (_meetingStageDirty || _meetingNotesDirty || _meetingBoardDirty)
        {
            var content = _meetingTab switch
            {
                MeetingTab.Meeting => (UIElement)MeetingStageRoot(),
                MeetingTab.Notes => MeetingNotesRoot(),
                _ => MeetingBoardRoot(),
            };
            if (!ReferenceEquals(MeetingsContent.Content, content)) MeetingsContent.Content = content;
        }
        if (_meetingTab == MeetingTab.Meeting && _meetingStageDirty) { _meetingStageDirty = false; RenderMeetingStage(); }
        if (_meetingTab == MeetingTab.Notes && _meetingNotesDirty) { _meetingNotesDirty = false; RenderMeetingNotes(); }
        if (_meetingTab == MeetingTab.Whiteboard && _meetingBoardDirty) { _meetingBoardDirty = false; RenderMeetingBoard(); }
        if (_meetingOverlayDirty) { _meetingOverlayDirty = false; RenderMeetingOverlay(); }
        if (_meetingChatDirty && MeetingChatHost.Visibility == Visibility.Visible) { _meetingChatDirty = false; RenderMeetingChat(); }
    }

    /// <summary>Meetings live in a project folder; without one every tab explains how to start.</summary>
    private Border ProjectRequiredCard()
    {
        var stack = new StackPanel { Spacing = 14 };
        stack.Children.Add(new FontIcon { Glyph = "\uE8B7", FontSize = 36, Foreground = NativeIconPalette.BrushFor("project") });
        stack.Children.Add(new TextBlock { Text = "Projekt erforderlich", FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock
        {
            Text = "Ein Meeting speichert Notes, Whiteboard und Chat im Projektordner. Wähle zuerst einen Projektordner; danach kannst du das Meeting starten oder per Link beitreten.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = ThemeBrush("MissumMutedTextBrush", 160),
        });
        stack.Children.Add(PrimaryMeetingButton("Projekt hinzufügen", "\uE8F4", () =>
        {
            OnAddProject(this, new RoutedEventArgs());
            return Task.CompletedTask;
        }));
        return new Border
        {
            MaxWidth = 480,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = ThemeBrush("MissumLayerBrush", 32),
            BorderBrush = ThemeBrush("MissumStrokeBrush", 48),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(28, 26, 28, 26),
            Child = stack,
        };
    }

    private void ShowMeetingError(string message)
    {
        if (_disposed) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            _meetingError = message;
            MarkMeetingsDirty(stage: true, overlay: true, chat: true);
        });
    }

    private string? _meetingError;

    private string? CurrentMeetingError => _meetingError ?? _meetings?.View.Error;

    // ── Video ────────────────────────────────────────────────────────────

    private void OnMeetingVideo(Guid participant, byte[] jpeg) => DispatcherQueue.TryEnqueue(() => _ = DecodeMeetingFrameAsync(participant, jpeg));

    private async Task DecodeMeetingFrameAsync(Guid participant, byte[] jpeg)
    {
        if (_disposed) return;
        if (!_meetingVideo.TryGetValue(participant, out var video))
            _meetingVideo[participant] = video = new MeetingVideoSource();
        if (video.Decoding) return;
        video.Decoding = true;
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(jpeg.AsBuffer());
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            await video.Source.SetBitmapAsync(bitmap);
            var first = video.LastFrame == default;
            video.LastFrame = DateTimeOffset.UtcNow;
            if (first) MarkMeetingsDirty(stage: true, overlay: true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { _ = exception; }
        finally { video.Decoding = false; }
    }

    private SoftwareBitmapSource? MeetingVideoFor(Guid participant, bool cameraOn) =>
        cameraOn && _meetingVideo.TryGetValue(participant, out var video) && DateTimeOffset.UtcNow - video.LastFrame < TimeSpan.FromSeconds(4)
            ? video.Source
            : null;

    private void OnMeetingLevel(Guid participant, double level) => DispatcherQueue.TryEnqueue(() =>
    {
        var previous = _meetingLevels.GetValueOrDefault(participant);
        _meetingLevels[participant] = Math.Max(previous, level);
        if ((previous > .12) != (level > .12)) MarkMeetingsDirty(stage: true);
    });

    private void OnMeetingMicrophoneLevel(double level) => OnMeetingLevel(_meetings?.LocalParticipantId ?? Guid.Empty, level);

    private void DecayMeetingLevels()
    {
        foreach (var key in _meetingLevels.Keys.ToArray())
        {
            var next = _meetingLevels[key] * .78;
            if ((_meetingLevels[key] > .12) != (next > .12)) _meetingStageDirty = true;
            _meetingLevels[key] = next < .01 ? 0 : next;
        }
    }

    private bool IsSpeaking(Guid participant) => _meetingLevels.GetValueOrDefault(participant) > .12;

    private sealed class MeetingVideoSource
    {
        public SoftwareBitmapSource Source { get; } = new();
        public bool Decoding { get; set; }
        public DateTimeOffset LastFrame { get; set; }
    }

    // ── Overlay (Notizen / Whiteboard) ───────────────────────────────────

    private Border? _overlayRoot;
    private bool? _overlayCollapsedChoice;
    // Narrow windows start with the compact panel folded so the documents stay visible.
    private bool _overlayCollapsed => _overlayCollapsedChoice ?? MeetingsHost.ActualWidth is > 0 and < 900;

    private void RenderMeetingOverlay()
    {
        var show = IsMeetingsMode && _meetingTab != MeetingTab.Meeting && _meetings?.Room is not null;
        MeetingsOverlayHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // Keep documents and the board readable next to the floating camera panel.
        var reserve = show ? 262 : 0;
        if (_notesScroll is not null) _notesScroll.Padding = new Thickness(0, 0, reserve, 0);
        if (_boardColumns is not null) _boardColumns.Padding = new Thickness(28, 8, 28 + reserve, 40);
        if (!show) return;
        var room = _meetings!.Room!;
        var view = _meetings.View;
        var (agent, people, participants) = room.Read(s => (
            new MeetingAgentStatus { State = s.Agent.State, Status = s.Agent.Status },
            s.Participants.Where(p => p.Connected && !p.IsAgent).Select(MeetingJson.Clone).ToList(),
            s.Participants.Count));
        _ = participants;

        _overlayRoot ??= new Border
        {
            Width = 236,
            CornerRadius = new CornerRadius(14),
            Background = ThemeBrush("MissumLayerBrush", 32),
            BorderBrush = ThemeBrush("MissumStrokeBrush", 48),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Shadow = new ThemeShadow(),
            Translation = new System.Numerics.Vector3(0, 0, 24),
        };
        var stack = new StackPanel { Spacing = 8 };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = view.IsLive ? "● Live" : "Meeting",
            FontSize = 12,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = view.IsLive ? ThemeBrush("MissumDangerBrush", 200) : ThemeBrush("MissumMutedTextBrush", 160),
        });
        var collapse = MeetingIconButton(_overlayCollapsed ? "" : "", _overlayCollapsed ? "Kameras einblenden" : "Kameras einklappen", () =>
        {
            _overlayCollapsedChoice = !_overlayCollapsed;
            RenderMeetingOverlay();
        }, size: 26);
        Grid.SetColumn(collapse, 1);
        header.Children.Add(collapse);
        stack.Children.Add(header);

        if (!_overlayCollapsed)
        {
            stack.Children.Add(AgentTile(agent, height: 118, compact: true));
            var selfVideo = MeetingVideoFor(view.LocalParticipantId, view.CameraOn);
            var peopleCard = new Border
            {
                Height = 82,
                CornerRadius = new CornerRadius(10),
                Background = ThemeBrush("MissumLayerStrongBrush", 40),
            };
            if (selfVideo is not null)
            {
                var grid = new Grid();
                grid.Children.Add(new Image { Source = selfVideo, Stretch = Stretch.UniformToFill });
                grid.Children.Add(NameChip(people.Count > 1 ? $"Du + {people.Count - 1} weitere" : "Du", view.MicOn, small: true));
                peopleCard.Child = grid;
            }
            else
            {
                var center = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                center.Children.Add(AvatarStack(people, 26));
                center.Children.Add(new TextBlock
                {
                    Text = people.Count == 0 ? "Noch niemand verbunden" : people.Count == 1 ? "1 Person" : $"{people.Count} Personen",
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = ThemeBrush("MissumMutedTextBrush", 160),
                });
                peopleCard.Child = center;
            }
            stack.Children.Add(peopleCard);
        }
        stack.Children.Add(MeetingControlBar(compact: true));
        _overlayRoot.Child = stack;
        if (!ReferenceEquals(MeetingsOverlayHost.Content, _overlayRoot)) MeetingsOverlayHost.Content = _overlayRoot;
    }

    // ── Shared building blocks ───────────────────────────────────────────

    internal static readonly Color[] ParticipantColors =
    [
        Color.FromArgb(255, 0x3B, 0x82, 0xF6), Color.FromArgb(255, 0x8B, 0x5C, 0xF6), Color.FromArgb(255, 0x0E, 0x9F, 0x8E),
        Color.FromArgb(255, 0xE1, 0x4D, 0x7A), Color.FromArgb(255, 0xF5, 0x9E, 0x0B), Color.FromArgb(255, 0x22, 0xA3, 0x4A),
        Color.FromArgb(255, 0xDC, 0x26, 0x26), Color.FromArgb(255, 0xA9, 0x70, 0xFF),
    ];

    internal static readonly Color[] StickyColors =
    [
        Color.FromArgb(255, 0xC9, 0xE3, 0xF8), Color.FromArgb(255, 0xE3, 0xDB, 0xF6), Color.FromArgb(255, 0xD3, 0xEC, 0xD6),
        Color.FromArgb(255, 0xF9, 0xD6, 0xE2), Color.FromArgb(255, 0xF8, 0xE7, 0xA6), Color.FromArgb(255, 0xDA, 0xF2, 0xE8),
        Color.FromArgb(255, 0xF8, 0xD4, 0xC4), Color.FromArgb(255, 0xF6, 0xDF, 0xCF),
    ];

    // Index 7 is reserved for Missum and always uses the live accent color.
    private static SolidColorBrush ParticipantBrush(int index) => Math.Abs(index) % ParticipantColors.Length == 7
        ? MeetingAgentVisual.AccentBrush
        : new(ParticipantColors[Math.Abs(index) % ParticipantColors.Length]);

    private static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => (parts[0][..1] + parts[^1][..1]).ToUpperInvariant(),
        };
    }

    private static Border Avatar(MeetingParticipant participant, double size) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size / 2),
        Background = ParticipantBrush(participant.ColorIndex),
        BorderBrush = ThemeBrush("MissumLayerBrush", 32),
        BorderThickness = new Thickness(2),
        Child = new TextBlock
        {
            Text = Initials(participant.Name),
            FontSize = Math.Max(10, size * .38),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    private static StackPanel AvatarStack(List<MeetingParticipant> people, double size)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = -size * .28 };
        foreach (var person in people.Take(6)) panel.Children.Add(Avatar(person, size));
        if (people.Count == 0)
            panel.Children.Add(new FontIcon { Glyph = "", FontSize = size * .7, Foreground = ThemeBrush("MissumMutedTextBrush", 160) });
        return panel;
    }

    private static Border NameChip(string name, bool micOn, bool small = false, bool agent = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        content.Children.Add(new FontIcon
        {
            Glyph = micOn ? "" : "",
            FontSize = small ? 10 : 12,
            Foreground = new SolidColorBrush(micOn ? Colors.White : Color.FromArgb(255, 0xFF, 0x8A, 0x8A)),
        });
        content.Children.Add(new TextBlock { Text = name, FontSize = small ? 11 : 13, Foreground = new SolidColorBrush(Colors.White) });
        _ = agent;
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 3, 8, 3),
            Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = content,
        };
    }

    /// <summary>Missum's tile with the animated mark and the current activity line.</summary>
    private static Border AgentTile(MeetingAgentStatus agent, double height, bool compact)
    {
        var visual = new MeetingAgentVisual { Height = compact ? 74 : Math.Min(150, height * .52), HorizontalAlignment = HorizontalAlignment.Center };
        visual.SetState(agent.State);
        var center = new StackPanel { Spacing = compact ? 4 : 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        center.Children.Add(visual);
        if (!compact || agent.State is not ("idle" or "listening"))
            center.Children.Add(new TextBlock
            {
                Text = agent.Status,
                FontSize = compact ? 11 : 15,
                Foreground = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        var grid = new Grid();
        grid.Children.Add(center);
        var chip = NameChip("Missum", micOn: false, small: compact, agent: true);
        // In the compact tile the status line sits at the bottom, so the name moves up.
        if (compact) chip.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(chip);
        return new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(compact ? 10 : 14),
            Background = new SolidColorBrush(Color.FromArgb(255, 0x1B, 0x1B, 0x1D)),
            Child = grid,
        };
    }

    /// <summary>
    /// A round control button. Media buttons use <paramref name="accent"/>: switched on they are
    /// filled with Missum's accent color, switched off they are neutral with a red, crossed-out icon —
    /// so "on" and "off" can never be confused.
    /// </summary>
    private Button MeetingIconButton(string glyph, string label, Action onClick, bool danger = false, bool active = true,
        double size = 40, bool accent = false, bool slashWhenOff = false)
    {
        var background = danger ? ThemeBrush("MissumDangerBrush", 200)
            : accent && active ? ThemeBrush("MissumAccentBrush", 170)
            : ThemeBrush("MissumLayerStrongBrush", 48);
        var foreground = danger ? new SolidColorBrush(Colors.White)
            : accent && active ? ThemeBrush("MissumAccentTextBrush", 255)
            : !active ? ThemeBrush("MissumDangerBrush", 200)
            : ThemeBrush("MissumTextBrush", 240);
        var icon = new Grid { Width = size * .5, Height = size * .5 };
        icon.Children.Add(new FontIcon { Glyph = glyph, FontSize = size * .4, Foreground = foreground });
        if (!active && slashWhenOff)
            icon.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = 1, Y1 = 1, X2 = size * .5 - 1, Y2 = size * .5 - 1,
                Stroke = foreground, StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });
        var button = new Button
        {
            Width = size * (danger && size >= 40 ? 1.35 : 1.1),
            Height = size,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(Math.Min(12, size / 2.6)),
            BorderThickness = new Thickness(0),
            Background = background,
            Content = icon,
        };
        // Keep the state color on hover; neutral buttons get the usual hover surface.
        var neutral = !danger && !(accent && active);
        button.Resources["ButtonBackgroundPointerOver"] = neutral ? ThemeBrush("MissumHoverBrush", 60) : background;
        button.Resources["ButtonBackgroundPressed"] = neutral ? ThemeBrush("MissumPressedBrush", 70) : background;
        button.Resources["ButtonForegroundPointerOver"] = foreground;
        button.Resources["ButtonForegroundPressed"] = foreground;
        ToolTipService.SetToolTip(button, label);
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => RunUiCallback("Meetings.Button", onClick);
        return button;
    }

    /// <summary>Mic, camera, chat, settings and leave — the same bar on the stage and in the overlay.</summary>
    private StackPanel MeetingControlBar(bool compact)
    {
        var view = Meetings.View;
        var size = compact ? 32 : 44;
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = compact ? 6 : 10, HorizontalAlignment = HorizontalAlignment.Center };
        if (!view.IsLive || view.Connection != MeetingConnectionState.Connected)
        {
            if (view.Role == "host" && Meetings.CanEdit)
            {
                var start = PrimaryMeetingButton("Meeting starten", "", async () => await StartMeetingFromUiAsync(), compact);
                bar.Children.Add(start);
            }
            else if (view.Role == "guest")
            {
                bar.Children.Add(PrimaryMeetingButton("Erneut beitreten", "", async () =>
                {
                    try { await Meetings.RejoinAsync(_lifetime.Token); await Meetings.SetMicrophoneAsync(true); }
                    catch (Exception exception) when (exception is not OutOfMemoryException) { ShowMeetingError(exception.Message); }
                }, compact));
            }
            return bar;
        }
        bar.Children.Add(MeetingIconButton(view.MicOn ? "" : "", view.MicOn ? "Mikrofon stummschalten" : "Mikrofon einschalten",
            () => _ = ToggleMeetingMediaAsync(microphone: true), active: view.MicOn, size: size, accent: true));
        bar.Children.Add(MeetingIconButton("", view.CameraOn ? "Kamera ausschalten" : "Kamera einschalten",
            () => _ = ToggleMeetingMediaAsync(microphone: false), active: view.CameraOn, size: size, accent: true, slashWhenOff: true));
        // While a device is switching, the two media buttons wait instead of queuing clicks.
        foreach (var button in bar.Children.OfType<Button>()) button.IsEnabled = !Meetings.MediaBusy;
        if (!compact)
        {
            bar.Children.Add(MeetingIconButton("", _meetingChatRequested ? "Chat ausblenden" : "Chat einblenden", () =>
            {
                _meetingChatRequested = !_meetingChatRequested;
                UpdateResponsiveLayout();
                MarkMeetingsDirty(stage: true, chat: true);
            }, size: size));
        }
        var settings = MeetingIconButton("", "Geräte und Name", () => { }, size: size);
        settings.Click += (_, _) => ShowMeetingSettingsFlyout(settings);
        bar.Children.Add(settings);
        bar.Children.Add(MeetingIconButton("", view.Role == "host" ? "Meeting beenden" : "Meeting verlassen", () => _ = EndMeetingFromUiAsync(), danger: true, size: size));
        return bar;
    }

    private Button PrimaryMeetingButton(string text, string glyph, Func<Task> onClick, bool compact = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = compact ? 13 : 15, Foreground = ThemeBrush("MissumAccentTextBrush", 255) });
        content.Children.Add(new TextBlock { Text = text, FontSize = compact ? 13 : 14, Foreground = ThemeBrush("MissumAccentTextBrush", 255) });
        var button = new Button
        {
            Content = content,
            Background = ThemeBrush("MissumAccentBrush", 170),
            CornerRadius = new CornerRadius(compact ? 10 : 12),
            Padding = compact ? new Thickness(12, 6, 12, 6) : new Thickness(18, 10, 18, 10),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        button.Resources["ButtonBackgroundPointerOver"] = ThemeBrush("AccentFillColorSecondaryBrush", 150);
        button.Resources["ButtonBackgroundPressed"] = ThemeBrush("AccentFillColorTertiaryBrush", 140);
        AutomationProperties.SetName(button, text);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await onClick(); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { ShowMeetingError(exception.Message); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    private async Task StartMeetingFromUiAsync()
    {
        _meetingError = null;
        await Meetings.StartMeetingAsync(_meetingStartMic, _meetingStartCamera);
        MarkMeetingsDirty(all: true);
    }

    private async Task EndMeetingFromUiAsync()
    {
        try { await Meetings.EndMeetingAsync(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowMeetingError(exception.Message); }
        MarkMeetingsDirty(all: true);
    }

    private async Task ToggleMeetingMediaAsync(bool microphone)
    {
        // Ignore clicks while a switch is still in progress; the service serializes anyway.
        if (Meetings.MediaBusy) return;
        var view = Meetings.View;
        _meetingError = null;
        if (microphone) await Meetings.SetMicrophoneAsync(!view.MicOn);
        else await Meetings.SetCameraAsync(!view.CameraOn);
        _stageTileSignature = null;
        MarkMeetingsDirty(stage: true, overlay: true);
    }

    private bool _meetingStartMic = true;
    private bool _meetingStartCamera;

    private async void ShowMeetingSettingsFlyout(FrameworkElement anchor)
    {
        var panel = new StackPanel { Spacing = 12, Width = 300 };
        panel.Children.Add(new TextBlock { Text = "Meeting-Einstellungen", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var name = AccentTextBox("Dein Name im Meeting");
        name.Header = "Dein Name im Meeting";
        name.Text = Meetings.DisplayName;
        name.MaxLength = 60;
        panel.Children.Add(name);
        var cameras = new ComboBox { Header = "Kamera", HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Wird gesucht …" };
        panel.Children.Add(cameras);
        var microphones = new ComboBox { Header = "Mikrofon", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var device in MeetingAudioService.ListMicrophones())
            microphones.Items.Add(new ComboBoxItem { Content = device.Name, Tag = device.Id });
        microphones.SelectedItem = microphones.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (_settings.Current.MeetingMicrophoneDevice ?? "-1"))
            ?? microphones.Items.FirstOrDefault();
        panel.Children.Add(microphones);
        panel.Children.Add(new TextBlock
        {
            Text = "Tipp: Mit Kopfhörern gibt es kein Echo. Gäste im selben Netzwerk treten über den Einladungslink bei; Windows fragt beim ersten Start eventuell nach einer Firewall-Freigabe für Missum.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = ThemeBrush("MissumMutedTextBrush", 160),
        });
        var save = new Button { Content = "Übernehmen", HorizontalAlignment = HorizontalAlignment.Right, Style = (Style)Application.Current.Resources["MissumPrimaryButtonStyle"] };
        panel.Children.Add(save);
        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.Top };
        save.Click += async (_, _) =>
        {
            var cameraId = (cameras.SelectedItem as ComboBoxItem)?.Tag as string;
            var micId = (microphones.SelectedItem as ComboBoxItem)?.Tag as string;
            var displayName = string.IsNullOrWhiteSpace(name.Text) ? null : name.Text.Trim();
            flyout.Hide();
            // Name and devices take effect immediately, also for everyone else in the meeting.
            await Meetings.ApplySettingsAsync(displayName, cameraId, micId);
            _stageTileSignature = null;
            MarkMeetingsDirty(all: true);
        };
        flyout.ShowAt(anchor);
        try
        {
            foreach (var device in await MeetingCameraService.ListAsync())
                cameras.Items.Add(new ComboBoxItem { Content = device.Name, Tag = device.Id });
            if (cameras.Items.Count == 0) cameras.PlaceholderText = "Keine Kamera gefunden";
            cameras.SelectedItem = cameras.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _settings.Current.MeetingCameraDeviceId)
                ?? cameras.Items.FirstOrDefault();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { cameras.PlaceholderText = "Kameras nicht verfügbar"; }
    }

    private static TextBox PlainTextBox(double fontSize, string placeholder = "")
    {
        var box = new TextBox
        {
            FontSize = fontSize,
            PlaceholderText = placeholder,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 4, 6, 4),
            MinHeight = 0,
        };
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
                     "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled" })
            box.Resources[key] = new SolidColorBrush(Colors.Transparent);
        box.Resources["TextControlForegroundFocused"] = ThemeBrush("MissumTextBrush", 240);
        box.Resources["TextControlForegroundPointerOver"] = ThemeBrush("MissumTextBrush", 240);
        return box;
    }

    private void DisposeMeetings()
    {
        _meetingRenderTimer?.Stop();
        if (_meetings is null) return;
        _meetings.Changed -= OnMeetingChanged;
        _meetings.SnapshotChanged -= OnMeetingSnapshot;
        _meetings.OperationApplied -= OnMeetingOperation;
        _meetings.VideoFrame -= OnMeetingVideo;
        _meetings.ParticipantLevel -= OnMeetingLevel;
        _meetings.MicrophoneLevel -= OnMeetingMicrophoneLevel;
        _meetings.JoinRequested -= OnMeetingJoinRequested;
    }
}
