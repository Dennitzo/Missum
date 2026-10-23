using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    internal static string CompactionReceiptLabel(JsonElement step) => S(step, "status") switch
    {
        "completed" => "Kontextkomprimierung abgeschlossen",
        "cancelled" or "interrupted" => "Kontextkomprimierung abgebrochen",
        "failed" or "denied" => "Kontextkomprimierung fehlgeschlagen",
        _ => "Kontext wird komprimiert",
    };

    // Persisted timeline metadata: no button, disclosure, deferred details or
    // navigation handlers. Updating the status retains the same single row.
    internal sealed class CompactionReceiptView : Grid
    {
        private readonly TextBlock _label = new()
        {
            FontSize = 14,
            Foreground = ThemeBrush("MissumMutedTextBrush", 160),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        internal string Label => _label.Text;

        internal CompactionReceiptView()
        {
            IsHitTestVisible = false;
            ColumnSpacing = 8;
            Margin = new(6, 5, 6, 5);
            ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            Children.Add(new FontIcon
            {
                Glyph = ToolIconGlyph("research"),
                FontSize = 14,
                Foreground = ToolGlyphBrush("research", "context.compact"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetColumn(_label, 1);
            Children.Add(_label);
        }

        internal void Update(JsonElement step)
        {
            var label = CompactionReceiptLabel(step);
            if (_label.Text == label) return;
            _label.Text = label;
            AutomationProperties.SetName(this, label);
        }
    }
}
