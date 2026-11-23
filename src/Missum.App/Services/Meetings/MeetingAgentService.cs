using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Missum.Ai.Client;
using Missum.Ai.Contracts;

namespace Missum.App.Services.Meetings;

/// <summary>
/// Missum's seat in every meeting this PC hosts. It reads chat, transcript,
/// notes and whiteboard, asks the local model for a structured update and
/// merges the result back as ordinary host operations. Work is triggered by a
/// quiet period after new content, by a burst of new content, by "@Missum" in
/// the chat, or by the explicit "Von Missum ordnen lassen" button.
/// </summary>
public sealed class MeetingAgentService : IDisposable
{
    internal static readonly TimeSpan QuietDelay = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan BurstDelay = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan MinimumSpacing = TimeSpan.FromSeconds(20);
    // Edits in notes or on the whiteboard: wait until people pause typing, then sync the other areas.
    internal static readonly TimeSpan EditQuietDelay = TimeSpan.FromSeconds(25);
    // Proactive chat hints about synced changes stay rare so the chat never fills with bot noise.
    internal static readonly TimeSpan ChatInfoSpacing = TimeSpan.FromMinutes(2);
    internal const int BurstThreshold = 8;
    private const int MaximumOutputTokens = 2_400;
    // Tool use (web search, page fetches) needs more time than a plain answer.
    private const int TimeoutSeconds = 600;

    /// <summary>
    /// The server tools Codex can use that need no workspace round trip: web search and
    /// page fetches, math and media analysis. They run on the gateway like in Codex.
    /// </summary>
    internal static readonly string[] MeetingServerTools = ["web.search", "web.fetch", "math.evaluate", "media.inspect", "media.analyze"];
    private const int TranscriptWindow = 150;
    private const int ChatWindow = 60;
    private static readonly JsonSerializerOptions ProtocolJson = MissumAiProtocol.CreateJsonOptions();

    private readonly MeetingHostService _host;
    private readonly MissumAiConnectionService _connection;
    private readonly SettingsCoordinator _settings;
    private readonly ILogger<MeetingAgentService> _logger;
    private readonly ConcurrentDictionary<Guid, AgentRoom> _rooms = new();
    private readonly CancellationTokenSource _lifetime = new();

    public MeetingAgentService(
        MeetingHostService host,
        MissumAiConnectionService connection,
        SettingsCoordinator settings,
        ILogger<MeetingAgentService> logger)
    {
        _host = host;
        _connection = connection;
        _settings = settings;
        _logger = logger;
        _host.OperationApplied += OnOperationApplied;
        _host.AgentRequested += OnAgentRequested;
    }

    private void OnOperationApplied(Guid meetingId, MeetingOp op)
    {
        if (op.ActorId == MeetingRoom.AgentId) return;
        var room = _rooms.GetOrAdd(meetingId, static _ => new AgentRoom());
        // Every area feeds one shared change log; Missum carries each change into the other areas.
        var change = MeetingChangeLog.Describe(op,
            id => _host.Read(meetingId, s => s.Documents.FirstOrDefault(d => d.Id == id)?.Title),
            id => _host.Read(meetingId, s => s.Whiteboard.Columns.FirstOrDefault(c => c.Id == id)?.Title),
            id => _host.Read(meetingId, s => s.Whiteboard.Notes.FirstOrDefault(n => n.Id == id)?.Text));
        if (change is not null) lock (room) MeetingChangeLog.Record(room.Changes, change);
        if (change is { IsEdit: true })
        {
            lock (room) room.PendingEdits = true;
            Schedule(meetingId, room, explicitRequest: null);
            return;
        }
        switch (op.Type)
        {
            case MeetingOpTypes.ChatAdd:
            case MeetingOpTypes.TranscriptAdd:
                if (op.Type == MeetingOpTypes.ChatAdd && MeetingHostService.MentionsAgent(op.Chat?.Text)) return;
                lock (room) room.NewContent++;
                Schedule(meetingId, room, explicitRequest: null);
                break;
            case MeetingOpTypes.RoomLive when op.Flag == true:
                SetStatus(meetingId, "listening", "Hört zu");
                break;
            case MeetingOpTypes.RoomLive:
                SetStatus(meetingId, "idle", "Bereit");
                break;
        }
    }

