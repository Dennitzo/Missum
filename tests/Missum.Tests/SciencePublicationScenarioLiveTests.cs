using System.Collections.Concurrent;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Extensions;
using Missum.Core.Models;
using Missum.Core.Research;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;
using Xunit.Abstractions;

namespace Missum.Tests;

/// <summary>Real coordinator, model, source fetches, Docker Python and PDF. Opt-in, isolated from user chats.</summary>
public sealed class SciencePublicationScenarioLiveTests(ITestOutputHelper output)
{
    private const string PhysicsUrl = "https://openstax.org/books/university-physics-volume-1/pages/15-5-damped-oscillations";
    private const string SolverUrl = "https://docs.scipy.org/doc/scipy/reference/generated/scipy.integrate.solve_ivp.html";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions ProtocolJson = MissumAiProtocol.CreateJsonOptions();

    [Fact]
    [Trait("Category", "Live")]
    public async Task DeepResearchProducesAProgressiveMathPublicationAndRealModelWrittenPythonFigure()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_SCIENCE_PRESENTATION_LIVE") != "1") return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var started = DateTimeOffset.UtcNow;
        var evidenceRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_EVIDENCE_DIRECTORY")
            ?? Path.Combine(Path.GetTempPath(), "Missum-science-presentation-evidence", started.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)));
        Directory.CreateDirectory(evidenceRoot);
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var runs = environment.Get<IMissumAiRunRepository>();
        var documents = environment.Get<IDocumentIngestor>();
        var artifacts = environment.Get<IChatArtifactRepository>();
        var blobs = environment.Get<IBinaryObjectStore>();
        var research = environment.Get<IScientificResearchRepository>();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(value => value with
        {
            MissumAiServerUrl = Environment.GetEnvironmentVariable("MISSUM_AI_SERVER_URL") ?? "http://127.0.0.1:8080",
            IsAutomaticSpeechEnabled = false,
        });
        var requests = new ConcurrentQueue<AcceptedRequest>();
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance,
            () => new ObservingHandler(requests));
        using var client = await connection.CreateClientAsync(timeout.Token);
        var catalog = await client.GetModelStatusAsync(timeout.Token);
        var requestedModel = Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_GENERAL_MODEL")?.Trim();
        var model = catalog.Models.Where(item => item.Role == "general" && item.Downloaded && item.SupportsTools)
            .Where(item => string.IsNullOrEmpty(requestedModel) || item.Id == requestedModel)
            .OrderByDescending(item => item.Id.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => item.Loaded).FirstOrDefault();
        Assert.NotNull(model);
        await settings.UpdateAsync(value => value with
        {
            SelectedModel = model.Id, SelectedCodingModel = null, ReasoningEffort = "none",
            ReasoningEffortsByModel = new(StringComparer.OrdinalIgnoreCase)
                { [MissumAiAssistantService.ReasoningKey(model.Id, "general")] = "none" },
        });
        var profile = AssistantRuntimeProfile.Resolve() with
            { DataDirectory = environment.Directory, NativeStateDirectory = Path.Combine(environment.Directory, "native-state") };
        using var sandbox = new ResearchSandboxService(profile);
        using var exporter = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
        using var publications = new ScientificPublicationService(research,
            (path, token) => exporter.EnsureCurrentAsync(path, sourceChanged: true, scientificPublication: true, cancellationToken: token),
            Path.Combine(environment.Directory, "publications"), chats, runs, sandbox);
        using var simulations = new ScientificSimulationService(research, sandbox);
        using var presentation = new ScientificPresentationCoordinator(publications, simulations);
        var documentTools = new LocalDocumentToolService(environment.Get<IGeneratedDocumentRepository>(), documents,
            artifacts, blobs, chats, environment.Get<IDocumentFileCodec>(), exporter);
        var actions = ExtensionActionCatalog.CreateWithBuiltIns();
        var broker = new LocalToolBroker(connection, documents, documentTools, chats, actions, researchSandbox: sandbox);
        using var microphone = new MicrophoneTranscriptionService(connection, settings, NullLogger<MicrophoneTranscriptionService>.Instance);
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        using var service = new MissumAiAssistantService(connection, chats, environment.Get<IAssistantAttachmentRepository>(),
            artifacts, runs, environment.Get<IClientToolExecutionRepository>(), blobs, documents,
            new DocumentContextPreparationService(documents), new SessionContextPreparationService(chats), broker,
            null!, microphone, settings, recent, NullLogger<MissumAiAssistantService>.Instance,
            extensionActions: actions, scientificResearch: research, researchSandbox: sandbox, sciencePresentation: presentation);
        var coordinator = new AssistantCoordinator(chats, documents, environment.Get<IContextAssembler>(),
            environment.Get<IPromptTriggerRepository>(), environment.Get<IAssistantAttachmentRepository>(), artifacts,
            environment.Get<IConversationSnapshotRepository>(), service, settings, recent, microphone, profile, actions,
            scientificResearch: research, scientificResearchExports: environment.Get<IScientificResearchExportService>());
        var session = await chats.CreateSessionAsync("Gedämpfter Oszillator – Publikation und Simulation", ChatMode.ClaudeScience, timeout.Token);
        await chats.SetCodingWorkspacePathAsync(session.Id, Directory.CreateDirectory(Path.Combine(environment.Directory, "workspace")).FullName, timeout.Token);
        await settings.UpdateAsync(value => value with { ActiveSessionId = session.Id, SelectedChatMode = ChatMode.ClaudeScience });
        var projectId = "research-" + session.Id.ToString("N");
        var observedPublications = new HashSet<string>(StringComparer.Ordinal);
        var events = new ConcurrentQueue<(string Type, JsonElement Payload)>();
        string? failure = null;
        Task? send = null;
        try
        {
            output.WriteLine($"profile={environment.Directory}; model={model.Id}; reasoning=none when supported; evidence={evidenceRoot}");
            await CommandAsync("session.open", new { sessionId = session.Id });
            var prompt = $$"""
                Untersuche kompakt einen frei gedämpften harmonischen Oszillator. Lies diese beiden Originalseiten:
                {{PhysicsUrl}}
                {{SolverUrl}}
                Verwende ergänzend eine kurze lokale SearXNG-Suche. Zwei gelesene Quellen genügen.
                Erkläre m*x''+c*x'+k*x=0 und die analytische Lösung mit LaTeX-Formeln in einer kurzen wissenschaftlichen Publikation.
                Führe anschließend tatsächlich eine Python-Simulation mit scipy.integrate.solve_ivp aus:
                m=1 kg, c=0.4 kg/s, k=4 N/m, x(0)=1 m, x'(0)=0 m/s, 0<=t<=10 s.
                Dies sind ausdrücklich gewählte Modellparameter, keine Messdaten. Nutze mindestens 101 Zeitpunkte und rtol=1e-9, atol=1e-11.
                Vergleiche numerische und analytische Lösung. Wähle eine gut lesbare Darstellung für Zeitverlauf und Fehler.
                Schreibe dein Skript mit research.code.write und führe es mit research.code.execute in der Forschungssandbox aus.
                Speichere /sandbox/artifacts/damped-oscillator.png sowie /sandbox/artifacts/oscillator-result.json
                mit JSON-Feldern t (Sekunden als Zahlenliste), x (numerische Auslenkung in Metern als Zahlenliste), max_abs_error (Zahl).
                Kopiere das ausgeführte Skript ebenfalls nach /sandbox/artifacts/damped-oscillator.py.
                Gib in der Publikation den real berechneten maximalen Fehler an, diskutiere kurz die Modellgrenzen und zitiere beide Quellen.
                Überschrift: "Gedämpfter Oszillator". Höchstens 600 Wörter, wenige sinnvolle Abschnitte, mindestens eine abgesetzte LaTeX-Gleichung.
                """;
            send = CommandAsync("chat.send", new { sessionId = session.Id, prompt, deepResearch = true, deepResearchProfile = "mathematicalInvestigation" });
            while (!send.IsCompleted)
            {
                await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(1), timeout.Token));
                timeout.Token.ThrowIfCancellationRequested();
                if (presentation.GetSnapshot(projectId)?.Publication is { } interim && observedPublications.Add(interim.ContentHash))
                {
                    File.Copy(interim.PdfPath, Path.Combine(evidenceRoot, $"intermediate-{observedPublications.Count}.pdf"), overwrite: true);
                    output.WriteLine($"{DateTimeOffset.UtcNow:O} live PDF revision={interim.Revision}; draft={interim.IsDraft}");
                }
            }
            await send;
            await presentation.WaitForIdleAsync(projectId, timeout.Token);
            var assistant = (await chats.ListMessagesAsync(session.Id, timeout.Token)).Last(message => message.Role == ChatRole.Assistant);
            Assert.True(assistant.Status == MessageStatus.Completed, $"Run ended {assistant.Status}: {assistant.Error}");
            var accepted = requests.Last(item => item.Request.SessionId == session.Id.ToString("D"));
            Assert.True(accepted.Request.DeepResearch);
            Assert.Equal(model.Id, accepted.Request.PreferredGeneralModelId);
            if (model.ReasoningEfforts?.Contains("none", StringComparer.OrdinalIgnoreCase) == true)
                Assert.Equal("none", accepted.Request.ReasoningEffort);
            Assert.Equal(RunState.Completed, (await client.GetRunAsync(accepted.RunId, timeout.Token)).State);
            var steps = assistant.ToolSteps ?? [];
            Assert.Contains(steps, step => step.Tool == "assistant.narration" && step.Id.StartsWith("science-progress:", StringComparison.Ordinal));
            Assert.Contains(steps, step => step.Tool == ClientToolNames.ResearchCodeWrite && step.Status == "completed");
            Assert.Contains(steps, step => step.Tool == ClientToolNames.ResearchCodeExecute && step.Status == "completed"
                && Text(Parse(step.OutputJson), "verificationStatus") == "ProcessSucceeded");
            foreach (var url in new[] { PhysicsUrl, SolverUrl })
                Assert.Contains(steps, step => step.Tool == "web.fetch" && step.Status == "completed"
                    && Text(Parse(step.OutputJson), "url").TrimEnd('/') == url.TrimEnd('/')
                    && Parse(step.OutputJson).TryGetProperty("found", out var found) && found.ValueKind == JsonValueKind.True);

            var layout = await sandbox.EnsureProjectAsync(projectId, timeout.Token);
            var numeric = Parse(await File.ReadAllTextAsync(Path.Combine(layout.ArtifactsPath, "oscillator-result.json"), timeout.Token));
            var times = numeric.GetProperty("t").EnumerateArray().Select(item => item.GetDouble()).ToArray();
            var values = numeric.GetProperty("x").EnumerateArray().Select(item => item.GetDouble()).ToArray();
            Assert.InRange(times.Length, 101, 100_000);
            Assert.Equal(times.Length, values.Length);
            Assert.Equal(0, times[0], 8);
            Assert.Equal(10, times[^1], 8);
            var frequency = Math.Sqrt(3.96);
            var actualError = times.Select((time, index) => Math.Abs(values[index] - Math.Exp(-0.2 * time)
                * (Math.Cos(frequency * time) + 0.2 / frequency * Math.Sin(frequency * time)))).Max();
            Assert.InRange(actualError, 0, 0.0001);
            Assert.InRange(Math.Abs(numeric.GetProperty("max_abs_error").GetDouble() - actualError), 0, 0.000001);
            Assert.True(File.Exists(Path.Combine(layout.ArtifactsPath, "damped-oscillator.py")), "Executed Python source must be retained.");

            var shown = presentation.GetSnapshot(projectId);
            Assert.NotNull(shown);
            Assert.Null(shown.PublicationError);
            Assert.NotNull(shown.Publication);
            Assert.NotNull(shown.Simulation);
            Assert.NotEmpty(observedPublications); // Mandatory PDF must also appear while work is in progress.
            Assert.Contains(shown.Simulation.Artifacts, item => Path.GetFileName(item.ImagePath) == "damped-oscillator.png" && item.IsResearchData);
            var publication = await File.ReadAllTextAsync(shown.Publication.MarkdownPath, timeout.Token);
            Assert.Contains("Oszillator", publication, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("$$", publication);
            var manuscriptPassage = assistant.Content.Split('\n')
                .Where(line => !line.StartsWith('#') && !string.IsNullOrWhiteSpace(line))
                .OrderByDescending(line => line.Length).First();
            Assert.Contains(manuscriptPassage, publication);
            using var pdf = PdfDocument.Open(shown.Publication.PdfPath);
            var pdfText = string.Join('\n', pdf.GetPages().Select(page => page.Text));
            Assert.Contains("Oszillator", pdfText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\\ddot", pdfText);
            Assert.True(pdf.NumberOfPages > 0);
            output.WriteLine($"Research+Python+PDF passed: {pdf.NumberOfPages} pages, error={actualError:R}, figures={shown.Simulation.Artifacts.Count}.");
        }
        catch (Exception exception) { failure = exception.ToString(); throw; }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var ownedRunStopped = false;
            try
            {
                var owned = requests.LastOrDefault(item => item.Request.SessionId == session.Id.ToString("D"));
                ownedRunStopped = owned is null;
                if (owned is not null)
                {
                    var state = await client.GetRunAsync(owned.RunId, cleanup.Token);
                    if (state.State is not (RunState.Completed or RunState.Failed or RunState.Cancelled or RunState.Interrupted))
                    {
                        await client.CancelRunAsync(owned.RunId, cleanup.Token);
                        do
                        {
                            state = await client.GetRunAsync(owned.RunId, cleanup.Token);
                            if (state.State is RunState.Completed or RunState.Failed or RunState.Cancelled or RunState.Interrupted) break;
                            await Task.Delay(250, cleanup.Token);
                        } while (!cleanup.IsCancellationRequested);
                    }
                    ownedRunStopped = state.State is RunState.Completed or RunState.Failed or RunState.Cancelled or RunState.Interrupted;
                }
                if (service.IsRunning) await service.CancelCurrentAsync(cleanup.Token);
                if (send is not null)
                {
                    try { await send.WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch (Exception exception) { output.WriteLine("Owned send ended: " + exception.Message); }
                }
            }
            catch (Exception exception) { output.WriteLine("Live cleanup: " + exception.Message); }
            try
            {
                using var evidenceTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                presentation.Dispose();
                await presentation.WaitForIdleAsync(projectId, evidenceTimeout.Token);
                var messages = await chats.ListMessagesAsync(session.Id, evidenceTimeout.Token);
                var archive = await research.LoadArchiveSnapshotAsync(projectId, evidenceTimeout.Token);
                var shown = presentation.GetSnapshot(projectId);
                var destination = Path.Combine(evidenceRoot, "science-presentation-" + session.Id.ToString("N"));
                Directory.CreateDirectory(destination);
                await File.WriteAllTextAsync(Path.Combine(destination, "receipt.json"), JsonSerializer.Serialize(new
                {
                    started, completedAt = DateTimeOffset.UtcNow, sessionId = session.Id, projectId, modelId = model.Id, failure,
                    requests, messages, archive, shown,
                    events = events.Select(item => new { item.Type, item.Payload }),
                }, Json), evidenceTimeout.Token);
                if (shown?.Publication is { } document)
                {
                    File.Copy(document.PdfPath, Path.Combine(destination, "Publikation.pdf"), overwrite: true);
                    File.Copy(document.MarkdownPath, Path.Combine(destination, "Publikation.md"), overwrite: true);
                }
                var layout = await sandbox.EnsureProjectAsync(projectId, evidenceTimeout.Token);
                foreach (var file in Directory.EnumerateFiles(layout.ArtifactsPath, "*", new EnumerationOptions
                         { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    var target = Path.Combine(destination, "artifacts", Path.GetRelativePath(layout.ArtifactsPath, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: true);
                }
                output.WriteLine("Evidence: " + destination);
                if (Environment.GetEnvironmentVariable("MISSUM_SCIENCE_KEEP_PROFILE") == "1" && ownedRunStopped && send?.IsCompleted != false)
                {
                    var retained = await KeepUiProfileAsync(environment.Directory, evidenceRoot, session.Id, evidenceTimeout.Token);
                    output.WriteLine("UI_PROFILE=" + retained);
                    output.WriteLine("Start portable Missum with ASSISTANT_DATA_ROOT set to this path and a distinct ASSISTANT_INSTANCE_KEY.");
                }
            }
            catch (Exception exception) { output.WriteLine("Live evidence: " + exception.Message); }
        }

        Task CommandAsync(string type, object payload) => coordinator.HandleAsync(new(AssistantWebBridge.ProtocolVersion,
            type, Guid.NewGuid().ToString("N"), JsonSerializer.SerializeToElement(payload, Json)), (eventType, data, _) =>
        {
            var value = JsonSerializer.SerializeToElement(data, Json);
            events.Enqueue((eventType, value));
            if (eventType == "host.error") throw new InvalidOperationException(Text(value, "message"));
            return Task.CompletedTask;
        }, timeout.Token);
    }

    private static async Task<string> KeepUiProfileAsync(string sourceDirectory, string evidenceRoot, Guid sessionId, CancellationToken token)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        if (!string.Equals(Path.GetDirectoryName(source), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(source).StartsWith("Missum-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Only this isolated test profile may be retained.");
        var destination = Path.GetFullPath(Path.Combine(evidenceRoot, "ui-profile"));
        if (Directory.Exists(destination)) destination = Path.GetFullPath(Path.Combine(evidenceRoot, "ui-profile-" + sessionId.ToString("N")));
        if (Directory.Exists(destination) || File.Exists(destination)
            || destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The UI profile destination must be a new directory outside the live test profile.");
        Directory.CreateDirectory(destination);

        // BackupDatabase includes committed WAL pages; copying a live .db file alone does not.
        await using (var input = new SqliteConnection(new SqliteConnectionStringBuilder
                     { DataSource = Path.Combine(source, "Missum.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        await using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder
                     { DataSource = Path.Combine(destination, "Missum.db"), Pooling = false }.ToString()))
        {
            await input.OpenAsync(token);
            await backup.OpenAsync(token);
            input.BackupDatabase(backup);
            var tables = new List<string>();
            await using (var list = backup.CreateCommand())
            {
                list.CommandText = "SELECT name FROM pragma_table_list WHERE schema='main' AND type='table' AND name NOT LIKE 'sqlite_%';";
                await using var reader = await list.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) tables.Add(reader.GetString(0));
            }
            // Remap only the unique test-root prefix, including JSON-escaped paths.
            // Binary blobs and their hashes are left untouched. FTS virtual/shadow tables are excluded.
            var replacements = new[]
            {
                (Old: JsonSerializer.Serialize(source)[1..^1], New: JsonSerializer.Serialize(destination)[1..^1]),
                (Old: source, New: destination),
                (Old: source.Replace('\\', '/'), New: destination.Replace('\\', '/')),
            };
            foreach (var table in tables)
            {
                var columns = new List<string>();
                await using (var fields = backup.CreateCommand())
                {
                    fields.CommandText = "SELECT name FROM pragma_table_info($table) WHERE upper(type) LIKE '%TEXT%';";
                    fields.Parameters.AddWithValue("$table", table);
                    await using var reader = await fields.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token)) columns.Add(reader.GetString(0));
                }
                foreach (var column in columns)
                foreach (var replacement in replacements)
                {
                    await using var update = backup.CreateCommand();
                    var field = Quote(column);
                    update.CommandText = $"UPDATE {Quote(table)} SET {field}=replace({field},$old,$new) WHERE typeof({field})='text' AND instr({field},$old)>0;";
                    update.Parameters.AddWithValue("$old", replacement.Old);
                    update.Parameters.AddWithValue("$new", replacement.New);
                    await update.ExecuteNonQueryAsync(token);
                }
            }
            await using var check = backup.CreateCommand();
            check.CommandText = "PRAGMA quick_check;";
            Assert.Equal("ok", await check.ExecuteScalarAsync(token));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", new EnumerationOptions
                 { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            if (relative is "Missum.db" or "Missum.db-wal" or "Missum.db-shm"
                || relative.StartsWith("DatabaseBackups" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (relative.Equals("settings.json", StringComparison.OrdinalIgnoreCase))
            {
                var settingsText = await File.ReadAllTextAsync(file, token);
                settingsText = settingsText.Replace(JsonSerializer.Serialize(source)[1..^1], JsonSerializer.Serialize(destination)[1..^1], StringComparison.Ordinal);
                await File.WriteAllTextAsync(target, settingsText, token);
            }
            else File.Copy(file, target, overwrite: false);
        }
        Directory.CreateDirectory(Path.Combine(destination, "workspace"));
        await File.WriteAllTextAsync(Path.Combine(destination, "ui-profile-ready.json"), JsonSerializer.Serialize(new
        {
            sessionId, retainedAt = DateTimeOffset.UtcNow, source = "Isolated SciencePublicationScenarioLiveTests profile",
            dataRoot = destination, instanceKey = "science-ui-" + sessionId.ToString("N"),
        }, Json), token);
        return destination;

        static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static JsonElement Parse(string? json) => string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<JsonElement>(json);
    private static string Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var text) ? text.ToString() : "";
    private sealed record AcceptedRequest(string RunId, RunRequest Request);
    private sealed class ObservingHandler(ConcurrentQueue<AcceptedRequest> requests) : DelegatingHandler(new HttpClientHandler { UseProxy = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var run = request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/runs"
                ? JsonSerializer.Deserialize<RunRequest>(await request.Content!.ReadAsStringAsync(cancellationToken), ProtocolJson) : null;
            var response = await base.SendAsync(request, cancellationToken);
            if (run is not null && response.IsSuccessStatusCode)
            {
                var accepted = JsonSerializer.Deserialize<RunAccepted>(await response.Content.ReadAsStringAsync(cancellationToken), ProtocolJson);
                if (accepted is not null) requests.Enqueue(new(accepted.RunId, run));
            }
            return response;
        }
    }
}
