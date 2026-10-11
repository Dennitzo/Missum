using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    // Opt-in runtime check on the real XAML dispatcher. Never enabled by build.ps1.
    private async Task VerifyResponsivenessSmokeAsync(JsonElement original)
    {
        if (!IsMessageFooterSmoke || Environment.GetEnvironmentVariable("MISSUM_SMOKE_UI_RESPONSIVENESS") is not ("1" or "focused")) return;
        var owner = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var messages = Enumerable.Range(0, 64).Select(index => new
        {
            id = Guid.NewGuid(), sessionId = owner, role = index % 8 == 0 ? "user" : "assistant",
            content = string.Join("\n\n", Enumerable.Range(0, 10).Select(paragraph =>
                $"Abschnitt {index}.{paragraph}: Die numerische Berechnung wird mit dem analytischen Kontrollfall verglichen. Die Genauigkeit muss unabhängig geprüft werden.")),
            status = index == 63 ? "streaming" : "completed",
            createdAt = now.AddMinutes(index - 64), updatedAt = now,
        }).ToArray();
        var snapshot = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
        snapshot["activeSessionId"] = JsonSerializer.SerializeToElement(owner);
        snapshot["chatMode"] = JsonSerializer.SerializeToElement("general");
        snapshot["isRunning"] = JsonSerializer.SerializeToElement(true);
        snapshot["activeRunSessionId"] = JsonSerializer.SerializeToElement(owner);
        snapshot["runMessageId"] = JsonSerializer.SerializeToElement(messages[^1].id);
        snapshot["messages"] = JsonSerializer.SerializeToElement(messages);
        var latencies = new List<double>();
        var ticks = _successfulUiRenderTicks;
        var faults = _uiCallbackFaultCount;
        try
        {
            ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(snapshot));
            RenderMessagesNow();
            await Task.Delay(150, _lifetime.Token);
            // Querying selection is a hot guard, not a request to reconstruct
            // clipboard text. Its cost must not grow with this long conversation.
            var selectionWatch = Stopwatch.StartNew();
            for (var query = 0; query < 10_000; query++)
                if (_conversationSelection?.IsSelecting == true) throw new InvalidOperationException("Unerwartete Auswahl im UI-Leistungstest.");
            selectionWatch.Stop();
            if (selectionWatch.ElapsedMilliseconds > 250)
                throw new InvalidOperationException("Die Auswahlprüfung durchsucht weiterhin den langen Chat.");
            var sourceControls = SourcesPanel.Children.ToArray();
            var sourceSignature = _sourcesSignature;
            // Post from another thread so probes also arrive DURING a busy
            // render callback, instead of measuring only its idle gaps.
            var inputProbes = Task.Run(async () =>
            {
                for (var probe = 0; probe < 64; probe++)
                {
                    var responsive = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var started = Stopwatch.GetTimestamp();
                    if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal,
                        () => responsive.TrySetResult(Stopwatch.GetElapsedTime(started).TotalMilliseconds)))
                        throw new InvalidOperationException("UI-Dispatcher nimmt keine Eingaben mehr an.");
                    latencies.Add(await responsive.Task.WaitAsync(TimeSpan.FromSeconds(3), _lifetime.Token));
                    await Task.Delay(43, _lifetime.Token);
                }
            }, _lifetime.Token);
            for (var fragment = 0; fragment < 32; fragment++)
            {
                var step = new { id = "reasoning-responsiveness", tool = "assistant.reasoning", status = "running",
                    outputJson = JsonSerializer.Serialize(new { text = "Kontrollrechnung " + fragment }), updatedAt = DateTimeOffset.UtcNow };
                ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new { sessionId = owner,
                    messageId = messages[^1].id, content = messages[^1].content + "\n\nFortschritt " + fragment, toolSteps = new[] { step } }));
                await Task.Delay(80, _lifetime.Token);
            }
            await inputProbes;
            RenderMessagesNow();
            if (_sourcesSignature != sourceSignature || !sourceControls.SequenceEqual(SourcesPanel.Children))
                throw new InvalidOperationException("Denkprozess-Deltas bauen unveränderte Quellen neu auf.");
            if (_uiCallbackFaultCount != faults || _successfulUiRenderTicks <= ticks || !_running)
                throw new InvalidOperationException("Render-Timer oder laufende Antwort wurden im Leistungstest unterbrochen.");
            latencies.Sort();
            var p95 = latencies[(int)Math.Ceiling(latencies.Count * .95) - 1];
            if (p95 > 250) throw new InvalidOperationException($"UI-Eingabeverzögerung im langen Streaming-Chat: {p95:F1} ms.");
            await SaveMathPreviewAsync(LayoutRoot, "native-responsiveness-preview.png");
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-responsiveness-validation.json"),
                JsonSerializer.Serialize(new { processId = Environment.ProcessId, messageCount = messages.Length,
                    paragraphCount = messages.Length * 10, selectionQueries = 10_000,
                    selectionQueryMilliseconds = selectionWatch.Elapsed.TotalMilliseconds,
                    dispatcherP95Milliseconds = p95, dispatcherMaxMilliseconds = latencies[^1],
                    timerTicks = _successfulUiRenderTicks - ticks, sourceControlsPreserved = true,
                    streamingRunPreserved = true, uiFaults = _uiCallbackFaultCount - faults }, JsonOptions));
        }
        finally
        {
            ApplyEvent("state.snapshot", original);
            RenderMessagesNow();
        }
    }
}
