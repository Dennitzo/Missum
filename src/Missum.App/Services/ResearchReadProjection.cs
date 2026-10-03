using System.Security.Cryptography;
using System.Text.Json;
using Missum.Core.Research;

namespace Missum.App.Services;

/// <summary>Pure, bounded views of one durable snapshot; cursors never span different snapshots.</summary>
internal static class ResearchReadProjection
{
    private const int TaskPageCharacters = 8000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly byte[] HashSeparator = [0];
    private static readonly string[] ReferenceFields =
        ["sourceIds", "claimIds", "experimentIds", "checkIds", "evidenceIds", "hypothesisIds", "requirementIds", "contributionIds"];

    internal static string Stamp(ResearchWorkingReadSnapshot snapshot)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(new { snapshot.Project.Id, snapshot.Project.SessionId, snapshot.Project.OriginalQuestion,
            snapshot.State.Revision, snapshot.State.PublicationRevision, snapshot.State.Title });
        foreach (var item in snapshot.State.Items.OrderBy(item => item.Id, StringComparer.Ordinal)) Append(item);
        foreach (var item in snapshot.Sources.OrderBy(item => item.WorkId, StringComparer.Ordinal)) Append(item);
        foreach (var item in snapshot.Evidence.OrderBy(item => item.Id, StringComparer.Ordinal)) Append(item);
        foreach (var item in snapshot.Experiments.OrderBy(item => item.Id, StringComparer.Ordinal)) Append(item);
        foreach (var item in snapshot.Checks.OrderBy(item => item.Id, StringComparer.Ordinal)) Append(item);
        return Convert.ToHexStringLower(hash.GetHashAndReset());

