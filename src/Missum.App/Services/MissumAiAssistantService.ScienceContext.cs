namespace Missum.App.Services;

public sealed partial class MissumAiAssistantService
{
    private static bool IsTechnicalResearchQuestion(string question) =>
        question.TrimStart().StartsWith("[MISSUM_WEB_RESEARCH_REQUEST]", StringComparison.Ordinal)
        || question.TrimStart().StartsWith("[MISSUM_SCIENCE_SESSION_CONTEXT]", StringComparison.Ordinal)
        || question.TrimStart().StartsWith("[MISSUM_RESEARCH_CONTINUATION]", StringComparison.Ordinal);

}
