using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Core.Research;

namespace Missum.App.Services;

public sealed partial class MissumAiAssistantService
{
    private async Task PersistResearchProgressAsync(MissumAiRunRecord run, RunEvent item, CancellationToken token)
    {
        if (scientificResearch is null || !ScientificResearchProgressStore.Handles(item)) return;
        try
        {
            await new ScientificResearchProgressStore(scientificResearch, runs).ApplyAsync(run, item, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && !token.IsCancellationRequested)
        {
            RunDiagnostic(logger, item.RunId, "Der gelesene Forschungszwischenstand konnte nicht gespeichert werden.", exception);
        }
    }

    private async Task EndResearchProgressAsync(MissumAiRunRecord run, string status)
    {
        if (scientificResearch is null) return;
        try
        {
            await new ScientificResearchProgressStore(scientificResearch, runs)
                .EndAsync(run, status, CancellationToken.None).ConfigureAwait(false);
            sciencePresentation?.Queue($"research-{run.SessionId:N}");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            RunDiagnostic(logger, run.ServerRunId ?? "", "Der Forschungszwischenstand konnte nicht beendet werden.", exception);
        }
    }
}

/// <summary>Stores actual fetched excerpts; a fetch never becomes a verified claim.</summary>
internal sealed class ScientificResearchProgressStore(IScientificResearchRepository repository, IMissumAiRunRepository runs)
{
    internal const string ReportKind = "researchProgress";
    internal const string EvidenceLevel = "retrievedExcerpt";
    private static readonly JsonSerializerOptions Json = MissumAiProtocol.CreateJsonOptions();

    internal static bool Handles(RunEvent item) => item.Type is RunEventTypes.ResearchProblemInterpreted
        or RunEventTypes.RunCancelled or RunEventTypes.RunFailed
        || item.Type == RunEventTypes.ServerToolCompleted && Text(item.Data, "tool") == "web.fetch";

    internal async Task ApplyAsync(MissumAiRunRecord run, RunEvent item, CancellationToken token = default)
    {
        if (run.ServerRunId != item.RunId || string.IsNullOrWhiteSpace(run.ServerRunId)) return;
        if (!await IsCurrentAttemptAsync(run, token).ConfigureAwait(false)) return;
        var beginning = item.Type == RunEventTypes.ResearchProblemInterpreted;
        var projectId = beginning ? Text(item.Data, "projectId") : $"research-{run.SessionId:N}";
        var project = await repository.GetProjectAsync(projectId, token).ConfigureAwait(false);
        if (project is null || project.SessionId != run.SessionId) return;
        var archive = await repository.LoadArchiveSnapshotAsync(project.Id, token).ConfigureAwait(false);
        if (!await CanApplyAsync(run, item.Id, archive.Report, final: false, token, beginning).ConfigureAwait(false)) return;
        var manifest = Parse(archive.Report?.ManifestJson);
        var sameRun = Text(manifest, "runId") == run.ServerRunId;
        // Only a research-start receipt enrolls a run. Ordinary web.fetch calls in
        // later non-research prompts must not overwrite an older research dossier.
        if (!beginning && !sameRun) return;
        var status = item.Type switch
        {
            RunEventTypes.RunCancelled => "cancelled",
            RunEventTypes.RunFailed => "failed",
            _ => "active",
        };
        var sameResearch = sameRun && archive.Report?.ReportKind == ReportKind;
        var works = sameResearch ? archive.Works.ToList() : [];
        var evidence = sameResearch ? archive.Evidence.ToList() : [];
        if (!beginning && status == "active")
        {
            if (!IsTrue(item.Data, "success") || !item.Data.TryGetProperty("result", out var result)
                || !TryAddSource(project.Id, run.ServerRunId, item.Id, result, works, evidence)) return;
        }
        if (beginning && !sameResearch)
        {
            // Historical reports remain stored. Current claims/verification must
            // not appear to certify the newly started research question.
            await repository.SaveResultSnapshotAsync(project.Id, new([], [], [], []), token).ConfigureAwait(false);
        }
        await SaveAsync(project, run, item.Id, status, works, evidence, token).ConfigureAwait(false);
    }

