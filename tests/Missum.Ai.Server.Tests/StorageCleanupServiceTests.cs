using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Runs;
using Missum.Ai.Server.Core.Storage;
using Missum.Ai.Server.Core.Workers;

namespace Missum.Ai.Server.Tests;

public sealed class StorageCleanupServiceTests
{
    [Theory]
    [InlineData("Running")]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [InlineData("Interrupted")]
    public async Task CleanupKeepsExpiredUploadReferencedByAnyRetainedRun(string state)
    {
        using var context = new TestServerContext();
        var uploadId = "upload-" + Guid.NewGuid().ToString("N");
        var uploadDirectory = Path.Combine(context.Options.UploadDirectory, uploadId);
        Directory.CreateDirectory(uploadDirectory);
        await File.WriteAllTextAsync(Path.Combine(uploadDirectory, "payload.bin"), "image");

        await using (var connection = await context.Database.OpenConnectionAsync())
        {
            var expired = Missum.Ai.Server.Core.Data.MissumAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow.AddMinutes(-1));
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO uploads(upload_id, file_name, media_type, total_length, total_sha256,
                    chunk_size, chunk_count, state, created_at, expires_at)
                VALUES($id, 'reference.webp', 'image/webp', 5, $sha, 5, 1, 'complete', $expired, $expired);
                """;
            insert.Parameters.AddWithValue("$id", uploadId);
            insert.Parameters.AddWithValue("$sha", new string('a', 64));
            insert.Parameters.AddWithValue("$expired", expired);
            _ = await insert.ExecuteNonQueryAsync();
        }

        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var (run, _) = await repository.CreateAsync(new RunRequest(
            MissumAiProtocol.Version,
            RunMode.Coding,
            [new RunMessage("user", [new ContentPart("upload", UploadId: uploadId)])],
            UploadIds: [uploadId]), null);
        await using (var connection = await context.Database.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE runs SET state=$state WHERE run_id=$run;";
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$run", run.RunId);
            await command.ExecuteNonQueryAsync();
        }

        var cleanup = new StorageCleanupService(context.Database, context.WrappedOptions);
        await cleanup.CleanupExpiredAsync();

        await using var verify = await context.Database.OpenConnectionAsync();
        await using var count = verify.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM uploads WHERE upload_id = $id;";
        count.Parameters.AddWithValue("$id", uploadId);
        Assert.Equal(1L, Convert.ToInt64(await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(Directory.Exists(uploadDirectory));
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [InlineData("Interrupted")]
    public async Task CleanupPreservesOldTerminalRunsAndEveryDurableDependentRecord(string state)
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var request = new RunRequest(
            MissumAiProtocol.Version,
            RunMode.General,
            [new RunMessage("user", [new ContentPart("text", "Dauerhafter Sitzungsverlauf")])]);
        var (run, _) = await repository.CreateAsync(request, null);
        await repository.AppendEventAsync(run.RunId, RunEventTypes.RunStarted, new { protocolVersion = MissumAiProtocol.Version });
        var originalEvents = await repository.GetEventsAfterAsync(run.RunId, 0);

        var artifactId = "artifact-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(context.Options.ArtifactDirectory);
        var artifactPath = Path.Combine(context.Options.ArtifactDirectory, artifactId + ".bin");
        await File.WriteAllTextAsync(artifactPath, "Historical evidence");

        await using (var connection = await context.Database.OpenConnectionAsync())
        {
            var old = Missum.Ai.Server.Core.Data.MissumAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow.AddDays(-40));
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE runs SET state = $state, updated_at = $old WHERE run_id = $run;
                INSERT INTO run_checkpoints(run_id,checkpoint_json,updated_at) VALUES($run,'{"messages":[]}', $old);
                INSERT INTO coding_session_contexts(run_id,checkpoint_json) VALUES($run,'{"messages":[]}');
                INSERT INTO session_context_heads(session_id,workspace_key,role,project_key,cache_epoch,run_id)
                    VALUES('retained-session','C:/Project','general','retained-project','project-context-v1',$run);
                INSERT INTO client_tool_proposals(proposal_id,run_id,name,proposal_json,expires_at,created_at)
                    VALUES('retained-proposal',$run,'coding.read','{}',$old,$old);
                INSERT INTO client_tool_results(proposal_id,run_id,result_json,created_at)
                    VALUES('retained-proposal',$run,'{"status":"completed"}',$old);
                INSERT INTO run_steering_inputs(run_id,input_id,session_id,text,accepted_at)
                    VALUES($run,'retained-steering','retained-session','Keep original evidence',$old);
                INSERT INTO run_provider_retries(run_id,attempt,next_attempt_at) VALUES($run,1,$old);
                INSERT INTO artifacts(artifact_id,file_name,media_type,total_length,sha256,file_path,metadata_json,created_at,expires_at)
                    VALUES($artifact,'evidence.txt','text/plain',19,$sha,$path,NULL,$old,$old);
                INSERT INTO gpu_leases(lease_id, run_id, workload, state, created_at, acquired_at, released_at)
                VALUES('lease-expired', $run, 'llm', 'released', $old, $old, $old);
                """;
            command.Parameters.AddWithValue("$old", old);
            command.Parameters.AddWithValue("$run", run.RunId);
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$artifact", artifactId);
            command.Parameters.AddWithValue("$sha", new string('b', 64));
            command.Parameters.AddWithValue("$path", artifactPath);
            _ = await command.ExecuteNonQueryAsync();
        }

