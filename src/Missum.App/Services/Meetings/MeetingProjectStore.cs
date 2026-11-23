using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Missum.App.Services.Meetings;

/// <summary>Who owns a stored meeting and how a guest gets back into it.</summary>
public sealed record MeetingFolderInfo(
    Guid SessionId,
    Guid MeetingId,
    string Role,
    string? JoinLink,
    string Title,
    DateTimeOffset CreatedAt,
    string? HostToken = null);

/// <summary>
/// Stores one meeting per Missum session in the session's project folder:
/// <c>&lt;Projekt&gt;\Meetings\&lt;Datum&gt;-&lt;Titel&gt;-&lt;Sitzung&gt;\</c>. Notes are plain
/// Markdown (block metadata lives in HTML comments), the whiteboard is JSON and
/// chat/transcript are append-friendly JSON lines. Every file is written
/// atomically so an interrupted save never leaves a half-written document.
/// </summary>
public static partial class MeetingProjectStore
{
    public const string FolderName = "Meetings";
    private const string MeetingFile = "meeting.json";
    private const string WhiteboardFile = "whiteboard.json";
    private const string ChatFile = "chat.jsonl";
    private const string TranscriptFile = "transcript.jsonl";
    private const string NotesFolder = "notes";
    private const string AssetsFolder = "assets";
    private const string AssetPrefix = "asset:";

    /// <summary>
    /// The meetings root inside a project. Meetings always belong to a project,
    /// so without a valid project folder there is no root (and nothing is written).
    /// </summary>
    public static string? ResolveRoot(string? workspacePath)
    {
        if (!string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(workspacePath))
            return Path.Combine(Path.GetFullPath(workspacePath), FolderName);
        return null;
    }

