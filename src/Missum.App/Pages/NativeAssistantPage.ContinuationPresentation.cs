using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    internal static string ContinuationReceiptLabel(JsonElement step) => S(step, "status") switch
    {
        "completed" => "Lauf fortgesetzt",
        "failed" or "denied" => "Fortsetzen fehlgeschlagen",
        "cancelled" or "interrupted" => "Fortsetzung abgebrochen",
        _ => "Fortsetzung wird vorbereitet",
    };

    /// <summary>
    /// An admission receipt is timeline metadata, not a tool with a lazy detail
    /// body. Its intrinsic height is always the single icon/text row, regardless
    /// of the default tool disclosure preference or deferred receipt contents.
    /// </summary>
    internal sealed class ContinuationReceiptView : Grid
    {
        private readonly TextBlock _label = new()
        {
            FontSize = 14,
            Foreground = ThemeBrush("MissumMutedTextBrush", 160),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        internal string Label => _label.Text;

        internal ContinuationReceiptView()
        {
            ColumnSpacing = 8;
            Margin = new(6, 5, 6, 5);
            ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            Children.Add(new FontIcon
            {
                Glyph = ToolIconGlyph("success"),
                FontSize = 14,
                Foreground = ToolGlyphBrush("success", "assistant.continuation"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetColumn(_label, 1);
            Children.Add(_label);
        }

        internal void Update(JsonElement step)
        {
            var label = ContinuationReceiptLabel(step);
            if (_label.Text == label) return;
            _label.Text = label;
            AutomationProperties.SetName(this, label);
        }
    }
}
