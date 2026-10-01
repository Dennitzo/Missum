using Missum.App.Controls;
using Windows.UI;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private static string ToolIconGlyph(string key) => key switch
    {
        "web" => "\uE774", "research" => "\uE721", "image" => "\uEB9F",
        "audio" or "speech" => "\uE767", "document" => "\uE8A5",
        "pdf" => "\uEA90", "captions" => "\uE8F2", "plan" => "\uEA80",
        "code" => "\uE943", _ => "\uEA86",
    };

    private static string ToolStepIconKey(string tool) => tool switch
    {
        "assistant.reasoning" or "assistant.progress" => "research",
        "coding.updatePlan" => "plan",
        "web.deepResearch" or "research.deep" => "research",
        "speech.translate" => "translate",
        _ when tool.StartsWith("research.code.", StringComparison.OrdinalIgnoreCase) => "code",
        _ when tool.StartsWith("coding.", StringComparison.OrdinalIgnoreCase) => "code",
        _ when tool.StartsWith("math.", StringComparison.OrdinalIgnoreCase)
            || tool.StartsWith("research.", StringComparison.OrdinalIgnoreCase) => "research",
        _ when tool.StartsWith("web.", StringComparison.OrdinalIgnoreCase) => "web",
        _ when tool.Contains("audiobook", StringComparison.OrdinalIgnoreCase) => "audiobook",
        _ when tool.Contains("caption", StringComparison.OrdinalIgnoreCase) => "captions",
        _ when tool.StartsWith("speech.", StringComparison.OrdinalIgnoreCase)
            || tool.Contains("transcription", StringComparison.OrdinalIgnoreCase) => "speech",
        _ when tool.Contains("image", StringComparison.OrdinalIgnoreCase) => "image",
        _ when tool.Contains("audio", StringComparison.OrdinalIgnoreCase) => "audio",
        _ when tool.Contains("video", StringComparison.OrdinalIgnoreCase) => "video",
        _ when tool.Contains("pdf", StringComparison.OrdinalIgnoreCase) => "pdf",
        _ when tool.Contains("document", StringComparison.OrdinalIgnoreCase) => "document",
        _ => "tool",
    };

    private static Color ToolGlyphColor(string iconKey, string actionId) => NativeIconPalette.ColorFor(iconKey, actionId);
}