    private void OnAgentRequested(Guid meetingId, MeetingOp op)
    {
        var room = _rooms.GetOrAdd(meetingId, static _ => new AgentRoom());
        var request = op.Type == MeetingOpTypes.ChatAdd && op.Chat is { } chat
            ? new AgentRequest(chat.Text, op.ActorName ?? chat.AuthorName, op.Text)
            : op.BlockId is not null && op.DocumentId is not null && !string.IsNullOrWhiteSpace(op.Text)
                // "@Missum ..." written in the notes: answered right below that line.
                ? new AgentRequest(op.Text, op.ActorName, "note", op.DocumentId, op.BlockId)
                : new AgentRequest(null, op.ActorName, op.Text ?? "organize");
        Schedule(meetingId, room, request);
    }

    private void Schedule(Guid meetingId, AgentRoom room, AgentRequest? explicitRequest)
    {
        TimeSpan delay;
        lock (room)
        {
            if (explicitRequest is not null) room.Requests.Enqueue(explicitRequest);
            if (room.Running)
            {
                room.RunAgain = true;
                return;
            }
            var sinceLast = DateTimeOffset.UtcNow - room.LastRun;
            delay = room.Requests.Count > 0 ? TimeSpan.Zero
                : room.NewContent >= BurstThreshold ? Max(BurstDelay, MinimumSpacing - sinceLast)
                : room.PendingEdits ? Max(EditQuietDelay, MinimumSpacing - sinceLast)
                : Max(QuietDelay, MinimumSpacing - sinceLast);
            room.Debounce?.Cancel();
            room.Debounce = new CancellationTokenSource();
        }
        var token = room.Debounce.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay, token).ConfigureAwait(false);
                await RunAsync(meetingId, room).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;

    private async Task RunAsync(Guid meetingId, AgentRoom room)
    {
        List<AgentRequest> requests;
        List<MeetingChange> changes;
        lock (room)
        {
            if (room.Running) { room.RunAgain = true; return; }
            room.Running = true;
            room.RunAgain = false;
            room.NewContent = 0;
            // One question per run: each gets its own answer; the rest wait for the next run.
            var all = room.Requests.ToList();
            var firstQuestion = all.FirstOrDefault(r => r.Question is not null);
            requests = all.Where(r => r.Question is null || ReferenceEquals(r, firstQuestion)).ToList();
            room.Requests.Clear();
            foreach (var waiting in all.Where(r => r.Question is not null && !ReferenceEquals(r, firstQuestion))) room.Requests.Enqueue(waiting);
            changes = room.Changes.ToList();
            room.Changes.Clear();
            room.PendingEdits = false;
        }
        var cancellation = _lifetime.Token;
        try
        {
            var snapshot = _host.Snapshot(meetingId);
            if (snapshot is null) return;
            var question = requests.LastOrDefault(r => r.Question is not null);
            var prompt = BuildPrompt(snapshot, requests, changes);
            var (_, _, limit) = ResolveSelection(_settings.Current);
            var used = EstimateTokens(prompt);
            SetStatus(meetingId, "thinking", question is not null ? "Denke über die Frage nach …" : SyncStatus(changes), used, limit ?? 0);
            var raw = await AskModelAsync(prompt, (state, status) => SetStatus(meetingId, state, state == "writing"
                ? question?.BlockId is not null ? "Schreibe die Antwort in die Notizen …"
                : question is not null ? "Schreibe die Antwort …" : "Aktualisiere Notizen und Whiteboard …"
                : status, used, limit ?? 0), cancellation).ConfigureAwait(false);
            var result = MeetingAgentMerge.Parse(raw);
            ApplyResult(meetingId, room, result, question);
            SetStatus(meetingId, "done", question is not null ? "Hat geantwortet" : "Notizen aktualisiert");
            await Task.Delay(TimeSpan.FromSeconds(4), cancellation).ConfigureAwait(false);
            var live = _host.Read(meetingId, static s => s.IsLive);
            SetStatus(meetingId, live ? "listening" : "idle", live ? "Hört zu" : "Bereit");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            MeetingLog.AgentFailed(_logger, exception);
            // Nothing is lost: the changes are carried into the next attempt.
            lock (room) room.Changes.InsertRange(0, changes);
            SetStatus(meetingId, "error", "AI nicht erreichbar");
            if (requests.LastOrDefault(r => r.Question is not null) is { } failedQuestion)
            {
                var message = $"Ich konnte gerade nicht antworten ({FriendlyError(exception)}). Frag gern gleich noch einmal, {failedQuestion.Author}.";
                if (failedQuestion.BlockId is not null) InsertNoteAnswer(meetingId, failedQuestion, message);
                else _host.ApplyLocal(meetingId, AgentChat(message));
            }
        }
        finally
        {
            bool again;
            lock (room)
            {
                room.Running = false;
                room.LastRun = DateTimeOffset.UtcNow;
                again = room.RunAgain || room.Requests.Count > 0;
            }
            if (again && !cancellation.IsCancellationRequested) Schedule(meetingId, room, explicitRequest: null);
        }
    }

