using System.Diagnostics;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.App.Controls;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Core.Research;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task RunContextEfficiencyBenchmarkAsync()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_CONTEXT_BENCHMARK_ENABLE") != "1"
            || Environment.GetEnvironmentVariable("MISSUM_CONTEXT_BENCHMARK_INPUT") is not { Length: > 0 } input) return;
        var configuration = JsonSerializer.Deserialize<NativeContextBenchmarkConfiguration>(await File.ReadAllTextAsync(input), JsonOptions)
            ?? throw new InvalidOperationException("Missing native context benchmark configuration.");
        var ownedRoot = Path.GetDirectoryName(Path.GetFullPath(configuration.DataDirectory))!;
        if (!Path.GetFullPath(App.Current.DataDirectory).Equals(Path.GetFullPath(configuration.DataDirectory), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(ownedRoot).StartsWith("context-benchmark-", StringComparison.Ordinal)
            || !Path.GetFullPath(configuration.ResultPath).StartsWith(ownedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The native benchmark must use its own isolated data/result directory.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(configuration.TimeoutMinutes, 2, 60)));
        var token = deadline.Token;
        var chats = App.Current.GetService<IChatRepository>();
        var runs = App.Current.GetService<IMissumAiRunRepository>();
        var research = App.Current.GetService<IScientificResearchRepository>();
        var states = (IScientificResearchStateRepository)research;
        var presentation = App.Current.GetService<ScientificPresentationCoordinator>();
        var connection = App.Current.GetService<MissumAiConnectionService>();
        var legs = new List<NativeContextBenchmarkLeg>();
        var warmups = new List<string>();
        string? firstPdf = null;
        DateTimeOffset? firstPdfAt = null;
        long? firstPdfRevision = null;
        string? finalPdf = null;
        string? stateHash = null;
        long? publicationRevision = null;
        ResearchWorkingState? finalState = null;
        var restored = false;
        var simulationRequired = false;
        Exception? failure = null;
        var mode = configuration.Mode switch
        {
            "general" => ChatMode.General, "coding" => ChatMode.Coding, "science" => ChatMode.ClaudeScience,
            _ => throw new InvalidOperationException("Unknown context benchmark mode."),
        };
        try
        {
            if (configuration.Variant is not ("legacy" or "compact-v1") || configuration.Cache is not ("cold" or "warm"))
                throw new InvalidOperationException("Unknown context benchmark variant/cache.");
            await _settings.UpdateAsync(value => value with
            {
                MissumAiServerUrl = configuration.ServerUrl, IsAutomaticSpeechEnabled = false,
                SelectedModel = configuration.ModelId,
                SelectedCodingModel = configuration.ModelId,
                ReasoningEffort = configuration.ReasoningEffort,
                ReasoningEffortsByModel = new(StringComparer.OrdinalIgnoreCase)
                {
                    [MissumAiAssistantService.ReasoningKey(configuration.ModelId, mode == ChatMode.Coding ? "coding" : "general")] = configuration.ReasoningEffort,
                },
            }, token);
            using (var client = await connection.CreateClientAsync(token))
            {
                var capabilities = await client.GetCapabilitiesAsync(token);
                var compact = capabilities.ContextProfiles?.Contains("compact-v1", StringComparer.Ordinal) == true;
                if (compact != (configuration.Variant == "compact-v1"))
                    throw new InvalidOperationException("The actual negotiated gateway profile does not match this benchmark variant.");
            }
            ChatSession session;
            if (configuration.SessionId is { } existing)
            {
                session = await chats.GetSessionAsync(existing, token) ?? throw new InvalidOperationException("Restart session missing.");
                if (session.ChatMode != mode || session.CodingWorkspacePath != configuration.WorkspacePath)
                    throw new InvalidOperationException("Restart changed project/session identity.");
                restored = true;
            }
            else
            {
                session = await chats.CreateSessionAsync("Kontextbenchmark " + configuration.Mode, mode, token);
                await chats.SetCodingWorkspacePathAsync(session.Id, configuration.WorkspacePath, token);
            }
            await _settings.UpdateAsync(value => value with
            {
                ActiveSessionId = session.Id, SelectedChatMode = mode,
                ActiveClaudeScienceSessionId = mode == ChatMode.ClaudeScience ? session.Id : value.ActiveClaudeScienceSessionId,
            }, token);
            if (!await CommandAsync("session.open", new { sessionId = session.Id }))
                throw new InvalidOperationException("Opening the isolated native benchmark session failed.");
            if (_session != session.Id || _mode != (mode == ChatMode.ClaudeScience ? "claudescience" : configuration.Mode))
                throw new InvalidOperationException("The native composer is displaying a different benchmark session.");
            var projectId = "research-" + session.Id.ToString("N");
            if (configuration.Stage == "resume")
            {
                if (mode != ChatMode.ClaudeScience) throw new InvalidOperationException("Only Science uses the restart stage.");
                var before = await states.LoadWorkingStateAsync(projectId, token);
                var hashPath = Path.Combine(ownedRoot, "before-restart-state.sha256");
                restored &= File.Exists(hashPath) && await File.ReadAllTextAsync(hashPath, token) == ContextEfficiencyBenchmarkScenarios.Hash(JsonSerializer.Serialize(before, JsonOptions));
                if (!restored) throw new InvalidOperationException("The canonical research state was not preserved by the actual client restart.");
                legs.Add(await MeasureAsync(ContextEfficiencyBenchmarkScenarios.Followup(configuration.Mode), "restart"));
            }
            else
            {
                if (configuration.Cache == "warm")
                    warmups.Add((await MeasureAsync(ContextEfficiencyBenchmarkScenarios.Warmup(configuration.Mode), "warmup")).RunId);
                legs.Add(await MeasureAsync(ContextEfficiencyBenchmarkScenarios.Prompt(configuration.Mode), "initial"));
                if (mode == ChatMode.General)
                    legs.Add(await MeasureAsync(ContextEfficiencyBenchmarkScenarios.Followup(configuration.Mode), "followup"));
            }
            if (mode == ChatMode.ClaudeScience)
            {
                presentation.Queue(projectId);
                await presentation.WaitForIdleAsync(projectId, token);
                var state = await states.LoadWorkingStateAsync(projectId, token);
                finalState = state;
                var snapshot = presentation.GetSnapshot(projectId);
                var verification = await ScientificDeliverablesVerifier.VerifyAsync(projectId, snapshot, state,
                    await research.LoadResultSnapshotAsync(projectId, token), token);
                if (!verification.GetProperty("success").GetBoolean()) throw new InvalidOperationException(verification.GetRawText());
                simulationRequired = verification.GetProperty("simulation").GetProperty("required").GetBoolean();
                finalPdf = snapshot?.Publication?.PdfPath;
                publicationRevision = state.PublicationRevision;
                stateHash = ContextEfficiencyBenchmarkScenarios.Hash(JsonSerializer.Serialize(state, JsonOptions));
                if (configuration.Stage != "resume")
                    await File.WriteAllTextAsync(Path.Combine(ownedRoot, "before-restart-state.sha256"), stateHash, token);
                if (simulationRequired) throw new InvalidOperationException("The purely theoretical benchmark unexpectedly requires a simulation.");
            }
            await SaveMathPreviewAsync(ConversationScroll, "context-benchmark-completion.png");

            async Task<NativeContextBenchmarkLeg> MeasureAsync(string prompt, string phase)
            {
                var baselineRevision = mode == ChatMode.ClaudeScience && await research.GetProjectAsync(projectId, token) is { ProtocolVersion: >= 2 }
                    ? (await states.LoadWorkingStateAsync(projectId, token)).PublicationRevision : 0;
                var priorMessages = _messages.Keys.ToHashSet(StringComparer.Ordinal);
                var shown = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                Composer.Text = prompt;
                var submitted = DateTimeOffset.UtcNow;
                var clock = Stopwatch.StartNew();
                var captureState = 0;
                var captured = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                DateTimeOffset? firstVisible = null;
                double? firstVisibleMilliseconds = null;
                DateTimeOffset? finished = null;
                double finishedMilliseconds = 0;
                void CapturePublication(object? sender, ScientificPresentationPublishedEventArgs published)
                {
                    if (configuration.Stage == "resume" || phase == "warmup" || mode != ChatMode.ClaudeScience
                        || published.ProjectId != projectId || published.Snapshot.PublicationError is not null
                        || published.Snapshot.Publication is not { SectionDelta: true } current
                        || current.Revision <= baselineRevision || Interlocked.CompareExchange(ref captureState, 1, 0) != 0) return;
                    try
                    {
                        var target = Path.Combine(ownedRoot, "first-publication.pdf");
                        // The publisher accepted this immutable edition. A later state revision must not hide the first accepted PDF.
                        File.Copy(current.PdfPath, target, overwrite: false);
                        firstPdf = target; firstPdfAt = published.PublishedAt; firstPdfRevision = current.Revision;
                        captured.TrySetResult(true);
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    { captured.TrySetException(exception); } // Propagate only to the benchmark, never the production render loop.
                }
                void Observe(object? sender, object frame)
                {
                    foreach (var message in _messages.Where(pair => !priorMessages.Contains(pair.Key) && S(pair.Value, "role") == "assistant"))
                    {
                        var value = message.Value;
                        if (S(value, "status") is "failed" or "cancelled" or "interrupted")
                        { shown.TrySetException(new InvalidOperationException(S(value, "error", S(value, "status")))); return; }
                        if (!_messageViews.TryGetValue(message.Key, out var view) || !_messageBlocks.TryGetValue(message.Key, out var blocks)) continue;
                        var signature = S(value, "content") + S(value, "toolSteps") + S(value, "error") + S(value, "artifacts") + S(value, "status") + S(value, "liveStatus");
                        if (view.Signature != signature) continue; // Observe the real renderer; never force an early render.
                        var visible = S(value, "content").Trim().Length > 0 && blocks.Any(pair => pair.Key.StartsWith("text:", StringComparison.Ordinal)
                            && pair.Value is NativeStreamingMarkdown { IsLoaded: true, ActualHeight: > 0, Visibility: Visibility.Visible });
                        if (visible && firstVisible is null) { firstVisible = DateTimeOffset.UtcNow; firstVisibleMilliseconds = clock.Elapsed.TotalMilliseconds; }
                        if (S(value, "status") == "completed" && view.View.IsLoaded && view.View.ActualHeight > 0)
                        {
                            finished = DateTimeOffset.UtcNow; finishedMilliseconds = clock.Elapsed.TotalMilliseconds;
                            shown.TrySetResult(value.Clone()); return;
                        }
                    }
                }
                CompositionTarget.Rendering += Observe;
                presentation.SnapshotPublished += CapturePublication;
                try
                {
                    var send = SendAsync(); // Actual native composer/send path, including client preparation.
                    while (!shown.Task.IsCompleted)
                    {
                        token.ThrowIfCancellationRequested();
                        if (send.IsFaulted || send.IsCanceled) await send;
                        await Task.WhenAny(shown.Task, Task.Delay(100, token));
                    }
                    var rendered = await shown.Task;
                    await send.WaitAsync(token);
                    if (Volatile.Read(ref captureState) != 0) await captured.Task.WaitAsync(token);
                    var assistantId = Guid.Parse(S(rendered, "id"));
                    var assistant = await chats.GetMessageAsync(assistantId, token) ?? throw new InvalidOperationException("Rendered assistant is not durable.");
                    var local = await runs.GetByAssistantMessageIdAsync(assistantId, token) ?? throw new InvalidOperationException("Missing client run receipt.");
                    if (local.ServerRunId is not { Length: > 0 } runId) throw new InvalidOperationException("Missing accepted gateway run ID.");
                    using var client = await connection.CreateClientAsync(token);
                    var remote = await client.GetRunAsync(runId, token);
                    if (remote.State != RunState.Completed || assistant.Status != MessageStatus.Completed)
                        throw new InvalidOperationException("The rendered benchmark task did not durably complete.");
                    var children = SubagentChatState.Read([assistant], App.Current.DataDirectory);
                    if (children.Any(child => child.Status != "completed" || child.ResultDelivered != true))
                        throw new InvalidOperationException("A child task did not successfully deliver its result.");
                    return new(runId, local.SelectedModel ?? "", phase, assistantId, submitted, firstVisible, finished!.Value,
                        finishedMilliseconds, firstVisibleMilliseconds, assistant.Content, assistant.ToolSteps ?? [],
                        children.SelectMany(child => child.AssistantMessage.ToolSteps ?? []).ToArray());
                }
                finally { CompositionTarget.Rendering -= Observe; presentation.SnapshotPublished -= CapturePublication; }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failure = exception;
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await App.Current.GetService<MissumAiAssistantService>().CancelCurrentAsync(cleanup.Token); }
            catch (Exception cleanupError) when (cleanupError is not OutOfMemoryException) { }
        }
        var result = new NativeContextBenchmarkResult(failure is null, failure?.ToString(), _session, legs, warmups,
            firstPdf, firstPdfAt, firstPdfRevision, finalPdf, stateHash, publicationRevision, restored, simulationRequired,
            ResearchState: finalState);
        await File.WriteAllTextAsync(configuration.ResultPath + ".tmp", JsonSerializer.Serialize(result, JsonOptions));
        File.Move(configuration.ResultPath + ".tmp", configuration.ResultPath, overwrite: false);
    }
}
