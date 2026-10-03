using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Missum.Core.Research;
using Microsoft.Data.Sqlite;

namespace Missum.Infrastructure.Research;

public sealed partial class SqliteScientificResearchRepository
{
    private static readonly HashSet<string> WorkingKinds = new(StringComparer.Ordinal)
        { "hypothesis", "claim", "requirement", "section", "contribution" };
    private static readonly HashSet<string> WorkingStatuses = new(StringComparer.Ordinal)
        { "planned", "pending", "active", "supported", "provisionallySupported", "verified", "refuted", "blocked",
            "unresolved", "superseded", "withdrawn", "completed", "openLimit", "draft" };
    private static readonly HashSet<string> WorkingDataFields = new(StringComparer.Ordinal)
        { "title", "contentMarkdown", "statement", "status", "assumptions", "prediction", "nextCheck", "reason",
            "required", "order", "sourceIds", "claimIds", "experimentIds", "checkIds", "evidenceIds",
            "hypothesisIds", "requirementIds", "contributionIds", "figureCaptions", "units", "description",
            "method", "expectedResult", "classification", "conclusion", "limit", "targetIds" };
    private static readonly string[] WorkingReferenceFields =
        ["sourceIds", "claimIds", "experimentIds", "checkIds", "evidenceIds", "hypothesisIds", "requirementIds", "contributionIds", "targetIds"];
    private static readonly string[] LimitEvidenceFields = ["sourceIds", "evidenceIds", "experimentIds", "checkIds"];
    private static readonly JsonSerializerOptions WorkingJsonOptions = new(JsonSerializerDefaults.Web);