    internal async Task EndAsync(MissumAiRunRecord run, string status, CancellationToken token = default)
    {
        if (!await IsCurrentAttemptAsync(run, token).ConfigureAwait(false)) return;
        var project = await repository.GetProjectAsync($"research-{run.SessionId:N}", token).ConfigureAwait(false);
        if (project is null || project.SessionId != run.SessionId || run.ServerRunId is null) return;
        var archive = await repository.LoadArchiveSnapshotAsync(project.Id, token).ConfigureAwait(false);
        var manifest = Parse(archive.Report?.ManifestJson);
        if (archive.Report?.ReportKind != ReportKind || Text(manifest, "runId") != run.ServerRunId
            || Text(manifest, "status") != "active") return;
        await SaveAsync(project, run, Number(manifest, "lastEventId"), status,
            archive.Works, archive.Evidence, token).ConfigureAwait(false);
    }

    internal async Task<bool> CanPersistResultAsync(MissumAiRunRecord run, RunEvent item, string projectId,
        CancellationToken token = default)
    {
        if (run.ServerRunId != item.RunId || !await IsCurrentAttemptAsync(run, token).ConfigureAwait(false)) return false;
        var project = await repository.GetProjectAsync(projectId, token).ConfigureAwait(false);
        if (project is null || project.SessionId != run.SessionId) return false;
        var archive = await repository.LoadArchiveSnapshotAsync(projectId, token).ConfigureAwait(false);
        return await CanApplyAsync(run, item.Id, archive.Report, final: true, token).ConfigureAwait(false);
    }

    private async Task<bool> IsCurrentAttemptAsync(MissumAiRunRecord run, CancellationToken token)
    {
        var durable = await runs.GetAsync(run.Id, token).ConfigureAwait(false);
        return durable is not null && durable.SessionId == run.SessionId && durable.ServerRunId == run.ServerRunId;
    }

