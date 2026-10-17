namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private string? _readingToolConversation;
    private long _conversationScrollIntent;

    private bool CanFollowConversationTail => _readingToolConversation != ConversationViewKey;
    private bool ShouldFollowConversationTail => CanFollowConversationTail && (_scrollNewSessionToLatest
        || _conversationSelection?.IsSelecting != true && ConversationScroll.ScrollableHeight - ConversationScroll.VerticalOffset < 90);

    private void OnToolDisclosureInteraction()
    {
        // Explicitly opening a step means reading at this position. Cancel even
        // an already queued tail-follow so async details cannot pull it away.
        _readingToolConversation = ConversationViewKey;
        _scrollNewSessionToLatest = false;
        _conversationScrollIntent++;
    }
}
