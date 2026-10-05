using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using Missum.Ai.Server.Core.Configuration;
using Missum.Ai.Server.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Missum.Ai.Server.Tests;

public sealed class DeepSeekReasoningLiveTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions EvidenceJson = new() { WriteIndented = true };
    [Theory]
    [InlineData("on")]
    [InlineData("none")]
    [Trait("Category", "Live")]
    public async Task NativeDeepSeekStreamsReasoningOnlyWhenEnabled(string effort)
    {
        if (Environment.GetEnvironmentVariable("MISSUM_AI_DEEPSEEK_REASONING_LIVE") != "1") return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var runtime = new ModelRuntimeClient(new HttpClient(), Options.Create(new MissumAiServerOptions
        {
            ModelRuntimeUri = new Uri(Environment.GetEnvironmentVariable("MISSUM_AI_NATIVE_RUNTIME_URL") ?? "http://127.0.0.1:8081"),
        }), NullLogger<ModelRuntimeClient>.Instance);
        var status = await runtime.GetStatusAsync(timeout.Token);
        var model = ModelRuntimeClient.ResolveModelStatus(status.Models,
            Environment.GetEnvironmentVariable("MISSUM_AI_DEEPSEEK_REASONING_MODEL")
                ?? "coding/DeepSeek-V4-Flash-Vision-Exp-UD-IQ1_S~31b467a216d0", "general");
        Assert.NotNull(model);
        Assert.Contains(effort, model.ReasoningEfforts!);
        var progress = new ConcurrentQueue<ModelRuntimeProgress>();
        var clock = Stopwatch.StartNew();
        var answer = await runtime.CompleteChatAsync(model.Id,
            [new("system", "Antworten auf Deutsch. Die finale Antwort ist ausschließlich die Ergebniszahl."),
                new("user", "Berechne 17 + 25.")], [], maximumOutputTokens: 512,
            modelRole: "general", reasoningEffort: effort,
            nativeProgress: (item, _) => { progress.Enqueue(item); return ValueTask.CompletedTask; },
            cancellationToken: timeout.Token);
        var streamedReasoning = progress.Where(item => !string.IsNullOrEmpty(item.ReasoningDelta))
            .Sum(item => item.ReasoningDelta!.Length);
        var streamedAnswer = progress.Where(item => !string.IsNullOrEmpty(item.ContentDelta))
            .Sum(item => item.ContentDelta!.Length);
        var enabled = effort == "on";
        var evidence = new
        {
            model = model.Id, effort, elapsedSeconds = clock.Elapsed.TotalSeconds,
            answerCharacters = answer.Content?.Length ?? 0,
            reasoningCharacters = answer.ReasoningContent?.Length ?? 0,
            streamedReasoningCharacters = streamedReasoning, streamedAnswerCharacters = streamedAnswer,
            reasoningFragments = progress.Count(item => !string.IsNullOrEmpty(item.ReasoningDelta)),
            phases = progress.Select(item => item.State).Distinct().ToArray(),
            answer.Metrics,
            containsCorrectAnswer = answer.Content?.Contains("42", StringComparison.Ordinal) == true,
        };
        var json = JsonSerializer.Serialize(evidence, EvidenceJson);
        output.WriteLine(json);
        if (Environment.GetEnvironmentVariable("MISSUM_AI_REASONING_EVIDENCE") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "deepseek-live-" + effort + ".json"), json, timeout.Token);
        }
        Assert.Contains("42", answer.Content);
        Assert.True(streamedAnswer > 0, "Actual answer text must arrive through the streaming callback.");
        if (enabled)
        {
            Assert.False(string.IsNullOrWhiteSpace(answer.ReasoningContent));
            Assert.True(streamedReasoning > 0, "Enabled reasoning must arrive through the actual streaming callback.");
        }
        else
        {
            Assert.True(string.IsNullOrWhiteSpace(answer.ReasoningContent));
            Assert.Equal(0, streamedReasoning);
        }
    }
}