    private async Task<bool> CanApplyAsync(MissumAiRunRecord run, long eventId, ResearchStoredReport? report,
        bool final, CancellationToken token, bool beginning = false)
    {
        if (report is null) return true;
        var manifest = Parse(report.ManifestJson);
        var storedRunId = Text(manifest, "runId");
        if (storedRunId == run.ServerRunId)
        {
            // A new explicit research-start receipt may begin another research
            // tool invocation within this run. Late fetches/final replays cannot.
            if (report.ReportKind != ReportKind) return beginning && eventId > Number(manifest, "lastEventId");
            if (!final && Text(manifest, "status") != "active") return false;
            return eventId > Number(manifest, "lastEventId");
        }
        DateTimeOffset previousStart;
        if (!DateTimeOffset.TryParse(Text(manifest, "runStartedAt"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out previousStart))
        {
            var previousRun = string.IsNullOrWhiteSpace(storedRunId) ? null
                : await runs.GetByServerRunIdAsync(storedRunId, token).ConfigureAwait(false);
            previousStart = previousRun?.CreatedAt ?? report.CreatedAt;
        }
        // A retry reuses its local run/message creation time, but only the exact
        // server attempt still bound to that durable run may supersede it.
        return run.CreatedAt > previousStart || run.CreatedAt == previousStart
            && Text(manifest, "localRunId") == run.Id.ToString("D");
    }

    private async Task SaveAsync(ScientificResearchProject project, MissumAiRunRecord run, long eventId, string status,
        IReadOnlyList<ResearchLiteratureEntry> works, IReadOnlyList<ResearchEvidenceRecord> evidence, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var revision = project.Revision + 1;
        // Project status is constrained to the scientific lifecycle vocabulary;
        // transport failure remains available in the progress manifest/checkpoint.
        var projectStatus = status == "failed" ? "blocked" : status;
        var metadata = JsonSerializer.Serialize(new
        {
            projectId = project.Id, runId = run.ServerRunId, localRunId = run.Id, runStartedAt = run.CreatedAt,
            lastEventId = eventId, isIntermediate = true, status, revision,
            works = works.Count, evidence = evidence.Count, createdAt = now,
        }, Json);
        var note = status == "active"
            ? works.Count == 0 ? "Die Recherche läuft. Noch keine Originalquelle wurde als gelesener Beleg gespeichert."
                : "Laufender Zwischenstand: Originalquellen wurden gelesen. Aussagen und Schlussfolgerungen sind noch nicht geprüft."
            : "Unvollständiger Zwischenstand: Die Recherche wurde " + (status == "cancelled" ? "abgebrochen"
                : status == "failed" ? "unterbrochen" : "ohne abschließendes Prüfergebnis beendet")
                + ". Die gelesenen Originalbelege bleiben erhalten; ein abschließendes Prüfergebnis liegt nicht vor.";
        var reportId = Id(project.Id, "progress-report", run.ServerRunId!);
        await repository.UpsertProjectAsync(project with { Status = projectStatus, Revision = revision, UpdatedAt = now }, token).ConfigureAwait(false);
        await repository.SaveCheckpointAsync(new(Id(project.Id, "progress-checkpoint", run.ServerRunId + ":" + eventId + ":" + status),
            project.Id, run.ServerRunId!, revision, "sources." + status, metadata, now), token).ConfigureAwait(false);
        // The archive's lastEventId is the durable replay marker, written last.
        await repository.SaveArchiveSnapshotAsync(project.Id, new(project.ProtocolVersion, "{}", works, evidence,
            reportId, ReportKind, "unresolved", note, metadata, "research.progress.persisted", metadata,
            run.ServerRunId!, revision, now), token).ConfigureAwait(false);
    }

    private static bool TryAddSource(string projectId, string runId, long eventId, JsonElement result,
        List<ResearchLiteratureEntry> works, List<ResearchEvidenceRecord> evidence)
    {
        if (!IsTrue(result, "found") || !IsTrue(result, "isUntrusted")
            || !Uri.TryCreate(Text(result, "url"), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)
            || !result.TryGetProperty("matches", out var matches) || matches.ValueKind != JsonValueKind.Array
            || !result.TryGetProperty("retrievedAt", out var retrieved) || retrieved.ValueKind != JsonValueKind.String
            || !retrieved.TryGetDateTimeOffset(out var retrievedAt)) return false;
        var excerpts = matches.EnumerateArray().Take(16).Where(match => !string.IsNullOrWhiteSpace(Text(match, "text"))).ToArray();
        if (excerpts.Length == 0) return false;
        var canonical = uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped).TrimEnd('/');
        var workId = Id(projectId, "work", canonical.ToLowerInvariant());
        var title = Text(result, "title");
        if (string.IsNullOrWhiteSpace(title)) title = uri.Host + uri.AbsolutePath;
        works.RemoveAll(work => work.WorkId == workId);
        works.Add(new(workId, projectId, title, canonical, "retrievedSource",
            JsonSerializer.Serialize(new { runId, eventId, retrievedAt, isIntermediate = true }, Json),
            "awaitingReview", EvidenceLevel, retrievedAt));
        foreach (var match in excerpts)
        {
            var excerpt = Text(match, "text");
            if (excerpt.Length > 16_000) excerpt = excerpt[..16_000];
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(excerpt)));
            var id = Id(projectId, "evidence", workId + "\n" + hash);
            if (evidence.Any(item => item.Id == id)) continue;
            evidence.Add(new(id, projectId, workId, excerpt, "Gelesener Originalauszug · noch nicht geprüft", hash,
                EvidenceLevel, JsonSerializer.Serialize(new { url = canonical, runId, eventId, match }, Json), retrievedAt));
        }
        return true;
    }

    private static string Id(string projectId, string kind, string value) => projectId + ":" + kind + ":"
        + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static JsonElement Parse(string? json) => string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<JsonElement>(json);
    private static string Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";
    private static bool IsTrue(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
    private static long Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.TryGetInt64(out var number) ? number : 0;
}
