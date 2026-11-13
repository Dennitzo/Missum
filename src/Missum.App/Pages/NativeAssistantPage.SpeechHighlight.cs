using System.Text.Json;
using Missum.App.Controls;
using Missum.App.Services;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private sealed record SpokenSentence(int Start, int Length, string? Source);
    private sealed record MessageSpeechHighlight(Guid SessionId, Guid MessageId, Guid ControlPlaybackId, string? StepId, SpokenSentence Sentence);

    private MessageSpeechHighlight? _messageSpeechHighlight;
    private (Guid PlaybackId, long Sequence)? _messageSpeechProgressCursor;

    private void UpdateMessageSpeechHighlight(SpeechPlaybackProgress progress)
    {
        var unit = progress.State == SpeechPlaybackState.Playing && progress.SourceUnits is { Count: 1 } units ? units[0] : null;
        var sentence = unit?.SentenceStart is { } start && unit.SentenceLength is { } length
            ? new SpokenSentence(start, length, unit.SentenceSource) : null;
        UpdateMessageSpeechHighlight(progress.SessionId, progress.SourceMessageId,
            progress.SourceStepId, progress.ControlPlaybackId ?? progress.PlaybackId, progress.PlaybackId, progress.EventSequence,
            progress.State == SpeechPlaybackState.Playing, sentence);
    }

    private void UpdateMessageSpeechHighlight(JsonElement data, Guid? controlPlaybackId)
    {
        if (controlPlaybackId is not { } control || MessageSpeechGuid(data, "playbackId") is not { } playback
            || MessageSpeechGuid(data, "sessionId") is not { } session) return;
        var playing = S(data, "state") == "playing";
        SpokenSentence? sentence = null;
        if (playing && data.TryGetProperty("sourceUnits", out var units)
            && units.ValueKind == JsonValueKind.Array && units.GetArrayLength() == 1)
        {
            var unit = units[0];
            if (unit.ValueKind == JsonValueKind.Object
                && unit.TryGetProperty("sentenceStart", out var start) && start.ValueKind == JsonValueKind.Number && start.TryGetInt32(out var offset)
                && unit.TryGetProperty("sentenceLength", out var length) && length.ValueKind == JsonValueKind.Number && length.TryGetInt32(out var count))
                sentence = new(offset, count, S(unit, "sentenceSource") is { Length: > 0 } source ? source : null);
        }
        long? sequence = data.TryGetProperty("eventSequence", out var sequenceValue)
            && sequenceValue.ValueKind == JsonValueKind.Number && sequenceValue.TryGetInt64(out var value) ? value : null;
        var step = S(data, "sourceStepId");
        UpdateMessageSpeechHighlight(session, MessageSpeechGuid(data, "sourceMessageId"),
            step.Length == 0 ? null : step, control, playback, sequence, playing, sentence);
    }

    private void UpdateMessageSpeechHighlight(Guid sessionId, Guid? messageId, string? stepId, Guid controlPlaybackId,
        Guid playbackId, long? sequence, bool playing, SpokenSentence? sentence)
    {
        if (sequence is { } next)
        {
            if (_messageSpeechProgressCursor is { } current && current.PlaybackId == playbackId && next <= current.Sequence)
                return;
            _messageSpeechProgressCursor = (playbackId, next);
        }
        if (playing && !MessageFooterMicrophone.IsSpeechPaused && messageId is { } source
            && sentence is { Start: >= 0, Length: > 0 } && sentence.Length <= int.MaxValue - sentence.Start)
            SetMessageSpeechHighlight(new(sessionId, source, controlPlaybackId, stepId, sentence));
        else if (_messageSpeechHighlight?.ControlPlaybackId == controlPlaybackId)
            SetMessageSpeechHighlight(null);
    }

    private void SetMessageSpeechHighlight(MessageSpeechHighlight? highlight)
    {
        if (_messageSpeechHighlight == highlight) return;
        var previous = _messageSpeechHighlight?.MessageId;
        _messageSpeechHighlight = highlight;
        if (previous is { } oldMessage && oldMessage != highlight?.MessageId) RefreshMessageSpeechHighlight(oldMessage);
        if (highlight is { } active) RefreshMessageSpeechHighlight(active.MessageId);
    }

    private void RefreshMessageSpeechHighlight(Guid messageId)
    {
        if (!_messageBlocks.TryGetValue(messageId.ToString(), out var blocks)) return;
        foreach (var block in blocks.Values)
        {
            var element = block is ActivitySlotView slot ? slot.Content : block;
            if (element is ContentControl host) element = host.Content;
            if (element is NativeStreamingMarkdown markdown) ApplyMessageSpeechHighlight(markdown);
        }
    }

    private void UpdateMessageSpeechSource(NativeStreamingMarkdown markdown, string messageId, int start, string source, string? stepId = null)
    {
        if (markdown.Tag is not NativeSourceAnchor current || current.MessageId != messageId
            || current.Start != start || current.Source != source || current.StepId != stepId)
            markdown.Tag = new NativeSourceAnchor(messageId, start, source, stepId);
        ApplyMessageSpeechHighlight(markdown);
    }

    private void ApplyMessageSpeechHighlight(NativeStreamingMarkdown markdown)
    {
        if (_messageSpeechHighlight is { } highlight && highlight.SessionId == _session
            && markdown.Tag is NativeSourceAnchor { Start: >= 0 } anchor
            && anchor.StepId == highlight.StepId
            && Guid.TryParse(anchor.MessageId, out var messageId) && messageId == highlight.MessageId
            && DisplayMessages.ContainsKey(anchor.MessageId))
        {
            var sentence = highlight.Sentence;
            var start = Math.Max(sentence.Start, anchor.Start);
            var end = Math.Min(sentence.Start + sentence.Length, anchor.Start + anchor.Source.Length);
            if (end > start && (sentence.Source is null || sentence.Source.Length == sentence.Length
                && anchor.Source.AsSpan(start - anchor.Start, end - start)
                    .SequenceEqual(sentence.Source.AsSpan(start - sentence.Start, end - start))))
            {
                markdown.SetSpeechHighlight(start - anchor.Start, end - start);
                return;
            }
        }
        markdown.ClearSpeechHighlight();
    }
}