    /// <summary>A camera-tile line that says which areas Missum is bringing together.</summary>
    internal static string SyncStatus(IReadOnlyList<MeetingChange> changes)
    {
        var areas = changes.Where(c => c.IsEdit).Select(c => c.Area).Distinct().ToList();
        return areas.Count switch
        {
            0 => "Lese mit …",
            1 => $"Übernehme {areas[0]} in die anderen Bereiche …",
            _ => $"Gleiche {string.Join(" und ", areas)} ab …",
        };
    }

    private void ApplyResult(Guid meetingId, AgentRoom room, AgentResult result, AgentRequest? question)
    {
        var current = _host.Snapshot(meetingId);
        if (current is null) return;
        if (current.Documents.FirstOrDefault() is { } notes)
        {
            var merged = MeetingAgentMerge.MergeNotes(notes.Blocks, result.Notes);
            _host.ApplyLocal(meetingId, new MeetingOp
            {
                Type = MeetingOpTypes.DocumentSetBlocks,
                ActorId = MeetingRoom.AgentId,
                ActorName = MeetingRoom.AgentName,
                DocumentId = notes.Id,
                Blocks = merged,
            });
        }
        // Re-read: people may have moved notes while the model was thinking.
        current = _host.Snapshot(meetingId) ?? current;
        var board = MeetingAgentMerge.MergeWhiteboard(current.Whiteboard, result);
        _host.ApplyLocal(meetingId, new MeetingOp
        {
            Type = MeetingOpTypes.WhiteboardSet,
            ActorId = MeetingRoom.AgentId,
            ActorName = MeetingRoom.AgentName,
            Whiteboard = board,
        });
        if (question is not null && result.ChatReply is { } reply)
        {
            if (question.BlockId is not null) InsertNoteAnswer(meetingId, question, reply);
            else _host.ApplyLocal(meetingId, AgentChat(reply));
        }
        else if (result.ChatInfo is { } info)
        {
            // A short heads-up in the chat when notes or whiteboard changed noticeably — rarely.
            bool post;
            lock (room)
            {
                post = DateTimeOffset.UtcNow - room.LastChatInfo >= ChatInfoSpacing;
                if (post) room.LastChatInfo = DateTimeOffset.UtcNow;
            }
            if (post) _host.ApplyLocal(meetingId, AgentChat(info));
        }
    }

    /// <summary>Places Missum's answer as its own block directly below the note that asked.</summary>
    private void InsertNoteAnswer(Guid meetingId, AgentRequest question, string answer) =>
        _host.ApplyLocal(meetingId, new MeetingOp
        {
            Type = MeetingOpTypes.BlockUpsert,
            ActorId = MeetingRoom.AgentId,
            ActorName = MeetingRoom.AgentName,
            DocumentId = question.DocumentId,
            AfterId = question.BlockId,
            Block = new MeetingNoteBlock { Kind = "p", Text = answer, Section = AnswerSection, AiOwned = true },
        });

    internal const string AnswerSection = "ai.answer";

    private static MeetingOp AgentChat(string text) => new()
    {
        Type = MeetingOpTypes.ChatAdd,
        ActorId = MeetingRoom.AgentId,
        ActorName = MeetingRoom.AgentName,
        Chat = new MeetingChatMessage(Guid.NewGuid(), MeetingRoom.AgentId, MeetingRoom.AgentName, text, DateTimeOffset.UtcNow, true),
    };

    private void SetStatus(Guid meetingId, string state, string status, int contextUsed = 0, int contextLimit = 0) =>
        _host.ApplyLocal(meetingId, new MeetingOp
        {
            Type = MeetingOpTypes.AgentStatus,
            ActorId = MeetingRoom.AgentId,
            Agent = new MeetingAgentStatus { State = state, Status = status, ContextUsed = contextUsed, ContextLimit = contextLimit },
        });

