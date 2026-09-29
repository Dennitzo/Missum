using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Missum.Ai.Client;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Extensions;
using Missum.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using UglyToad.PdfPig;
using Xunit.Abstractions;

namespace Missum.Tests;

public sealed class AdditionalToolsLiveTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = MissumAiProtocol.CreateJsonOptions();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AudiobookLengthInstructionsPreserveAnExplicitShortParagraphRequest(bool continuing)
    {
        const string direction = "Schreibe genau zwei kurze Absätze mit jeweils einem Satz über Lina und einen Schlüssel.";
        var match = AssistantCoordinator.CreateToolMatch("audiobook", direction);
        var prompt = MissumAiAssistantService.BuildAudiobookPrompt(match, direction, continuing);
        Assert.Contains(direction, prompt, StringComparison.Ordinal);
        Assert.Contains("Wenn der Nutzer keine ausdrückliche Längenangabe macht", prompt, StringComparison.Ordinal);
        Assert.Contains("eintausendfünfhundert bis zweitausendfünfhundert Wörter", prompt, StringComparison.Ordinal);
        Assert.Contains("Wort-, Satz- oder Absatzanzahl hat Vorrang", prompt, StringComparison.Ordinal);
        Assert.Contains("mindestens zehn Prozent Sicherheitsabstand", prompt, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task DocumentCreatePdfUsesRealRendererAndPersistsReadableHashVerifiedArtifact()
    {
        if (!Enabled("MISSUM_AI_DOCUMENT_PDF_LIVE")) return;
        await using var environment = await TestEnvironment.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = deadline.Token;
        var chats = environment.Get<IChatRepository>();
        var artifacts = environment.Get<IChatArtifactRepository>();
        var blobs = environment.Get<IBinaryObjectStore>();
        var generated = environment.Get<IGeneratedDocumentRepository>();
        var session = await chats.CreateSessionAsync("Echte document.create PDF-Abnahme");
        var other = await chats.CreateSessionAsync("Unveränderter zweiter Chat");
        await chats.SaveDraftAsync(other.Id, "Entwurf bleibt erhalten", ct);
        var otherBefore = await chats.GetSessionAsync(other.Id, ct);
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "PDF wird erstellt", MessageStatus.Streaming);
        using var renderer = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
        var service = new LocalDocumentToolService(generated, environment.Get<IDocumentIngestor>(), artifacts,
            blobs, chats, environment.Get<IDocumentFileCodec>(), renderer);
        const string content = "PDFCREATESTART Der echte Dokumentdienst erzeugt diese PDF aus Markdown.\n\n"
            + "| Position | Wert |\n| --- | ---: |\n| Berechnetes Beispiel | 42 |\n\n"
            + "PDFCREATEEND Die Ausgabe muss als PDF lesbar und mit dem gespeicherten Blob identisch sein.";
        var result = JsonSerializer.SerializeToElement(await service.CreateAsync(JsonSerializer.SerializeToElement(new
        {
            operation = "create", reference = "Tool-Abnahme.pdf", format = "pdf", sectionId = "verification",
            heading = "Dokumentwerkzeug PDF", content,
        }), session.Id, message.Id, ct), Json);
        var artifact = await artifacts.GetAsync(result.GetProperty("artifactId").GetGuid(), ct);
        var document = await generated.GetAsync(result.GetProperty("documentId").GetGuid(), ct);
        Assert.NotNull(artifact);
        Assert.NotNull(document);
        Assert.Equal("pdf", document.Format);
        Assert.Equal(session.Id, document.SessionId);
        Assert.Equal(1, document.Revision);
        Assert.Equal(Hash(Encoding.UTF8.GetBytes(document.SourceMarkdown)), document.Sha256);
        Assert.Equal(document.Sha256, result.GetProperty("sha256").GetString());
        Assert.Equal("Tool-Abnahme.pdf", artifact.FileName);
        Assert.Equal("application/pdf", artifact.ContentType);
        Assert.Equal(message.Id, artifact.MessageId);
        Assert.Equal(artifact.Id, Assert.Single(await artifacts.ListForMessageAsync(message.Id, ct)).Id);
        byte[] bytes;
        await using (var stored = await blobs.OpenReadAsync(artifact.BlobId, ct))
        {
            using var copy = new MemoryStream();
            await stored.CopyToAsync(copy, ct);
            bytes = copy.ToArray();
        }
        Assert.Equal(artifact.Length, bytes.LongLength);
        Assert.Equal(artifact.Sha256, Hash(bytes));
        Assert.True(await blobs.VerifyAsync(artifact.BlobId, ct));
        var path = Path.Combine(environment.Directory, artifact.FileName);
        await File.WriteAllBytesAsync(path, bytes, ct);
        using var pdf = PdfDocument.Open(path);
        Assert.True(pdf.NumberOfPages > 0);
        var text = string.Join('\n', pdf.GetPages().Select(page => page.Text));
        Assert.Contains("PDFCREATESTART", text, StringComparison.Ordinal);
        Assert.Contains("PDFCREATEEND", text, StringComparison.Ordinal);
        Assert.Contains("42", text, StringComparison.Ordinal);
        var read = JsonSerializer.SerializeToElement(await service.ReadAsync(JsonSerializer.SerializeToElement(new
        {
            scope = "session", mode = "read", reference = artifact.Id.ToString("D"), maximumCharacters = 4000,
        }), session.Id, ct), Json);
        Assert.Contains("PDFCREATESTART", read.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("PDFCREATEEND", read.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(otherBefore, await chats.GetSessionAsync(other.Id, ct));
        Assert.Empty(await artifacts.ListForSessionAsync(other.Id, ct));
        await SaveEvidenceAsync("document-create-pdf", new
        {
            result, document.Id, document.Revision, artifact, pages = pdf.NumberOfPages,
            binarySha256 = Hash(bytes), sourceSha256 = document.Sha256, read, otherSessionUnchanged = true,
        }, [(artifact.FileName, bytes)]);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task CoordinatorAudiobookGeneratesTwoShortParagraphsAndRealPlayableAudioArtifacts()
    {
        if (!Enabled("MISSUM_AI_AUDIOBOOK_LIVE")) return;
        await using var environment = await TestEnvironment.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var ct = deadline.Token;
        var model = Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_GENERAL_MODEL");
        Assert.False(string.IsNullOrWhiteSpace(model), "MISSUM_AI_LIVE_GENERAL_MODEL must select an installed model.");
        PortableToolAcceptance.AssertDeepSeekReasoningOff(model!);
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(value => value with
        {
            MissumAiServerUrl = Environment.GetEnvironmentVariable("MISSUM_AI_SERVER_URL") ?? "http://127.0.0.1:8080",
            IsAutomaticSpeechEnabled = true, SelectedModel = model,
            ReasoningEffort = PortableToolAcceptance.ReasoningEffort(),
            ReasoningEffortsByModel = PortableToolAcceptance.ReasoningSelections((model!, "general")),
        });
        var observation = new AudioObservation();
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance,
            () => new AudioObservationHandler(observation));
        using var microphone = new MicrophoneTranscriptionService(connection, settings, NullLogger<MicrophoneTranscriptionService>.Instance);
        var chats = environment.Get<IChatRepository>();
        var documents = environment.Get<IDocumentIngestor>();
        var artifacts = environment.Get<IChatArtifactRepository>();
        var blobs = environment.Get<IBinaryObjectStore>();
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        var catalog = ExtensionActionCatalog.CreateWithBuiltIns();
        using var renderer = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
        var documentTools = new LocalDocumentToolService(environment.Get<IGeneratedDocumentRepository>(), documents,
            artifacts, blobs, chats, environment.Get<IDocumentFileCodec>(), renderer);
        var broker = new LocalToolBroker(connection, documents, documentTools, chats, catalog);
        using var service = new MissumAiAssistantService(connection, chats, environment.Get<IAssistantAttachmentRepository>(),
            artifacts, environment.Get<IMissumAiRunRepository>(), environment.Get<IClientToolExecutionRepository>(), blobs,
            documents, new DocumentContextPreparationService(documents), new SessionContextPreparationService(chats), broker,
            null!, microphone, settings, recent, NullLogger<MissumAiAssistantService>.Instance, extensionActions: catalog);
        var profile = AssistantRuntimeProfile.Resolve() with { DataDirectory = environment.Directory,
            NativeStateDirectory = Path.Combine(environment.Directory, "native-state") };
        var coordinator = new AssistantCoordinator(chats, documents,
            environment.Get<IContextAssembler>(), environment.Get<IPromptTriggerRepository>(), environment.Get<IAssistantAttachmentRepository>(),
            artifacts, environment.Get<IConversationSnapshotRepository>(), service, settings, recent, microphone, profile, catalog);
        var events = new ConcurrentQueue<(string Type, JsonElement Payload)>();
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var files = new List<(string Name, byte[] Bytes)>();
        ChatMessage? story = null;
        microphone.Changed += ObserveMicrophone;
        using var client = await connection.CreateClientAsync(ct);
        try
        {
            await Command("mode.switch", new { chatMode = "general" });
            var snapshot = JsonSerializer.SerializeToElement(await coordinator.BuildSnapshotAsync(ct), Json);
            var sessionId = snapshot.GetProperty("activeSessionId").GetGuid();
            await Command("action.invoke", new { sessionId, actionId = BuiltInActionIds.CreateAudiobook, enabled = true });
            Assert.Equal(BuiltInActionIds.CreateAudiobook, (await chats.GetSessionAsync(sessionId, ct))!.PersistentExtensionActionId);
            await Command("chat.send", new
            {
                sessionId,
                prompt = "Erstelle ein sehr kurzes Hörbuchkapitel auf Deutsch: nach einer separaten Kapitelüberschrift genau zwei kurze Prosaabsätze, "
                    + "jeder mit genau einem Satz, insgesamt höchstens neunzig Wörter. Diese ausdrückliche Kürze ersetzt den Standardumfang. "
                    + "Die Hauptfigur heißt Lina. Im ersten Absatz findet sie einen roten Schlüssel; im zweiten öffnet sie damit eine blaue Tür. "
                    + "Trenne die beiden Absätze mit einer Leerzeile. Gib ausschließlich die Überschrift und die beiden Absätze aus.",
            });
            story = (await chats.ListMessagesAsync(sessionId, ct)).Last(item => item.Role == ChatRole.Assistant);
            Assert.True(story.Status == MessageStatus.Completed, story.Error);
            Assert.Equal(MessageContentProfile.Audiobook, story.ContentProfile);
            var prose = string.Join('\n', story.Content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
                .Where(line => !line.TrimStart().StartsWith('#')));
            var paragraphs = prose.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(2, paragraphs.Length);
            Assert.InRange(prose.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, 10, 90);
            Assert.Contains("Schlüssel", paragraphs[0], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Tür", paragraphs[1], StringComparison.OrdinalIgnoreCase);
            Assert.Contains(observation.Runs, request => request.ConversationProfile == ConversationProfile.Audiobook);
            Assert.Equal("Abgeschlossen", await done.Task.WaitAsync(ct));
            Assert.False(microphone.Current.IsRecording);
            var audio = observation.Paragraphs.ToArray();
            Assert.True(audio.Length >= 2, "Both generated prose paragraphs must reach actual speech synthesis.");
            Assert.Contains(audio, item => item.Request.Text.Contains("Schlüssel", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(audio, item => item.Request.Text.Contains("Tür", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(audio.Length, audio.Select(item => item.Response.Artifact.ArtifactId).Distinct(StringComparer.Ordinal).Count());
            Assert.Contains(events, item => item.Type == "speech.progress" && item.Payload.GetProperty("state").GetString() == "playing");
            foreach (var item in audio)
            {
                Assert.Equal(SpeechProviderIds.SupertonicF5Cuda, item.Response.Provider);
                Assert.True(item.Response.DurationSeconds > 0);
                var artifact = item.Response.Artifact;
                Assert.Equal("audio/wav", artifact.MediaType);
                var name = artifact.ArtifactId + ".wav";
                var path = Path.Combine(environment.Directory, name);
                await client.DownloadArtifactAsync(artifact.ArtifactId, path, cancellationToken: ct);
                var bytes = await File.ReadAllBytesAsync(path, ct);
                Assert.Equal(artifact.Length, bytes.LongLength);
                Assert.Equal(artifact.Sha256, Hash(bytes));
                using var wave = new WaveFileReader(path);
                Assert.Equal(item.Response.SampleRate, wave.WaveFormat.SampleRate);
                Assert.True(wave.TotalTime.TotalSeconds > 0.1);
                var samples = wave.ToSampleProvider();
                var buffer = new float[4096];
                double energy = 0;
                long count = 0;
                int read;
                while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (var index = 0; index < read; index++) energy += buffer[index] * buffer[index];
                    count += read;
                }
                Assert.True(count > 0 && energy / count > 1e-9, "Generated audiobook audio must contain a non-silent waveform.");
                files.Add((name, bytes));
            }
            Assert.Empty(observation.ActiveSpeechSessions);
            Assert.Equal(BuiltInActionIds.CreateAudiobook, (await chats.GetSessionAsync(sessionId, ct))!.PersistentExtensionActionId);
            Assert.DoesNotContain(observation.Paths, path => path.Contains("transcription", StringComparison.OrdinalIgnoreCase)
                || path.Contains("live-caption", StringComparison.OrdinalIgnoreCase));
            output.WriteLine($"Actual audiobook model {model}: two prose paragraphs, {audio.Length} verified speech artifacts and physical playback.");
        }
        finally
        {
            microphone.Changed -= ObserveMicrophone;
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await service.CancelSpeechAsync(cleanup.Token);
                if (service.IsRunning) await service.CancelCurrentAsync(cleanup.Token);
                foreach (var session in observation.ActiveSpeechSessions.Keys)
                {
                    try { await client.CancelSpeechSessionAsync(session, cleanup.Token); }
                    catch (MissumAiApiException error) when (error.StatusCode == HttpStatusCode.NotFound) { }
                }
            }
            finally
            {
                await SaveEvidenceAsync("audiobook", new
                {
                    model, story, requests = observation.Runs.ToArray(), speech = observation.Paragraphs.ToArray(),
                    events = events.Select(item => new { item.Type, item.Payload }), microphoneDisabled = !microphone.Current.IsRecording,
                }, files);
            }
        }

        Task Command(string type, object payload) => coordinator.HandleAsync(new(AssistantWebBridge.ProtocolVersion, type,
            Guid.NewGuid().ToString("N"), JsonSerializer.SerializeToElement(payload, Json)), Emit, ct);
        Task Emit(string type, object payload, string? requestId)
        {
            var value = JsonSerializer.SerializeToElement(payload, Json);
            events.Enqueue((type, value));
            if (type == "host.error") throw new InvalidOperationException(value.GetProperty("message").GetString());
            if (type == "speech.status" && !value.GetProperty("active").GetBoolean())
            {
                if (value.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                    done.TrySetException(new InvalidOperationException(error.GetString()));
                else done.TrySetResult(value.GetProperty("status").GetString() ?? "");
            }
            return Task.CompletedTask;
        }
        void ObserveMicrophone(object? sender, MicrophoneSnapshot value)
        {
            if (value.Error is { Length: > 0 } error) done.TrySetException(new InvalidOperationException(error));
        }
    }

    private static bool Enabled(string flag) => Environment.GetEnvironmentVariable(flag) == "1"
        || Environment.GetEnvironmentVariable("MISSUM_AI_ADDITIONAL_TOOLS_LIVE") == "1";
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private async Task SaveEvidenceAsync(string label, object receipt, IEnumerable<(string Name, byte[] Bytes)> files)
    {
        output.WriteLine(JsonSerializer.Serialize(receipt, Json));
        var root = Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_EVIDENCE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(root)) return;
        var directory = Path.Combine(Path.GetFullPath(root), "additional-" + label + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "receipt.json"), JsonSerializer.Serialize(receipt, Json));
        foreach (var file in files) await File.WriteAllBytesAsync(Path.Combine(directory, file.Name), file.Bytes);
        output.WriteLine("Evidence: " + directory);
    }

    private sealed class AudioObservation
    {
        public ConcurrentQueue<RunRequest> Runs { get; } = new();
        public ConcurrentQueue<SpeechObservation> Paragraphs { get; } = new();
        public ConcurrentQueue<string> Paths { get; } = new();
        public ConcurrentDictionary<string, byte> ActiveSpeechSessions { get; } = new(StringComparer.Ordinal);
    }
    private sealed record SpeechObservation(SpeechParagraphRequest Request, SpeechParagraphResponse Response);

    /// <summary>Records real HTTP requests/responses; every call is forwarded unchanged to the gateway.</summary>
    private sealed class AudioObservationHandler(AudioObservation observation) : DelegatingHandler(new HttpClientHandler { UseProxy = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            observation.Paths.Enqueue(path);
            SpeechParagraphRequest? paragraph = null;
            if (request.Method == HttpMethod.Post && path == "/v1/runs")
                observation.Runs.Enqueue(JsonSerializer.Deserialize<RunRequest>(await request.Content!.ReadAsStringAsync(cancellationToken), Json)!);
            if (request.Method == HttpMethod.Post && path.EndsWith("/paragraphs", StringComparison.Ordinal))
                paragraph = JsonSerializer.Deserialize<SpeechParagraphRequest>(await request.Content!.ReadAsStringAsync(cancellationToken), Json)!;
            var response = await base.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return response;
            if (paragraph is not null)
                observation.Paragraphs.Enqueue(new(paragraph,
                    JsonSerializer.Deserialize<SpeechParagraphResponse>(await response.Content.ReadAsStringAsync(cancellationToken), Json)!));
            if (request.Method == HttpMethod.Post && path == "/v1/audio/speech/sessions")
            {
                var session = JsonSerializer.Deserialize<SpeechSessionSnapshot>(await response.Content.ReadAsStringAsync(cancellationToken), Json)!;
                observation.ActiveSpeechSessions.TryAdd(session.SessionId, 0);
            }
            if (request.Method == HttpMethod.Post && path.StartsWith("/v1/audio/speech/sessions/", StringComparison.Ordinal)
                && (path.EndsWith("/end", StringComparison.Ordinal) || path.EndsWith("/cancel", StringComparison.Ordinal)))
            {
                var session = JsonSerializer.Deserialize<SpeechSessionSnapshot>(await response.Content.ReadAsStringAsync(cancellationToken), Json)!;
                observation.ActiveSpeechSessions.TryRemove(session.SessionId, out _);
            }
            return response;
        }
    }
}
