using System.Text.Json;

namespace Missum.App.Services.Meetings;

public sealed record AgentActionItem(string Task, string? Owner, string? Due);

public sealed record AgentNotes(
    string? Summary,
    IReadOnlyList<string>? Decisions,
    IReadOnlyList<AgentActionItem>? ActionItems,
    IReadOnlyList<string>? OpenQuestions);

public sealed record AgentColumn(string? Id, string Title, string? Subtitle, IReadOnlyList<string> NoteIds);

public sealed record AgentNewNote(string Text, string? Column);

public sealed record AgentResult(
    string? ChatReply,
    AgentNotes? Notes,
    IReadOnlyList<AgentColumn> Columns,
    IReadOnlyList<AgentNewNote> NewNotes,
    IReadOnlyList<string> RemoveNoteIds,
    string? ChatInfo = null);

/// <summary>
/// Turns the model's proposal into concrete note and whiteboard state. The
/// rules are deliberately conservative: Missum only rewrites what Missum wrote
/// and nobody touched since; content written or edited by people is never
/// removed or rephrased, only sorted into columns.
/// </summary>
public static class MeetingAgentMerge
{
    public const string RootSection = "ai.root";
    public const string AgentAuthor = MeetingRoom.AgentName;
    public const int AgentColorIndex = 7;

    internal static readonly (string Key, string Title, string Kind)[] Sections =
    [
        ("ai.summary", "Zusammenfassung", "p"),
        ("ai.decisions", "Entscheidungen", "bullet"),
        ("ai.actions", "Aufgaben", "todo"),
        ("ai.questions", "Offene Fragen", "bullet"),
    ];