    internal static string BuildPrompt(MeetingRoomState state, IReadOnlyList<AgentRequest> requests, IReadOnlyList<MeetingChange>? changes = null)
    {
        var builder = new StringBuilder();
        builder.Append("""
            Du bist Missum und nimmst still an einem Meeting teil. Du bereitest Gespräch, Chat und Notizen live auf.
            Antworte ausschließlich mit einem JSON-Objekt genau in dieser Form, ohne Markdown-Codeblock:
            {"chatReply": string oder null, "chatInfo": string oder null,
             "notes": {"summary": string, "decisions": [string], "actionItems": [{"task": string, "owner": string oder null, "due": string oder null}], "openQuestions": [string]},
             "whiteboard": {"columns": [{"id": string oder null, "title": string, "subtitle": string oder null, "noteIds": [string]}], "newNotes": [{"text": string, "column": string}], "removeNoteIds": [string]}}

            Regeln:
            - Schreibe auf Deutsch, kurz, klar und sachlich. Erfinde nichts, was nicht gesagt oder geschrieben wurde.
            - chatReply nur, wenn unten eine Frage an Missum steht. Beantworte sie dann direkt in 1 bis 4 Sätzen. Sonst null.
            - summary: 1 bis 3 Sätze über das bisherige Meeting.
            - decisions: nur echte, getroffene Entscheidungen. actionItems: konkrete Aufgaben mit Person und Termin, falls genannt.
            - openQuestions: Fragen, die noch offen sind.
            - Notizen mit [von Teilnehmer bearbeitet] sind verbindlich: widersprich ihnen nicht und wiederhole sie nicht.
            - Whiteboard: Ordne alle vorhandenen Haftnotizen über noteIds in 2 bis 5 sinnvolle Spalten mit kurzem Titel und Untertitel.
              Nutze vorhandene Spalten-IDs, wenn die Spalte passt. Formuliere Notizen von Teilnehmern niemals um.
            - newNotes nur für wichtige Punkte aus Gespräch oder Chat, die noch auf keiner Haftnotiz stehen (höchstens 6, je unter 80 Zeichen).
            - removeNoteIds nur für eigene, veraltete Missum-Notizen.
            - Gibt es noch keinen Inhalt, liefere leere Listen.
            - Meeting, Gespräch, Chat, Notizen und Whiteboard gehören zusammen. Halte sie synchron:
              Was in einem Bereich neu ist oder geändert wurde (siehe "Änderungen seit deinem letzten Durchgang"),
              übernimmst du sinngemäß in die anderen: Entscheidungen, Aufgaben und offene Fragen aus Chat, Gespräch,
              den Notes und dem Whiteboard gehören in Missums Live-Notizen; wichtige Punkte aus Notizen, Chat und Gespräch
              gehören als Haftnotiz auf das Whiteboard; erledigte Aufgaben und verschobene Notizen ändern Zusammenfassung und Listen.
            - chatInfo: höchstens ein kurzer Satz an alle, nur wenn du Änderungen aus Notizen oder Whiteboard spürbar in andere
              Bereiche übernommen hast (z. B. "Ich habe die neue Entscheidung vom Whiteboard in die Notizen übernommen."). Sonst null.
            - Du hast Werkzeuge: Websuche (web.search), Webseiten abrufen (web.fetch), Rechnen (math.evaluate) und Bildanalyse.
              Nutze sie, wenn eine Frage aktuelle oder externe Informationen braucht, statt zu sagen, du hättest keinen Zugriff.
              Nenne in chatReply kurz die Quelle (Titel oder Domain). Gib nach der Werkzeugnutzung trotzdem nur das JSON aus.

            """);

        builder.Append("Meeting: ").Append(state.Title).Append('\n');
        var people = state.Participants.Where(p => !p.IsAgent).Select(p => p.Name).Distinct().ToArray();
        builder.Append("Teilnehmer: ").Append(people.Length == 0 ? "–" : string.Join(", ", people)).Append("\n\n");

        builder.Append("Transkript (neueste zuletzt):\n");
        foreach (var entry in state.Transcript.TakeLast(TranscriptWindow))
            builder.Append('[').Append(entry.At.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)).Append("] ")
                .Append(entry.SpeakerName).Append(": ").Append(Shorten(entry.Text, 400)).Append('\n');
        if (state.Transcript.Count == 0) builder.Append("(noch nichts gesprochen)\n");

