using Missum.App.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private static string ToolIconGlyph(string key) => key switch
    {
        "subagent" => "\uE8D4", "web" => "\uE774", "research" => "\uE721", "image" => "\uEB9F",
        "audio" or "speech" or "audiobook" or "translate" => "\uE767",
        "document" or "attachment" => "\uE8A5", "video" => "\uE714",
        "pdf" => "\uEA90", "captions" => "\uE8F2", "plan" => "\uEA80",
        "project" or "folder" => "\uE8B7",
        "lean" => "\uE73E",
        "code" => "\uE943", "success" => "\uE768", _ => "\uEA86",
    };

    private static string ToolStepIconKey(string tool) => tool switch
    {
        "subagent" or "subagent.completed" or "subagent.spawn" or "subagent.wait" => "subagent",
        "assistant.reasoning" or "assistant.progress" or "context.compact" => "research",
        "assistant.continuation" => "success",
        "coding.updatePlan" => "plan",
        "math.formalProof" => "lean",
        "web.deepResearch" or "research.deep" => "research",
        "media.analyze" or "media.inspect" => "image",
        "context.embed" or "context.retrieve" => "research",
        "workspace.open" => "project",
        "speech.translate" => "translate",
        _ when tool.StartsWith("research.code.", StringComparison.OrdinalIgnoreCase) => "code",
        _ when tool.StartsWith("coding.", StringComparison.OrdinalIgnoreCase) => "code",
        _ when tool.StartsWith("math.", StringComparison.OrdinalIgnoreCase) => "code",
        _ when tool.StartsWith("research.", StringComparison.OrdinalIgnoreCase) => "research",
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
    private static SolidColorBrush ToolGlyphBrush(string iconKey, string actionId) => NativeIconPalette.BrushFor(iconKey, actionId);
}
