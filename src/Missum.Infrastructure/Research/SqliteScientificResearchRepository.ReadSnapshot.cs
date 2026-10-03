using Missum.Core.Research;

namespace Missum.Infrastructure.Research;

public sealed partial class SqliteScientificResearchRepository
{
    public Task<ResearchWorkingReadSnapshot> LoadWorkingReadSnapshotAsync(string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return database.WriteAsync(async (connection, transaction, token) =>
        {
            await EnsureWorkingStateAsync(connection, transaction, projectId, token).ConfigureAwait(false);
            var state = await ReadWorkingStateAsync(connection, transaction, projectId, token).ConfigureAwait(false);
            ScientificResearchProject project;
            var sources = new List<ResearchLiteratureEntry>();
            var evidence = new List<ResearchEvidenceRecord>();
            var experiments = new List<ResearchExperiment>();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$project", projectId);
            command.CommandText = ProjectSelect + " WHERE id=$project;";
            await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("Forschungsprojekt fehlt.");
                project = ReadProject(reader);
            }
            command.CommandText = """
                SELECT w.id,pw.project_id,w.title,w.canonical_url,w.version_kind,w.metadata_json,pw.screening_status,pw.evidence_level,w.updated_at,w.doi,w.pmid,w.pmcid,w.arxiv_id,w.datacite_id,w.openalex_id
                FROM research_project_works pw JOIN research_works w ON w.id=pw.work_id WHERE pw.project_id=$project ORDER BY w.id;
                """;
            await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                    sources.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                        reader.IsDBNull(7) ? null : reader.GetString(7), Parse(reader.GetString(8)), reader.IsDBNull(9) ? null : reader.GetString(9),
                        reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12),
                        reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14)));
            command.CommandText = "SELECT id,project_id,work_id,exact_excerpt,normalized_statement,content_hash,evidence_level,locator_json,retrieved_at,page,section,table_or_figure FROM research_evidence WHERE project_id=$project ORDER BY id;";
            await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                    evidence.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), Parse(reader.GetString(8)),
                        reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11)));
            command.CommandText = "SELECT id,project_id,environment_lock,source_files_json,random_seeds_json,input_hashes_json,command_text,resource_limits_json,stdout_evidence,stderr_evidence,result_artifacts_json,verification_status,created_at,updated_at,hypothesis_id FROM research_experiments WHERE project_id=$project ORDER BY id;";
            await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                    experiments.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
                        reader.GetString(9), reader.GetString(10), reader.GetString(11), Parse(reader.GetString(12)), Parse(reader.GetString(13)), reader.IsDBNull(14) ? null : reader.GetString(14)));
            var checks = await ReadExecutionVerificationsAsync(connection, transaction, projectId, token).ConfigureAwait(false);
            return new ResearchWorkingReadSnapshot(project, state, sources, evidence, experiments, checks);
        }, cancellationToken);
    }
}