        var cleanup = new StorageCleanupService(context.Database, context.WrappedOptions);
        await cleanup.CleanupExpiredAsync();

        await using var verify = await context.Database.OpenConnectionAsync();
        foreach (var table in new[] { "runs", "run_events", "run_checkpoints", "coding_session_contexts", "session_context_heads",
            "client_tool_proposals", "client_tool_results", "run_steering_inputs", "run_provider_retries", "artifacts" })
        {
            await using var count = verify.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM {table};";
            Assert.Equal(table == "run_events" ? (long)originalEvents.Count : 1L,
                Convert.ToInt64(await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        }
        await using var leaseCount = verify.CreateCommand();
        leaseCount.CommandText = "SELECT COUNT(*) FROM gpu_leases;";
        Assert.Equal(0L, Convert.ToInt64(await leaseCount.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(state, (await repository.GetAsync(run.RunId))!.State.ToString());
        Assert.Equal(originalEvents.Select(item => (item.Id, item.Type, item.Data.GetRawText())),
            (await repository.GetEventsAfterAsync(run.RunId, 0)).Select(item => (item.Id, item.Type, item.Data.GetRawText())));
        Assert.True(File.Exists(artifactPath));
    }

    [Fact]
    public async Task CleanupStillRemovesExpiredUnreferencedUploadsAndOnlyOldTerminalLeases()
    {
        using var context = new TestServerContext();
        var uploadId = "upload-" + Guid.NewGuid().ToString("N");
        var uploadDirectory = Path.Combine(context.Options.UploadDirectory, uploadId);
        Directory.CreateDirectory(uploadDirectory);
        await File.WriteAllTextAsync(Path.Combine(uploadDirectory, "payload.bin"), "unused");
        await using (var connection = await context.Database.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO uploads(upload_id,file_name,media_type,total_length,total_sha256,chunk_size,chunk_count,state,created_at,expires_at)
                    VALUES($upload,'unused.txt','text/plain',6,$sha,6,1,'complete',$old,$old);
                INSERT INTO gpu_leases(lease_id,workload,state,created_at) VALUES('old-released','llm','released',$old);
                INSERT INTO gpu_leases(lease_id,workload,state,created_at) VALUES('old-cancelled','llm','cancelled',$old);
                INSERT INTO gpu_leases(lease_id,workload,state,created_at) VALUES('old-interrupted','llm','interrupted',$old);
                INSERT INTO gpu_leases(lease_id,workload,state,created_at) VALUES('old-active','llm','acquired',$old);
                INSERT INTO gpu_leases(lease_id,workload,state,created_at) VALUES('recent-released','llm','released',$recent);
                """;
            command.Parameters.AddWithValue("$upload", uploadId);
            command.Parameters.AddWithValue("$sha", new string('a', 64));
            command.Parameters.AddWithValue("$old", Missum.Ai.Server.Core.Data.MissumAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow.AddHours(-25)));
            command.Parameters.AddWithValue("$recent", Missum.Ai.Server.Core.Data.MissumAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync();
        }
        await new StorageCleanupService(context.Database, context.WrappedOptions).CleanupExpiredAsync();
        Assert.False(Directory.Exists(uploadDirectory));
        await using var verify = await context.Database.OpenConnectionAsync();
        await using var count = verify.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM uploads;";
        Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        count.CommandText = "SELECT COUNT(*) FROM gpu_leases WHERE lease_id IN ('old-active','recent-released');";
        Assert.Equal(2L, Convert.ToInt64(await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        count.CommandText = "SELECT COUNT(*) FROM gpu_leases;";
        Assert.Equal(2L, Convert.ToInt64(await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("{\"purpose\":\"speech-playback\",\"transient\":\"true\"}", true)]
    [InlineData("{\"purpose\":\"speech-playback\",\"transient\":true}", true)]
    [InlineData("{\"purpose\":\"speech-playback\",\"transient\":1}", true)]
    [InlineData("{\"purpose\":\"speech-playback\",\"transient\":\"TRUE\"}", true)]
    [InlineData("{\"purpose\":\"speech-playback\",\"transient\":false}", false)]
    [InlineData("{\"purpose\":\"speech-playback\"}", false)]
    [InlineData("{\"purpose\":\"audio-tool\",\"transient\":true}", false)]
    [InlineData("{\"provider\":\"supertonic\",\"durationSeconds\":5}", false)]
    [InlineData("{}", false)]
    [InlineData("null", false)]
    [InlineData("not valid JSON", false)]
    [InlineData(null, false)]
    public async Task CleanupOnlyRemovesExpiredExplicitPlaybackArtifacts(string? metadata, bool removed)
    {
        using var context = new TestServerContext();
        var artifactId = "artifact-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(context.Options.ArtifactDirectory);
        var path = Path.Combine(context.Options.ArtifactDirectory, artifactId + ".bin");
        var futurePath = Path.Combine(context.Options.ArtifactDirectory, "artifact-not-expired.bin");
        await File.WriteAllTextAsync(path, "WAV");
        await File.WriteAllTextAsync(futurePath, "WAV");
        await using (var connection = await context.Database.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO artifacts(artifact_id,file_name,media_type,total_length,sha256,file_path,metadata_json,created_at,expires_at)
                    VALUES($artifact,'paragraph.wav','audio/wav',3,$sha,$path,$metadata,$old,$old);
                INSERT INTO artifacts(artifact_id,file_name,media_type,total_length,sha256,file_path,metadata_json,created_at,expires_at)
                    VALUES('artifact-not-expired','paragraph.wav','audio/wav',3,$sha,$futurePath,$metadata,$old,$future);
                """;
            command.Parameters.AddWithValue("$artifact", artifactId);
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$futurePath", futurePath);
            command.Parameters.AddWithValue("$metadata", (object?)metadata ?? DBNull.Value);
            command.Parameters.AddWithValue("$sha", new string('c', 64));
            command.Parameters.AddWithValue("$old", Missum.Ai.Server.Core.Data.MissumAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow.AddDays(-2)));
            command.Parameters.AddWithValue("$future", Missum.Ai.Server.Core.Data.MissumAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow.AddHours(1)));
            await command.ExecuteNonQueryAsync();
        }
        await new StorageCleanupService(context.Database, context.WrappedOptions).CleanupExpiredAsync();
        Assert.Equal(!removed, File.Exists(path));
        Assert.True(File.Exists(futurePath));
        await using var verify = await context.Database.OpenConnectionAsync();
        await using var count = verify.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM artifacts;";
        Assert.Equal(removed ? 1L : 2L, Convert.ToInt64(await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void OnlyPlaybackParagraphsReceiveTransientMetadataAndWorkerMetadataIsNotMutated()
    {
        var original = new Dictionary<string, string> { ["sampleRate"] = "24000" };
        var playback = WorkerOrchestrator.SpeechArtifactMetadata(original, transientPlayback: true);
        Assert.NotNull(playback);
        Assert.Equal("speech-playback", playback["purpose"]);
        Assert.Equal("true", playback["transient"]);
        Assert.Equal("24000", playback["sampleRate"]);
        Assert.False(original.ContainsKey("transient"));
        Assert.Same(original, WorkerOrchestrator.SpeechArtifactMetadata(original, transientPlayback: false));
        Assert.Null(WorkerOrchestrator.SpeechArtifactMetadata(null, transientPlayback: false));
    }
}