        void Append<T>(T value)
        {
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));
            hash.AppendData(HashSeparator);
        }
    }

    internal static JsonElement Create(ResearchWorkingReadSnapshot snapshot, JsonElement arguments)
    {
        var view = Text(arguments, "view") ?? "overview";
        var ids = arguments.TryGetProperty("ids", out var requested)
            ? requested.EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal) : [];
        var limit = arguments.TryGetProperty("limit", out var count) ? count.GetInt32() : 16;
        var cursor = Text(arguments, "cursor");
        var stamp = Stamp(snapshot);
        var projectId = snapshot.Project.Id;
        var state = snapshot.State;
        var filterHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(ids.Order(StringComparer.Ordinal), JsonOptions)));
        var offset = arguments.TryGetProperty("offset", out var start) ? start.GetInt32() : 0;
        if (cursor is not null)
        {
            ResearchCursor? parsed;
            try { parsed = JsonSerializer.Deserialize<ResearchCursor>(Convert.FromBase64String(cursor), JsonOptions); }
            catch (Exception exception) when (exception is FormatException or JsonException)
            { throw new InvalidDataException("Der Forschungs-Cursor ist ungültig.", exception); }
            if (parsed is null || parsed.Version != 1 || parsed.ProjectId != projectId || parsed.View != view || parsed.FilterHash != filterHash || parsed.Offset < 0)
                throw new InvalidDataException("Der Cursor gehört zu einer anderen Forschungsansicht oder Auswahl.");
            if (parsed.StateStamp != stamp)
                return JsonSerializer.SerializeToElement(new
                {
                    success = false, projectId, protocol = "section-delta-v1", view, stateStamp = stamp,
                    state.Revision, state.PublicationRevision, errorCode = "research.cursor_stale", restartRequired = true,
                    message = "Der Forschungsstand hat sich geändert. Diese Ansicht ohne Cursor neu beginnen; bereits gelesene Inhalte bleiben gespeichert.",
                }, JsonOptions);
            offset = parsed.Offset;
        }
        if (view == "overview" && cursor is null && offset == 0 && ids.Count == 0 && Text(arguments, "knownStateStamp") == stamp)
            return JsonSerializer.SerializeToElement(new
            {
                success = true, projectId, protocol = "section-delta-v1", view, stateStamp = stamp, unchanged = true,
                state.Revision, state.PublicationRevision,
            }, JsonOptions);

        string? Next(int nextOffset, int total) => nextOffset >= total ? null : Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new ResearchCursor(1, projectId, view, filterHash, stamp, nextOffset), JsonOptions));

        if (view == "task")
        {
            var original = snapshot.Project.OriginalQuestion;
            if (offset > original.Length || offset > 0 && offset < original.Length && char.IsLowSurrogate(original[offset]))
                throw new InvalidDataException("Der Cursor liegt außerhalb des ursprünglichen Forschungsauftrags.");
            var length = Math.Min(TaskPageCharacters, original.Length - offset);
            if (length > 0 && offset + length < original.Length && char.IsHighSurrogate(original[offset + length - 1])) length--;
            return JsonSerializer.SerializeToElement(new
            {
                success = true, projectId, protocol = "section-delta-v1", view, stateStamp = stamp, unchanged = false,
                state.Revision, state.PublicationRevision, originalQuestion = original.Substring(offset, length),
                characterOffset = offset, totalCharacters = original.Length, nextCursor = Next(offset + length, original.Length),
            }, JsonOptions);
        }

        IEnumerable<object> entries;
        if (view is "overview" or "objects")
        {
            var items = snapshot.State.Items.Where(item => ids.Count == 0 || ids.Contains(item.Id));
            if (view == "overview")
                items = items.OrderBy(item => OverviewPriority(item)).ThenByDescending(item => item.UpdatedAt).ThenBy(item => item.Id, StringComparer.Ordinal);
            else items = items.OrderBy(item => item.Id, StringComparer.Ordinal);
            entries = items.Select(item => view == "overview" ? OverviewItem(item) : LocalToolBroker.ResearchItemReceipt(item, full: ids.Count > 0));
        }
        else if (view == "sources")
        {
            entries = snapshot.Sources.Where(source => ids.Count == 0 || ids.Contains(source.WorkId)).OrderBy(source => source.WorkId, StringComparer.Ordinal)
                .Select(source => (object)new
                {
                    id = source.WorkId, kind = "source", title = source.Title, url = source.CanonicalUrl,
                    evidenceIds = snapshot.Evidence.Where(evidence => evidence.WorkId == source.WorkId).Select(evidence => evidence.Id).Order(StringComparer.Ordinal).ToArray(),
                    details = ids.Count > 0 ? source : null,
                });
            if (ids.Count > 0)
                entries = entries.Concat(snapshot.Evidence.Where(evidence => ids.Contains(evidence.Id)).OrderBy(evidence => evidence.Id, StringComparer.Ordinal)
                    .Select(evidence => (object)new { id = evidence.Id, kind = "evidence", sourceId = evidence.WorkId,
                        excerpt = evidence.ExactExcerpt, statement = evidence.NormalizedStatement, hash = evidence.ContentHash,
                        locator = evidence.LocatorJson, evidence.Page, evidence.Section,
                        evidence.EvidenceLevel, evidence.RetrievedAt, evidence.TableOrFigure }));
        }
        else if (view == "experiments")
            entries = snapshot.Experiments.Where(experiment => ids.Count == 0 || ids.Contains(experiment.Id)).OrderBy(experiment => experiment.Id, StringComparer.Ordinal)
                .Select(experiment => (object)new { id = experiment.Id, status = experiment.VerificationStatus,
                    command = Bound(experiment.CommandText, 500), receipt = ids.Count > 0 ? experiment.StdoutEvidence : null,
                    artifactPaths = ids.Count > 0 ? experiment.ResultArtifactsJson : null,
                    details = ids.Count > 0 ? experiment : null });
        else if (view == "checks")
            entries = snapshot.Checks.Where(check => ids.Count == 0 || ids.Contains(check.Id) || ids.Contains(check.TargetId)).OrderBy(check => check.Id, StringComparer.Ordinal)
                .Select(check => (object)new { id = check.Id, targetId = check.TargetId, method = check.Method,
                    status = check.Status, receipt = ids.Count > 0 ? check.EvidenceJson : null });
        else throw new InvalidDataException("Unbekannte Forschungsansicht.");

        var all = entries.ToArray();
        if (offset > all.Length) throw new InvalidDataException("Der Cursor liegt außerhalb dieser Forschungsansicht.");
        var selected = all.Skip(offset).Take(limit).ToArray();
        return JsonSerializer.SerializeToElement(new
        {
            success = true, projectId, protocol = "section-delta-v1", view, stateStamp = stamp, unchanged = false,
            state.Revision, state.PublicationRevision, state.Title,
            items = selected, totalItems = all.Length, nextCursor = Next(offset + selected.Length, all.Length),
            task = view == "overview" ? new
            {
                preview = Bound(snapshot.Project.OriginalQuestion, 1200), totalCharacters = snapshot.Project.OriginalQuestion.Length,
                complete = snapshot.Project.OriginalQuestion.Length <= 1200, readView = "task",
            } : null,
            counts = view == "overview" ? new
            {
                objects = snapshot.State.Items.Count, sources = snapshot.Sources.Count, evidence = snapshot.Evidence.Count,
                experiments = snapshot.Experiments.Count, checks = snapshot.Checks.Count,
                requirements = snapshot.State.Items.Count(item => item.Kind == "requirement"),
                sections = snapshot.State.Items.Count(item => item.Kind == "section"),
            } : null,
            note = "Gespeicherte Forschungsdaten; technische Belege sind keine allgemeine Wahrheitsprüfung. Fehlende Details gezielt über view und ids lesen.",
        }, JsonOptions);
    }

    private static object OverviewItem(ResearchWorkingItem item)
    {
        var data = item.Data;
        return new
        {
            item.Id, item.Kind, item.Revision, item.OwnerAgentId,
            data = new
            {
                title = Bound(Text(data, "title"), 200), statement = Bound(Text(data, "statement"), 400),
                status = Text(data, "status"), required = data.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.True,
                nextCheck = Bound(Text(data, "nextCheck"), 300), reason = Bound(Text(data, "reason"), 300),
                references = ReferenceFields.Where(field => data.TryGetProperty(field, out var refs) && refs.ValueKind == JsonValueKind.Array && refs.GetArrayLength() > 0)
                    .ToDictionary(field => field, field => data.GetProperty(field).Clone(), StringComparer.Ordinal),
            },
        };
    }

    private static int OverviewPriority(ResearchWorkingItem item)
    {
        var active = Text(item.Data, "status") is "active" or "planned" or "pending" or "blocked" or "unresolved";
        if (item.Kind == "requirement" && active) return 0;
        if (item.Kind == "hypothesis" && active) return 1;
        if (item.Kind == "section") return 2;
        if (item.Kind == "hypothesis" && Text(item.Data, "status") is "refuted" or "superseded") return 3;
        return 4;
    }

    private static string? Text(JsonElement value, string field) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(field, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
    private static string? Bound(string? value, int maximum)
    {
        if (value is null || value.Length <= maximum) return value;
        if (char.IsHighSurrogate(value[maximum - 1])) maximum--;
        return value[..maximum] + "…";
    }

    private sealed record ResearchCursor(int Version, string ProjectId, string View, string FilterHash, string StateStamp, int Offset);
}
