using System.Text.Json;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    // SQLite -> compact snapshot -> native dispatcher -> both message renderers.
    // This path only writes to the disposable profile used by the portable smoke.
    private async Task VerifySteeringPromptSmokeAsync(JsonElement original)
    {
        var directory = Path.GetFullPath(App.Current.DataDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Directory.GetParent(directory);
        if (parent is null || !Guid.TryParseExact(parent.Name, "N", out _)
            || !string.Equals(parent.Parent?.FullName, Path.Combine(Path.GetTempPath(), "Missum-Smoke").TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(directory), "Data", StringComparison.OrdinalIgnoreCase)
            || Environment.GetEnvironmentVariable("ASSISTANT_DISABLE_RUNTIME_AUTOSTART") != "1")
            throw new InvalidOperationException("Steering smoke requires an isolated disposable profile.");
        var chats = App.Current.GetService<IChatRepository>();
        var previousFixture = _selectionInputSmokeActive;
        _selectionInputSmokeActive = true;
        try
        {
            foreach (var large in new[] { false, true })
            {
                var session = await chats.CreateSessionAsync("Umlenkung " + large, cancellationToken: _lifetime.Token);
                var content = large ? string.Concat(Enumerable.Repeat("Ein früherer unveränderter Absatz.\n\n", 270)) : "Bisherige Antwort.\n\n";
                var assistant = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, content, MessageStatus.Streaming, cancellationToken: _lifetime.Token);
                if (!await CommandAsync("session.open", new { sessionId = session.Id }))
                    throw new InvalidOperationException("Steering fixture did not open.");
                ShowChatView();
                RenderMessagesNow();
                if (SubagentsSection.Visibility != Visibility.Collapsed)
                    throw new InvalidOperationException("An empty subagent summary remained visible.");
                var inputId = Guid.NewGuid().ToString("N");
                var prompt = "Prüfe den alternativen Ansatz.\n\nErhalte alle mathematischen Ausdrücke.";
                var pending = AddPendingUserMessage(session.Id, prompt, inputId);
                // Simulate an acknowledgement racing ahead of its payload.
                ApplyEvent("chat.delta", JsonSerializer.SerializeToElement(new
                {
                    messageId = assistant.Id, sessionId = session.Id, revision = 1,
                    toolStep = new { id = "steering-" + inputId, tool = "assistant.steering", status = "running", detailsDeferred = true, contentOffset = content.Length },
                    toolStepOrdinal = 0, toolStepCount = 1,
                }));
                RenderMessagesNow();
                if (!_pendingUserMessages.ContainsKey(pending) || !_messageViews.ContainsKey(pending))
                    throw new InvalidOperationException("A textless acknowledgement removed the pending prompt.");

                await chats.SaveToolStepDeltaAsync(assistant.Id, new("steering-" + inputId, "assistant.steering", "completed", prompt,
                    ContentOffset: content.Length, UpdatedAt: DateTimeOffset.UtcNow), _lifetime.Token);
                await chats.UpdateMessageAsync(assistant.Id, content + "Antwort auf die neue Anweisung.", MessageStatus.Streaming, cancellationToken: _lifetime.Token);
                if (!await CommandAsync("conversation.refresh", new { sessionId = session.Id }))
                    throw new InvalidOperationException("Stored steering could not be refreshed.");
                RenderMessagesNow();
                await WaitForSteeringBubbleAsync(assistant.Id.ToString(), "steering-" + inputId, prompt);
                if (_pendingUserMessages.ContainsKey(pending) || _messages.ContainsKey(pending))
                    throw new InvalidOperationException("Stored steering did not replace its pending prompt exactly once.");
                if (_virtualMessages.ContainsKey(assistant.Id.ToString()) != large)
                    throw new InvalidOperationException("Steering smoke did not exercise both message renderers.");
                await CommandAsync("session.open", new { sessionId = Guid.Parse(S(original, "activeSessionId")) });
                await CommandAsync("session.open", new { sessionId = session.Id });
                RenderMessagesNow();
                await WaitForSteeringBubbleAsync(assistant.Id.ToString(), "steering-" + inputId, prompt);
                await SaveMathPreviewAsync(LayoutRoot, large ? "native-steering-virtual.png" : "native-steering-short.png");
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "native-steering-prompt-validation.json"), JsonSerializer.Serialize(new
            {
                processId = Environment.ProcessId, pendingRetainedUntilText = true, compactDatabaseReload = true,
                shortMessageBubble = true, virtualMessageBubble = true, sessionReload = true, emptySubagentsHidden = true,
            }), _lifetime.Token);
        }
        finally
        {
            ApplyEvent("state.snapshot", original);
            RenderMessagesNow();
            _selectionInputSmokeActive = previousFixture;
        }
    }

    private async Task WaitForSteeringBubbleAsync(string messageId, string stepId, string prompt)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            RenderMessagesNow();
            OnScrollToBottom(this, new RoutedEventArgs());
            if (_messageBlocks.TryGetValue(messageId, out var blocks)
                && blocks.TryGetValue("steering:" + stepId, out var view)
                && view is Border { Child: TextBlock text } && text.Text == prompt)
                return;
            await Task.Delay(50, _lifetime.Token);
        }
        await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-steering-failure-state.json"), JsonSerializer.Serialize(new
        {
            sessionId = _session, messageId, expectedStepId = stepId,
            messages = _messages.Values.Select(message => new { id = S(message, "id"), contentLength = MessageContent(message).Length,
                version = MessageVersion(message), steps = MessageSteps(message).Select(step => new { id = S(step, "id"), tool = S(step, "tool"), detailLength = S(step, "detail").Length }) }),
            virtualMessages = _virtualMessages.Select(pair => new { id = pair.Key, pair.Value.Version, pair.Value.Preparing,
                blocks = pair.Value.Blocks.Select(block => block.Key), pair.Value.Panel.RealizedCount }),
        }), _lifetime.Token);
        throw new InvalidOperationException("The stored steering prompt was not rendered as a user bubble.");
    }
}
