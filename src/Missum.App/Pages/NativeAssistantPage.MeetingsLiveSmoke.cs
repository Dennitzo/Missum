using System.Collections.Concurrent;
using System.Text.Json;
using Missum.App.Services.Meetings;

namespace Missum.App.Pages;

/// <summary>
/// MISSUM_SMOKE_UI_MEETINGS=live-host / live-guest: a real two-instance meeting with the
/// local model. The host starts the meeting with the virtual camera and microphone
/// (MISSUM_MEETING_VIRTUAL_MEDIA=1) and publishes its link in MISSUM_SMOKE_MEETINGS_LIVE_DIR;
/// the guest joins through that link, turns on its camera and writes chat, notes and a
/// whiteboard note. The host then asks Missum and waits for the answer and the notes.
/// Each side writes native-meetings-live-*.json and PNG captures; failed steps are
/// recorded, not thrown, so one report shows the whole meeting.
/// </summary>
public sealed partial class NativeAssistantPage
{
    private static readonly JsonSerializerOptions LiveReportJson = new() { WriteIndented = true };

    private async Task VerifyMeetingsLiveAsync(bool isHost)
    {
        var shared = Environment.GetEnvironmentVariable("MISSUM_SMOKE_MEETINGS_LIVE_DIR") ?? App.Current.DataDirectory;
        Directory.CreateDirectory(shared);
        var role = isHost ? "host" : "guest";
        var report = new Dictionary<string, object?> { ["role"] = role, ["virtualMedia"] = MeetingVirtualMedia.Enabled };
        var steps = new Dictionary<string, object?>();
        report["steps"] = steps;
        var started = DateTimeOffset.Now;
        var frames = new ConcurrentDictionary<Guid, int>();
        var levels = new ConcurrentDictionary<Guid, double>();
        Meetings.VideoFrame += (participant, _) => frames.AddOrUpdate(participant, 1, (_, count) => count + 1);
        Meetings.ParticipantLevel += (participant, level) => levels.AddOrUpdate(participant, level, (_, old) => Math.Max(old, level));
        var shot = 0;
        async Task Capture(string name) => report["shot-" + name] = await CaptureMeetingsAsync($"live-{role}-{++shot}-{name}.png");
        async Task<bool> Step(string name, int seconds, Func<bool> condition)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                if (clock.Elapsed.TotalSeconds > seconds) { steps[name] = new { ok = false, seconds = Math.Round(clock.Elapsed.TotalSeconds) }; Save(); return false; }
                MarkMeetingsDirty(all: true);
                RunMeetingCallback("Smoke.Step", RenderMeetingsIfDirty);
                await Task.Delay(250);
            }
            steps[name] = new { ok = true, seconds = Math.Round(clock.Elapsed.TotalSeconds, 1) };
            Save();
            return true;
        }
        void Save()
        {
            report["elapsedSeconds"] = Math.Round((DateTimeOffset.Now - started).TotalSeconds);
            report["frames"] = frames.ToDictionary(pair => pair.Key.ToString("D")[..8], pair => pair.Value);
            report["levels"] = levels.ToDictionary(pair => pair.Key.ToString("D")[..8], pair => Math.Round(pair.Value, 3));
            if (Meetings.Room is { } room) report["room"] = room.Read(s => new
            {
                s.Title, s.TitleLocked, s.IsLive,
                participants = s.Participants.Select(p => new { id = p.Id.ToString("D")[..8], p.Name, p.Role, p.MicOn, p.CameraOn, p.Connected }).ToList(),
                chat = s.Chat.Select(c => (c.IsAgent ? "[Missum] " : c.AuthorName + ": ") + c.Text).ToList(),
                transcript = s.Transcript.Select(t => t.SpeakerName + ": " + t.Text).ToList(),
                notes = s.Documents.SelectMany(d => d.Blocks).Where(b => !string.IsNullOrWhiteSpace(b.Text)).Select(b => (b.AiOwned ? "[AI " : "[") + b.Kind + "] " + b.Text).ToList(),
                board = s.Whiteboard.Notes.Select(n => (n.IsAi ? "[AI] " : n.Author + ": ") + n.Text).ToList(),
                agent = s.Agent.State + " " + s.Agent.Status,
            });
            report["view"] = new { Meetings.View.Role, Meetings.View.Connection, Meetings.View.MicOn, Meetings.View.CameraOn, Meetings.View.Error };
            try { File.WriteAllText(Path.Combine(App.Current.DataDirectory, $"native-meetings-live-{role}.json"), JsonSerializer.Serialize(report, LiveReportJson)); }
            catch (IOException) { }
        }

        if (App.Current.MainWindow is { } window) window.AppWindow.Resize(SmokeWindowSize(1640, 1000));
        await NavigateAsync("mode.switch", new { chatMode = MeetingsMode });
        var project = Path.Combine(App.Current.DataDirectory, isHost ? "Live Kundenevent" : "Live Gast");
        Directory.CreateDirectory(project);
        await NavigateAsync("session.projectCreate", new { workspacePath = project, chatMode = MeetingsMode });
        await Step("sessionOpened", 60, () => IsMeetingsMode && Meetings.Room is not null && Meetings.SessionId == _session);
        report["model"] = _settings.Current.SelectedModel;
        report["devices"] = new { cameras = (await MeetingCameraService.ListAsync()).Select(d => d.Name).ToList(), microphones = MeetingAudioService.ListMicrophones().Select(d => d.Name).ToList() };

        if (isHost) await RunLiveHostAsync(shared, Step, Capture, frames, levels, report);
        else await RunLiveGuestAsync(shared, Step, Capture, frames, report);
        Save();
    }

    private async Task RunLiveHostAsync(string shared, Func<string, int, Func<bool>, Task<bool>> step, Func<string, Task> capture,
        ConcurrentDictionary<Guid, int> frames, ConcurrentDictionary<Guid, double> levels, Dictionary<string, object?> report)
    {
        await Meetings.ApplySettingsAsync("Dennis", MeetingVirtualMedia.DeviceId, MeetingVirtualMedia.DeviceId);
        var loadingSeen = false;
        Meetings.OperationApplied += op => { if (op.Type == MeetingOpTypes.AgentStatus && op.Agent?.State == "loading") loadingSeen = true; };
        var titleOps = new List<string>();
        report["titleOps"] = titleOps;
        Meetings.OperationApplied += op =>
        {
            if (op.Type == MeetingOpTypes.RoomTitle)
                lock (titleOps) titleOps.Add($"{DateTimeOffset.Now:HH:mm:ss} {(op.ActorId == MeetingRoom.AgentId ? "Missum" : op.ActorId == Meetings.LocalParticipantId ? "host" : op.ActorId.ToString("D")[..8])}: {op.Text}");
        };
        // "Meeting starten": the meeting goes live only after the model and Whisper are loaded.
        _meetingStartMic = true;
        _meetingStartCamera = true;
        var preparing = new List<string>();
        report["preparing"] = preparing;
        var startTask = StartMeetingFromUiAsync();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!startTask.IsCompleted)
        {
            if (MeetingPreparingStatus is { } status && !preparing.Contains(status))
            {
                preparing.Add(status);
                if (!Meetings.View.IsLive) report["notLiveWhilePreparing"] = true;
                await capture("lobby-preparing");
            }
            if (Meetings.View.IsLive && MeetingPreparingStatus is not null) report["notLiveWhilePreparing"] = false;
            await Task.Delay(100);
        }
        await startTask;
        report["startSeconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
        await step("live", 20, () => Meetings.View.IsLive);
        await step("preparedModelAndWhisper", 1, () => preparing.Count == 2 && App.Current.GetService<MeetingTranscriptionHub>().SpeechReady);
        _ = loadingSeen;
        await step("ownCameraFrames", 20, () => frames.GetValueOrDefault(Meetings.LocalParticipantId) > 10);
        var link = Meetings.View.ShareLink;
        report["link"] = link;
        File.WriteAllText(Path.Combine(shared, "link.txt"), link ?? "");
        await capture("stage-alone");

        // The guest joins through the link.
        var me = Meetings.LocalParticipantId;
        Guid Guest() => Meetings.Room!.Read(s => s.Participants.FirstOrDefault(p => p.Id != me && !p.IsAgent && p.Connected)?.Id ?? Guid.Empty);
        await step("guestJoined", 240, () => Guest() != Guid.Empty);
        var guest = Guest();
        await step("guestCameraOn", 60, () => Meetings.Room!.Read(s => s.Participants.Any(p => p.Id == guest && p.CameraOn)));
        await step("guestVideoFrames", 60, () => frames.GetValueOrDefault(guest) > 10);
        await step("guestAudioLevel", 90, () => levels.GetValueOrDefault(guest) > 0.02);
        // While the guest talks, the name lights green on the stage; the compact overlay shows them.
        await step("guestSpeakingGreen", 30, () => SpeakingParticipants.Contains(guest));
        await capture("stage-with-guest");
        SelectMeetingTab(MeetingTab.Notes);
        await step("overlayShowsSpeaker", 15, () => OverlaySpeakerIds.Contains(guest));
        await Task.Delay(400);
        await capture("overlay-speaker");
        SelectMeetingTab(MeetingTab.Meeting);

        // Speech from both virtual microphones becomes transcript lines.
        await step("hostTranscript", 240, () => Meetings.Room!.Read(s => s.Transcript.Any(t => t.SpeakerId == me)));
        await step("guestTranscript", 240, () => Meetings.Room!.Read(s => s.Transcript.Any(t => t.SpeakerId == guest)));
        await step("guestChat", 120, () => Meetings.Room!.Read(s => s.Chat.Any(c => c.AuthorId == guest)));
        await step("guestNote", 120, () => Meetings.Room!.Read(s => s.Documents.SelectMany(d => d.Blocks).Any(b => b.Authors.Contains(guest))));
        await step("guestBoardNote", 120, () => Meetings.Room!.Read(s => s.Whiteboard.Notes.Any(n => n.AuthorId == guest)));

        // Missum answers a direct question with the selected local model.
        // Ask only once everything said has been transcribed (recognition runs one utterance after another).
        await step("allSpeechTranscribed", 180, () => Meetings.Room!.Read(s =>
            s.Transcript.Any(t => t.Text.Contains("Location bucht", StringComparison.OrdinalIgnoreCase))
            && s.Transcript.Any(t => t.Text.Contains("Buffet", StringComparison.OrdinalIgnoreCase))));
        await step("modelReadyBeforeQuestion", 900, () => Meetings.Room!.Read(s => s.Agent.State != "loading"));
        var titleBefore = Meetings.Room!.Read(s => s.Title);
        Meetings.SendChat("@Missum Was haben wir bisher entschieden und was ist noch offen? Bitte kurz.");
        await step("missumAnswer", 1500, () => Meetings.Room!.Read(s => s.Chat.Any(c => c.IsAgent)));
        await capture("stage-answer");
        // Rebuilding camera tiles on every tab switch must keep the shown frames alive (RO_E_CLOSED crash).
        for (var round = 0; round < 12; round++)
        {
            SelectMeetingTab((MeetingTab)(round % 3 == 0 ? MeetingTab.Meeting : round % 3 == 1 ? MeetingTab.Notes : MeetingTab.Whiteboard));
            MarkMeetingsDirty(all: true);
            RunMeetingCallback("Smoke.TabSwitch", RenderMeetingsIfDirty);
            await Task.Delay(350);
        }
        SelectMeetingTab(MeetingTab.Meeting);
        report["tabSwitchesSurvived"] = true;

        // "Zusammenfassen" fills the notes; Missum also names the meeting.
        Meetings.RequestAgent("summarize");
        await step("missumNotes", 900, () => Meetings.Room!.Read(s => s.Documents.SelectMany(d => d.Blocks).Any(b => b.AiOwned && !string.IsNullOrWhiteSpace(b.Text))));
        await step("missumIdle", 600, () => Meetings.Room!.Read(s => s.Agent.State is "idle" or "done" or "" or null));
        report["titleBefore"] = titleBefore;
        // Missum names the meeting; the sidebar follows without turning it into the host's (locked) name.
        await step("missumNamedMeeting", 120, () => Meetings.Room!.Read(s => !MeetingRoom.IsPlaceholderTitle(s.Title) && !s.TitleLocked)
            && Items(_snapshot, "sessions").Any(item => S(item, "id") == _session.ToString("D") && S(item, "title") == Meetings.Room!.Read(s => s.Title)));
        await Task.Delay(3000);
        report["titleChangedByMissum"] = Meetings.Room!.Read(s => s.Title != titleBefore && !s.TitleLocked);
        SelectMeetingTab(MeetingTab.Notes);
        await Task.Delay(1200);
        await capture("notes");
        SelectMeetingTab(MeetingTab.Whiteboard);
        await Task.Delay(1200);
        await capture("whiteboard");
        SelectMeetingTab(MeetingTab.Meeting);
        await Task.Delay(800);
        await capture("stage-end");
        File.WriteAllText(Path.Combine(shared, "host-done.txt"), DateTimeOffset.Now.ToString("O"));
        // Give the guest time to see the final state before the room closes.
        await step("guestFinished", 120, () => File.Exists(Path.Combine(shared, "guest-done.txt")));

    }

    private async Task RunLiveGuestAsync(string shared, Func<string, int, Func<bool>, Task<bool>> step, Func<string, Task> capture,
        ConcurrentDictionary<Guid, int> frames, Dictionary<string, object?> report)
    {
        await Meetings.ApplySettingsAsync("Ines", MeetingVirtualMedia.DeviceId, MeetingVirtualMedia.DeviceId);
        var linkFile = Path.Combine(shared, "link.txt");
        await step("linkPublished", 300, () => File.Exists(linkFile) && new FileInfo(linkFile).Length > 0);
        var text = File.ReadAllText(linkFile).Trim();
        report["link"] = text;
        if (!MeetingLink.TryParse(text, out var link) || link is null) { report["error"] = "Link nicht lesbar"; return; }
        try { await JoinMeetingAsync(link); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { report["joinError"] = exception.Message; }
        await step("connected", 60, () => Meetings.View.Role == "guest" && Meetings.View.Connection == MeetingConnectionState.Connected);
        await step("microphoneOn", 20, () => Meetings.View.MicOn);
        await Meetings.SetCameraAsync(true);
        await step("cameraOn", 20, () => Meetings.View.CameraOn);
        var me = Meetings.LocalParticipantId;
        Guid Host() => Meetings.Room!.Read(s => s.Participants.FirstOrDefault(p => p.Role == "host")?.Id ?? Guid.Empty);
        await step("seesHost", 30, () => Host() != Guid.Empty);
        await step("hostVideoFrames", 60, () => frames.GetValueOrDefault(Host()) > 10);
        await Task.Delay(1500);
        await capture("stage-joined");

        // Contributions from the guest side.
        Meetings.SendChat("Ich kümmere mich um die Gästeliste, sie steht bis Ende Januar.");
        var document = Meetings.Room!.Read(s => s.Documents[0].Id);
        Meetings.Send(new MeetingOp { Type = MeetingOpTypes.BlockUpsert, DocumentId = document, Block = new MeetingNoteBlock { Kind = "todo", Text = "Ines: Gästeliste bis Ende Januar" } });
        var column = Meetings.Room!.Read(s => s.Whiteboard.Columns.FirstOrDefault()?.Id ?? "");
        Meetings.Send(new MeetingOp { Type = MeetingOpTypes.NoteUpsert, Note = new WhiteboardNote { Text = "Food Trucks statt Buffet?", ColumnId = column, Order = 1 } });
        await step("ownNoteEchoed", 30, () => Meetings.Room!.Read(s => s.Documents.SelectMany(d => d.Blocks).Any(b => b.Authors.Contains(me))));

        // The host's question and Missum's answer reach the guest.
        await step("seesMissumAnswer", 1800, () => Meetings.Room!.Read(s => s.Chat.Any(c => c.IsAgent)));
        await step("seesMissumNotes", 900, () => Meetings.Room!.Read(s => s.Documents.SelectMany(d => d.Blocks).Any(b => b.AiOwned && !string.IsNullOrWhiteSpace(b.Text))));
        await step("hostDone", 900, () => File.Exists(Path.Combine(shared, "host-done.txt")));
        await Task.Delay(2000);
        await capture("stage-end");
        SelectMeetingTab(MeetingTab.Notes);
        await Task.Delay(1200);
        await capture("notes");
        SelectMeetingTab(MeetingTab.Whiteboard);
        await Task.Delay(1200);
        await capture("whiteboard");
        File.WriteAllText(Path.Combine(shared, "guest-done.txt"), DateTimeOffset.Now.ToString("O"));
    }
}
