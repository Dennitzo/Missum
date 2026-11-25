using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Missum.App.Services.Meetings;

namespace Missum.App.Pages;

/// <summary>
/// MISSUM_SMOKE_UI_MEETINGS=1: switches to Claude Meetings, fills a meeting with
/// demo content (participants, chat, transcript and a canned Missum update),
/// visits every tab and writes PNG snapshots plus a validation report. The
/// snapshots are the basis for the visual review of the mode.
/// </summary>
public sealed partial class NativeAssistantPage
{
    private async Task VerifyMeetingsSmokeAsync()
    {
        if (App.Current.MainWindow is { } window)
            window.AppWindow.Resize(SmokeWindowSize(1640, 1000)); // logical size, also on high-DPI displays
        await NavigateAsync("mode.switch", new { chatMode = MeetingsMode });
        var report = new Dictionary<string, object?>();
        // Without a project the mode explains what to do and starts nothing.
        await WaitForAsync(() => IsMeetingsMode && Meetings.View.NeedsProject, "Projektpflicht wurde nicht angezeigt");
        MarkMeetingsDirty(all: true);
        await Task.Delay(500);
        report["projectRequired"] = await CaptureMeetingsAsync("meetings-0-project-required.png");
        var blockedWithoutProject = false;
        try { await Meetings.StartMeetingAsync(false, false); }
        catch (InvalidOperationException) { blockedWithoutProject = true; }

        var project = Path.Combine(App.Current.DataDirectory, "SmokeProjekt Kundenveranstaltung");
        Directory.CreateDirectory(project);
        await NavigateAsync("session.projectCreate", new { workspacePath = project, chatMode = MeetingsMode });
        await WaitForAsync(() => IsMeetingsMode && Meetings.Room is not null && Meetings.SessionId == _session, "Meeting wurde nicht geöffnet");
        await Task.Delay(400);
        report["lobby"] = await CaptureMeetingsAsync("meetings-1-lobby.png");

        await Meetings.StartMeetingAsync(microphone: false, camera: false);
        await WaitForAsync(() => Meetings.View.IsLive, "Meeting wurde nicht gestartet");
        SeedMeetingDemo(App.Current.GetService<MeetingHostService>(), Meetings.Room!.Read(s => s.Id));
        await WaitForAsync(() => Meetings.Room!.Read(s => s.Chat.Count >= 4 && s.Whiteboard.Notes.Count >= 8), "Demo-Inhalte fehlen");
        MarkMeetingsDirty(all: true);
        await Task.Delay(700);
        report["stage"] = await CaptureMeetingsAsync("meetings-2-stage.png");
        var overlayOnStage = MeetingsOverlayHost.Visibility == Visibility.Visible;
        var lobbyNameField = _hostLobbyName is not null;

        // Missum's camera shows what it is doing, e.g. while the model loads.
        var host = App.Current.GetService<MeetingHostService>();
        var meetingId = Meetings.Room!.Read(s => s.Id);
        host.ApplyLocal(meetingId, new MeetingOp { Type = MeetingOpTypes.AgentStatus, ActorId = MeetingRoom.AgentId, Agent = new MeetingAgentStatus { State = "loading", Status = "AI-Modell wird geladen …" } });
        MarkMeetingsDirty(stage: true);
        await Task.Delay(600);
        report["stageLoading"] = await CaptureMeetingsAsync("meetings-2b-stage-loading.png");
        host.ApplyLocal(meetingId, new MeetingOp { Type = MeetingOpTypes.AgentStatus, ActorId = MeetingRoom.AgentId, Agent = new MeetingAgentStatus { State = "thinking", Status = "Denke nach …", ContextUsed = 12_000, ContextLimit = 262_144 } });
        await WaitForAsync(() => Meetings.Room!.Read(s => s.Agent.ContextLimit == 262_144), "Kontextstand kam nicht an");
        UpdateMeetingModelFooter();
        report["contextTooltip"] = MeetingContextDescription;
        if (MeetingContextDescription?.StartsWith("Kontext: ", StringComparison.Ordinal) != true || !MeetingContextDescription.Contains("Token", StringComparison.Ordinal))
            throw new InvalidOperationException("Kontext-Tooltip im Meeting-Chat fehlt: " + MeetingContextDescription);

        // Renaming applies immediately and is visible to everyone.
        await Meetings.ApplySettingsAsync("Dennis", _settings.Current.MeetingCameraDeviceId, _settings.Current.MeetingMicrophoneDevice);
        var local = Meetings.LocalParticipantId;
        await WaitForAsync(() => Meetings.Room!.Read(s => s.Participants.Any(p => p.Id == local && p.Name == "Dennis")), "Name wurde nicht übernommen");
        var renameApplied = true;

        // "@" in the meeting chat opens the participant suggestions.
        var mentionOpened = false;
        if (_meetingChatInput is not null && _meetingChatMention is not null)
        {
            _meetingChatInput.Focus(FocusState.Programmatic);
            await Task.Delay(150);
            _meetingChatInput.Text = "Frage an @";
            _meetingChatInput.SelectionStart = _meetingChatInput.Text.Length;
            await Task.Delay(300);
            mentionOpened = _meetingChatMention.IsOpen;
            _meetingChatMention.Close();
            _meetingChatInput.Text = "";
        }
        // The chat sidebar has no view tabs any more; it is the chat.
        var headerTabs = FindName("HeaderTrailingTabs") is null;
        MarkMeetingsDirty(all: true);
        await Task.Delay(500);
        report["stageRenamed"] = await CaptureMeetingsAsync("meetings-2c-stage-renamed.png");

        // Control bar states: microphone on (if this PC has one), camera off.
        await Meetings.SetMicrophoneAsync(true);
        _stageTileSignature = null;
        MarkMeetingsDirty(stage: true);
        await Task.Delay(600);
        report["controlsMicOn"] = new { micOn = Meetings.View.MicOn, cameraOn = Meetings.View.CameraOn, shot = await CaptureMeetingsAsync("meetings-2d-controls.png") };
        await Meetings.SetMicrophoneAsync(false);
        MarkMeetingsDirty(stage: true);

        // Transcript tab: the lobby's words by participant, live, with chat and camera panel beside it.
        var transcript = new Dictionary<string, object?>();
        SelectMeetingTab(MeetingTab.Transcript);
        var expectedLines = Meetings.Room!.Read(s => s.Transcript.Count);
        await WaitForAsync(() => { RenderMeetingsIfDirty(); return TranscriptLinesShown == expectedLines; }, "Transkript zeigte nicht alle Zeilen");
        transcript["lines"] = TranscriptLinesShown;
        transcript["noPromptWindow"] = BodyGrid.Visibility == Visibility.Collapsed;
        transcript["overlay"] = MeetingsOverlayHost.Visibility == Visibility.Visible;
        transcript["chatSidebar"] = MeetingChatHost.Visibility == Visibility.Visible;
        var spoken = "Wir nehmen die Location in Hamburg, wenn das Angebot bis Freitag kommt.";
        var guestSpeaker = Meetings.Room!.Read(s => s.Participants.First(p => !p.IsAgent && p.Id != Meetings.LocalParticipantId));
        App.Current.GetService<MeetingHostService>().ApplyLocal(Meetings.Room!.Read(s => s.Id), new MeetingOp
        {
            Type = MeetingOpTypes.TranscriptAdd, ActorId = guestSpeaker.Id, ActorName = guestSpeaker.Name,
            Transcript = new MeetingTranscriptEntry(Guid.NewGuid(), guestSpeaker.Id, guestSpeaker.Name, spoken, DateTimeOffset.UtcNow),
        });
        await WaitForAsync(() => { RenderMeetingsIfDirty(); return TranscriptLinesShown == expectedLines + 1; }, "Neue Zeile kam nicht an");
        transcript["streams"] = !TranscriptRevealDone;
        await WaitForAsync(() => TranscriptRevealDone, "Zeile wurde nicht fertig angezeigt");
        report["transcriptTab"] = await CaptureMeetingsAsync("meetings-2e-transcript.png");
        report["transcript"] = transcript;

        SelectMeetingTab(MeetingTab.Notes);
        await Task.Delay(700);
        report["notes"] = await CaptureMeetingsAsync("meetings-3-notes.png");
        var overlayOnNotes = MeetingsOverlayHost.Visibility == Visibility.Visible;
        var rework = new Dictionary<string, object?>();
        foreach (var (key, value) in transcript) rework["transcript." + key] = value;
        rework["notesToolbar"] = _notesToolbar?.Parent is not null;
        var notesDocument = Meetings.Room!.Read(s => s.Documents[0].Id);
        rework["noTitleBlock"] = Meetings.Room!.Read(s => s.Documents[0].Blocks.FirstOrDefault()?.Kind != "h1");

        // At most one empty field: three empty blocks shrink to one once nobody is in them.
        for (var i = 0; i < 3; i++)
            Meetings.Send(new MeetingOp { Type = MeetingOpTypes.BlockUpsert, DocumentId = notesDocument, Block = new MeetingNoteBlock { Kind = "p", Text = "" } });
        _notesFocusedBlockId = null; _notesPendingFocus = null;
        MarkMeetingsDirty(notes: true);
        await WaitForAsync(() =>
        {
            RenderMeetingsIfDirty();
            MarkMeetingsDirty(notes: true);
            return Meetings.Room!.Read(s => s.Documents[0].Blocks.Count(b => string.IsNullOrWhiteSpace(b.Text) && !b.AiOwned)) <= 1;
        }, "Leere Felder wurden nicht aufgeräumt");
        rework["emptyFields"] = Meetings.Room!.Read(s => s.Documents[0].Blocks.Count(b => string.IsNullOrWhiteSpace(b.Text) && !b.AiOwned));

        // Authors after the text: a block written by me shows my nickname.
        var mine = new MeetingNoteBlock { Kind = "p", Text = "Catering-Angebote bis Montag vergleichen" };
        Meetings.Send(new MeetingOp { Type = MeetingOpTypes.BlockUpsert, DocumentId = notesDocument, Block = mine });
        await WaitForAsync(() => Meetings.Room!.Read(s => s.Documents[0].Blocks.Any(b => b.Id == mine.Id && b.Authors.Contains(Meetings.LocalParticipantId))), "Autor fehlt");
        MarkMeetingsDirty(notes: true);
        await Task.Delay(500);
        rework["authorBar"] = _noteRows.TryGetValue(mine.Id, out var mineRow) && mineRow.Accent.Background is not null && mineRow.Root.ColumnDefinitions.Count == 3;
        rework["authorTooltip"] = mineRow?.AuthorTooltip;
        rework["authorTooltipHasTime"] = mineRow?.AuthorTooltip?.Contains(" · ", StringComparison.Ordinal) == true;

        // One title everywhere: Missum renames, the sidebar session follows.
        var hostService = App.Current.GetService<MeetingHostService>();
        var meetingKey = Meetings.Room!.Read(s => s.Id);
        hostService.ApplyLocal(meetingKey, new MeetingOp { Type = MeetingOpTypes.RoomTitle, ActorId = MeetingRoom.AgentId, Text = "Kundenevent 2027" });
        MarkMeetingsDirty(notes: true);
        await WaitForAsync(() =>
        {
            RenderMeetingsIfDirty();
            return Items(_snapshot, "sessions").Any(item => S(item, "id") == _session.ToString("D") && S(item, "title") == "Kundenevent 2027");
        }, "Sitzungsname folgte dem Titel nicht");
        await WaitForAsync(() => { MarkMeetingsDirty(notes: true); RenderMeetingsIfDirty(); return _notesTitle?.Text == "Kundenevent 2027"; }, "Notes-Titel folgte nicht");
        rework["titleSynced"] = _notesTitle?.Text == "Kundenevent 2027";
        report["notesRework"] = await CaptureMeetingsAsync("meetings-3b-notes-rework.png");

        // The camera panel moves and scales.
        MoveMeetingOverlay(-260, 140);
        ResizeMeetingOverlay(330);
        await Task.Delay(500);
        rework["overlayGeometry"] = MeetingOverlayGeometry.ToString();
        rework["overlayMoved"] = MeetingOverlayGeometry.X < -200 && MeetingOverlayGeometry.Y > 100;
        rework["overlayResized"] = Math.Abs(MeetingOverlayGeometry.Width - 330) < 1;
        report["overlayMoved"] = await CaptureMeetingsAsync("meetings-3c-overlay-moved.png");
        MoveMeetingOverlay(0, 0);
        ResizeMeetingOverlay(236);
        report["rework"] = rework;

        SelectMeetingTab(MeetingTab.Whiteboard);
        await Task.Delay(900);
        await SeedCanvasDemoAsync();
        await WaitForAsync(() => _canvasViews.Count >= 4, "Whiteboard-Elemente fehlen");
        await Task.Delay(700);
        report["whiteboard"] = await CaptureMeetingsAsync("meetings-4-whiteboard.png");
        _boardScroll!.ChangeView(0, 560, null, true);
        await Task.Delay(500);
        report["whiteboardElements"] = await CaptureMeetingsAsync("meetings-4a-whiteboard-elements.png");
        _boardScroll.ChangeView(0, 0, null, true);
        await Task.Delay(300);
        var overlayOnBoard = MeetingsOverlayHost.Visibility == Visibility.Visible;
        rework["boardToolbar"] = _boardToolbar?.Parent is not null;
        var elementsRendered = _canvasViews.Count;
        var arrowsRendered = _boardArrows?.Children.Count ?? 0;

        // Panning: the same ChangeView the left-drag gesture uses must move the view.
        var before = _boardScroll!.HorizontalOffset;
        _boardScroll.ChangeView(before + 420, _boardScroll.VerticalOffset + 160, null, true);
        await Task.Delay(400);
        var panned = _boardScroll.HorizontalOffset > before + 300;
        report["whiteboardPanned"] = await CaptureMeetingsAsync("meetings-4b-whiteboard-panned.png");
        _boardScroll.ChangeView(0, 0, null, true);
        await Task.Delay(300);

        // The host's model picker in the meeting chat opens the shared Codex flyout.
        var pickerOpened = false;
        var pickerVisible = _meetingModelButton is { Visibility: Visibility.Visible } && _meetingModelFooter?.Visibility == Visibility.Visible;
        if (_meetingModelButton is not null)
        {
            OnReasoningClick(_meetingModelButton, new RoutedEventArgs());
            await Task.Delay(900);
            pickerOpened = _reasoningFlyout is not null && ReferenceEquals(PickerAnchor, _meetingModelButton);
            report["modelPicker"] = await CaptureMeetingsAsync("meetings-4c-model-picker.png");
            _reasoningFlyout?.Hide();
            _modelSelectionFlyout?.Hide();
        }

        var tabs = SessionTabsPanel.Children.Count(child => _meetingTabButtons.Values.Contains(child));
        var chatVisible = Inspector.Visibility == Visibility.Visible && MeetingChatHost.Visibility == Visibility.Visible;
        var folder = Meetings.View.Folder;
        await Task.Delay(2000);
        var filesSaved = folder is not null && File.Exists(Path.Combine(folder, "meeting.json")) && File.Exists(Path.Combine(folder, "whiteboard.json"));

        // Light theme pass: Missum switches the theme for the whole app.
        App.Current.ApplyTheme(Missum.Core.Models.AppTheme.Light);
        _overlayRoot = null;
        MarkMeetingsDirty(all: true);
        await Task.Delay(700);
        report["whiteboardLight"] = await CaptureMeetingsAsync("meetings-5-whiteboard-light.png");
        SelectMeetingTab(MeetingTab.Notes);
        await Task.Delay(700);
        report["notesLight"] = await CaptureMeetingsAsync("meetings-6-notes-light.png");
        SelectMeetingTab(MeetingTab.Meeting);
        await Task.Delay(700);
        report["stageLight"] = await CaptureMeetingsAsync("meetings-7-stage-light.png");
        App.Current.ApplyTheme(Missum.Core.Models.AppTheme.Dark);
        // Narrow window: the chat sidebar yields and the layout must stay usable.
        if (App.Current.MainWindow is { } narrow)
        {
            narrow.AppWindow.Resize(SmokeWindowSize(1000, 760));
            SelectMeetingTab(MeetingTab.Whiteboard);
            await Task.Delay(900);
            report["whiteboardNarrow"] = await CaptureMeetingsAsync("meetings-8-whiteboard-narrow.png");
        }

        // Switching away must bring the normal chat back (regression: the main area stayed empty).
        var meetingSession = _session;
        await NavigateAsync("mode.switch", new { chatMode = "coding" });
        await WaitForAsync(() => !IsMeetingsMode, "Moduswechsel zu Codex kam nicht an");
        await Task.Delay(700);
        var chatRestored = BodyGrid.Visibility == Visibility.Visible && MeetingsHost.Visibility != Visibility.Visible
            && MeetingsOverlayHost.Visibility != Visibility.Visible && !_meetingTabButtons.Values.Any(b => b.Parent is not null);
        report["codexAfterMeetings"] = await CaptureMeetingsAsync("meetings-9-codex-after.png");
        await NavigateAsync("mode.switch", new { chatMode = MeetingsMode });
        await WaitForAsync(() => IsMeetingsMode && MeetingsHost.Visibility == Visibility.Visible, "Rückkehr zu Claude Meetings fehlgeschlagen");
        var meetingBack = BodyGrid.Visibility != Visibility.Visible;

        report["checks"] = new
        {
            chatRestored,
            meetingBack,
            meetingSession,
            tabs,
            chatVisible,
            lobbyNameField,
            renameApplied,
            mentionOpened,
            headerTabs,
            blockedWithoutProject,
            elementsRendered,
            arrowsRendered,
            panned,
            pickerVisible,
            pickerOpened,
            overlayOnStage,
            overlayOnNotes,
            overlayOnBoard,
            filesSaved,
            folder,
            hostRunning = App.Current.GetService<MeetingHostService>().IsRunning,
        };
        // The host renames in the sidebar: the room follows and Missum keeps the host's name.
        await CommandAsync("session.rename", new { sessionId = _session, title = "Kundentag Hamburg" });
        await WaitForAsync(() => { SyncMeetingTitle(); return Meetings.Room!.Read(s => s.Title == "Kundentag Hamburg" && s.TitleLocked); }, "Manuelles Umbenennen kam im Raum nicht an");
        rework["hostRenameLocks"] = true;
        await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-meetings-validation.json"),
            JsonSerializer.Serialize(report, MeetingJson.Indented));
        if (tabs != 4 || !chatRestored || !meetingBack || !chatVisible || !lobbyNameField || !mentionOpened || !headerTabs || overlayOnStage || !overlayOnNotes || !overlayOnBoard || !filesSaved
            || !blockedWithoutProject || elementsRendered < 5 || arrowsRendered < 4 || !panned || !pickerVisible || !pickerOpened)
            throw new InvalidOperationException("Claude Meetings layout check failed: " + JsonSerializer.Serialize(report["checks"]));
        if (rework.Any(pair => pair.Value is false))
            throw new InvalidOperationException("Claude Meetings rework check failed: " + JsonSerializer.Serialize(rework));
    }

    /// <summary>Places one of each canvas element type next to the columns.</summary>
    private async Task SeedCanvasDemoAsync()
    {
        var title = new WhiteboardElement { Kind = "text", X = 40, Y = 640, Width = 420, Height = 64, FontSize = 40, Text = "Event-Plan 2027" };
        var table = new WhiteboardElement
        {
            Kind = "table", X = 40, Y = 720, Width = 420, Height = 170,
            Rows = [["Aufgabe", "Wer", "Bis"], ["Location buchen", "Vinny", "15. Jan."], ["Einladungen", "Ines", "Februar"], ["Speaker", "June", "Freitag"]],
        };
        var shape = new WhiteboardElement { Kind = "shape", Shape = "ellipse", X = 540, Y = 650, Width = 200, Height = 110, ColorIndex = 4, FontSize = 18, Text = "18. März" };
        var diamond = new WhiteboardElement { Kind = "shape", Shape = "diamond", X = 540, Y = 820, Width = 200, Height = 130, ColorIndex = 2, FontSize = 16, Text = "Stadt?" };
        foreach (var element in new[] { title, table, shape, diamond })
            Meetings.Send(new MeetingOp { Type = MeetingOpTypes.ElementUpsert, Element = element });

        // A generated gradient stands in for a photo.
        var bitmap = new Windows.Graphics.Imaging.SoftwareBitmap(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, 480, 300, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
        var pixels = new byte[480 * 300 * 4];
        for (var y = 0; y < 300; y++)
            for (var x = 0; x < 480; x++)
            {
                var i = (y * 480 + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)(120 + x / 4), (byte)(80 + y / 3), (byte)(160 + y / 4), 255);
            }
        bitmap.CopyFromBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(pixels));
        var jpeg = await MeetingCameraService.EncodeJpegAsync(bitmap, 480);
        var image = new WhiteboardElement { Kind = "image", X = 40, Y = 930, Width = 280, Height = 175, ImageData = Convert.ToBase64String(jpeg) };
        Meetings.Send(new MeetingOp { Type = MeetingOpTypes.ElementUpsert, Element = image });
        await WaitForAsync(() => Meetings.Room!.Read(s => s.Whiteboard.Elements.Count >= 5), "Elemente kamen nicht an");
        Meetings.Send(new MeetingOp { Type = MeetingOpTypes.ElementUpsert, Element = new WhiteboardElement { Kind = "arrow", FromId = table.Id, ToId = shape.Id } });
        Meetings.Send(new MeetingOp { Type = MeetingOpTypes.ElementUpsert, Element = new WhiteboardElement { Kind = "arrow", FromId = table.Id, ToId = diamond.Id } });
        MarkMeetingsDirty(board: true);
    }

    private static async Task WaitForAsync(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(failure);
            await Task.Delay(100);
        }
    }

    private async Task<object> CaptureMeetingsAsync(string fileName)
    {
        RenderMeetingsIfDirty();
        LayoutRoot.UpdateLayout();
        await Task.Delay(150);
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(LayoutRoot);
        var pixels = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(await bitmap.GetPixelsAsync());
        var path = Path.Combine(App.Current.DataDirectory, fileName);
        using (var file = File.Create(path))
        using (var stream = file.AsRandomAccessStream())
        {
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync();
        }
        return new { path, bitmap.PixelWidth, bitmap.PixelHeight };
    }

    private static void SeedMeetingDemo(MeetingHostService host, Guid meetingId)
    {
        var people = new[] { ("Ines", Guid.NewGuid()), ("Vinny", Guid.NewGuid()), ("June", Guid.NewGuid()) };
        foreach (var (name, id) in people)
            host.ApplyLocal(meetingId, new MeetingOp
            {
                Type = MeetingOpTypes.ParticipantJoin,
                ActorId = id,
                Participant = new MeetingParticipant { Id = id, Name = name, Role = "guest", MicOn = name != "June" },
            });
        var now = DateTimeOffset.UtcNow;
        void Chat(Guid id, string name, string text, int minutes) => host.ApplyLocal(meetingId, new MeetingOp
        {
            Type = MeetingOpTypes.ChatAdd,
            ActorId = id,
            ActorName = name,
            Chat = new MeetingChatMessage(Guid.NewGuid(), id, name, text, now.AddMinutes(minutes)),
        });
        Chat(people[0].Item2, "Ines", "Können wir 16.000 € als Reserve zurückhalten?", -8);
        Chat(people[1].Item2, "Vinny", "Ja, die Location ist die größte Unbekannte.", -7);
        Chat(people[1].Item2, "Vinny", "@Missum, was ist noch offen?", -6);
        host.ApplyLocal(meetingId, new MeetingOp
        {
            Type = MeetingOpTypes.ChatAdd,
            ActorId = MeetingRoom.AgentId,
            Chat = new MeetingChatMessage(Guid.NewGuid(), MeetingRoom.AgentId, "Missum",
                "Zwei Dinge: die Stadt und die Zielgröße der Gäste. Die Location hängt von beidem ab, deshalb habe ich beides in die Notizen aufgenommen.", now.AddMinutes(-6)),
        });
        Chat(people[2].Item2, "June", "Die Speaker sind bis Freitag bestätigt. Catering übernehme ich.", -3);
        foreach (var (name, text) in new[]
                 {
                     ("Ines", "Der Termin für die Kundenveranstaltung ist der 18. März."),
                     ("Vinny", "Budget liegen wir bei 180.000 Euro."),
                     ("June", "Das Save the Date geht am 8. Dezember raus, sobald die Marke final ist."),
                 })
        {
            var speaker = people.First(p => p.Item1 == name).Item2;
            host.ApplyLocal(meetingId, new MeetingOp
            {
                Type = MeetingOpTypes.TranscriptAdd,
                ActorId = speaker,
                Transcript = new MeetingTranscriptEntry(Guid.NewGuid(), speaker, name, text, now.AddMinutes(-10)),
            });
        }

        var state = host.Snapshot(meetingId)!;
        var inbox = state.Whiteboard.Columns[0].Id;
        var ideas = new (string Text, string Author)[]
        {
            ("Keynote mit Kundenstimmen", "Ines"), ("Live-Demo der neuen Plattform", "June"), ("Workshop-Inseln", "Vinny"),
            ("Abendevent mit Food Trucks", "June"), ("Location in Hamburg?", "Vinny"), ("Location in München?", "Ines"),
            ("Gästeliste bis Januar", "Ines"), ("Speaker bis Freitag bestätigen", "June"),
        };
        var notes = new List<WhiteboardNote>();
        for (var index = 0; index < ideas.Length; index++)
        {
            var author = people.First(p => p.Item1 == ideas[index].Author);
            var note = new WhiteboardNote { Text = ideas[index].Text, ColumnId = inbox, Order = index + 1 };
            notes.Add(note);
            host.ApplyLocal(meetingId, new MeetingOp { Type = MeetingOpTypes.NoteUpsert, ActorId = author.Item2, ActorName = author.Item1, Note = note });
        }
        host.ApplyLocal(meetingId, new MeetingOp { Type = MeetingOpTypes.NoteReact, ActorId = people[1].Item2, ActorName = "Vinny", NoteId = notes[1].Id, Reaction = "heart" });
        host.ApplyLocal(meetingId, new MeetingOp { Type = MeetingOpTypes.NoteReact, ActorId = people[0].Item2, ActorName = "Ines", NoteId = notes[0].Id, Reaction = "plus" });

        // A canned Missum update stands in for the model, so the merge and the animation are exercised offline.
        var result = new AgentResult(null,
            new AgentNotes("Das Team hat die Kundenveranstaltung für den 18. März mit einem Budget von 180.000 € festgelegt.",
                ["Das Save the Date geht am 8. Dezember raus, nachdem die Marke final ist.", "16.000 € bleiben als Reserve zurück."],
                [new AgentActionItem("bucht die Location", "Vinny", "15. Januar"), new AgentActionItem("verschickt die Einladungen im Februar", "Ines", null),
                 new AgentActionItem("wählt das Catering aus", "June", null), new AgentActionItem("bestätigt die Speaker", "June", "Freitag")],
                ["In welcher Stadt soll die Veranstaltung stattfinden?", "Wie viele Gäste erwarten wir?"]),
            [
                new AgentColumn(null, "Programm", "Inhalte auf der Bühne", [notes[0].Id, notes[1].Id, notes[2].Id]),
                new AgentColumn(null, "Rahmen", "Location, Catering, Abend", [notes[4].Id, notes[5].Id, notes[3].Id]),
                new AgentColumn(null, "To-dos", "Mit Termin", [notes[6].Id, notes[7].Id]),
            ],
            [new AgentNewNote("Zielgröße der Gäste klären", "Rahmen")],
            []);
        var current = host.Snapshot(meetingId)!;
        host.ApplyLocal(meetingId, new MeetingOp
        {
            Type = MeetingOpTypes.DocumentSetBlocks,
            ActorId = MeetingRoom.AgentId,
            DocumentId = current.Documents[0].Id,
            Blocks = MeetingAgentMerge.MergeNotes(current.Documents[0].Blocks, result.Notes),
        });
        host.ApplyLocal(meetingId, new MeetingOp
        {
            Type = MeetingOpTypes.WhiteboardSet,
            ActorId = MeetingRoom.AgentId,
            Whiteboard = MeetingAgentMerge.MergeWhiteboard(host.Snapshot(meetingId)!.Whiteboard, result),
        });
        host.ApplyLocal(meetingId, new MeetingOp
        {
            Type = MeetingOpTypes.AgentStatus,
            ActorId = MeetingRoom.AgentId,
            Agent = new MeetingAgentStatus { State = "writing", Status = "Whiteboard wird erstellt" },
        });
    }
}
