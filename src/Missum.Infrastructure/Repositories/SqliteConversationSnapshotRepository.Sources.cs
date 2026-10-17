using Missum.Core.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Missum.Infrastructure.Repositories;

public sealed partial class SqliteConversationSnapshotRepository
{
    private const string SourceToolRowsSql = """
        SELECT step.message_id,step.step_json AS value FROM chat_tool_steps step JOIN chat_messages owner ON owner.id=step.message_id
        WHERE owner.session_id=$session AND (step.tool_name LIKE 'web.%' OR step.tool_name LIKE 'research.%' OR step.tool_name='subagent')
        UNION ALL
        SELECT owner.id,s.value FROM chat_messages owner,json_each(owner.tool_steps_json) s
        WHERE owner.session_id=$session AND NOT EXISTS(SELECT 1 FROM chat_tool_step_messages normalized WHERE normalized.message_id=owner.id)
        """;

    public async Task<IReadOnlyList<ConversationSource>> GetSourcesAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // JSON receipts may embed another JSON string (including inline child
        // transcripts). Traverse those inside SQLite, returning only source
        // addresses and labels. Closed tool outputs never cross into the UI.
        command.CommandText = $$"""
            WITH RECURSIVE documents(message_id,step_id,tool,at,payload,depth) AS (
                SELECT m.id,json_extract(s.value,'$.id'),json_extract(s.value,'$.tool'),
                    COALESCE(json_extract(s.value,'$.updatedAt'),json_extract(s.value,'$.startedAt'),m.created_at),
                    s.value,0
                FROM chat_messages m JOIN ({{SourceToolRowsSql}}) s ON s.message_id=m.id
                WHERE m.session_id=$session AND (json_extract(s.value,'$.tool') LIKE 'web.%'
                    OR json_extract(s.value,'$.tool') LIKE 'research.%' OR json_extract(s.value,'$.tool')='subagent')
                UNION ALL
                SELECT d.message_id,d.step_id,d.tool,d.at,CAST(j.value AS TEXT),d.depth+1
                FROM documents d,json_tree(d.payload) j
                WHERE d.depth<8 AND j.type='text' AND json_valid(j.value)
                    AND substr(ltrim(j.value),1,1) IN ('{','[')
            )
            SELECT DISTINCT d.message_id,d.step_id,d.tool,d.at,CAST(j.value AS TEXT),
                substr(COALESCE(json_extract(d.payload,j.path||'.title'),
                    json_extract(d.payload,j.path||'.pageTitle'),json_extract(d.payload,j.path||'.name'),''),1,512)
            FROM documents d,json_tree(d.payload) j
            WHERE j.type='text' AND lower(CAST(j.key AS TEXT)) LIKE '%url%'
                AND lower(CAST(j.key AS TEXT)) NOT LIKE '%thumbnail%'
                AND lower(CAST(j.key AS TEXT)) NOT LIKE '%image%'
                AND lower(CAST(j.key AS TEXT)) NOT LIKE '%icon%'
                AND length(j.value)<=8192
            ORDER BY d.at DESC,d.message_id,d.step_id;
            """;
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        var sources = new List<ConversationSource>();
        var identities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var url = reader.GetString(4);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
            var id = reader.IsDBNull(1) ? reader.GetString(0) + ":source" : reader.GetString(1);
            var identity = id + ":" + uri.AbsoluteUri;
            var title = reader.IsDBNull(5) ? "" : reader.GetString(5);
            if (identities.TryGetValue(identity, out var existing))
            {
                if (sources[existing].Title.Length == 0 && title.Length > 0) sources[existing] = sources[existing] with { Title = title };
                continue;
            }
            identities[identity] = sources.Count;
            sources.Add(new(id, reader.GetString(2), title, uri.AbsoluteUri,
                DateTimeOffset.TryParse(reader.GetString(3), out var time) ? time : DateTimeOffset.MinValue));
        }
        // Older large child receipts point to a lossless sidecar. Read those
        // only on explicit source navigation and project their web receipts;
        // neither their chat bodies nor tool details are returned to the UI.
        command.CommandText = $$"""
            SELECT DISTINCT json_extract(s.value,'$.outputJson')->>'$.subagentStorageKey',
                json_extract(s.value,'$.outputJson')->>'$.agentId',json_extract(s.value,'$.outputJson')->>'$.runId'
            FROM chat_messages m JOIN ({{SourceToolRowsSql}}) s ON s.message_id=m.id
            WHERE m.session_id=$session AND json_extract(s.value,'$.tool')='subagent'
                AND json_valid(json_extract(s.value,'$.outputJson'))
                AND json_extract(s.value,'$.outputJson')->>'$.subagentStorageKey' IS NOT NULL;
            """;
        var sidecars = new List<(string Key, string Agent, string Run)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                if (!reader.IsDBNull(0) && !reader.IsDBNull(1) && !reader.IsDBNull(2))
                    sidecars.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        foreach (var (key, agent, run) in sidecars)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (key.Length != 64 || key.Any(character => !char.IsAsciiHexDigit(character))
                || !key.Equals(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId + ":" + run + ":" + agent))), StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var transcriptPath = Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "Subagents", key + ".json");
                await using var stream = File.OpenRead(transcriptPath);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                var root = document.RootElement;
                if (ReadSourceText(root, "sessionId") != sessionId.ToString("D") || ReadSourceText(root, "agentId") != agent
                    || ReadSourceText(root, "runId") != run) continue;
                var messages = root.TryGetProperty("previousMessages", out var previous) && previous.ValueKind == JsonValueKind.Array
                    ? previous.EnumerateArray().ToList() : [];
                if (root.TryGetProperty("assistantMessage", out var assistant)) messages.Add(assistant);
                foreach (var message in messages)
                    if (message.ValueKind == JsonValueKind.Object && message.TryGetProperty("toolSteps", out var steps) && steps.ValueKind == JsonValueKind.Array)
                        foreach (var step in steps.EnumerateArray())
                        {
                            var tool = ReadSourceText(step, "tool");
                            if (!tool.StartsWith("web.", StringComparison.Ordinal) && !tool.StartsWith("research.", StringComparison.Ordinal)) continue;
                            var at = DateTimeOffset.TryParse(ReadSourceText(step, "updatedAt"), out var timestamp) ? timestamp : DateTimeOffset.MinValue;
                            if (step.TryGetProperty("inputJson", out var input)) CollectExternalSources(input, ReadSourceText(step, "id"), tool, at, sources, 0);
                            if (step.TryGetProperty("outputJson", out var output)) CollectExternalSources(output, ReadSourceText(step, "id"), tool, at, sources, 0);
                        }
                // New transcript journals append only changed tool receipts; their source URLs remain available without hydrating a child chat.
                var journalPath = transcriptPath + ".journal.jsonl";
                if (File.Exists(journalPath))
                {
                    using var journal = new StreamReader(journalPath, Encoding.UTF8);
                    while (await journal.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                    {
                        if (line.Length == 0) continue;
                        using var patch = JsonDocument.Parse(line);
                        if (!patch.RootElement.TryGetProperty("metadata", out var metadata)
                            || ReadSourceText(metadata, "sessionId") != sessionId.ToString("D") || ReadSourceText(metadata, "runId") != run
                            || !patch.RootElement.TryGetProperty("tools", out var changes) || changes.ValueKind != JsonValueKind.Array) continue;
                        foreach (var change in changes.EnumerateArray())
                        {
                            if (!change.TryGetProperty("step", out var step)) continue;
                            var tool = ReadSourceText(step, "tool");
                            if (!tool.StartsWith("web.", StringComparison.Ordinal) && !tool.StartsWith("research.", StringComparison.Ordinal)) continue;
                            var at = DateTimeOffset.TryParse(ReadSourceText(step, "updatedAt"), out var timestamp) ? timestamp : DateTimeOffset.MinValue;
                            if (step.TryGetProperty("inputJson", out var input)) CollectExternalSources(input, ReadSourceText(step, "id"), tool, at, sources, 0);
                            if (step.TryGetProperty("outputJson", out var output)) CollectExternalSources(output, ReadSourceText(step, "id"), tool, at, sources, 0);
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return sources.GroupBy(source => source.Id + ":" + source.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(source => source.Title.Length > 0).First()).OrderByDescending(source => source.Time).ToArray();
    }

    private static void CollectExternalSources(JsonElement value, string id, string tool, DateTimeOffset at,
        List<ConversationSource> sources, int depth)
    {
        if (depth > 24) return;
        if (value.ValueKind == JsonValueKind.Object)
        {
            var title = ReadSourceText(value, "title");
            if (title.Length == 0) title = ReadSourceText(value, "pageTitle");
            if (title.Length > 512) title = title[..512];
            foreach (var property in value.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.String && property.Name.Contains("url", StringComparison.OrdinalIgnoreCase)
                    && !property.Name.Contains("image", StringComparison.OrdinalIgnoreCase) && !property.Name.Contains("thumbnail", StringComparison.OrdinalIgnoreCase)
                    && !property.Name.Contains("icon", StringComparison.OrdinalIgnoreCase) && property.Value.GetString() is { Length: <= 8192 } url
                    && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                    sources.Add(new(id, tool, title, uri.AbsoluteUri, at));
                else CollectExternalSources(property.Value, id, tool, at, sources, depth + 1);
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CollectExternalSources(item, id, tool, at, sources, depth + 1);
        else if (value.ValueKind == JsonValueKind.String && value.GetString()?.TrimStart() is { Length: > 0 } json && json[0] is '{' or '[')
            try
            {
                using var nested = JsonDocument.Parse(json);
                CollectExternalSources(nested.RootElement, id, tool, at, sources, depth + 1);
            }
            catch (JsonException) { }
    }

    private static string ReadSourceText(JsonElement owner, string name) => owner.ValueKind == JsonValueKind.Object
        && owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
