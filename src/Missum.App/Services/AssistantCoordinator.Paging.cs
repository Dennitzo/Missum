using System.Text.Json;
using Missum.Core.Models;

namespace Missum.App.Services;

public sealed partial class AssistantCoordinator
{
    private async Task<bool> HandleDisplayReadAsync(WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit, CancellationToken token)
    {
        if (await HandleSourceReadAsync(envelope, emit, token).ConfigureAwait(false)) return true;
        if (envelope.Type == "conversation.prompts")
        {
            var sessionId = GetRequiredGuid(envelope.Payload, "sessionId");
            var prompts = await Task.Run(() => conversationSnapshots.GetPromptsAsync(sessionId, token), token).ConfigureAwait(false);
            await emit("conversation.prompts", new { sessionId, prompts }, envelope.RequestId).ConfigureAwait(false);
            return true;
        }
        if (envelope.Type is "conversation.page" or "conversation.promptPage")
        {
            var sessionId = GetRequiredGuid(envelope.Payload, "sessionId");
            var before = envelope.Payload.TryGetProperty("before", out var cursor) && cursor.ValueKind == JsonValueKind.Object
                ? cursor.Deserialize<ConversationCursor>(JsonSerializerOptions.Web) : null;
            var targeted = envelope.Type == "conversation.promptPage";
            var promptId = targeted ? GetRequiredGuid(envelope.Payload, "promptId") : Guid.Empty;
            var navigationIntent = envelope.Payload.TryGetProperty("navigationIntent", out var intent) && intent.TryGetInt64(out var parsedIntent) ? parsedIntent : 0;
            var timelineGap = envelope.Payload.TryGetProperty("timelineGap", out var gap) && gap.ValueKind == JsonValueKind.String ? gap.GetString() : null;
            var page = await Task.Run(() => targeted ? conversationSnapshots.GetPromptPageAsync(sessionId, promptId, token)
                    : conversationSnapshots.GetPageAsync(sessionId, before: before, cancellationToken: token), token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Die Sitzung ist nicht mehr vorhanden.");
            var auxiliary = await PrepareDisplayedPageAsync(page, token).ConfigureAwait(false);
            await emit(envelope.Type, new
            {
                sessionId, promptId, navigationIntent, timelineGap, page.HasMore, page.Before, page.TotalMessages, toolStepWindows = page.ToolStepWindows,
                messages = page.Conversation.Messages.Select(message => ToMessageDto(message,
                    page.Conversation.Artifacts.GetValueOrDefault(message.Id), page.ToolStepWindows?.GetValueOrDefault(message.Id))),
                auxiliaryMessages = auxiliary.Select(message => new { id = message.Id, toolSteps = message.ToolSteps ?? [] }),
                subagents = ToSubagentDtos(ReadActivitySummaries(auxiliary)),
            }, envelope.RequestId).ConfigureAwait(false);
            return true;
        }
        if (envelope.Type == "conversation.blocks.page")
        {
            var sessionId = GetRequiredGuid(envelope.Payload, "sessionId");
            var messageId = GetRequiredGuid(envelope.Payload, "messageId");
            long? before = envelope.Payload.TryGetProperty("beforeOrdinal", out var ordinal) && ordinal.TryGetInt64(out var value) ? value : null;
            var page = await conversationSnapshots.GetToolStepsPageAsync(sessionId, messageId, 64, before, token).ConfigureAwait(false);
            if (page is null) throw new InvalidOperationException("Die Werkzeugnachricht ist nicht mehr vorhanden.");
            await emit("conversation.blocks.page", new { sessionId, messageId, page.Steps, page.Window }, envelope.RequestId).ConfigureAwait(false);
            return true;
        }
        if (envelope.Type == "subagent.blocks.page")
        {
            var sessionId = GetRequiredGuid(envelope.Payload, "sessionId");
            var parentId = GetRequiredGuid(envelope.Payload, "parentMessageId");
            var messageId = GetRequiredGuid(envelope.Payload, "messageId");
            var agentId = GetRequiredString(envelope.Payload, "agentId", 128);
            long? before = envelope.Payload.TryGetProperty("beforeOrdinal", out var ordinal) && ordinal.TryGetInt64(out var value) ? value : null;
            var parent = await ReadSubagentParentAsync(sessionId, parentId, agentId, token).ConfigureAwait(false);
            if (parent is null) throw new InvalidOperationException("Der Subagent gehört nicht zur Sitzung.");
            var child = await SubagentChatState.ReadAsync(parent, agentId, settings.DataDirectory, token).ConfigureAwait(false);
            child ??= await ReadHistoricalDeepResearchAsync(sessionId, parentId, agentId, token).ConfigureAwait(false);
            var page = child?.GetToolStepPage(messageId, beforeOrdinal: before)
                ?? throw new InvalidOperationException("Diese Subagenten-Nachricht ist nicht mehr vorhanden.");
            await emit("subagent.blocks.page", new { sessionId, parentMessageId = parentId, agentId, messageId, page.Steps, page.Window }, envelope.RequestId).ConfigureAwait(false);
            return true;
        }
        if (envelope.Type == "tool.details")
        {
            var sessionId = GetRequiredGuid(envelope.Payload, "sessionId");
            var messageId = GetRequiredGuid(envelope.Payload, "messageId");
            var stepId = GetRequiredString(envelope.Payload, "stepId", 200);
            AssistantToolStep? step;
            if (GetOptionalGuid(envelope.Payload, "parentMessageId") is { } parentId)
            {
                var parent = await ReadSubagentParentAsync(sessionId, parentId, GetRequiredString(envelope.Payload, "agentId", 128), token).ConfigureAwait(false);
                if (parent?.SessionId != sessionId) throw new InvalidOperationException("Der Subagent gehört nicht zur Sitzung.");
                var agentId = GetRequiredString(envelope.Payload, "agentId", 128);
                var child = await SubagentChatState.ReadAsync(parent, agentId, settings.DataDirectory, token).ConfigureAwait(false)
                    ?? await ReadHistoricalDeepResearchAsync(sessionId, parentId, agentId, token).ConfigureAwait(false);
                step = child?.FindToolStep(messageId, stepId);
            }
            else step = FindLiveToolStep(sessionId, messageId, stepId)
                ?? await Task.Run(() => conversationSnapshots.GetToolStepAsync(sessionId, messageId, stepId, token), token).ConfigureAwait(false);
            if (step is null) throw new InvalidOperationException("Dieser Werkzeugschritt ist nicht mehr vorhanden.");
            step = await EnrichLeanDisplayAsync(step, sessionId, token).ConfigureAwait(false);
            // Match compact snapshots so their next unchanged update can retain
            // the already hydrated body instead of fetching it again.
            step = step with { DisplayHeader = AssistantToolStepHeaders.Create(step) };
            await emit("tool.details", new { sessionId, messageId, toolStep = step }, envelope.RequestId).ConfigureAwait(false);
            return true;
        }
        if (envelope.Type == "subagent.load")
        {
            var sessionId = GetRequiredGuid(envelope.Payload, "sessionId");
            var parentMessageId = GetRequiredGuid(envelope.Payload, "parentMessageId");
            var agentId = GetRequiredString(envelope.Payload, "agentId", 128);
            var parent = await ReadSubagentParentAsync(sessionId, parentMessageId, agentId, token).ConfigureAwait(false);
            if (parent?.SessionId != sessionId) throw new InvalidOperationException("Der Subagent gehört nicht zur Sitzung.");
            var child = await SubagentChatState.ReadAsync(parent, agentId, settings.DataDirectory, token).ConfigureAwait(false)
                ?? await ReadHistoricalDeepResearchAsync(sessionId, parentMessageId, agentId, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Der Subagentenverlauf ist nicht mehr vorhanden.");
            await emit("subagent.snapshot", ToSubagentDto(child.ForDisplay()), envelope.RequestId).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    private async Task<ChatMessage?> ReadSubagentParentAsync(Guid sessionId, Guid messageId, string agentId, CancellationToken token)
    {
        var parent = await chats.GetMessageTextAsync(messageId, token).ConfigureAwait(false);
        if (parent?.SessionId != sessionId) return null;
        var receipt = FindLiveToolStep(sessionId, messageId, agentId, subagent: true)
            ?? await conversationSnapshots.GetSubagentReceiptAsync(sessionId, messageId, agentId, token).ConfigureAwait(false);
        if (receipt is not null) return parent with { ToolSteps = [receipt] };
        // Old research steps have no companion receipt. Keep only their thin
        // headers here; full details are read by the explicit historical loader.
        if (agentId.StartsWith("deep-research:", StringComparison.Ordinal))
            return (await conversationSnapshots.GetAuxiliaryMessagesAsync(sessionId, token).ConfigureAwait(false))
                .FirstOrDefault(message => message.Id == messageId) ?? parent;
        return parent with { ToolSteps = [] };
    }
    private async Task<AssistantToolStep> EnrichLeanDisplayAsync(AssistantToolStep step, Guid sessionId, CancellationToken token)
    {
        if (step.Tool != "math.formalProof") return step;
        try
        {
            var output = System.Text.Json.Nodes.JsonNode.Parse(step.OutputJson ?? "{}") as System.Text.Json.Nodes.JsonObject;
            if (output?["lean"] is not System.Text.Json.Nodes.JsonObject lean) return step;
            var projectId = output["projectId"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(projectId)) return step;
            if (projectId != "research-" + sessionId.ToString("N")) return step;
            var session = await chats.GetSessionAsync(sessionId, token).ConfigureAwait(false);
            var workspace = session?.CodingWorkspacePath;
            if (string.IsNullOrWhiteSpace(workspace) && session?.SessionGroupId is { } groupId)
                workspace = (await chats.ListSessionGroupsAsync(session.ChatMode, token).ConfigureAwait(false))
                    .FirstOrDefault(group => group.Id == groupId)?.WorkspacePath;
            if (string.IsNullOrWhiteSpace(workspace)) return step;
            var root = Path.GetFullPath(Path.Combine(workspace, "work"));
            if (!Directory.Exists(root)) root = Path.GetFullPath(Path.Combine(workspace, "Science", projectId, "work"));
            if (!Directory.Exists(root)) root = Path.GetFullPath(Path.Combine(workspace, ".assistant", "research", projectId));
            var source = await Task.Run(async () =>
            {
                async Task<string?> ReadBounded(string? relative)
                {
                    if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return null;
                    var path = Path.GetFullPath(Path.Combine(root, relative));
                    if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
                    if (!File.Exists(path) || new FileInfo(path).Length > 2_097_152) return null;
                    for (var part = new FileInfo(path) as FileSystemInfo; part is not null; part = part is FileInfo file ? file.Directory : ((DirectoryInfo)part).Parent)
                    {
                        if ((part.Attributes & FileAttributes.ReparsePoint) != 0) return null;
                        if (string.Equals(part.FullName, root, StringComparison.OrdinalIgnoreCase)) break;
                    }
                    return await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
                }
                string? text = null;
                var receipt = await ReadBounded(lean["receipt"]?.GetValue<string>()).ConfigureAwait(false);
                if (receipt is not null)
                    text = System.Text.Json.Nodes.JsonNode.Parse(receipt)?["sourceText"]?.GetValue<string>();
                text ??= await ReadBounded(lean["source"]?.GetValue<string>()).ConfigureAwait(false);
                if (text is null) return null;
                var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
                return string.Equals(hash, lean["sourceSha256"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase) ? text : null;
            }, token).ConfigureAwait(false);
            if (source is not null) lean["sourceText"] = source;
            // Display projection only: never persist the augmented receipt or send it to the model.
            return step with { OutputJson = output.ToJsonString() };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        { return step; }
    }

}