    public Task SaveExecutionVerificationAsync(ResearchVerification verification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verification);
        ArgumentException.ThrowIfNullOrWhiteSpace(verification.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(verification.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(verification.TargetId);
        using var evidence = JsonDocument.Parse(verification.EvidenceJson);
        if (evidence.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Ein Ausführungsnachweis benötigt strukturierte Prozessbelege.", nameof(verification));
        return database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM research_projects WHERE id=$project;";
            command.Parameters.AddWithValue("$project", verification.ProjectId);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException("Das Forschungsprojekt für den Prüfnachweis fehlt.");
            if (verification.TargetType == "experiment")
            {
                command.CommandText = "SELECT COUNT(*) FROM research_experiments WHERE project_id=$project AND id=$target;";
                command.Parameters.AddWithValue("$target", verification.TargetId);
                if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 1)
                    throw new InvalidOperationException("Das Experiment für den Prüfnachweis gehört nicht zu diesem Projekt.");
                command.Parameters.RemoveAt("$target");
            }
            command.CommandText = """
                INSERT INTO research_verifications(id,project_id,target_type,target_id,dimension,method,status,evidence_json,created_at)
                VALUES($id,$project,$type,$target,$dimension,$method,$status,$evidence,$created)
                ON CONFLICT(id) DO UPDATE SET status=excluded.status,evidence_json=excluded.evidence_json,created_at=excluded.created_at
                WHERE research_verifications.project_id=excluded.project_id
                    AND research_verifications.target_type=excluded.target_type AND research_verifications.target_id=excluded.target_id
                    AND research_verifications.method=excluded.method AND research_verifications.dimension=excluded.dimension
                    AND excluded.created_at>=research_verifications.created_at;
                """;
            command.Parameters.AddWithValue("$id", verification.Id); command.Parameters.AddWithValue("$type", verification.TargetType);
            command.Parameters.AddWithValue("$target", verification.TargetId); command.Parameters.AddWithValue("$dimension", verification.Dimension);
            command.Parameters.AddWithValue("$method", verification.Method); command.Parameters.AddWithValue("$status", verification.Status);
            command.Parameters.AddWithValue("$evidence", verification.EvidenceJson); command.Parameters.AddWithValue("$created", Format(verification.CreatedAt));
            var changed = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            if (changed > 0)
            {
                command.CommandText = "INSERT INTO research_execution_receipts(verification_id,project_id,receipt_sha256,recorded_at) VALUES($id,$project,$digest,$created) ON CONFLICT(verification_id) DO UPDATE SET receipt_sha256=excluded.receipt_sha256,recorded_at=excluded.recorded_at;";
                command.Parameters.AddWithValue("$digest", ExecutionVerificationDigest(verification));
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<ResearchVerification>> LoadExecutionVerificationsAsync(string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadExecutionVerificationsAsync(connection, null, projectId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ResearchVerification>> ReadExecutionVerificationsAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string projectId, CancellationToken cancellationToken)
    {
        var verifications = new List<ResearchVerification>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT v.id,v.project_id,v.target_type,v.target_id,v.dimension,v.method,v.status,v.evidence_json,v.created_at,r.receipt_sha256
            FROM research_verifications v JOIN research_execution_receipts r ON r.verification_id=v.id AND r.project_id=v.project_id
            WHERE v.project_id=$project ORDER BY v.created_at,v.id;
            """;
        command.Parameters.AddWithValue("$project", projectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var verification = new ResearchVerification(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), Parse(reader.GetString(8)));
            if (string.Equals(ExecutionVerificationDigest(verification), reader.GetString(9), StringComparison.Ordinal))
                verifications.Add(verification);
        }
        return verifications;
    }

    private static string ExecutionVerificationDigest(ResearchVerification verification)
    {
        using var evidence = JsonDocument.Parse(verification.EvidenceJson);
        var data = JsonSerializer.SerializeToElement(new { verification.Id, verification.ProjectId, verification.TargetType,
            verification.TargetId, verification.Dimension, verification.Method, verification.Status,
            evidence = evidence.RootElement, createdAt = Format(verification.CreatedAt) });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalWorkingJson(data)))).ToLowerInvariant();
    }

    public Task<ResearchWorkingState> LoadWorkingStateAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return database.WriteAsync(async (connection, transaction, token) =>
        {
            await EnsureWorkingStateAsync(connection, transaction, projectId, token).ConfigureAwait(false);
            return await ReadWorkingStateAsync(connection, transaction, projectId, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public async Task<ResearchWorkingUpdateResult?> ReadWorkingOperationAsync(string projectId, string operationId,
        string? actorAgentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        actorAgentId = string.IsNullOrWhiteSpace(actorAgentId) ? null : actorAgentId;
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        WorkingReceipt receipt;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT actor_agent_id,result_json FROM research_working_operations WHERE project_id=$project AND operation_id=$operation;";
            command.Parameters.AddWithValue("$project", projectId);
            command.Parameters.AddWithValue("$operation", operationId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            var storedActor = reader.IsDBNull(0) ? null : reader.GetString(0);
            if (!string.Equals(storedActor, actorAgentId, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Der gespeicherte Forschungsvorgang gehört zu einem anderen Agenten.");
            receipt = JsonSerializer.Deserialize<WorkingReceipt>(reader.GetString(1), WorkingJsonOptions)
                ?? throw new InvalidOperationException("Gespeicherte Forschungsquittung ist ungültig.");
        }
        var state = await ReadWorkingStateAsync(connection, transaction, projectId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(receipt.Success, state, receipt.ChangedIds, receipt.Conflicts, true);
    }

    public Task<ResearchWorkingUpdateResult> ApplyWorkingUpdateAsync(string projectId, string operationId,
        string? actorAgentId, string? title, IReadOnlyList<ResearchWorkingChange> changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(changes);
        if (operationId.Length > 512 || actorAgentId?.Length > 256) throw new ArgumentException("Forschungskennung ist zu lang.");
        actorAgentId = string.IsNullOrWhiteSpace(actorAgentId) ? null : actorAgentId;
        var digest = WorkingDigest(actorAgentId, title, changes);
        return database.WriteAsync(async (connection, transaction, token) =>
        {
            await EnsureWorkingStateAsync(connection, transaction, projectId, token).ConfigureAwait(false);
            var state = await ReadWorkingStateAsync(connection, transaction, projectId, token).ConfigureAwait(false);
            await using (var replay = connection.CreateCommand())
            {
                replay.Transaction = transaction;
                replay.CommandText = "SELECT arguments_sha256,result_json FROM research_working_operations WHERE project_id=$project AND operation_id=$operation;";
                replay.Parameters.AddWithValue("$project", projectId);
                replay.Parameters.AddWithValue("$operation", operationId);
                await using var reader = await replay.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (!string.Equals(reader.GetString(0), digest, StringComparison.Ordinal))
                        return new ResearchWorkingUpdateResult(false, state, [],
                            [new("$operation", "operation_reused", "Diese Vorgangskennung wurde bereits für andere Änderungen verwendet.")]);
                    var receipt = JsonSerializer.Deserialize<WorkingReceipt>(reader.GetString(1), WorkingJsonOptions)
                        ?? throw new InvalidOperationException("Gespeicherte Forschungsquittung ist ungültig.");
                    return new ResearchWorkingUpdateResult(receipt.Success, state, receipt.ChangedIds, receipt.Conflicts, true);
                }
            }

            var conflicts = new List<ResearchWorkingConflict>();
            if (changes.Count > 64) conflicts.Add(new("$update", "too_many_changes", "Bitte höchstens 64 Forschungsobjekte gleichzeitig ändern."));
            if (title is not null && (string.IsNullOrWhiteSpace(title) || title.Length > 1000 || title.Any(char.IsControl)))
                conflicts.Add(new("$title", "invalid_title", "Der Publikationstitel muss ein nicht leerer, einzeiliger wissenschaftlicher Titel sein."));
            if (title is not null && actorAgentId is not null)
                conflicts.Add(new("$title", "owner_required", "Nur der Hauptagent führt den Publikationstitel zusammen."));
            var current = state.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var change in changes)
            {
                if (string.IsNullOrWhiteSpace(change.Id) || change.Id.Length > 256 || change.Id.StartsWith('$') || change.Id.Any(char.IsControl))
                {
                    conflicts.Add(new(change.Id ?? "", "invalid_id", "Das Forschungsobjekt benötigt eine stabile Kennung bis 256 Zeichen."));
                    continue;
                }
                if (!seen.Add(change.Id)) conflicts.Add(new(change.Id, "duplicate_id", "Ein Objekt darf im selben Vorgang nur einmal geändert werden."));
                if (!WorkingKinds.Contains(change.Kind)) conflicts.Add(new(change.Id, "invalid_kind", "Unbekannte Art des Forschungsobjekts."));
                current.TryGetValue(change.Id, out var previous);
                if (change.ExpectedRevision != (previous?.Revision ?? 0))
                    conflicts.Add(new(change.Id, "revision_conflict", "Das Objekt wurde inzwischen geändert. Lade nur dieses Objekt erneut.", previous?.Revision ?? 0));
                if (previous is not null && !string.Equals(change.Kind, previous.Kind, StringComparison.Ordinal))
                    conflicts.Add(new(change.Id, "kind_conflict", "Die Art eines bestehenden Objekts bleibt erhalten.", previous.Revision));
                if (actorAgentId is not null && (change.Kind == "section" ||
                    previous is not null && !string.Equals(previous.OwnerAgentId, actorAgentId, StringComparison.Ordinal) ||
                    previous is null && !change.Id.StartsWith(actorAgentId + ":", StringComparison.Ordinal)))
                    conflicts.Add(new(change.Id, "owner_required", "Subagenten bearbeiten eigene Objekte mit ihrer Kennung als Präfix; Publikationsabschnitte führt der Hauptagent zusammen.", previous?.Revision));
                ValidateWorkingData(change, conflicts);
                if (previous is { Kind: "requirement", OwnerAgentId: null } && previous.Data.ValueKind == JsonValueKind.Object
                    && previous.Data.TryGetProperty("required", out var wasRequired) && wasRequired.ValueKind == JsonValueKind.True
                    && change.Data.ValueKind == JsonValueKind.Object)
                {
                    if (!change.Data.TryGetProperty("required", out var staysRequired) || staysRequired.ValueKind != JsonValueKind.True
                        || change.Data.TryGetProperty("status", out var nextStatus) && nextStatus.ValueKind == JsonValueKind.String && nextStatus.GetString() == "withdrawn")
                        conflicts.Add(new(change.Id, "required_goal_preserved", "Ein bereits verbindliches Arbeitsergebnis darf nicht eigenständig gestrichen werden. required=true beibehalten; eine fachliche Grenze mit openLimit, Grund und vorhandenen Belegen dokumentieren.", previous.Revision));
                    if (change.Data.TryGetProperty("status", out var limitStatus) && limitStatus.ValueKind == JsonValueKind.String && limitStatus.GetString() == "openLimit"
                        && (!change.Data.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(reason.GetString())
                            || !LimitEvidenceFields.Any(field => WorkingRefs(change.Data, field).Any())))
                        conflicts.Add(new(change.Id, "limit_evidence_required", "Die offene Grenze eines verbindlichen Arbeitsergebnisses benötigt reason und zugeordnete Quellen-, Evidenz-, Experiment- oder Prüfnachweise.", previous.Revision));
                }
            }
            if (conflicts.Count == 0)
                await ValidateWorkingReferencesAsync(connection, transaction, projectId, current, changes, conflicts, token).ConfigureAwait(false);
            if (conflicts.Count != 0)
            {
                await SaveWorkingReceiptAsync(connection, transaction, projectId, operationId, actorAgentId, digest,
                    new(false, [], conflicts), token).ConfigureAwait(false);
                return new(false, state, [], conflicts);
            }

            var accepted = changes.Where(change => !current.TryGetValue(change.Id, out var previous)
                || !string.Equals(CanonicalWorkingJson(previous.Data), CanonicalWorkingJson(change.Data), StringComparison.Ordinal)).ToArray();
            var titleChanged = title is not null && !string.Equals(title, state.Title, StringComparison.Ordinal);
            var changedIds = accepted.Select(change => change.Id).ToList();
            if (titleChanged) changedIds.Insert(0, "$title");
            if (changedIds.Count != 0)
            {
                var now = DateTimeOffset.UtcNow;
                var publicationChanged = titleChanged || PublicationAffected(current, accepted);
                foreach (var change in accepted)
                {
                    var owner = current.TryGetValue(change.Id, out var previous) ? previous.OwnerAgentId : actorAgentId;
                    await SaveWorkingItemAsync(connection, transaction, projectId, operationId, actorAgentId,
                        new(change.Id, change.Kind, (previous?.Revision ?? 0) + 1, owner, change.Data.Clone(), now), token).ConfigureAwait(false);
                }
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE research_working_states SET revision=revision+1,publication_revision=publication_revision+$publication,title=$title,updated_at=$now WHERE project_id=$project;";
                update.Parameters.AddWithValue("$project", projectId);
                update.Parameters.AddWithValue("$publication", publicationChanged ? 1 : 0);
                update.Parameters.AddWithValue("$title", title ?? state.Title);
                update.Parameters.AddWithValue("$now", Format(now));
                await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                if (titleChanged)
                    await SaveWorkingHistoryAsync(connection, transaction, projectId, "$title", state.Revision + 1,
                        operationId, actorAgentId, "title", JsonSerializer.Serialize(new { title }), now, token).ConfigureAwait(false);
                state = await ReadWorkingStateAsync(connection, transaction, projectId, token).ConfigureAwait(false);
            }
            await SaveWorkingReceiptAsync(connection, transaction, projectId, operationId, actorAgentId, digest,
                new(true, changedIds, []), token).ConfigureAwait(false);
            return new(true, state, changedIds, []);
        }, cancellationToken);
    }

    private static void ValidateWorkingData(ResearchWorkingChange change, List<ResearchWorkingConflict> conflicts)
    {
        if (change.Data.ValueKind != JsonValueKind.Object)
        {
            conflicts.Add(new(change.Id, "invalid_data", "Die Objektdaten müssen ein JSON-Objekt sein."));
            return;
        }
        if (change.Data.GetRawText().Length > 250_000)
            conflicts.Add(new(change.Id, "section_too_large", "Bitte diesen Abschnitt in mehrere fachlich eigenständige Abschnitte aufteilen."));
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in change.Data.EnumerateObject())
        {
            if (!names.Add(field.Name)) conflicts.Add(new(change.Id, "duplicate_field", $"Das Feld {field.Name} ist mehrfach angegeben."));
            if (!WorkingDataFields.Contains(field.Name))
                conflicts.Add(new(change.Id, "unsupported_field", $"Das Feld {field.Name} ist nicht vorgesehen. Ausführungs- und Prüfnachweise werden ausschließlich von Missum gespeichert."));
        }
        var textField = change.Kind is "section" or "contribution" ? "contentMarkdown" : "statement";
        if (!change.Data.TryGetProperty(textField, out var text) || text.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(text.GetString()))
            conflicts.Add(new(change.Id, "missing_content", $"Bitte {textField} mit dem vollständigen Inhalt dieses einzelnen Objekts angeben."));
        if (change.Kind is "section" or "contribution" && (!change.Data.TryGetProperty("title", out var heading)
            || heading.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(heading.GetString())))
            conflicts.Add(new(change.Id, "missing_heading", "Dieser Abschnitt benötigt eine fachliche Überschrift in title."));
        if (change.Data.TryGetProperty("status", out var status) && (status.ValueKind != JsonValueKind.String || !WorkingStatuses.Contains(status.GetString() ?? "")))
            conflicts.Add(new(change.Id, "invalid_status", "Der fachliche Status ist unbekannt."));
        if (change.Data.TryGetProperty("required", out var required) && required.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            conflicts.Add(new(change.Id, "invalid_required", "required muss true oder false sein."));
        foreach (var field in new[] { "title", "contentMarkdown", "statement", "prediction", "nextCheck", "reason", "description", "method", "expectedResult", "classification", "conclusion", "limit" })
            if (change.Data.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.String)
                conflicts.Add(new(change.Id, "invalid_text", $"{field} muss Text enthalten."));
        if (change.Data.TryGetProperty("assumptions", out var assumptions) && assumptions.ValueKind != JsonValueKind.String
            && (assumptions.ValueKind != JsonValueKind.Array || assumptions.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String)))
            conflicts.Add(new(change.Id, "invalid_assumptions", "assumptions enthält Text oder eine Liste von Annahmen."));
        if (change.Data.TryGetProperty("order", out var order) && (order.ValueKind != JsonValueKind.Number || !order.TryGetInt32(out _)))
            conflicts.Add(new(change.Id, "invalid_order", "order muss eine ganze Zahl sein."));
        ValidateWorkingMetadata(change, "units", ["symbol", "meaning", "unit"], conflicts);
        ValidateWorkingMetadata(change, "figureCaptions", ["experimentId", "artifactPath", "caption"], conflicts);
        foreach (var field in WorkingReferenceFields)
        {
            if (!change.Data.TryGetProperty(field, out var refs)) continue;
            if (refs.ValueKind != JsonValueKind.Array || refs.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())))
                conflicts.Add(new(change.Id, "invalid_reference", $"{field} muss eine Liste vorhandener Kennungen sein."));
        }
    }

    private static void ValidateWorkingMetadata(ResearchWorkingChange change, string field, string[] keys,
        List<ResearchWorkingConflict> conflicts)
    {
        if (!change.Data.TryGetProperty(field, out var value)) return;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 128
            || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object
                || item.EnumerateObject().Count() != keys.Length
                || item.EnumerateObject().Any(property => !keys.Contains(property.Name, StringComparer.Ordinal))
                || keys.Any(key => !item.TryGetProperty(key, out var text) || text.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(text.GetString()))))
            conflicts.Add(new(change.Id, "invalid_metadata", $"{field} muss eine Liste mit den Textfeldern {string.Join(", ", keys)} sein."));
    }

    private static async Task ValidateWorkingReferencesAsync(SqliteConnection connection, SqliteTransaction transaction,
        string projectId, Dictionary<string, ResearchWorkingItem> current, IReadOnlyList<ResearchWorkingChange> changes,
        List<ResearchWorkingConflict> conflicts, CancellationToken token)
    {
        var available = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var checks = new Dictionary<string, (string TargetId, string Status, string Evidence)>(StringComparer.Ordinal);
        var experimentArtifacts = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (field, sql) in new[]
        {
            ("sourceIds", "SELECT work_id FROM research_project_works WHERE project_id=$project"),
            ("evidenceIds", "SELECT id FROM research_evidence WHERE project_id=$project"),
            ("experimentIds", "SELECT id FROM research_experiments WHERE project_id=$project"),
        })
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = sql; command.Parameters.AddWithValue("$project", projectId);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) ids.Add(reader.GetString(0));
            available.Add(field, ids);
        }
        foreach (var check in await ReadExecutionVerificationsAsync(connection, transaction, projectId, token).ConfigureAwait(false))
            checks.Add(check.Id, (check.TargetId, check.Status, check.EvidenceJson));
        available["checkIds"] = checks.Keys.ToHashSet(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id,result_artifacts_json FROM research_experiments WHERE project_id=$project;";
            command.Parameters.AddWithValue("$project", projectId);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                using var artifacts = JsonDocument.Parse(reader.GetString(1));
                var paths = artifacts.RootElement.ValueKind == JsonValueKind.Array
                    ? artifacts.RootElement.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString()!.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                experimentArtifacts.Add(reader.GetString(0), paths);
            }
        }
        foreach (var (field, kind) in new[] { ("claimIds", "claim"), ("hypothesisIds", "hypothesis"), ("requirementIds", "requirement"), ("contributionIds", "contribution") })
            available[field] = current.Values.Where(item => item.Kind == kind).Select(item => item.Id)
                .Concat(changes.Where(item => item.Kind == kind).Select(item => item.Id)).ToHashSet(StringComparer.Ordinal);
        available["targetIds"] = current.Keys.Concat(changes.Select(item => item.Id)).Concat(available["experimentIds"]).ToHashSet(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            foreach (var field in WorkingReferenceFields)
            {
                foreach (var id in WorkingRefs(change.Data, field))
                    if (!available[field].Contains(id))
                        conflicts.Add(new(change.Id, "unknown_reference", $"{field}: {id} ist in diesem Projekt nicht vorhanden. Lade den benötigten Beleg mit research.read."));
            }
            if (change.Data.TryGetProperty("figureCaptions", out var captions))
                foreach (var caption in captions.EnumerateArray())
                {
                    var experimentId = caption.GetProperty("experimentId").GetString()!;
                    var artifactPath = caption.GetProperty("artifactPath").GetString()!.Replace('\\', '/');
                    if (!WorkingRefs(change.Data, "experimentIds").Contains(experimentId, StringComparer.Ordinal)
                        || !experimentArtifacts.TryGetValue(experimentId, out var paths) || !paths.Contains(artifactPath))
                        conflicts.Add(new(change.Id, "unknown_figure", "Die Abbildung muss ein gespeichertes Ergebnis eines unter experimentIds zugeordneten Experiments sein."));
                }
            if (change.Data.TryGetProperty("status", out var status) && status.GetString() == "verified")
            {
                // Model-authored prose must never manufacture an execution/verification receipt.
                var verified = WorkingRefs(change.Data, "checkIds").Any(id => checks.TryGetValue(id, out var check)
                    && (check.TargetId == change.Id || WorkingRefs(change.Data, "experimentIds").Contains(check.TargetId, StringComparer.Ordinal))
                    && check.Status is "verified" or "passed" or "succeeded"
                    && !string.IsNullOrWhiteSpace(check.Evidence) && check.Evidence is not ("{}" or "[]" or "null"));
                if (!verified) conflicts.Add(new(change.Id, "verification_required", "verified erfordert einen erfolgreichen, von Missum gespeicherten Prüfnachweis für genau dieses Objekt; bis dahin einen vorläufigen Status verwenden."));
            }
        }
    }

    private static bool PublicationAffected(Dictionary<string, ResearchWorkingItem> current, IReadOnlyList<ResearchWorkingChange> accepted)
    {
        if (accepted.Any(change => change.Kind == "section")) return true;
        var affected = accepted.Select(change => change.Id).ToHashSet(StringComparer.Ordinal);
        var discovered = true;
        while (discovered)
        {
            discovered = false;
            foreach (var item in current.Values)
                if (!affected.Contains(item.Id) && WorkingReferenceFields.SelectMany(field => WorkingRefs(item.Data, field)).Any(affected.Contains))
                    discovered |= affected.Add(item.Id);
        }
        return current.Values.Any(item => item.Kind == "section" && affected.Contains(item.Id));
    }

    private static IEnumerable<string> WorkingRefs(JsonElement data, string field) =>
        data.TryGetProperty(field, out var refs) && refs.ValueKind == JsonValueKind.Array
            ? refs.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!)
            : [];

    private static async Task EnsureWorkingStateAsync(SqliteConnection connection, SqliteTransaction transaction, string projectId, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM research_working_states WHERE project_id=$project;";
        command.Parameters.AddWithValue("$project", projectId);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 0) return;
        command.CommandText = "SELECT COUNT(*) FROM research_projects WHERE id=$project;";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 0)
            throw new InvalidOperationException("Das Forschungsprojekt ist nicht vorhanden.");
        var now = DateTimeOffset.UtcNow;
        command.CommandText = "INSERT INTO research_working_states(project_id,revision,publication_revision,title,updated_at) VALUES($project,0,0,'',$now);";
        command.Parameters.AddWithValue("$now", Format(now));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        command.Parameters.RemoveAt("$now");
        command.CommandText = """
            SELECT id,'hypothesis',statement,status,updated_at FROM research_hypotheses WHERE project_id=$project
            UNION ALL SELECT id,'claim',statement,conclusion_status,updated_at FROM research_claims WHERE project_id=$project;
            """;
        var imported = new List<ResearchWorkingItem>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var id = reader.GetString(0); var kind = reader.GetString(1); var status = reader.GetString(3);
                if (!used.Add(id)) { id = "legacy:" + kind + ":" + id; used.Add(id); }
                // Keep legacy scientific conclusions, without retroactively claiming a checked execution.
                if (status == "verified") status = "provisionallySupported";
                if (!WorkingStatuses.Contains(status)) status = "unresolved";
                var data = JsonSerializer.SerializeToElement(new { statement = reader.GetString(2), status,
                    reason = "Aus dem bisherigen Forschungsstand übernommen; vorhandene Belege bleiben im Projektarchiv." });
                imported.Add(new(id, kind, 1, null, data, Parse(reader.GetString(4))));
            }
        }
        foreach (var item in imported)
            await SaveWorkingItemAsync(connection, transaction, projectId, "migration:legacy-results", null, item, token).ConfigureAwait(false);
        if (imported.Count > 0)
        {
            command.CommandText = "UPDATE research_working_states SET revision=1 WHERE project_id=$project;";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }

    private static async Task<ResearchWorkingState> ReadWorkingStateAsync(SqliteConnection connection, SqliteTransaction transaction, string projectId, CancellationToken token)
    {
        long revision; long publicationRevision; string title; DateTimeOffset updatedAt;
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT revision,publication_revision,title,updated_at FROM research_working_states WHERE project_id=$project;";
        command.Parameters.AddWithValue("$project", projectId);
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("Forschungsstand fehlt.");
            revision = reader.GetInt64(0); publicationRevision = reader.GetInt64(1); title = reader.GetString(2); updatedAt = Parse(reader.GetString(3));
        }
        var items = new List<ResearchWorkingItem>();
        command.CommandText = "SELECT id,kind,revision,owner_agent_id,data_json,updated_at FROM research_working_items WHERE project_id=$project ORDER BY kind,id;";
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                using var data = JsonDocument.Parse(reader.GetString(4));
                items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetString(3), data.RootElement.Clone(), Parse(reader.GetString(5))));
            }
        }
        return new(projectId, revision, publicationRevision, title, items, updatedAt);
    }

    private static async Task SaveWorkingItemAsync(SqliteConnection connection, SqliteTransaction transaction, string projectId,
        string operationId, string? actorAgentId, ResearchWorkingItem item, CancellationToken token)
    {
        var json = CanonicalWorkingJson(item.Data);
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO research_working_items(project_id,id,kind,revision,owner_agent_id,data_json,updated_at)
            VALUES($project,$id,$kind,$revision,$owner,$json,$now)
            ON CONFLICT(project_id,id) DO UPDATE SET revision=excluded.revision,data_json=excluded.data_json,updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$project", projectId); command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$kind", item.Kind); command.Parameters.AddWithValue("$revision", item.Revision);
        command.Parameters.AddWithValue("$owner", (object?)item.OwnerAgentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$json", json); command.Parameters.AddWithValue("$now", Format(item.UpdatedAt));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await SaveWorkingHistoryAsync(connection, transaction, projectId, item.Id, item.Revision, operationId, actorAgentId, item.Kind, json, item.UpdatedAt, token).ConfigureAwait(false);
    }

    private static async Task SaveWorkingHistoryAsync(SqliteConnection connection, SqliteTransaction transaction, string projectId,
        string id, long revision, string operationId, string? actorAgentId, string kind, string json, DateTimeOffset now, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO research_working_history(project_id,item_id,revision,operation_id,actor_agent_id,kind,data_json,updated_at) VALUES($project,$id,$revision,$operation,$actor,$kind,$json,$now);";
        command.Parameters.AddWithValue("$project", projectId); command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$revision", revision); command.Parameters.AddWithValue("$operation", operationId);
        command.Parameters.AddWithValue("$actor", (object?)actorAgentId ?? DBNull.Value); command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$json", json); command.Parameters.AddWithValue("$now", Format(now));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task SaveWorkingReceiptAsync(SqliteConnection connection, SqliteTransaction transaction, string projectId,
        string operationId, string? actorAgentId, string digest, WorkingReceipt receipt, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO research_working_operations(project_id,operation_id,actor_agent_id,arguments_sha256,result_json,created_at) VALUES($project,$operation,$actor,$digest,$result,$now);";
        command.Parameters.AddWithValue("$project", projectId); command.Parameters.AddWithValue("$operation", operationId);
        command.Parameters.AddWithValue("$actor", (object?)actorAgentId ?? DBNull.Value); command.Parameters.AddWithValue("$digest", digest);
        command.Parameters.AddWithValue("$result", JsonSerializer.Serialize(receipt, WorkingJsonOptions));
        command.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static string WorkingDigest(string? actorAgentId, string? title, IReadOnlyList<ResearchWorkingChange> changes)
    {
        var json = JsonSerializer.SerializeToElement(new { actorAgentId, title,
            changes = changes.OrderBy(change => change.Id, StringComparer.Ordinal).Select(change => new
                { change.Id, change.Kind, change.ExpectedRevision, data = change.Data.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : change.Data }) });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalWorkingJson(json)))).ToLowerInvariant();
    }

    private static string CanonicalWorkingJson(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonicalWorkingJson(writer, element);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonicalWorkingJson(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name); WriteCanonicalWorkingJson(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) WriteCanonicalWorkingJson(writer, item); writer.WriteEndArray();
        }
        else element.WriteTo(writer);
    }

    private sealed record WorkingReceipt(bool Success, IReadOnlyList<string> ChangedIds, IReadOnlyList<ResearchWorkingConflict> Conflicts);
}