    public static AgentResult Parse(string raw)
    {
        var text = (raw ?? "").Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("Missum hat kein auswertbares JSON geliefert.");
        using var document = JsonDocument.Parse(text[start..(end + 1)], new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Missum hat kein JSON-Objekt geliefert.");

        AgentNotes? notes = null;
        if (root.TryGetProperty("notes", out var notesElement) && notesElement.ValueKind == JsonValueKind.Object)
        {
            notes = new AgentNotes(
                String(notesElement, "summary"),
                Strings(notesElement, "decisions"),
                notesElement.TryGetProperty("actionItems", out var actions) && actions.ValueKind == JsonValueKind.Array
                    ? actions.EnumerateArray().Select(ActionItem).Where(item => item is not null).Select(item => item!).ToList()
                    : null,
                Strings(notesElement, "openQuestions"));
        }

        var columns = new List<AgentColumn>();
        var newNotes = new List<AgentNewNote>();
        var remove = new List<string>();
        if (root.TryGetProperty("whiteboard", out var board) && board.ValueKind == JsonValueKind.Object)
        {
            if (board.TryGetProperty("columns", out var columnElements) && columnElements.ValueKind == JsonValueKind.Array)
                foreach (var column in columnElements.EnumerateArray())
                {
                    var title = String(column, "title");
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    columns.Add(new AgentColumn(String(column, "id"), title, String(column, "subtitle"), Strings(column, "noteIds") ?? []));
                }
            if (board.TryGetProperty("newNotes", out var noteElements) && noteElements.ValueKind == JsonValueKind.Array)
                foreach (var note in noteElements.EnumerateArray())
                {
                    var noteText = note.ValueKind == JsonValueKind.String ? note.GetString() : String(note, "text");
                    if (!string.IsNullOrWhiteSpace(noteText)) newNotes.Add(new AgentNewNote(noteText.Trim(), String(note, "column")));
                }
            remove.AddRange(Strings(board, "removeNoteIds") ?? []);
        }

        var reply = String(root, "chatReply");
        var info = String(root, "chatInfo");
        return new AgentResult(string.IsNullOrWhiteSpace(reply) ? null : reply.Trim(), notes, columns, newNotes.Take(8).ToList(), remove,
            string.IsNullOrWhiteSpace(info) ? null : MeetingRoom.Clip(info, 300, ""));
    }

    public static List<MeetingNoteBlock> MergeNotes(IReadOnlyList<MeetingNoteBlock> existing, AgentNotes? notes)
    {
        var blocks = existing.Select(MeetingJson.Clone).ToList();
        if (notes is null) return blocks;

        var root = blocks.FirstOrDefault(b => b.Section == RootSection);
        if (root is null)
        {
            root = new MeetingNoteBlock { Kind = "h2", Text = "Missums Live-Notizen", Section = RootSection, AiOwned = true };
            blocks.Add(root);
        }
        else if (root.AiOwned && !root.HumanEdited)
        {
            // Older meetings still carry the previous agent name in the heading.
            root.Text = "Missums Live-Notizen";
        }

        var anchor = root;
        foreach (var (key, title, kind) in Sections)
        {
            var heading = blocks.FirstOrDefault(b => b.Section == key + ".h");
            if (heading is null)
            {
                heading = new MeetingNoteBlock { Kind = "h3", Text = title, Section = key + ".h", AiOwned = true };
                blocks.Insert(LastIndexOfSection(blocks, anchor) + 1, heading);
            }

            var proposed = ProposedItems(key, notes);
            if (proposed is not null)
            {
                var kept = blocks.Where(b => b.Section == key && (b.HumanEdited || !b.AiOwned)).ToList();
                blocks.RemoveAll(b => b.Section == key && b.AiOwned && !b.HumanEdited);
                var known = kept.Select(b => Normalize(b.Text)).ToHashSet(StringComparer.Ordinal);
                var insertAt = LastIndexOfSection(blocks, heading) + 1;
                foreach (var item in proposed)
                {
                    var normalized = Normalize(item);
                    if (normalized.Length == 0 || !known.Add(normalized)) continue;
                    blocks.Insert(insertAt++, new MeetingNoteBlock { Kind = kind, Text = item.Trim(), Section = key, AiOwned = true });
                }
            }
            anchor = heading;
        }
        return blocks;
    }

    private static IReadOnlyList<string>? ProposedItems(string key, AgentNotes notes) => key switch
    {
        "ai.summary" => string.IsNullOrWhiteSpace(notes.Summary) ? null : [notes.Summary.Trim()],
        "ai.decisions" => notes.Decisions,
        "ai.actions" => notes.ActionItems?.Select(FormatAction).ToList(),
        "ai.questions" => notes.OpenQuestions,
        _ => null,
    };

    internal static string FormatAction(AgentActionItem item)
    {
        var task = item.Task.Trim();
        var text = string.IsNullOrWhiteSpace(item.Owner) || task.StartsWith(item.Owner.Trim(), StringComparison.OrdinalIgnoreCase)
            ? task
            : $"{item.Owner.Trim()}: {task}";
        return string.IsNullOrWhiteSpace(item.Due) ? text : $"{text} (bis {item.Due.Trim()})";
    }

    /// <summary>The index of the last block that belongs to <paramref name="heading"/>'s section.</summary>
    private static int LastIndexOfSection(List<MeetingNoteBlock> blocks, MeetingNoteBlock heading)
    {
        var index = blocks.IndexOf(heading);
        if (index < 0) return blocks.Count - 1;
        var key = heading.Section is { } section && section.EndsWith(".h", StringComparison.Ordinal) ? section[..^2] : null;
        if (key is null) return index;
        // Items of a section may be interleaved with people's own blocks; the
        // section ends at its last item, so new items join the existing ones.
        var last = index;
        for (var i = index + 1; i < blocks.Count; i++)
        {
            if (blocks[i].Section == key) last = i;
            else if (blocks[i].Section is { } other && other.StartsWith("ai.", StringComparison.Ordinal)) break;
        }
        return last;
    }

    public static MeetingWhiteboard MergeWhiteboard(MeetingWhiteboard existing, AgentResult result)
    {
        var board = MeetingJson.Clone(existing);
        board.Columns ??= [];
        board.Notes ??= [];

        // Missum may only retire its own untouched notes that nobody reacted to.
        var removable = result.RemoveNoteIds.ToHashSet(StringComparer.Ordinal);
        board.Notes.RemoveAll(n => removable.Contains(n.Id) && n.IsAi && !n.HumanEdited && n.Reactions.Count == 0);

        var resolved = new List<WhiteboardColumn>();
        foreach (var proposal in result.Columns)
        {
            var column = board.Columns.FirstOrDefault(c => c.Id == proposal.Id)
                ?? board.Columns.FirstOrDefault(c => string.Equals(c.Title.Trim(), proposal.Title.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? resolved.FirstOrDefault(c => string.Equals(c.Title.Trim(), proposal.Title.Trim(), StringComparison.OrdinalIgnoreCase));
            if (column is null)
            {
                column = new WhiteboardColumn { Title = MeetingRoom.Clip(proposal.Title, 80, "Spalte"), Subtitle = Subtitle(proposal.Subtitle), IsAi = true };
                board.Columns.Add(column);
            }
            else if (column.IsAi)
            {
                column.Title = MeetingRoom.Clip(proposal.Title, 80, column.Title);
                column.Subtitle = Subtitle(proposal.Subtitle) ?? column.Subtitle;
            }
            if (resolved.Contains(column)) continue;
            resolved.Add(column);

            var order = 1;
            foreach (var noteId in proposal.NoteIds)
            {
                var note = board.Notes.FirstOrDefault(n => n.Id == noteId);
                if (note is null) continue;
                note.ColumnId = column.Id;
                note.Order = order++;
            }
        }

        var known = board.Notes.Select(n => Normalize(n.Text)).ToHashSet(StringComparer.Ordinal);
        foreach (var proposal in result.NewNotes)
        {
            var normalized = Normalize(proposal.Text);
            if (normalized.Length == 0 || !known.Add(normalized) || board.Notes.Count >= MeetingRoom.MaximumNotes) continue;
            var column = resolved.FirstOrDefault(c => c.Id == proposal.Column)
                ?? resolved.FirstOrDefault(c => string.Equals(c.Title, proposal.Column?.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? resolved.FirstOrDefault()
                ?? board.Columns.FirstOrDefault();
            if (column is null)
            {
                column = new WhiteboardColumn { Title = "Sammlung", IsAi = true };
                board.Columns.Add(column);
            }
            board.Notes.Add(new WhiteboardNote
            {
                Text = MeetingRoom.Clip(proposal.Text, 600, ""),
                Author = AgentAuthor,
                AuthorId = MeetingRoom.AgentId,
                ColorIndex = AgentColorIndex,
                ColumnId = column.Id,
                Order = board.Notes.Where(n => n.ColumnId == column.Id).Select(n => n.Order).DefaultIfEmpty(0).Max() + 1,
                IsAi = true,
            });
        }

        if (resolved.Count > 0)
        {
            // Proposed columns come first in Missum's order; people's other columns follow unchanged.
            var rest = board.Columns.Where(c => !resolved.Contains(c)).ToList();
            board.Columns = resolved.Concat(rest).ToList();
            var used = board.Notes.Select(n => n.ColumnId).ToHashSet(StringComparer.Ordinal);
            board.Columns.RemoveAll(c => !resolved.Contains(c) && !used.Contains(c.Id) && (c.IsAi || IsDefaultInbox(c)) && board.Columns.Count > 1);
        }

        if (board.Columns.Count == 0) board.Columns.Add(new WhiteboardColumn { Title = "Sammlung" });
        var columnIds = board.Columns.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var note in board.Notes.Where(n => !columnIds.Contains(n.ColumnId))) note.ColumnId = board.Columns[0].Id;
        foreach (var group in board.Notes.GroupBy(n => n.ColumnId))
        {
            var order = 1;
            foreach (var note in group.OrderBy(n => n.Order).ThenBy(n => n.UpdatedAt)) note.Order = order++;
        }
        return board;
    }

    private static bool IsDefaultInbox(WhiteboardColumn column) =>
        string.Equals(column.Title, "Sammlung", StringComparison.Ordinal);

    private static string? Subtitle(string? value) => string.IsNullOrWhiteSpace(value) ? null : MeetingRoom.Clip(value, 140, "");

    internal static string Normalize(string? text) =>
        new string((text ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static AgentActionItem? ActionItem(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return string.IsNullOrWhiteSpace(element.GetString()) ? null : new AgentActionItem(element.GetString()!, null, null);
        var task = String(element, "task");
        return string.IsNullOrWhiteSpace(task) ? null : new AgentActionItem(task, String(element, "owner"), String(element, "due"));
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                _ => null,
            }
            : null;

    private static List<string>? Strings(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return null;
        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ValueKind == JsonValueKind.Object ? String(item, "text") : null)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!.Trim())
            .ToList();
    }
}