    /// <summary>Finds the folder owned by <paramref name="sessionId"/>, if one exists.</summary>
    public static string? FindFolder(string root, Guid sessionId)
    {
        if (!Directory.Exists(root)) return null;
        var suffix = "-" + ShortId(sessionId);
        return Directory.EnumerateDirectories(root)
            .Where(path => Path.GetFileName(path).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public static string CreateFolderPath(string root, Guid sessionId, string title, DateTimeOffset createdAt)
    {
        var name = $"{createdAt.ToLocalTime():yyyy-MM-dd}-{Slug(title)}-{ShortId(sessionId)}";
        return Path.Combine(root, name);
    }

    public static async Task<(MeetingFolderInfo Info, MeetingRoomState State)?> LoadAsync(
        string folder,
        CancellationToken cancellationToken = default)
    {
        var meetingPath = Path.Combine(folder, MeetingFile);
        if (!File.Exists(meetingPath)) return null;
        var manifest = JsonSerializer.Deserialize<MeetingManifest>(
            await File.ReadAllTextAsync(meetingPath, cancellationToken).ConfigureAwait(false), MeetingJson.Options);
        if (manifest is null || manifest.MeetingId == Guid.Empty) return null;

        var state = new MeetingRoomState
        {
            Id = manifest.MeetingId,
            Title = manifest.Title,
            CreatedAt = manifest.CreatedAt,
            Revision = manifest.Revision,
            Participants = manifest.Participants ?? [],
        };
        foreach (var participant in state.Participants)
        {
            participant.Connected = participant.IsAgent;
            participant.MicOn = false;
            participant.CameraOn = false;
        }

        foreach (var entry in manifest.Documents ?? [])
        {
            var notePath = Path.Combine(folder, NotesFolder, entry.File);
            if (!IsInside(folder, notePath) || !File.Exists(notePath)) continue;
            var markdown = await File.ReadAllTextAsync(notePath, cancellationToken).ConfigureAwait(false);
            state.Documents.Add(new MeetingDocument { Id = entry.Id, Title = entry.Title, Blocks = ParseMarkdown(markdown) });
        }

        var boardPath = Path.Combine(folder, WhiteboardFile);
        if (File.Exists(boardPath))
            state.Whiteboard = JsonSerializer.Deserialize<MeetingWhiteboard>(
                await File.ReadAllTextAsync(boardPath, cancellationToken).ConfigureAwait(false), MeetingJson.Options) ?? new();
        state.Whiteboard.Elements ??= [];
        foreach (var element in state.Whiteboard.Elements.Where(e => e.ImageData?.StartsWith(AssetPrefix, StringComparison.Ordinal) == true).ToList())
        {
            var assetPath = Path.Combine(folder, AssetsFolder, element.ImageData![AssetPrefix.Length..]);
            if (IsInside(folder, assetPath) && File.Exists(assetPath))
                element.ImageData = Convert.ToBase64String(await File.ReadAllBytesAsync(assetPath, cancellationToken).ConfigureAwait(false));
            else
                state.Whiteboard.Elements.Remove(element);
        }

        state.Chat = await ReadLinesAsync<MeetingChatMessage>(Path.Combine(folder, ChatFile), MeetingRoom.MaximumChat, cancellationToken).ConfigureAwait(false);
        state.Transcript = await ReadLinesAsync<MeetingTranscriptEntry>(Path.Combine(folder, TranscriptFile), MeetingRoom.MaximumTranscript, cancellationToken).ConfigureAwait(false);
        MeetingRoom.Normalize(state);
        return (new MeetingFolderInfo(manifest.SessionId, manifest.MeetingId, manifest.Role, manifest.JoinLink, manifest.Title, manifest.CreatedAt, manifest.HostToken), state);
    }

    public static async Task SaveAsync(
        string folder,
        MeetingFolderInfo info,
        MeetingRoomState state,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.Combine(folder, NotesFolder));
        var documents = new List<MeetingManifestDocument>();
        var usedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in state.Documents)
        {
            var file = $"{documents.Count + 1:00}-{Slug(document.Title)}.md";
            usedFiles.Add(file);
            documents.Add(new MeetingManifestDocument(document.Id, document.Title, file));
            await WriteAtomicAsync(Path.Combine(folder, NotesFolder, file), RenderMarkdown(document), cancellationToken).ConfigureAwait(false);
        }
        // Renamed or deleted documents must not come back on the next load.
        foreach (var stale in Directory.EnumerateFiles(Path.Combine(folder, NotesFolder), "*.md"))
            if (!usedFiles.Contains(Path.GetFileName(stale)))
                TryDelete(stale);

        var manifest = new MeetingManifest
        {
            SessionId = info.SessionId,
            MeetingId = state.Id,
            Role = info.Role,
            JoinLink = info.JoinLink,
            HostToken = info.HostToken,
            Title = state.Title,
            CreatedAt = state.CreatedAt,
            Revision = state.Revision,
            SavedAt = DateTimeOffset.UtcNow,
            Participants = state.Participants.Where(p => !p.IsAgent).Select(MeetingJson.Clone).ToList(),
            Documents = documents,
        };
        await WriteAtomicAsync(Path.Combine(folder, MeetingFile), JsonSerializer.Serialize(manifest, MeetingJson.Indented), cancellationToken).ConfigureAwait(false);
        await WriteAtomicAsync(Path.Combine(folder, WhiteboardFile), JsonSerializer.Serialize(await StoreImagesAsync(folder, state.Whiteboard, cancellationToken).ConfigureAwait(false), MeetingJson.Indented), cancellationToken).ConfigureAwait(false);
        await WriteAtomicAsync(Path.Combine(folder, ChatFile), Lines(state.Chat), cancellationToken).ConfigureAwait(false);
        await WriteAtomicAsync(Path.Combine(folder, TranscriptFile), Lines(state.Transcript), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes every image element as <c>assets/&lt;id&gt;.jpg</c> and returns a copy of the
    /// board that references the files instead of embedding the pictures.
    /// </summary>
    private static async Task<MeetingWhiteboard> StoreImagesAsync(string folder, MeetingWhiteboard board, CancellationToken cancellationToken)
    {
        var copy = MeetingJson.Clone(board);
        copy.Elements ??= [];
        var assets = Path.Combine(folder, AssetsFolder);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in copy.Elements.Where(e => e.Kind == "image" && !string.IsNullOrEmpty(e.ImageData)))
        {
            var file = Slug(element.Id) + ".jpg";
            used.Add(file);
            var path = Path.Combine(assets, file);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(assets);
                var temporary = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
                await File.WriteAllBytesAsync(temporary, Convert.FromBase64String(element.ImageData!), cancellationToken).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);
            }
            element.ImageData = AssetPrefix + file;
        }
        if (Directory.Exists(assets))
            foreach (var stale in Directory.EnumerateFiles(assets, "*.jpg"))
                if (!used.Contains(Path.GetFileName(stale))) TryDelete(stale);
        return copy;
    }

    internal static string RenderMarkdown(MeetingDocument document)
    {
        var builder = new StringBuilder();
        builder.Append("<!-- missum-meeting-notes v1 -->\n");
        foreach (var block in document.Blocks)
        {
            builder.Append("<!-- block id=").Append(block.Id)
                .Append(" kind=").Append(block.Kind);
            if (!string.IsNullOrEmpty(block.Section)) builder.Append(" section=").Append(block.Section);
            if (block.AiOwned) builder.Append(" ai=1");
            if (block.HumanEdited) builder.Append(" edited=1");
            if (block.Done) builder.Append(" done=1");
            if (!string.IsNullOrEmpty(block.EditedBy)) builder.Append(" by=").Append(Uri.EscapeDataString(block.EditedBy));
            builder.Append(" -->\n");
            var text = block.Text.Replace("\r\n", "\n", StringComparison.Ordinal);
            builder.Append(block.Kind switch
            {
                "h1" => "# " + text,
                "h2" => "## " + text,
                "h3" => "### " + text,
                "bullet" => "- " + text,
                "todo" => (block.Done ? "- [x] " : "- [ ] ") + text,
                _ => text,
            }).Append("\n\n");
        }
        return builder.ToString();
    }

    internal static List<MeetingNoteBlock> ParseMarkdown(string markdown)
    {
        var blocks = new List<MeetingNoteBlock>();
        MeetingNoteBlock? current = null;
        var inferred = false;
        var text = new StringBuilder();

        void Flush()
        {
            if (current is null) return;
            var value = text.ToString().Trim('\n', '\r');
            current.Text = current.Kind switch
            {
                "h1" => StripPrefix(value, "# "),
                "h2" => StripPrefix(value, "## "),
                "h3" => StripPrefix(value, "### "),
                "bullet" => StripPrefix(value, "- "),
                "todo" => StripPrefix(StripPrefix(value, "- [ ] "), "- [x] "),
                _ => value,
            };
            blocks.Add(current);
            current = null;
            inferred = false;
            text.Clear();
        }

        foreach (var rawLine in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var match = BlockComment().Match(rawLine);
            if (match.Success)
            {
                Flush();
                current = new MeetingNoteBlock();
                foreach (Match attribute in BlockAttribute().Matches(match.Groups[1].Value))
                {
                    var value = attribute.Groups[2].Value;
                    switch (attribute.Groups[1].Value)
                    {
                        case "id": current.Id = value; break;
                        case "kind": current.Kind = value; break;
                        case "section": current.Section = value; break;
                        case "ai": current.AiOwned = value == "1"; break;
                        case "edited": current.HumanEdited = value == "1"; break;
                        case "done": current.Done = value == "1"; break;
                        case "by": current.EditedBy = Uri.UnescapeDataString(value); break;
                    }
                }
                continue;
            }
            if (rawLine.StartsWith("<!-- missum-meeting-notes", StringComparison.Ordinal)) continue;
            // A file edited outside Missum may contain lines without metadata:
            // a blank line or a new heading/list item starts the next block.
            if (inferred && (string.IsNullOrWhiteSpace(rawLine) || StartsBlock(rawLine))) Flush();
            if (current is null)
            {
                if (string.IsNullOrWhiteSpace(rawLine)) continue;
                current = InferBlock(rawLine);
                inferred = true;
            }
            if (text.Length > 0) text.Append('\n');
            text.Append(rawLine);
        }
        Flush();
        return blocks.Where(b => b.Text.Length > 0 || b.Kind == "p").ToList();
    }

    private static bool StartsBlock(string line) =>
        line.StartsWith("# ", StringComparison.Ordinal) || line.StartsWith("## ", StringComparison.Ordinal)
        || line.StartsWith("### ", StringComparison.Ordinal) || line.StartsWith("- ", StringComparison.Ordinal);

    private static MeetingNoteBlock InferBlock(string line) => new()
    {
        Kind = line.StartsWith("### ", StringComparison.Ordinal) ? "h3"
            : line.StartsWith("## ", StringComparison.Ordinal) ? "h2"
            : line.StartsWith("# ", StringComparison.Ordinal) ? "h1"
            : line.StartsWith("- [", StringComparison.Ordinal) ? "todo"
            : line.StartsWith("- ", StringComparison.Ordinal) ? "bullet"
            : "p",
        Done = line.StartsWith("- [x] ", StringComparison.OrdinalIgnoreCase),
    };

    private static string StripPrefix(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;

    private static string Lines<T>(IEnumerable<T> items)
    {
        var builder = new StringBuilder();
        foreach (var item in items) builder.Append(JsonSerializer.Serialize(item, MeetingJson.Options)).Append('\n');
        return builder.ToString();
    }

    private static async Task<List<T>> ReadLinesAsync<T>(string path, int maximum, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        if (!File.Exists(path)) return items;
        foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<T>(line, MeetingJson.Options) is { } item) items.Add(item);
            }
            catch (JsonException)
            {
                // A torn last line from a crash is skipped; earlier lines stay usable.
            }
        }
        return items.Count > maximum ? items[^maximum..] : items;
    }

    internal static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsInside(string folder, string path) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static string ShortId(Guid id) => id.ToString("N")[..8];

    internal static string Slug(string? value)
    {
        var text = (value ?? "").Trim();
        var builder = new StringBuilder();
        foreach (var character in text.Normalize(NormalizationForm.FormC))
        {
            if (char.IsLetterOrDigit(character)) builder.Append(character);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
            if (builder.Length >= 40) break;
        }
        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "Meeting" : slug;
    }

    [GeneratedRegex(@"^<!--\s*block\s+(.*?)\s*-->\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"(\w+)=(\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex BlockAttribute();

    private sealed class MeetingManifest
    {
        public int Version { get; set; } = 1;
        public Guid SessionId { get; set; }
        public Guid MeetingId { get; set; }
        public string Role { get; set; } = "host";
        public string? JoinLink { get; set; }
        /// <summary>Keeps a host's invitation link valid across restarts.</summary>
        public string? HostToken { get; set; }
        public string Title { get; set; } = "Meeting";
        public DateTimeOffset CreatedAt { get; set; }
        public long Revision { get; set; }
        public DateTimeOffset SavedAt { get; set; }
        public List<MeetingParticipant>? Participants { get; set; }
        public List<MeetingManifestDocument>? Documents { get; set; }
    }

    private sealed record MeetingManifestDocument(string Id, string Title, string File);

}