        builder.Append("\nChat (neueste zuletzt):\n");
        foreach (var message in state.Chat.TakeLast(ChatWindow))
            builder.Append('[').Append(message.At.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)).Append("] ")
                .Append(message.AuthorName).Append(": ").Append(Shorten(message.Text, 600)).Append('\n');
        if (state.Chat.Count == 0) builder.Append("(noch keine Nachrichten)\n");

        if (state.Documents.FirstOrDefault() is { } notes)
        {
            builder.Append("\nAktuelle Notizen (\"").Append(notes.Title).Append("\", hier schreibst du mit):\n");
            foreach (var block in notes.Blocks)
            {
                if (string.IsNullOrWhiteSpace(block.Text)) continue;
                builder.Append("- ").Append(block.Kind == "todo" ? block.Done ? "[x] " : "[ ] " : "").Append(Shorten(block.Text, 300));
                if (block.HumanEdited || !block.AiOwned) builder.Append(" [von Teilnehmer bearbeitet]");
                builder.Append('\n');
            }
        }

        builder.Append("\nWhiteboard-Spalten:\n");
        foreach (var column in state.Whiteboard.Columns)
            builder.Append("- id=").Append(column.Id).Append(" \"").Append(column.Title).Append('"')
                .Append(column.Subtitle is null ? "" : " (" + column.Subtitle + ")").Append('\n');
        builder.Append("Haftnotizen:\n");
        foreach (var note in state.Whiteboard.Notes)
            builder.Append("- id=").Append(note.Id).Append(" spalte=").Append(note.ColumnId)
                .Append(" von ").Append(note.IsAi ? "Missum" : note.Author).Append(": ").Append(Shorten(note.Text, 200)).Append('\n');
        if (state.Whiteboard.Notes.Count == 0) builder.Append("(noch keine)\n");
        // Free elements are people's own content: context only, never changed by the agent.
        var elements = state.Whiteboard.Elements.Where(e => e.Kind is "text" or "shape" or "table").ToList();
        if (elements.Count > 0)
        {
            builder.Append("Weitere Inhalte auf dem Whiteboard (nur lesen):\n");
            foreach (var element in elements.Take(40))
            {
                var text = element.Kind == "table" && element.Rows is { } rows
                    ? string.Join(" | ", rows.Take(12).Select(row => string.Join(", ", row.Where(cell => cell.Length > 0))))
                    : element.Text;
                if (!string.IsNullOrWhiteSpace(text))
                    builder.Append("- ").Append(element.Kind == "table" ? "Tabelle: " : "").Append(Shorten(text, 400)).Append('\n');
            }
        }

        if (changes is { Count: > 0 })
        {
            builder.Append("\nÄnderungen seit deinem letzten Durchgang (übernimm sie in alle anderen Bereiche):\n");
            foreach (var change in changes.TakeLast(50))
                builder.Append("- [").Append(change.Area).Append("] ").Append(change.Actor).Append(' ').Append(change.Text).Append('\n');
        }

        var questions = requests.Where(r => r.Question is not null).ToArray();
        if (questions.Length > 0)
        {
            builder.Append(questions.Any(q => q.BlockId is not null)
                ? "\nFrage an Missum (in den Notizen; antworte in chatReply, die Antwort wird unter die Zeile geschrieben):\n"
                : "\nFrage an Missum:\n");
            foreach (var request in questions)
                builder.Append(request.Author ?? "Teilnehmer").Append(": ").Append(Shorten(request.Question!, 800)).Append('\n');
        }
        else if (requests.Any(r => r.Reason == "organize"))
        {
            builder.Append("\nAuftrag: Ordne das Whiteboard jetzt gründlich neu und fasse die Notizen zusammen.\n");
        }
        return builder.ToString();
    }

    private static string Shorten(string text, int maximum)
    {
        var value = text.Replace('\n', ' ').Trim();
        return value.Length <= maximum ? value : value[..maximum] + "…";
    }

    /// <summary>
    /// Runs the model and reports what Missum is doing for the camera tile:
    /// connecting, waiting, loading the model, thinking (reasoning) and writing.
    /// </summary>
    private async Task<string> AskModelAsync(string prompt, Action<string, string> progress, CancellationToken cancellationToken)
    {
        progress("loading", "Verbinde mit dem AI-Dienst …");
        using var client = await _connection.CreateClientAsync(cancellationToken).ConfigureAwait(false);
        var (modelId, reasoning, contextLimit) = ResolveSelection(_settings.Current);
        var request = new RunRequest(
            MissumAiProtocol.Version,
            RunMode.General,
            [new RunMessage("user", [new ContentPart("text", Text: prompt)])],
            ClientCapabilities: [],
            Limits: new RunLimits(MaximumOutputTokens, contextLimit, TimeoutSeconds),
            // A normal conversation run (not the tool-less context preparation profile), so
            // Missum can look things up on the web like it does in Codex.
            AllowedServerTools: MeetingServerTools,
            PreferredGeneralModelId: string.IsNullOrWhiteSpace(modelId) ? null : modelId,
            ConversationProfile: null,
            ReasoningEffort: reasoning);
        var accepted = await client.CreateRunAsync(request, $"meeting-{Guid.NewGuid():N}", cancellationToken).ConfigureAwait(false);
        var content = new StringBuilder();
        var phase = "";
        void Report(string state, string status)
        {
            if (phase == state + status) return;
            phase = state + status;
            progress(state, status);
        }
        Report("loading", "Wartet auf das AI-Modell …");
        try
        {
            await foreach (var item in client.StreamRunEventsAsync(accepted.RunId, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                switch (item.Type)
                {
                    case RunEventTypes.QueueChanged when content.Length == 0:
                        Report("loading", "Wartet in der Warteschlange …");
                        break;
                    case RunEventTypes.ModelLoading when content.Length == 0:
                        Report("loading", "AI-Modell wird geladen …");
                        break;
                    case RunEventTypes.ModelSelected or RunEventTypes.RunStarted or RunEventTypes.ReasoningDelta when content.Length == 0:
                        Report("thinking", "Denke nach …");
                        break;
                    case RunEventTypes.ServerToolStarted:
                        Report("thinking", ToolStatus(item.Data));
                        break;
                    case RunEventTypes.TextDelta:
                        Report("writing", "Schreibe …");
                        content.Append(item.Data.Deserialize<TextDeltaEvent>(ProtocolJson)?.Delta);
                        break;
                    case RunEventTypes.RunFailed:
                        throw new InvalidOperationException(item.Data.Deserialize<RunFailedEvent>(ProtocolJson)?.Message ?? "Der Lauf ist fehlgeschlagen.");
                    case RunEventTypes.RunCancelled:
                        throw new OperationCanceledException("Der Lauf wurde abgebrochen.");
                    case RunEventTypes.RunCompleted:
                        return content.ToString();
                }
            }
            throw new InvalidDataException("Der Lauf endete ohne Abschluss.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { await client.CancelRunAsync(accepted.RunId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { _ = exception; }
            throw;
        }
    }

    /// <summary>The host's choice from the meeting chat picker (shared with Codex): model, reasoning, context.</summary>
    internal static (string? ModelId, string? Reasoning, int? ContextLimit) ResolveSelection(Missum.Core.Models.AppSettings settings)
    {
        var modelId = settings.SelectedModel;
        if (string.IsNullOrWhiteSpace(modelId)) return (null, null, null);
        return (modelId,
            MissumAiAssistantService.StoredReasoning(settings, modelId, "coding"),
            ModelContextSelection.Resolve(settings, modelId, ModelContextProfiles.ResolveMaximum(modelId, "coding")));
    }

    internal static int EstimateTokens(string text) => (text.Length + 3) / 4;

    /// <summary>A short German status for the camera tile while a server tool runs.</summary>
    internal static string ToolStatus(JsonElement data)
    {
        string? Name(string property) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var tool = Name("tool") ?? Name("name") ?? Name("toolName") ?? "";
        return tool switch
        {
            "web.search" => "Suche im Web …",
            "web.fetch" => "Lese eine Webseite …",
            "math.evaluate" => "Rechne …",
            "media.inspect" or "media.analyze" => "Sehe mir ein Bild an …",
            _ => "Nutze ein Werkzeug …",
        };
    }

    private static string FriendlyError(Exception exception) => exception switch
    {
        HttpRequestException => "der lokale AI-Dienst ist nicht erreichbar",
        InvalidOperationException { Message.Length: > 0 and < 160 } invalid => invalid.Message.TrimEnd('.'),
        _ => "unerwarteter Fehler",
    };

    public void Dispose()
    {
        _host.OperationApplied -= OnOperationApplied;
        _host.AgentRequested -= OnAgentRequested;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    internal sealed record AgentRequest(string? Question, string? Author, string? Reason, string? DocumentId = null, string? BlockId = null);

    private sealed class AgentRoom
    {
        public int NewContent;
        public bool Running;
        public bool RunAgain;
        public DateTimeOffset LastRun = DateTimeOffset.MinValue;
        public DateTimeOffset LastChatInfo = DateTimeOffset.MinValue;
        public bool PendingEdits;
        public List<MeetingChange> Changes { get; } = [];
        public CancellationTokenSource? Debounce;
        public Queue<AgentRequest> Requests { get; } = new();
    }
}
