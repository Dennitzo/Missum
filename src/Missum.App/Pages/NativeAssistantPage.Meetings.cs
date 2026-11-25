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

    private enum MeetingTab { Meeting, Transcript, Notes, Whiteboard }

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
            _meetingRenderTimer.Tick += (_, _) => RunMeetingCallback("Meetings.Render", RenderMeetingsIfDirty);
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
        SyncMeetingTitle();
    }

    // One name everywhere: notes, whiteboard and the sidebar session show the room title. Missum
    // keeps it current; the host renames it in the sidebar, which then locks it against Missum.
    private Guid _meetingTitleSession;
    private string? _meetingTitleSynced;
    private string? _meetingTitleRenaming;

    private static string MeetingSessionTitle(MeetingRoomState snapshot) => snapshot.Title;

    private DateTimeOffset _meetingTitleRenamingSince;

    private void SyncMeetingTitle()
    {
        if (!IsMeetingsMode || _meetings?.Room is not { } room || _session == Guid.Empty || _meetings.SessionId != _session) return;
        if (_meetingTitleSession != _session) { _meetingTitleSession = _session; _meetingTitleSynced = null; _meetingTitleRenaming = null; }
        var roomTitle = room.Read(state => state.Title);
        var sessionTitle = Items(_snapshot, "sessions").Where(item => S(item, "id") == _session.ToString("D"))
            .Select(item => S(item, "title")).FirstOrDefault() ?? "";
        if (sessionTitle.Length == 0) return;
        var host = _meetings.View.Role == "host";
        if (_meetingTitleRenaming is { } pending)
        {
            // The sidebar has not taken over the room's title yet: its old name is no rename by the host.
            if (sessionTitle == pending) { _meetingTitleRenaming = null; _meetingTitleSynced = pending; return; }
            if (roomTitle == pending && DateTimeOffset.UtcNow - _meetingTitleRenamingSince < TimeSpan.FromSeconds(10)) return;
            _meetingTitleRenaming = null;
        }
        if (host && _meetingTitleSynced is { } synced && sessionTitle != synced && roomTitle == synced)
        {
            // The host renamed the session in the sidebar.
            _meetingTitleSynced = sessionTitle;
            _meetings.Send(new MeetingOp { Type = MeetingOpTypes.RoomTitle, Text = sessionTitle });
            return;
        }
        if (roomTitle != sessionTitle)
        {
            _meetingTitleRenaming = roomTitle;
            _meetingTitleRenamingSince = DateTimeOffset.UtcNow;
            _meetingTitleSynced = roomTitle;
            _ = CommandAsync("session.rename", new { sessionId = _session, title = roomTitle });
            return;
        }
        _meetingTitleSynced = sessionTitle;
        _meetingTitleRenaming = null;
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
            SessionViewKind.Transcript => MeetingTab.Transcript,
            SessionViewKind.Notes => MeetingTab.Notes,
            _ => MeetingTab.Whiteboard,
        };
        if (!_meetingTabButtons.TryGetValue(tab, out var button))
        {
            var (glyph, title, help) = tab switch
            {
                MeetingTab.Meeting => ("", "Meeting", "Kameras, Steuerung und Einladungslink"),
                MeetingTab.Transcript => ("", "Transcript", "Live-Transkript der Lobby, nach Teilnehmer"),
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
        _meetingTranscriptDirty |= all || chat;
        _meetingNotesDirty |= all || notes;
        _meetingBoardDirty |= all || board;
        _meetingChatDirty |= all || chat;
        _meetingOverlayDirty |= all || overlay;
    }

    private void OnMeetingChanged() => DispatcherQueue.TryEnqueue(() => RunMeetingCallback("Meetings.Changed", () => MarkMeetingsDirty(stage: true, chat: true, overlay: true, notes: true, board: true)));
    private void OnMeetingSnapshot() => DispatcherQueue.TryEnqueue(() => RunMeetingCallback("Meetings.Snapshot", () => MarkMeetingsDirty(all: true)));

    private void OnMeetingOperation(MeetingOp op) => DispatcherQueue.TryEnqueue(() => RunMeetingCallback("Meetings.Operation", () =>
    {
        switch (op.Type)
        {
            case MeetingOpTypes.ChatAdd: MarkMeetingsDirty(chat: true); break;
            case MeetingOpTypes.TranscriptAdd:
                // Recognized speech counts as speaking, also when the level was borderline.
                if (op.Transcript is { } spoken) MarkSpeech(spoken.SpeakerId, TimeSpan.FromMilliseconds(1500));
                MarkMeetingsDirty(chat: true);
                break;
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
    }));

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
        if (_meetingStageDirty || _meetingNotesDirty || _meetingBoardDirty || _meetingTranscriptDirty)
        {
            var content = _meetingTab switch
            {
                MeetingTab.Meeting => (UIElement)MeetingStageRoot(),
                MeetingTab.Transcript => MeetingTranscriptRoot(),
                MeetingTab.Notes => MeetingNotesRoot(),
                _ => MeetingBoardRoot(),
            };
            if (!ReferenceEquals(MeetingsContent.Content, content)) MeetingsContent.Content = content;
        }
        if (_meetingTab == MeetingTab.Meeting && _meetingStageDirty) { _meetingStageDirty = false; RenderMeetingStage(); }
        if (_meetingTab == MeetingTab.Transcript && _meetingTranscriptDirty) { _meetingTranscriptDirty = false; RenderMeetingTranscript(); }
        if (_meetingTab == MeetingTab.Notes && _meetingNotesDirty) { _meetingNotesDirty = false; RenderMeetingNotes(); }
        if (_meetingTab == MeetingTab.Whiteboard && _meetingBoardDirty) { _meetingBoardDirty = false; RenderMeetingBoard(); }
        if (_meetingOverlayDirty) { _meetingOverlayDirty = false; RenderMeetingOverlay(); }
        if (_meetingChatDirty && MeetingChatHost.Visibility == Visibility.Visible) { _meetingChatDirty = false; RenderMeetingChat(); }
        SyncMeetingTitle();
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
            var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            try { await video.Source.SetBitmapAsync(bitmap); }
            catch { bitmap.Dispose(); throw; }
            // The source keeps reading the bitmap it shows (e.g. when a tile is rebuilt on a tab switch):
            // only the frame it no longer shows may be released, or XAML fails with RO_E_CLOSED.
            var previous = video.Bitmap;
            video.Bitmap = bitmap;
            previous?.Dispose();
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

    private void OnMeetingLevel(Guid participant, double level) => DispatcherQueue.TryEnqueue(() => RunMeetingCallback("Meetings.Level", () =>
    {
        var previous = _meetingLevels.GetValueOrDefault(participant);
        _meetingLevels[participant] = Math.Max(previous, level);
        // Only a clear level counts; background noise stays below this.
        if (level >= SpeakingLevel) MarkSpeech(participant, TimeSpan.FromMilliseconds(600));
    }));

    // Speaking: a clear microphone level or recognized speech lights the name green for a moment.
    private const double SpeakingLevel = .18;
    private static readonly TimeSpan SpeakerHold = TimeSpan.FromSeconds(5);
    private readonly Dictionary<Guid, DateTimeOffset> _meetingSpeechUntil = [];
    private readonly Dictionary<Guid, DateTimeOffset> _meetingLastSpeech = [];
    private readonly HashSet<Guid> _meetingSpeakingNow = [];
    private string _overlaySpeakerKey = "";

    private void MarkSpeech(Guid participant, TimeSpan hold)
    {
        if (participant == Guid.Empty) return;
        var now = DateTimeOffset.UtcNow;
        if (_meetingSpeechUntil.GetValueOrDefault(participant) < now + hold) _meetingSpeechUntil[participant] = now + hold;
        _meetingLastSpeech[participant] = now;
        RefreshSpeaking();
    }

    /// <summary>Updates who speaks now; tiles and the overlay re-render only when that changes.</summary>
    private void RefreshSpeaking()
    {
        var now = DateTimeOffset.UtcNow;
        var changed = false;
        foreach (var (id, until) in _meetingSpeechUntil)
        {
            var speaking = until > now;
            if (speaking ? _meetingSpeakingNow.Add(id) : _meetingSpeakingNow.Remove(id)) changed = true;
        }
        if (changed) MarkMeetingsDirty(stage: true, overlay: true);
        var key = string.Join(",", OverlaySpeakers(_meetings?.LocalParticipantId ?? Guid.Empty));
        if (key != _overlaySpeakerKey) { _overlaySpeakerKey = key; MarkMeetingsDirty(overlay: true); }
    }

    /// <summary>
    /// Who the compact overlay shows: people who spoke within the last seconds, latest first,
    /// at most two, others before yourself. The tiles rotate as the conversation moves on.
    /// </summary>
    private List<Guid> OverlaySpeakers(Guid self)
    {
        var now = DateTimeOffset.UtcNow;
        return _meetingLastSpeech
            .Where(pair => _meetingSpeakingNow.Contains(pair.Key) || now - pair.Value < SpeakerHold)
            .OrderBy(pair => pair.Key == self ? 1 : 0)
            .ThenByDescending(pair => pair.Value)
            .Select(pair => pair.Key)
            .Take(2)
            .ToList();
    }

    internal IReadOnlyCollection<Guid> SpeakingParticipants => _meetingSpeakingNow;
    internal IReadOnlyList<Guid> OverlaySpeakerIds => OverlaySpeakers(_meetings?.LocalParticipantId ?? Guid.Empty);

    private void OnMeetingMicrophoneLevel(double level) => OnMeetingLevel(_meetings?.LocalParticipantId ?? Guid.Empty, level);

    private void DecayMeetingLevels()
    {
        foreach (var key in _meetingLevels.Keys.ToArray())
        {
            var next = _meetingLevels[key] * .78;
            _meetingLevels[key] = next < .01 ? 0 : next;
        }
        RefreshSpeaking();
    }

    private bool IsSpeaking(Guid participant) => _meetingSpeakingNow.Contains(participant);

    private sealed class MeetingVideoSource
    {
        public SoftwareBitmapSource Source { get; } = new();
        public SoftwareBitmap? Bitmap { get; set; }
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
        var reserve = show ? _overlayWidth + 26 : 0;
        if (_notesScroll is not null) _notesScroll.Padding = new Thickness(0, 0, reserve, 0);
        if (_transcriptScroll is not null) _transcriptScroll.Padding = new Thickness(0, 0, reserve, 0);
        if (_boardColumns is not null) _boardColumns.Padding = new Thickness(28, 8, 28 + reserve, 40);
        if (!show) return;
        var room = _meetings!.Room!;
        var view = _meetings.View;
        var (agent, people, participants) = room.Read(s => (
            new MeetingAgentStatus { State = s.Agent.State, Status = s.Agent.Status },
            s.Participants.Where(p => p.Connected && !p.IsAgent).Select(MeetingJson.Clone).ToList(),
            s.Participants.Count));
        _ = participants;

        if (_overlayRoot is null)
        {
            _overlayRoot = new Border
            {
                CornerRadius = new CornerRadius(18),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8),
                Shadow = new ThemeShadow(),
                Translation = new System.Numerics.Vector3(0, 0, 24),
            };
            AttachOverlayManipulation(_overlayRoot);
        }
        _overlayRoot.Width = _overlayWidth;
        UpdateOverlayGlass();
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
            stack.Children.Add(AgentTile(agent, height: Math.Round(_overlayWidth * 0.5), compact: true));
            var speakers = OverlaySpeakers(view.LocalParticipantId)
                .Select(id => people.FirstOrDefault(p => p.Id == id))
                .OfType<MeetingParticipant>()
                .ToList();
            var selfVideo = MeetingVideoFor(view.LocalParticipantId, view.CameraOn);
            var peopleCard = new Border
            {
                Height = Math.Round(_overlayWidth * 0.35),
                CornerRadius = new CornerRadius(10),
                Background = ThemeBrush("MissumLayerStrongBrush", 40),
            };
            if (speakers.Count > 0)
            {
                // Whoever speaks is shown here; the tiles change as the conversation moves on.
                var slots = new Grid { ColumnSpacing = 4 };
                for (var index = 0; index < speakers.Count; index++)
                {
                    slots.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var speaker = speakers[index];
                    var self = speaker.Id == view.LocalParticipantId;
                    var tile = OverlaySpeakerTile(speaker, self, Math.Round(_overlayWidth * 0.35), speakers.Count == 1);
                    Grid.SetColumn(tile, index);
                    slots.Children.Add(tile);
                }
                peopleCard.Child = slots;
                peopleCard.Height = Math.Round(_overlayWidth * (speakers.Count == 1 ? 0.5625 : 0.35));
            }
            else if (selfVideo is not null)
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

    /// <summary>A speaker's camera (or avatar) in the overlay, with the green name while speaking.</summary>
    private Border OverlaySpeakerTile(MeetingParticipant person, bool self, double height, bool wide)
    {
        var grid = new Grid();
        if (MeetingVideoFor(person.Id, person.CameraOn) is { } video)
            grid.Children.Add(new Image
            {
                Source = video, Stretch = Stretch.UniformToFill,
                RenderTransformOrigin = new Windows.Foundation.Point(.5, .5),
                RenderTransform = self ? new ScaleTransform { ScaleX = -1 } : null,
            });
        else
        {
            var avatar = Avatar(person, Math.Clamp(height * .42, 26, 60));
            avatar.HorizontalAlignment = HorizontalAlignment.Center;
            avatar.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(avatar);
        }
        var speaking = IsSpeaking(person.Id) && person.MicOn;
        grid.Children.Add(NameChip(self ? "Du" : person.Name, person.MicOn, small: true, speaking: speaking));
        _ = wide;
        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Blend(ParticipantColors[person.ColorIndex % ParticipantColors.Length], Color.FromArgb(255, 0x22, 0x22, 0x24), .82)),
            BorderBrush = speaking ? new SolidColorBrush(SpeakingGreen) : new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(2),
            Child = grid,
        };
    }

    // The camera panel: translucent like the tool pills, dragged by its header, resized at its
    // side edges. Handlers live on the outer border, which survives every re-render.
    private double _overlayWidth = 236;
    private bool _overlayHovered;
    private string? _overlayGesture;
    private Windows.Foundation.Point _overlayGestureStart;
    private (double X, double Y, double Width) _overlayGestureOrigin;
    private readonly TranslateTransform _overlayOffset = new();

    private void UpdateOverlayGlass()
    {
        if (_overlayRoot is null) return;
        var input = ThemeBrush("MissumInputBrush", 40).Color;
        _overlayRoot.Background = new SolidColorBrush(Color.FromArgb(_overlayHovered ? (byte)0xE6 : (byte)0xB8, input.R, input.G, input.B));
        _overlayRoot.BorderBrush = ThemeBrush("MissumStrokeBrush", 70);
    }

    private void AttachOverlayManipulation(Border root)
    {
        MeetingsOverlayHost.RenderTransform = _overlayOffset;
        root.PointerEntered += (_, _) => { _overlayHovered = true; UpdateOverlayGlass(); };
        root.PointerExited += (_, _) => { _overlayHovered = false; UpdateOverlayGlass(); if (_overlayGesture is null) ProtectedCursor = null; };
        root.PointerPressed += (_, args) =>
        {
            if (args.OriginalSource is DependencyObject source && IsInsideButton(source, root)) return;
            var point = args.GetCurrentPoint(root).Position;
            _overlayGesture = OverlayEdge(root, point) ?? (point.Y < 34 ? "move" : null);
            if (_overlayGesture is null) return;
            _overlayGestureStart = args.GetCurrentPoint(MeetingsHost).Position;
            _overlayGestureOrigin = (_overlayOffset.X, _overlayOffset.Y, _overlayWidth);
            root.CapturePointer(args.Pointer);
            args.Handled = true;
        };
        root.PointerMoved += (_, args) =>
        {
            if (_overlayGesture is null)
            {
                // The side edges scale the panel; the cursor says so.
                var edge = OverlayEdge(root, args.GetCurrentPoint(root).Position);
                ProtectedCursor = edge is null ? null : Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
                return;
            }
            var point = args.GetCurrentPoint(MeetingsHost).Position;
            var dx = point.X - _overlayGestureStart.X;
            var dy = point.Y - _overlayGestureStart.Y;
            if (_overlayGesture == "move") MoveMeetingOverlay(_overlayGestureOrigin.X + dx, _overlayGestureOrigin.Y + dy);
            else if (_overlayGesture == "left") ResizeMeetingOverlay(_overlayGestureOrigin.Width - dx);
            else
            {
                // Right edge: the left side of the panel stays where it is.
                ResizeMeetingOverlay(_overlayGestureOrigin.Width + dx);
                MoveMeetingOverlay(_overlayGestureOrigin.X + (_overlayWidth - _overlayGestureOrigin.Width), _overlayOffset.Y);
            }
            args.Handled = true;
        };
        void End(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
        {
            if (_overlayGesture is null) return;
            _overlayGesture = null;
            root.ReleasePointerCapture(args.Pointer);
        }
        root.PointerReleased += End;
        root.PointerCaptureLost += (_, _) => { _overlayGesture = null; ProtectedCursor = null; };
        root.DoubleTapped += (_, args) =>
        {
            if (args.GetPosition(root).Y > 34) return;
            _overlayOffset.X = 0; _overlayOffset.Y = 0;
        };
    }

    /// <summary>"left" or "right" when the point lies on a side edge of the panel, else null.</summary>
    private static string? OverlayEdge(FrameworkElement root, Windows.Foundation.Point point) =>
        point.X <= 7 ? "left" : point.X >= root.ActualWidth - 7 ? "right" : null;

    private static bool IsInsideButton(DependencyObject source, DependencyObject root)
    {
        for (var current = source; current is not null && !ReferenceEquals(current, root); current = VisualTreeHelper.GetParent(current))
            if (current is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase) return true;
        return false;
    }

    /// <summary>Moves the panel, kept fully inside the meeting area.</summary>
    internal void MoveMeetingOverlay(double x, double y)
    {
        var width = _overlayRoot?.ActualWidth ?? _overlayWidth;
        var height = _overlayRoot?.ActualHeight ?? 200;
        // Anchored top-right with margins 16 (top) and 18 (right).
        var minX = -Math.Max(0, MeetingsHost.ActualWidth - width - 36);
        var maxY = Math.Max(0, MeetingsHost.ActualHeight - height - 32);
        _overlayOffset.X = Math.Clamp(x, minX, 0);
        _overlayOffset.Y = Math.Clamp(y, -4, maxY);
    }

    internal void ResizeMeetingOverlay(double width)
    {
        var maximum = Math.Max(200, Math.Min(460, MeetingsHost.ActualWidth * 0.45));
        var next = Math.Round(Math.Clamp(width, 190, maximum));
        if (Math.Abs(next - _overlayWidth) < 1) return;
        _overlayWidth = next;
        RenderMeetingOverlay();
    }

    internal (double X, double Y, double Width) MeetingOverlayGeometry => (_overlayOffset.X, _overlayOffset.Y, _overlayWidth);

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

    private static readonly Color SpeakingGreen = Color.FromArgb(255, 0x2F, 0xC4, 0x6B);

    private static Border NameChip(string name, bool micOn, bool small = false, bool agent = false, bool speaking = false)
    {
        if (speaking && micOn)
        {
            // Speaking: the name lights up green with a soft halo, readable on any camera picture.
            var lit = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            lit.Children.Add(new FontIcon { Glyph = "\uE720", FontSize = small ? 10 : 12, Foreground = new SolidColorBrush(Colors.White) });
            lit.Children.Add(new TextBlock { Text = name, FontSize = small ? 11 : 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Colors.White) });
            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(90, SpeakingGreen.R, SpeakingGreen.G, SpeakingGreen.B)),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(3),
                Margin = new Thickness(5),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Child = new Border
                {
                    Background = new SolidColorBrush(SpeakingGreen),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(7, 3, 8, 3),
                    Child = lit,
                },
            };
        }
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
        if (Meetings.View.Role == "host" && !Meetings.View.IsLive) await PrepareMeetingAiAsync();
        await Meetings.StartMeetingAsync(_meetingStartMic, _meetingStartCamera);
        MarkMeetingsDirty(all: true);
    }

    /// <summary>
    /// The meeting goes live only once Missum can work: first the selected AI model, then speech
    /// recognition (Whisper). The lobby shows each step; a failure keeps the meeting closed.
    /// </summary>
    internal async Task PrepareMeetingAiAsync()
    {
        try
        {
            SetMeetingPreparing("AI-Modell wird geladen …");
            await App.Current.GetService<MeetingAgentService>().PrepareModelAsync(_lifetime.Token);
            SetMeetingPreparing("Spracherkennung (Whisper) wird geladen …");
            await App.Current.GetService<MeetingTranscriptionHub>().PrepareAsync(_lifetime.Token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            throw new InvalidOperationException("Das Meeting startet erst, wenn AI-Modell und Spracherkennung bereit sind. " + exception.Message, exception);
        }
        finally { SetMeetingPreparing(null); }
    }

    private string? _meetingPreparing;
    private StackPanel? _meetingPrepareLine;

    internal string? MeetingPreparingStatus => _meetingPreparing;

    private void SetMeetingPreparing(string? status)
    {
        _meetingPreparing = status;
        if (_meetingPrepareLine is { } line)
        {
            line.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
            if (line.Children.Count > 1 && line.Children[1] is TextBlock text) text.Text = status ?? "";
        }
    }

    private StackPanel MeetingPrepareLine()
    {
        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = _meetingPreparing is null ? Visibility.Collapsed : Visibility.Visible,
        };
        line.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16 });
        line.Children.Add(new TextBlock { Text = _meetingPreparing ?? "", FontSize = 13, Foreground = ThemeBrush("MissumMutedTextBrush", 170), VerticalAlignment = VerticalAlignment.Center });
        _meetingPrepareLine = line;
        return line;
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
