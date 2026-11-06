using Missum.Ai.Contracts;
using Missum.Core.Models;
using System.Text.Json;

namespace Missum.App.Services;

public sealed partial class MissumAiAssistantService
{
    private sealed record ActiveSteeringSink(Guid SessionId, Guid MessageId, Func<AssistantToolStep, Task> Persist);

    internal static Guid SteeringPromptMessageId(Guid parentId, string stepId) =>
        Missum.Core.Chat.SteeringPromptIdentity.Create(parentId, stepId);

    internal Task SpeakSteeringPromptAsync(Guid sessionId, Guid parentMessageId, string stepId, string text,
        Func<MissumAiSpeechUpdate, Task> update, Func<SpeechPlaybackProgress, Task>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("Die Umlenkung benötigt eine Sitzung.", nameof(sessionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(update);
        var promptId = SteeringPromptMessageId(parentMessageId, stepId);
        CancelAutomaticSpeech();
        // The UI owns the accepted immutable prompt snapshot, which can precede
        // its database commit. Never interpret it as a document request or load
        // the containing assistant message as the speech source.
        return SpeakCoreAsync(sessionId, text, promptId,
            status => update(status with { SessionId = sessionId, SourceMessageId = promptId }), progress,
            null, null, new(text, "Nutzernachricht", null, promptId, MessageContentProfile.General),
            cancellationToken, resetPauseForDirectSource: true);
    }

    public async Task<RunSteeringAccepted> SteerAsync(Guid sessionId, string prompt, string inputId,
        string? expectedRunId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputId);
        if (prompt.Length > 100_000 || inputId.Length > 128) throw new ArgumentException("Die Umlenkung ist zu lang.");
        if (!IsRunning || ActiveSessionId != sessionId)
        {
            // A lost HTTP acknowledgement can be retried after completion. The
            // gateway only returns an existing idempotent receipt for a terminal
            // run; an unknown input is rejected, never turned into a new run.
            var previous = expectedRunId is null ? null
                : await runs.GetByServerRunIdAsync(expectedRunId, cancellationToken).ConfigureAwait(false);
            if (previous?.SessionId != sessionId || previous.State is not ("completed" or "failed" or "cancelled" or "interrupted"))
                throw new InvalidOperationException("In dieser Sitzung läuft kein Auftrag zum Umlenken.");
            return await SubmitAsync(expectedRunId!).ConfigureAwait(false);
        }
        var activeAttempt = _activeCancellation;
        // A prompt may arrive during local preparation, before POST /runs returns.
        // Await only that identity; never cancel, acquire the send gate or start a run.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (ActiveRunId is null && IsRunning && ActiveSessionId == sessionId && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        var runId = ActiveRunId;
        if (!IsRunning || ActiveSessionId != sessionId || string.IsNullOrWhiteSpace(runId)
            || !ReferenceEquals(activeAttempt, _activeCancellation))
            throw new InvalidOperationException("Der Auftrag ist noch nicht bereit oder bereits beendet. Die Eingabe bleibt erhalten.");
        if (expectedRunId is not null && !string.Equals(runId, expectedRunId, StringComparison.Ordinal))
            throw new InvalidOperationException("Der laufende Auftrag hat sich geändert. Die Eingabe wurde nicht an einen anderen Lauf gesendet.");
        return await SubmitAsync(runId).ConfigureAwait(false);

        async Task<RunSteeringAccepted> SubmitAsync(string targetRunId)
        {
            using var client = await connection.CreateClientAsync(cancellationToken).ConfigureAwait(false);
            var capabilities = await client.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            if (!capabilities.SupportsRunSteering)
                throw new InvalidOperationException("Der AI-Server unterstützt Umlenken noch nicht. Aktualisiere den Gateway.");
            var receipt = await client.SteerRunAsync(targetRunId, new(sessionId.ToString("D"), inputId, prompt.Trim()), cancellationToken).ConfigureAwait(false);
            await PersistAcceptedSteeringAsync(receipt, prompt.Trim()).ConfigureAwait(false);
            await chats.ClearDraftIfMatchesAsync(sessionId, prompt, CancellationToken.None).ConfigureAwait(false);
            return receipt;
        }
    }

    private async Task PersistAcceptedSteeringAsync(RunSteeringAccepted receipt, string prompt)
    {
        var localRun = await runs.GetByServerRunIdAsync(receipt.RunId, CancellationToken.None).ConfigureAwait(false);
        if (localRun is null) return;
        var message = await chats.GetMessageTextAsync(localRun.AssistantMessageId, CancellationToken.None).ConfigureAwait(false);
        if (message is null || message.SessionId != localRun.SessionId) return;
        var now = DateTimeOffset.UtcNow;
        var completed = receipt.State == "applied";
        var step = new AssistantToolStep(
            "steering-" + receipt.InputId,
            "assistant.steering",
            completed ? "completed" : "running",
            prompt,
            InputJson: JsonSerializer.Serialize(new
            {
                receipt.InputId,
                receipt.Sequence,
                Text = prompt,
            }, JsonOptions),
            OutputJson: JsonSerializer.Serialize(new { applied = completed }, JsonOptions),
            ContentOffset: message.Content.Length,
            StartedAt: now,
            CompletedAt: completed ? now : null,
            UpdatedAt: now);
        ActiveSteeringSink? sink;
        lock (_activeRunLock) sink = _activeSteeringSink;
        if (sink is not null && sink.SessionId == localRun.SessionId && sink.MessageId == message.Id)
        {
            try
            {
                await sink.Persist(step).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) { }
        }
        await chats.SaveToolStepDeltaAsync(message.Id, step, CancellationToken.None).ConfigureAwait(false);
    }

    // Steering lives inside the same durable run message so in-flight tool receipts
    // retain their owner. For model context it expands to ordinary chronological
    // assistant/user messages, exactly as the gateway's session checkpoint does.
    internal static IReadOnlyList<ChatMessage> ExpandSteeringHistory(IReadOnlyList<ChatMessage> source)
    {
        var expanded = new List<ChatMessage>();
        foreach (var message in source)
        {
            var inputs = (message.ToolSteps ?? []).Where(step => step.Tool == "assistant.steering" && !WasAutomaticSteering(step))
                .OrderBy(step => step.ContentOffset ?? message.Content.Length)
                .ThenBy(SteeringSequence).ToArray();
            if (message.Role != ChatRole.Assistant || inputs.Length == 0) { expanded.Add(message); continue; }
            var offset = 0;
            var ordinal = 0;
            foreach (var input in inputs)
            {
                var end = Math.Clamp(input.ContentOffset ?? message.Content.Length, offset, message.Content.Length);
                if (end > offset)
                    expanded.Add(Segment(message.Content[offset..end], ChatRole.Assistant, "before-" + input.Id));
                if (!string.IsNullOrWhiteSpace(input.Detail))
                    expanded.Add(Segment(input.Detail, ChatRole.User, input.Id));
                offset = end;
            }
            expanded.Add(message with { Content = message.Content[offset..],
                ToolSteps = message.ToolSteps?.Where(step => step.Tool != "assistant.steering").ToArray(),
                CreatedAt = message.CreatedAt.AddTicks(ordinal) });

            ChatMessage Segment(string text, ChatRole role, string suffix)
            {
                return message with { Id = SteeringPromptMessageId(message.Id, suffix), Content = text, Role = role,
                    Status = role == ChatRole.User ? MessageStatus.Completed : message.Status,
                    ToolSteps = [], ContextSummary = null, ToolExecution = null,
                    CreatedAt = message.CreatedAt.AddTicks(ordinal++) };
            }
        }
        return expanded;
    }

    private static bool WasAutomaticSteering(AssistantToolStep step)
    {
        if (step.InputJson is not { } input) return false;
        try
        {
            using var json = JsonDocument.Parse(input);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("agentProfile", out var profile)
                && profile.ValueKind == JsonValueKind.String;
        }
        catch (JsonException) { return false; }
    }

    private static long SteeringSequence(AssistantToolStep step)
    {
        try
        {
            using var json = JsonDocument.Parse(step.InputJson ?? "{}");
            return json.RootElement.TryGetProperty("sequence", out var sequence) ? sequence.GetInt64() : 0;
        }
        catch (JsonException) { return 0; }
    }
}
