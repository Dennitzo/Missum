using System.Globalization;
using System.Text;
using System.Text.Json;
using Missum.App.Services;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    // A live message is updated in place, independently of its durable JSON
    // snapshot. A token never serializes the preceding tools or transcript.
    private readonly Dictionary<string, NativeMessageProjection> _liveMessageProjections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _renderedMessageVersions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _toolStepWindows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _blockPagesLoading = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, JsonElement>> _messageAuxiliarySteps = new(StringComparer.Ordinal);

    private NativeMessageProjection ProjectionFor(JsonElement message)
    {
        var id = S(message, "id");
        ObserveMessageToolStepWindow(message);
        if (!_liveMessageProjections.TryGetValue(id, out var projection))
            _liveMessageProjections[id] = projection = new NativeMessageProjection(message,
                _toolStepWindows.TryGetValue(id, out var initialWindow) && initialWindow.TryGetProperty("beforeOrdinal", out var ordinal)
                && ordinal.ValueKind == JsonValueKind.Number ? ordinal.GetInt64() : 0);
        else if (!projection.Source.Equals(message))
        {
            var hasWindow = _toolStepWindows.TryGetValue(id, out var nextWindow);
            projection.Adopt(message, hasWindow && nextWindow.TryGetProperty("beforeOrdinal", out var nextOrdinal)
                && nextOrdinal.ValueKind == JsonValueKind.Number ? nextOrdinal.GetInt64()
                    : hasWindow && S(nextWindow, "hasMore") != "True" ? 0 : null,
                hasWindow && S(nextWindow, "hasMore") == "True");
        }
        if (_toolStepWindows.TryGetValue(id, out var currentWindow) && S(currentWindow, "hasMore") == "True"
            && currentWindow.TryGetProperty("beforeOrdinal", out var currentOrdinal) && currentOrdinal.ValueKind == JsonValueKind.Number
            && projection.FirstOrdinal < currentOrdinal.GetInt64())
            _toolStepWindows[id] = JsonSerializer.SerializeToElement(new { totalSteps = projection.TotalSteps,
                hasMore = projection.FirstOrdinal > 0, beforeOrdinal = projection.FirstOrdinal });
        return projection;
    }

    private string MessageContent(JsonElement message) => ProjectionFor(message).Content;
    private JsonElement[] MessageSteps(JsonElement message) => ProjectionFor(message).Steps;
    private string MessageUpdatedAt(JsonElement message) => ProjectionFor(message).UpdatedAt;
    private long MessageVersion(JsonElement message) => ProjectionFor(message).Version;

    private void ApplyNativeMessageDelta(JsonElement data)
    {
        var id = S(data, "messageId");
        if (!_messages.TryGetValue(id, out var message)) return;
        if (data.TryGetProperty("toolStep", out var auxiliary) && S(auxiliary, "tool") is "assistant.continuation" or "subagent")
            RememberAuxiliaryStep(id, auxiliary);
        var projection = ProjectionFor(message);
        if (!projection.Apply(data)) return;
        if (ConversationScroll.ScrollableHeight - ConversationScroll.VerticalOffset < 90
            && _conversationSelection?.IsSelecting != true && projection.TrimHead(128) is { } first)
            _toolStepWindows[id] = JsonSerializer.SerializeToElement(new { totalSteps = projection.TotalSteps, hasMore = true, beforeOrdinal = first });
        _renderedMessageSources.Remove(id);
        if (data.TryGetProperty("toolSteps", out _) || data.TryGetProperty("toolStep", out var changedStep) && IsSourceToolStep(changedStep))
            _sourceMessageSignatures.Remove(id);
        RenderMessages();
    }

    private void ObserveMessageToolStepWindow(JsonElement message)
    {
        var id = S(message, "id");
        if (id.Length == 0 || !message.TryGetProperty("toolStepWindow", out var window)
            || window.ValueKind != JsonValueKind.Object) return;
        // Read a snapshot's embedded window once. A cached JSON envelope is
        // intentionally not rewritten by live token/receipt deltas.
        if (_liveMessageProjections.TryGetValue(id, out var projection) && projection.Source.Equals(message)) return;
        _toolStepWindows[id] = window.Clone();
    }

    private void RememberAuxiliaryStep(string messageId, JsonElement step)
    {
        if (!_messageAuxiliarySteps.TryGetValue(messageId, out var steps))
            _messageAuxiliarySteps[messageId] = steps = new(StringComparer.Ordinal);
        steps[S(step, "id")] = step;
    }

    private IEnumerable<JsonElement> MessageTimingSteps(JsonElement message)
    {
        var id = S(message, "id");
        var stored = _messageAuxiliarySteps.TryGetValue(id, out var steps) ? steps.Values : Enumerable.Empty<JsonElement>();
        return stored.Concat(MessageSteps(message)).Where(step => S(step, "tool") == "assistant.continuation")
            .GroupBy(step => S(step, "id"), StringComparer.Ordinal).Select(group => group.Last());
    }

    private void ObserveAuxiliaryMessages(JsonElement data)
    {
        foreach (var message in Items(data, "auxiliaryMessages"))
            foreach (var step in Items(message, "toolSteps"))
                if (S(step, "tool") is "assistant.continuation" or "subagent") RememberAuxiliaryStep(S(message, "id"), step);
    }

    private void ObserveToolStepWindows(JsonElement data)
    {
        ObserveAuxiliaryMessages(data);
        if (!data.TryGetProperty("toolStepWindows", out var windows) || windows.ValueKind != JsonValueKind.Object) return;
        foreach (var window in windows.EnumerateObject()) _toolStepWindows[window.Name] = window.Value.Clone();
    }

    private async Task LoadEarlierToolBlocksAsync(string messageId)
    {
        if (_disposed || !_toolStepWindows.TryGetValue(messageId, out var window)
            || S(window, "hasMore") != "True" || !window.TryGetProperty("beforeOrdinal", out var before)
            || before.ValueKind != JsonValueKind.Number || !_blockPagesLoading.Add(messageId)) return;
        try
        {
            var child = _subagents.Values.FirstOrDefault(item => item.SessionId == _session && item.Messages.ContainsKey(messageId));
            if (child is null)
                await CommandAsync("conversation.blocks.page", new { sessionId = _session, messageId,
                    beforeOrdinal = before.GetInt64(), pageSize = 64 });
            else
                await CommandAsync("subagent.blocks.page", new { sessionId = _session, messageId, agentId = child.AgentId,
                    parentMessageId = S(child.Snapshot, "parentMessageId"), beforeOrdinal = before.GetInt64(), pageSize = 64 });
        }
        finally { _blockPagesLoading.Remove(messageId); }
    }

    internal sealed class NativeMessageProjection
    {
        private readonly StringBuilder _content = new();
        private string? _contentValue;
        private readonly List<JsonElement> _steps = [];
        private readonly Dictionary<string, int> _stepIndices = new(StringComparer.Ordinal);
        private JsonElement[]? _stepValues;
        private readonly Dictionary<string, long> _ordinals = new(StringComparer.Ordinal);
        private long _firstOrdinal;
        internal long TotalSteps { get; private set; }
        internal long FirstOrdinal => _firstOrdinal;
        private long _eventRevision;
        private string? _streamGeneration;
        internal JsonElement Source { get; private set; }
        internal long Version { get; private set; }
        internal string UpdatedAt { get; private set; } = "";
        internal string Content => _contentValue ??= _content.ToString();
        internal JsonElement[] Steps => _stepValues ??= _steps.ToArray();

        internal NativeMessageProjection(JsonElement source, long firstOrdinal) { _firstOrdinal = firstOrdinal; Adopt(source); }

        internal void Adopt(JsonElement source, long? firstOrdinal = null, bool preserveEarlier = false)
        {
            var at = S(source, "updatedAt");
            var retainLive = NativeMessageSnapshotPolicy.Compare(Source, source, UpdatedAt).PreservePayload;
            var lifecycleChanged = S(Source, "status") != S(source, "status") || S(Source, "error") != S(source, "error");
            Source = source;
            if (retainLive)
            {
                // Terminal chrome (including Fortsetzen) is part of the rendered
                // revision even when a later token's content stays untouched.
                if (lifecycleChanged) Version++;
                return;
            }
            var preceding = preserveEarlier && firstOrdinal.HasValue
                ? _steps.Where(step => _ordinals.GetValueOrDefault(S(step, "id"), long.MaxValue) < firstOrdinal.Value).ToArray()
                : [];
            var previousFirst = _firstOrdinal;
            var previousSteps = _steps.ToDictionary(step => S(step, "id"), StringComparer.Ordinal);
            if (firstOrdinal.HasValue) _firstOrdinal = firstOrdinal.Value;
            _content.Clear().Append(S(source, "content"));
            _contentValue = null;
            _steps.Clear(); _stepIndices.Clear(); _stepValues = null;
            _ordinals.Clear();
            foreach (var step in preceding) PutStep(step, previousFirst + _steps.Count);
            var incomingFirst = _firstOrdinal;
            var incomingIndex = 0;
            foreach (var step in Items(source, "toolSteps"))
                PutStep(RetainHydratedStep(step, previousSteps.GetValueOrDefault(S(step, "id"))), incomingFirst + incomingIndex++);
            if (preceding.Length > 0) _firstOrdinal = previousFirst;
            TotalSteps = incomingFirst + incomingIndex;
            UpdatedAt = at;
            Version++;
        }

        private static JsonElement RetainHydratedStep(JsonElement incoming, JsonElement previous)
        {
            if (previous.ValueKind != JsonValueKind.Object || S(incoming, "detailsDeferred") != "True") return incoming;
            // The compact header is not a deletion of an expanded receipt. Its
            // own timestamp, status and metadata identify the precise detail
            // version; a newer receipt must still fetch/render its new result.
            if (S(incoming, "updatedAt").Length == 0 || S(incoming, "updatedAt") != S(previous, "updatedAt")) return incoming;
            foreach (var property in incoming.EnumerateObject())
            {
                if (property.Name is "detail" or "inputJson" or "outputJson" or "previewHtml" or "explanation" or "detailsDeferred") continue;
                if (!previous.TryGetProperty(property.Name, out var value) || !JsonElement.DeepEquals(property.Value, value)) return incoming;
            }
            return previous;
        }

        internal void BeginGeneration(string generation)
        {
            if (string.IsNullOrEmpty(generation) || generation == _streamGeneration) return;
            _streamGeneration = generation;
            _eventRevision = 0;
        }

        internal bool Apply(JsonElement delta)
        {
            if (delta.TryGetProperty("streamGeneration", out var generation) && generation.ValueKind == JsonValueKind.String)
            {
                var incomingGeneration = generation.GetString();
                if (_streamGeneration is null) BeginGeneration(incomingGeneration ?? "");
                else if (incomingGeneration != _streamGeneration) return false;
            }
            long revision = 0;
            var hasRevision = delta.TryGetProperty("revision", out var value) && value.TryGetInt64(out revision);
            if (hasRevision && revision > 0 && revision <= _eventRevision) return false;
            if (hasRevision && revision > 0) _eventRevision = revision;
            if (delta.TryGetProperty("contentReplace", out var replace) && replace.ValueKind == JsonValueKind.String)
            { _content.Clear().Append(replace.GetString()); _contentValue = null; }
            else if (delta.TryGetProperty("contentAppend", out var append) && append.ValueKind == JsonValueKind.String)
            { _content.Append(append.GetString()); _contentValue = null; }
            else if (delta.TryGetProperty("content", out var legacy) && legacy.ValueKind == JsonValueKind.String)
            { _content.Clear().Append(legacy.GetString()); _contentValue = null; }
            if (delta.TryGetProperty("toolSteps", out var legacySteps) && legacySteps.ValueKind == JsonValueKind.Array)
            {
                _steps.Clear(); _stepIndices.Clear();
                foreach (var step in legacySteps.EnumerateArray()) PutStep(step);
                _stepValues = null;
            }
            if (delta.TryGetProperty("toolStep", out var changed) && changed.ValueKind == JsonValueKind.Object)
                PutStep(changed, delta.TryGetProperty("toolStepOrdinal", out var ordinal) && ordinal.ValueKind == JsonValueKind.Number
                    ? ordinal.GetInt64() : null);
            if (delta.TryGetProperty("toolStepCount", out var count) && count.ValueKind == JsonValueKind.Number)
                TotalSteps = Math.Max(TotalSteps, count.GetInt64());
            if (delta.TryGetProperty("updatedAt", out var updated)) UpdatedAt = updated.GetString() ?? UpdatedAt;
            Version++;
            return true;
        }

        internal void PutStep(JsonElement step, long? ordinal = null)
        {
            var id = S(step, "id");
            if (_stepIndices.TryGetValue(id, out var index)) _steps[index] = step;
            else { _stepIndices[id] = _steps.Count; _steps.Add(step); }
            if (ordinal.HasValue) _ordinals[id] = ordinal.Value;
            else if (!_ordinals.ContainsKey(id)) _ordinals[id] = Math.Max(_firstOrdinal + _steps.Count - 1, TotalSteps);
            TotalSteps = Math.Max(TotalSteps, _ordinals[id] + 1);
            _stepValues = null;
            Version++;
        }

        internal long? TrimHead(int keep)
        {
            if (_steps.Count <= keep * 2) return null;
            var removed = _steps.Take(_steps.Count - keep).Select(step => S(step, "id")).ToArray();
            _steps.RemoveRange(0, removed.Length);
            foreach (var id in removed) _ordinals.Remove(id);
            _stepIndices.Clear();
            for (var index = 0; index < _steps.Count; index++) _stepIndices[S(_steps[index], "id")] = index;
            _firstOrdinal = _ordinals.GetValueOrDefault(S(_steps[0], "id"));
            _stepValues = null;
            Version++;
            return _firstOrdinal;
        }

        internal void Prepend(IEnumerable<JsonElement> earlier)
        {
            var newSteps = earlier.Where(step => !_stepIndices.ContainsKey(S(step, "id"))).ToArray();
            if (newSteps.Length == 0) return;
            _steps.InsertRange(0, newSteps);
            _firstOrdinal = Math.Max(0, _firstOrdinal - newSteps.Length);
            for (var index = 0; index < newSteps.Length; index++) _ordinals[S(newSteps[index], "id")] = _firstOrdinal + index;
            _stepIndices.Clear();
            for (var index = 0; index < _steps.Count; index++) _stepIndices[S(_steps[index], "id")] = index;
            _stepValues = null;
            Version++;
        }
    }
}
