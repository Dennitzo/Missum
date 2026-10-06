using Missum.Core.Models;

namespace Missum.App.Services;

public sealed partial class MissumAiAssistantService
{
    internal async Task<BrowserSpeechPlan> PrepareBrowserSpeechAsync(
        BrowserSpeechRequest request, CancellationToken cancellationToken)
    {
        // Reuse the exact document/message resolution and immutable source plan
        // used by desktop playback. This path deliberately never opens a player.
        var source = await ResolveSpeechSourceAsync(request.SessionId, request.Text,
            request.MessageId, request.StartAnchor, request.ExpectedMessageUpdatedAt,
            cancellationToken, request.MessageExcerpt).ConfigureAwait(false);
        return BrowserSpeechPlan.Create(request.SessionId, source.MessageId, source.Kind,
            source.Text, request.StartAnchor);
    }
}

internal sealed record BrowserSpeechRequest(Guid SessionId, string? Text, Guid? MessageId,
    SpeechStartAnchor? StartAnchor = null, DateTimeOffset? ExpectedMessageUpdatedAt = null,
    string? MessageExcerpt = null);

internal sealed record BrowserSpeechPlan(Guid SessionId, Guid? MessageId, string SourceKind,
    IReadOnlyList<SpeechSourceUnit> SourceUnits, IReadOnlyList<PreparedSpeechSegment> Segments,
    IReadOnlyList<SpeechPlaybackBatchPlan> Batches)
{
    internal static BrowserSpeechPlan Create(Guid sessionId, Guid? messageId, string kind,
        string text, SpeechStartAnchor? anchor = null)
    {
        var cleaned = MicrophoneTranscriptionService.PrepareSpeechText(text);
        if (string.IsNullOrWhiteSpace(cleaned))
            throw new InvalidOperationException("Es ist kein vorlesbarer Text vorhanden.");
        var allUnits = SpeechSourceSegmentation.CreateUnits(text);
        if (allUnits.Count == 0) allUnits = SpeechSourceSegmentation.CreateUnits(cleaned);
        var units = MissumAiAssistantService.SelectSpeechUnitsFromAnchor(allUnits, anchor);
        if (anchor is not null) cleaned = string.Join(Environment.NewLine, units.Select(unit => unit.SpeechText));
        var segments = SpeechSourceSegmentation.CreateDirectSegments(units, cleaned);
        if (segments.Count == 0)
            throw new InvalidOperationException("Es konnten keine vorlesbaren Sprachsegmente erstellt werden.");
        return new(sessionId, messageId, kind, units, segments,
            SpeechSourceSegmentation.CreatePlaybackBatches(segments));
    }
}
