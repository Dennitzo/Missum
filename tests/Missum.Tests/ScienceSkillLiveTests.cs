using System.Text;
using System.Text.Json;
using Missum.Ai.Client;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Coding;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class ScienceSkillLiveTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task DeepSeekStagesLeanNumericAndPublicationThroughRealGatewayAndNativeTools()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_SCIENCE_SKILL_LIVE") != "1") return;
        await using var environment = await TestEnvironment.CreateAsync();
        var model = Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_GENERAL_MODEL") ?? throw new InvalidOperationException("Live model missing");
        var output = Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_EVIDENCE_DIRECTORY") ?? environment.Directory;
        Directory.CreateDirectory(output);
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Science skill acceptance", ChatMode.ClaudeScience);
        var workspace = Path.Combine(environment.Directory, "science-workspace");
        Directory.CreateDirectory(workspace);
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace, activateCoding: false);
        await File.WriteAllTextAsync(Path.Combine(workspace, "Publikation.md"), "# Algebraische Kontrolle\n\n## Literatur und Daten\n\n### Fachliteratur\n\n### Forschungsdaten\n");
        var profile = new AssistantRuntimeProfile("test", "Missum", environment.Directory, new(Environment.GetEnvironmentVariable("MISSUM_AI_SERVER_URL") ?? "http://127.0.0.1:8080"),
            8081, 8082, Path.Combine(environment.Directory, "native"), Path.Combine(environment.Directory, "llama.exe"), Path.Combine(environment.Directory, "stack"), "test");
        using var sandbox = new ResearchSandboxService(profile, chats);
        var broker = new LocalToolBroker(null!, null!, null!, chats, researchSandbox: sandbox);
        using var http = new HttpClient { BaseAddress = profile.GatewayUri, Timeout = Timeout.InfiniteTimeSpan };
        using var client = new MissumAiClient(http, streamIdleTimeout: TimeSpan.FromMinutes(5));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(60));
        const string task = """
            Bearbeite diesen kleinen Forschungsauftrag mit dem Science-Skill in drei knappen überprüfbaren Etappen
            (Herleitung/Lean, Python, Publikation). Keine zusätzliche doppelte acceptanceCriteria-Liste nötig:
            Untersuche die Identität (x+y)^2 = x^2 + 2*x*y + y^2 für reelle Zahlen.
            Leite sie verständlich her und erstelle einen dazu passenden echten Lean-Beweis mit math.formalProof.
            Prüfe anschließend unabhängig mit einem Python-Skript die Zahlenpaare (2,3), (-4,1) und (0,7).
            Verwende dafür die normale projektlokale Python-Ausführung; Zusatzpakete sind nicht erforderlich.
            Arbeite Herleitung, Voraussetzungen, tatsächliche Prüfergebnisse und Beweis als mathematischen Fließtext
            in Publikation.md ein. Verlinke Skript und geprüfte Lean-Datei unter Literatur und Daten.
            Halte die Publikation für diesen kleinen Test knapp (höchstens 250 Wörter), mit echter Herleitung
            und den drei Zahlenwerten; keine wiederholten Erklärungen oder ausführlichen Metadatenlisten.
            Keine Plots erforderlich; keine Websuche für diese Eigenherleitung. Frage nicht nach Freigaben.
            Etappen nach vorhandenen erfolgreichen Belegen abschließen. Keine weiteren Forschungsthemen anfangen.
            """;
        var request = new RunRequest(MissumAiProtocol.Version, RunMode.Coding, [new("user", [new("text", task)])],
            ClientCapabilities: ["coding", "coding.process", "coding.evidence", "math.lean"],
            Limits: new(MaximumContextTokens: 258000, TimeoutSeconds: 0), SessionId: session.Id.ToString("D"),
            AllowedServerTools: [], PreferredCodingModelId: model, CodingOptions: new(WorkspacePath: workspace, ContinueSessionContext: true),
            ReasoningEffort: Environment.GetEnvironmentVariable("MISSUM_SCIENCE_SKILL_REASONING") ?? "none",
            ContextProfileVersion: "compact-v1", WorkflowProfile: "claude-science-v1");
        var accepted = await client.CreateRunAsync(request, cancellationToken: deadline.Token);
        var store = new CodingRunEvidenceStore(environment.Directory, session.Id, accepted.RunId);
        var events = new List<RunEvent>(); var results = new List<ClientToolResult>();
        var text = new StringBuilder(); RunCompletedEvent? completion = null; string? failure = null;
        try
        {
            await foreach (var item in client.StreamRunEventsAsync(accepted.RunId, cancellationToken: deadline.Token))
            {
                events.Add(item);
                if (item.Type == RunEventTypes.TextDelta && item.Data.TryGetProperty("delta", out var delta)) text.Append(delta.GetString());
                if (item.Type == RunEventTypes.ClientToolProposed)
                {
                    var proposal = item.Data.Deserialize<ToolProposal>(MissumAiProtocol.CreateJsonOptions())!;
                    var result = await broker.ExecuteAsync(proposal, session.Id, null, workspace, evidenceStore: store, cancellationToken: deadline.Token);
                    results.Add(result);
                    await File.AppendAllTextAsync(Path.Combine(output, "science-live-tools.jsonl"),
                        JsonSerializer.Serialize(new { proposal, result }, MissumAiProtocol.CreateJsonOptions()) + "\n", deadline.Token);
                    await client.SubmitClientToolResultAsync(accepted.RunId, result, deadline.Token);
                }
                if (item.Type == RunEventTypes.RunFailed) throw new InvalidOperationException(item.Data.GetRawText());
                if (item.Type == RunEventTypes.RunCompleted) { completion = item.Data.Deserialize<RunCompletedEvent>(MissumAiProtocol.CreateJsonOptions()); break; }
            }
            Assert.NotNull(completion);
            Assert.Equal("clarified", completion.ScientificOutcome);
            Assert.Contains(results, result => result.Status == "completed" && result.Result.TryGetProperty("formalVerification", out var status) && status.GetString() == "KernelAccepted");
            Assert.Contains(events, item => item.Type == RunEventTypes.ServerToolCompleted && item.Data.GetProperty("tool").GetString() == "coding.updatePlan");
            var publication = await File.ReadAllTextAsync(Path.Combine(workspace, "Publikation.md"));
            Assert.Contains("Literatur und Daten", publication, StringComparison.Ordinal);
            Assert.Contains(".lean", publication, StringComparison.Ordinal);
            Assert.Contains(".py", publication, StringComparison.Ordinal);
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(output, "science-live.json"), JsonSerializer.Serialize(new
                { accepted.RunId, completion, failure, events, results, text = text.ToString(), workspace }, MissumAiProtocol.CreateJsonOptions()));
            foreach (var file in Directory.GetFiles(workspace, "*", SearchOption.AllDirectories).Where(file => !file.Contains(Path.DirectorySeparatorChar + ".lake" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            {
                var target = Path.Combine(output, "workspace", Path.GetRelativePath(workspace, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, overwrite: true);
            }
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await client.CancelRunAsync(accepted.RunId, cleanup.Token); }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
            { await File.WriteAllTextAsync(Path.Combine(output, "cleanup-error.txt"), error.ToString()); }
        }
    }
}
