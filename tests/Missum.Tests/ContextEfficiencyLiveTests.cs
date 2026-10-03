using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Missum.Ai.Client;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Models;
using Missum.Core.Research;
using UglyToad.PdfPig;
using Xunit.Abstractions;

namespace Missum.Tests;

/// <summary>Runs the published native client, not a mocked coordinator or a replay of completed text.</summary>
public sealed partial class ContextEfficiencyLiveTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] Modes = ["general", "coding", "science"];
    private static readonly string[] Caches = ["cold", "warm"];
    private static readonly string[] BeforeAfter = ["legacy", "compact-v1"];
    private static readonly string[] AfterBefore = ["compact-v1", "legacy"];

    [Fact]
    [Trait("Category", "Live")]
    public async Task PublishedNativeClientCompletesMatchedContextWorkflowsAndWritesBoundEvidence()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_CONTEXT_BENCHMARK_LIVE") != "1") return;
        Assert.Equal("1", Environment.GetEnvironmentVariable("MISSUM_CONTEXT_BENCHMARK_EXCLUSIVE_RUNTIME"));
        var executable = RequiredPath("MISSUM_CONTEXT_BENCHMARK_EXE");
        var evidence = Path.GetFullPath(Required("MISSUM_CONTEXT_BENCHMARK_EVIDENCE"));
        Directory.CreateDirectory(evidence);
        var generalModel = Required("MISSUM_AI_LIVE_GENERAL_MODEL");
        var codingModel = Required("MISSUM_AI_LIVE_CODING_MODEL");
        var effort = Required("MISSUM_CONTEXT_BENCHMARK_REASONING");
        var legacyUrl = Environment.GetEnvironmentVariable("MISSUM_CONTEXT_BENCHMARK_LEGACY_URL") ?? "http://127.0.0.1:18080";
        var compactUrl = Environment.GetEnvironmentVariable("MISSUM_CONTEXT_BENCHMARK_COMPACT_URL") ?? "http://127.0.0.1:18081";
        var legacyDatabase = RequiredPath("MISSUM_CONTEXT_BENCHMARK_LEGACY_DATABASE");
        var compactDatabase = RequiredPath("MISSUM_CONTEXT_BENCHMARK_COMPACT_DATABASE");
        var nativeUrl = Environment.GetEnvironmentVariable("MISSUM_AI_NATIVE_RUNTIME_URL") ?? "http://127.0.0.1:8081";
        var controlUrl = Environment.GetEnvironmentVariable("MISSUM_CONTEXT_BENCHMARK_CONTROL_URL") ?? "http://127.0.0.1:8082";
        var selectedMode = Environment.GetEnvironmentVariable("MISSUM_CONTEXT_BENCHMARK_MODE") ?? "all";
        var selectedCache = Environment.GetEnvironmentVariable("MISSUM_CONTEXT_BENCHMARK_CACHE") ?? "all";
        var selectedRepeat = Environment.GetEnvironmentVariable("MISSUM_CONTEXT_BENCHMARK_REPETITION") ?? "all";
        Assert.True(selectedMode == "all" || Modes.Contains(selectedMode, StringComparer.Ordinal));
        Assert.True(selectedCache == "all" || Caches.Contains(selectedCache, StringComparer.Ordinal));
        Assert.True(selectedRepeat == "all" || selectedRepeat is "1" or "2" or "3");
        var pairs = new List<object>();
        var manifest = Path.Combine(evidence, "native-workflows-manifest.json");
        using var deadline = new CancellationTokenSource(TimeSpan.FromHours(12));
        var token = deadline.Token;
        using var control = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(10) };
        using var native = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) };
        string? activeCase = null;
        string? activeRoot = null;
        try
        {
        foreach (var mode in Modes.Where(value => selectedMode == "all" || value == selectedMode))
        foreach (var cache in Caches.Where(value => selectedCache == "all" || value == selectedCache))
        for (var repetition = 1; repetition <= 3; repetition++)
        {
            if (selectedRepeat != "all" && selectedRepeat != repetition.ToString(System.Globalization.CultureInfo.InvariantCulture)) continue;
            var model = mode == "coding" ? codingModel : generalModel;
            var sides = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var variant in repetition % 2 == 0 ? AfterBefore : BeforeAfter)
            {
                var gateway = variant == "legacy" ? legacyUrl : compactUrl;
                var database = variant == "legacy" ? legacyDatabase : compactDatabase;
                using var gatewayHttp = new HttpClient(new HttpClientHandler { UseProxy = false })
                    { BaseAddress = new Uri(gateway.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(10) };
                using var gatewayClient = new MissumAiClient(gatewayHttp);
                var capabilities = await gatewayClient.GetCapabilitiesAsync(token);
                Assert.Equal(variant == "compact-v1", capabilities.ContextProfiles?.Contains("compact-v1", StringComparer.Ordinal) == true);
                var id = $"{mode}-{cache}-{repetition}-{variant}";
                var root = Directory.CreateDirectory(Path.Combine(evidence, "context-benchmark-" + id + "-" + Guid.NewGuid().ToString("N"))).FullName;
                activeCase = id; activeRoot = root;
                await ProgressAsync("starting", null);
                var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
                var data = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
                if (mode == "coding")
                {
                    await File.WriteAllTextAsync(Path.Combine(workspace, "calculator.py"), ContextEfficiencyBenchmarkScenarios.Calculator, token);
                    await File.WriteAllTextAsync(Path.Combine(workspace, "test_calculator.py"), ContextEfficiencyBenchmarkScenarios.CalculatorTest, token);
                }
                output.WriteLine($"START {id} {DateTimeOffset.UtcNow:O}");
                if (cache == "cold")
                {
                    // Only after the caller explicitly reserves the shared runtime. No raw slot erase or foreign session edits.
                    string[] instances = [model + "@subagent", model];
                    foreach (var instance in instances)
                    {
                        if (!await IsResidentAsync(instance, includeReplica: false)) continue;
                        using var unloadBody = new StringContent(JsonSerializer.Serialize(new { model = instance }, Json), Encoding.UTF8, "application/json");
                        using var unloaded = await native.PostAsync(nativeUrl.TrimEnd('/') + "/models/unload", unloadBody, token);
                        await EnsureModelControlSucceededAsync(unloaded, "unload", instance);
                        while (await IsResidentAsync(instance, includeReplica: false)) await Task.Delay(100, token);
                    }
                }
                using (var loadBody = new StringContent(JsonSerializer.Serialize(new { model }, Json), Encoding.UTF8, "application/json"))
                using (var loaded = await control.PostAsync(controlUrl.TrimEnd('/') + "/models/load", loadBody, token))
                    await EnsureModelControlSucceededAsync(loaded, "load", model);
                var config = new NativeContextBenchmarkConfiguration(mode, variant, cache, repetition, model, effort,
                    gateway, data, workspace, Path.Combine(root, "initial-result.json"), mode == "science" ? "initial" : "complete");
                var initial = await RunNativeAsync(config, executable, token);
                var legs = initial.Legs.ToList();
                NativeContextBenchmarkResult final = initial;
                if (mode == "science")
                {
                    final = await RunNativeAsync(config with { Stage = "resume", SessionId = initial.SessionId,
                        ResultPath = Path.Combine(root, "restart-result.json") }, executable, token);
                    Assert.True(final.CanonicalStateRestored, final.Error);
                    Assert.Equal(initial.SessionId, final.SessionId);
                    legs.AddRange(final.Legs);
                }
                Assert.NotEmpty(legs);
                Assert.All(legs, leg =>
                {
                    Assert.Equal(model, leg.ModelId);
                    Assert.True(leg.Milliseconds > 0);
                    Assert.NotNull(leg.FirstVisibleAnswerAt);
                    Assert.True(leg.VisibleCompletedAt >= leg.FirstVisibleAnswerAt);
                });
                var journal = new List<RunEvent>();
                foreach (var leg in legs)
                {
                    Assert.Equal(RunState.Completed, (await gatewayClient.GetRunAsync(leg.RunId, token)).State);
                    await foreach (var item in gatewayClient.StreamRunEventsAsync(leg.RunId, 0, token))
                    { journal.Add(item); if (item.Type is RunEventTypes.RunCompleted or RunEventTypes.RunFailed or RunEventTypes.RunCancelled) break; }
                }
                var firstMetrics = journal.First(item => item.RunId == legs[0].RunId && item.Type == "model.turn.metrics").Data;
                Assert.Equal(variant, firstMetrics.GetProperty("contextProfile").GetString());
                Assert.Equal(effort, firstMetrics.GetProperty("reasoningEffort").GetString());
                var metrics = firstMetrics.GetProperty("metrics");
                var cached = metrics.GetProperty("cachedPromptTokens").GetInt32();
                if (cache == "cold") Assert.Equal(0, cached);
                else Assert.True(cached >= metrics.GetProperty("inputTokens").GetInt32() / 2d, "Actual warm prefix reuse is below 50%.");
                var tools = legs.SelectMany(leg => leg.ToolSteps.Concat(leg.ChildToolSteps)).ToArray();
                if (mode == "general")
                {
                    Assert.Equal(2, legs.Count);
                    Assert.Contains(tools, step => step.Tool == "web.fetch" && step.Status == "completed"
                        && Parse(step.OutputJson).TryGetProperty("found", out var found) && found.ValueKind == JsonValueKind.True
                        && Parse(step.InputJson).GetProperty("url").GetString()?.TrimEnd('/') == ContextEfficiencyBenchmarkScenarios.SourceUrl.TrimEnd('/'));
                    Assert.True(HasMathResult(legs[0], 9)); Assert.Contains("9", legs[0].Content, StringComparison.Ordinal);
                    Assert.True(HasMathResult(legs[1], 36)); Assert.Contains("36", legs[1].Content, StringComparison.Ordinal);
                }
                else if (mode == "coding")
                {
                    Assert.Equal(ContextEfficiencyBenchmarkScenarios.CalculatorTest, await File.ReadAllTextAsync(Path.Combine(workspace, "test_calculator.py"), token));
                    Assert.NotEqual(ContextEfficiencyBenchmarkScenarios.Calculator, await File.ReadAllTextAsync(Path.Combine(workspace, "calculator.py"), token));
                    Assert.Contains(tools, step => step.Tool == ClientToolNames.CodingRead && step.Status == "completed");
                    Assert.Contains(tools, step => step.Tool is ClientToolNames.CodingEdit or ClientToolNames.CodingWrite && step.Status == "completed");
                    Assert.Contains(tools, step => step.Tool == ClientToolNames.CodingCommand && step.Status == "completed"
                        && Parse(step.OutputJson).TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True);
                    await VerifyPythonAsync(workspace, root, token);
                }
                else
                {
                    Assert.Equal(2, legs.Count); Assert.True(HasMathResult(legs[0], 9));
                    Assert.False(initial.RequiredSimulation); Assert.False(final.RequiredSimulation);
                    Assert.NotNull(initial.FirstPdfAt); Assert.NotNull(initial.FirstPdfPath);
                    Assert.True(final.PublicationRevision > initial.PublicationRevision);
                    Assert.NotNull(initial.ResearchState); Assert.NotNull(final.ResearchState);
                    ValidateResearchState(initial.ResearchState, initial.SessionId);
                    ValidateResearchState(final.ResearchState, final.SessionId);
                    ValidateRestartDelta(initial.ResearchState, final.ResearchState);
                    ValidatePdf(initial.FirstPdfPath!, requireExample: false);
                    ValidatePdf(final.FinalPdfPath!, requireExample: true);
                }
                var checks = ContextEfficiencyBenchmarkScenarios.RequiredChecks(mode).ToDictionary(check => check, _ => true, StringComparer.Ordinal);
                var runIds = legs.Select(leg => leg.RunId).ToArray();
                var acceptancePath = Path.Combine(root, "acceptance.json");
                var acceptance = new Dictionary<string, object?>
                {
                    [runIds.Length == 1 ? "runId" : "runIds"] = runIds.Length == 1 ? runIds[0] : runIds,
                    ["passed"] = true, ["fixtureSha256"] = ContextEfficiencyBenchmarkScenarios.FixtureHash(mode),
                    ["acceptanceSha256"] = ContextEfficiencyBenchmarkScenarios.AcceptanceHash(mode), ["checks"] = checks,
                    ["clientTiming"] = new { captureMode = "actual-ui-submit-to-visible-completion", renderer = "WinUI3", legs,
                        activeMilliseconds = legs.Sum(leg => leg.Milliseconds), initial.WarmupRunIds },
                    ["initialResult"] = config.ResultPath, ["finalResult"] = final,
                };
                await File.WriteAllTextAsync(acceptancePath, JsonSerializer.Serialize(acceptance, Json), token);
                var side = new Dictionary<string, object?>
                {
                    [runIds.Length == 1 ? "runId" : "runIds"] = runIds.Length == 1 ? runIds[0] : runIds,
                    ["database"] = database, ["acceptanceFile"] = acceptancePath,
                    ["acceptanceEvidenceSha256"] = HashFile(acceptancePath),
                };
                if (mode == "science")
                {
                    var pdfEvidence = Path.Combine(root, "first-pdf.json");
                    var started = journal.First(item => item.RunId == runIds[0] && item.Type == RunEventTypes.RunStarted).CreatedAt;
                    await File.WriteAllTextAsync(pdfEvidence, JsonSerializer.Serialize(new
                    {
                        runId = runIds[0], runIds, passed = true, captureMode = "run-scoped-first-pdf", startedAt = started,
                        firstPdfAt = initial.FirstPdfAt, pdfPath = initial.FirstPdfPath, pdfSha256 = HashFile(initial.FirstPdfPath!),
                        initial.SessionId, projectId = "research-" + initial.SessionId.ToString("N"), initial.FirstPdfRevision,
                    }, Json), token);
                    side["firstPdfFile"] = pdfEvidence; side["firstPdfEvidenceSha256"] = HashFile(pdfEvidence);
                }
                sides[variant == "legacy" ? "before" : "after"] = side;
                await ProgressAsync("completed", legs.Sum(leg => leg.Milliseconds));
                output.WriteLine($"PASS {id}; UI active milliseconds={legs.Sum(leg => leg.Milliseconds):F0}");
            }
            pairs.Add(new { id = $"{mode}-{cache}-{repetition}", mode, cache, fixtureId = mode + "-representative-v1",
                fixtureSha256 = ContextEfficiencyBenchmarkScenarios.FixtureHash(mode), acceptanceSha256 = ContextEfficiencyBenchmarkScenarios.AcceptanceHash(mode),
                modelId = model, reasoningEffort = effort, requiredChecks = ContextEfficiencyBenchmarkScenarios.RequiredChecks(mode),
                before = sides["before"], after = sides["after"] });
            await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new { schema = "missum.context-efficiency.v1", minimumWarmReuseFraction = .5, pairs }, Json), token);
        }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await ProgressAsync("failed", null, exception.GetType().Name);
            throw;
        }
        output.WriteLine("Collector manifest: " + manifest);

        async Task ProgressAsync(string state, double? uiActiveMilliseconds, string? errorType = null)
        {
            var path = Path.Combine(evidence, "progress.json");
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(new
            {
                state, caseId = activeCase, artifactRoot = activeRoot, observedAt = DateTimeOffset.UtcNow,
                uiActiveMilliseconds, errorType, successfulPairs = pairs.Count,
            }, Json), CancellationToken.None);
            File.Move(path + ".tmp", path, overwrite: true);
        }

        async Task<bool> IsResidentAsync(string model, bool includeReplica)
        {
            using var catalog = JsonDocument.Parse(await native.GetStringAsync(nativeUrl.TrimEnd('/') + "/v1/models", token));
            return catalog.RootElement.GetProperty("data").EnumerateArray().Any(item =>
                item.GetProperty("id").GetString() is { } id && (id == model || includeReplica && id == model + "@subagent")
                && item.GetProperty("status").GetProperty("value").GetString() is "loaded" or "loading" or "sleeping");
        }

        async Task EnsureModelControlSucceededAsync(HttpResponseMessage response, string operation, string model)
        {
            if (response.IsSuccessStatusCode) return;
            const int maximumCharacters = 4096;
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var reader = new StreamReader(stream);
            var characters = new char[maximumCharacters + 1];
            var count = await reader.ReadBlockAsync(characters.AsMemory(), token);
            var body = new string(characters, 0, Math.Min(count, maximumCharacters));
            body = new string(body.Select(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t') ? ' ' : character).ToArray());
            var diagnostic = $"Model {operation} failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}; model={model}\n"
                + body + (count > maximumCharacters ? "\n[body truncated at 4096 characters]" : "");
            if (activeRoot is not null)
                await File.WriteAllTextAsync(Path.Combine(activeRoot, "model-control-error.txt"), diagnostic, token);
            output.WriteLine(diagnostic);
            throw new HttpRequestException(diagnostic, null, response.StatusCode);
        }
    }

    private async Task<NativeContextBenchmarkResult> RunNativeAsync(NativeContextBenchmarkConfiguration configuration, string executable, CancellationToken token)
    {
        var input = configuration.ResultPath + ".input.json";
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(configuration, Json), token);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)!, WindowStyle = ProcessWindowStyle.Hidden };
        start.Environment["ASSISTANT_DATA_ROOT"] = configuration.DataDirectory;
        start.Environment["MISSUM_DATA_DIRECTORY"] = configuration.DataDirectory;
        start.Environment["ASSISTANT_INSTANCE_KEY"] = "context-benchmark-" + Guid.NewGuid().ToString("N");
        start.Environment["ASSISTANT_GATEWAY_URL"] = configuration.ServerUrl;
        start.Environment["ASSISTANT_NATIVE_STATE_ROOT"] = Path.Combine(Path.GetDirectoryName(configuration.DataDirectory)!, "native-state");
        start.Environment["ASSISTANT_DISABLE_RUNTIME_AUTOSTART"] = "1";
        start.Environment["MISSUM_CONTEXT_BENCHMARK_ENABLE"] = "1";
        start.Environment["MISSUM_CONTEXT_BENCHMARK_INPUT"] = input;
        start.Environment.Remove("MISSUM_SMOKE_INSTANCE_KEY");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the published native client.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(configuration.TimeoutMinutes + 2));
        try
        {
            while (!File.Exists(configuration.ResultPath))
            {
                Assert.False(process.HasExited, "The native benchmark client exited before producing its result.");
                await Task.Delay(250, deadline.Token);
            }
            var result = JsonSerializer.Deserialize<NativeContextBenchmarkResult>(await File.ReadAllTextAsync(configuration.ResultPath, deadline.Token), Json);
            Assert.NotNull(result); Assert.Equal("WinUI3", result.Renderer); Assert.True(result.Passed, result.Error);
            return result;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None); }
                catch (TimeoutException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            }
            output.WriteLine("Native evidence: " + configuration.ResultPath);
        }
    }

    private static async Task VerifyPythonAsync(string workspace, string root, CancellationToken token)
    {
        var python = Environment.GetEnvironmentVariable("MISSUM_CONTEXT_BENCHMARK_PYTHON") ?? "C:/Python314/python.exe";
        Assert.True(File.Exists(python), "An actual installed Python executable is required.");
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workspace,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-m"); start.ArgumentList.Add("unittest"); start.ArgumentList.Add("-v");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(token); var stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var text = await stdout + await stderr;
        await File.WriteAllTextAsync(Path.Combine(root, "independent-unittest.log"), text, token);
        Assert.Equal(0, process.ExitCode); Assert.Contains("Ran 3 tests", text, StringComparison.Ordinal); Assert.Contains("OK", text, StringComparison.Ordinal);
    }

    private static void ValidatePdf(string path, bool requireExample)
    {
        Assert.True(File.Exists(path));
        using var document = PdfDocument.Open(path);
        Assert.True(document.NumberOfPages > 0);
        var text = string.Join('\n', document.GetPages().Select(page => page.Text));
        Assert.Contains("Energie", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kg", text, StringComparison.OrdinalIgnoreCase);
        if (requireExample) { Assert.Contains("9", text, StringComparison.Ordinal); Assert.Contains("Herleitung", text, StringComparison.OrdinalIgnoreCase); }
        Assert.DoesNotContain("Standarddossier", text, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateResearchState(ResearchWorkingState state, Guid sessionId)
    {
        Assert.Equal("research-" + sessionId.ToString("N"), state.ProjectId);
        var sections = state.Items.Where(item => item.Kind == "section").ToArray();
        Assert.Equal(2, sections.Length);
        Assert.All(sections, section =>
        {
            Assert.Null(section.OwnerAgentId);
            Assert.True(Text(section.Data, "contentMarkdown").Length >= 80, "An actual complete scientific section is required.");
            Assert.True(Text(section.Data, "status") is "completed" or "supported" or "verified");
        });
        Assert.Single(sections, section => Text(section.Data, "title").Contains("Grundlagen", StringComparison.OrdinalIgnoreCase));
        Assert.Single(sections, section => Text(section.Data, "title").Contains("Herleitung", StringComparison.OrdinalIgnoreCase));
        var content = string.Join('\n', sections.Select(section => Text(section.Data, "contentMarkdown")));
        Assert.True(EnergyFormula().IsMatch(content), "The scientific text must actually give one-half m v-squared, not just name energy.");
        var unitContent = content + JsonSerializer.Serialize(sections.Select(section => section.Data));
        Assert.Contains("kg", unitContent, StringComparison.OrdinalIgnoreCase);
        Assert.True(unitContent.Contains("m/s", StringComparison.OrdinalIgnoreCase)
            || unitContent.Contains("m s", StringComparison.OrdinalIgnoreCase)
            || unitContent.Contains("m\\", StringComparison.OrdinalIgnoreCase), "Velocity units are absent.");
        Assert.Contains("J", unitContent, StringComparison.Ordinal);
        Assert.True(content.Contains("relativ", StringComparison.OrdinalIgnoreCase)
            || content.Contains("Lichtgeschwindigkeit", StringComparison.OrdinalIgnoreCase), "The required model limit is absent.");
        Assert.Contains(state.Items, item => item.Kind == "requirement"
            && item.Data.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.True
            && Text(item.Data, "status") is "completed" or "supported" or "verified");
    }

    private static void ValidateRestartDelta(ResearchWorkingState before, ResearchWorkingState after)
    {
        var foundation = Assert.Single(before.Items, item => item.Kind == "section"
            && Text(item.Data, "title").Contains("Grundlagen", StringComparison.OrdinalIgnoreCase));
        foreach (var prior in before.Items)
        {
            var current = Assert.Single(after.Items, item => item.Id == prior.Id);
            Assert.Equal(prior.Kind, current.Kind); Assert.Equal(prior.OwnerAgentId, current.OwnerAgentId);
            if (prior.Id != foundation.Id)
            {
                Assert.Equal(prior.Revision, current.Revision);
                Assert.True(JsonElement.DeepEquals(prior.Data, current.Data), $"Unchanged research object {prior.Id} differs.");
                continue;
            }
            Assert.True(current.Revision > prior.Revision);
            Assert.NotEqual(Text(prior.Data, "contentMarkdown"), Text(current.Data, "contentMarkdown"));
            Assert.Contains(Text(prior.Data, "contentMarkdown").Trim(), Text(current.Data, "contentMarkdown"), StringComparison.Ordinal);
            var addedText = Text(current.Data, "contentMarkdown").Replace(Text(prior.Data, "contentMarkdown").Trim(), "", StringComparison.Ordinal);
            Assert.True(addedText.Contains("Lichtgeschwindigkeit", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(addedText, @"v\s*(?:\\ll|<<|≪)\s*c", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
                "The restart must add the requested speed-of-light validity limit to the foundations.");
            var oldFields = prior.Data.EnumerateObject().Where(field => field.Name != "contentMarkdown").ToDictionary(field => field.Name, field => field.Value);
            var newFields = current.Data.EnumerateObject().Where(field => field.Name != "contentMarkdown").ToDictionary(field => field.Name, field => field.Value);
            Assert.Equal(oldFields.Count, newFields.Count);
            foreach (var field in oldFields) Assert.True(JsonElement.DeepEquals(field.Value, newFields[field.Key]), $"Unchanged field {field.Key} differs.");
        }
    }

    // Accept ordinary TeX half-fraction and plain equivalent notation; this is a fixed fixture check, not a mathematical oracle.
    [GeneratedRegex(@"(?:\\(?:tfrac|frac)\s*(?:\{\s*1\s*\}\s*\{\s*2\s*\}|1\s*2)|0[.,]5|1\s*/\s*2)\s*(?:[\s*·]|\\[,!;])*m\s*(?:[\s*·]|\\[,!;])*v\s*(?:\^\s*\{?\s*2\s*\}?|²)|\\(?:tfrac|frac)\s*\{\s*m\s*(?:[\s*·]|\\[,!;])*v\s*\^\s*\{?\s*2\s*\}?\s*\}\s*\{\s*2\s*\}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex EnergyFormula();
    private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";

    private static bool HasMathResult(NativeContextBenchmarkLeg leg, double expected) => leg.ToolSteps.Concat(leg.ChildToolSteps)
        .Any(step => step.Tool == "math.evaluate" && step.Status == "completed" && Parse(step.OutputJson).TryGetProperty("result", out var result)
            && (result.ValueKind == JsonValueKind.Number && result.GetDouble() == expected
                || result.ValueKind == JsonValueKind.Array && result.GetArrayLength() == 1 && result[0].GetDouble() == expected));
    private static JsonElement Parse(string? value) => string.IsNullOrWhiteSpace(value) ? default : JsonSerializer.Deserialize<JsonElement>(value);
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException(name + " is required.");
    private static string RequiredPath(string name) { var path = Path.GetFullPath(Required(name)); Assert.True(File.Exists(path), name + " does not exist."); return path; }
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
}
