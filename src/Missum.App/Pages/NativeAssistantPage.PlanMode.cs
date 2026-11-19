using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Missum.App.Controls;
using Missum.Core.Extensions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private const string PlanMarker = "```assistant-plan";
    private const string PlanCardName = "NativePlanInteraction";
    private bool _submittingPlanInteraction;
    private Func<string?, bool, Task>? _planInteractionSmokeAdapter;
    private readonly HashSet<string> _discardedPlans = new(StringComparer.Ordinal);

    private static int PlanPayloadStart(string content)
    {
        var fenced = content.LastIndexOf(PlanMarker, StringComparison.OrdinalIgnoreCase);
        if (fenced >= 0 && (fenced == 0 || content[fenced - 1] == '\n')) return fenced;
        var trimmed = content.TrimStart();
        if (trimmed.StartsWith('{')
            && (trimmed.Contains("\"kind\":\"plan\"", StringComparison.Ordinal)
                || trimmed.Contains("\"kind\":\"questions\"", StringComparison.Ordinal)
                || ReadPlanPayload(trimmed, out _) is not null))
            return content.Length - trimmed.Length;
        return -1;
    }

    private static JsonElement? ReadPlanPayload(string source, out int length)
    {
        length = source.Length;
        if (source.StartsWith(PlanMarker, StringComparison.OrdinalIgnoreCase))
        {
            var end = source.IndexOf("\n```", PlanMarker.Length, StringComparison.Ordinal);
            if (end < 0) return null;
            length = end + 4;
            source = source[PlanMarker.Length..end].Trim();
        }
        try
        {
            var data = JsonSerializer.Deserialize<JsonElement>(source);
            return S(data, "kind") is "questions" or "plan" ? data : null;
        }
        catch (JsonException) { return null; }
    }

    internal static string HidePlanPayload(string content)
    {
        var start = PlanPayloadStart(content);
        if (start < 0) return content;
        _ = ReadPlanPayload(content[start..], out var length);
        // Preserve the source offsets used by tool rows and attachments.
        return content[..start] + new string(' ', length) + content[(start + length)..];
    }

    internal static string PlanApprovalText(string content)
    {
        var start = PlanPayloadStart(content);
        if (start < 0 || ReadPlanPayload(content[start..], out var length) is not { } payload || S(payload, "kind") != "plan")
            return "";
        var plan = FullPlanText(content, start, length, payload);
        return PlanQuestions(payload, plan).Length == 0 ? plan : "";
    }

    private static string FullPlanText(string content, int start, int length, JsonElement payload)
    {
        var body = string.Join("\n\n", new[] { content[..start].Trim(), S(payload, "markdown").Trim(), content[(start + length)..].Trim() }
            .Where(part => part.Length > 0));
        var title = PlanTitlePrefix(payload, body);
        return string.Join("\n\n", new[] { title, body }.Where(part => part.Length > 0));
    }

    private sealed record PlanQuestion(string Text, JsonElement[] Options, bool AllowFreeText);

    private static PlanQuestion[] PlanQuestions(JsonElement payload, string plan)
    {
        var structured = Items(payload, "questions")
            .Where(question => !string.IsNullOrWhiteSpace(S(question, "text")))
            .Select(question => new PlanQuestion(S(question, "text"), Items(question, "options").Take(12).ToArray(),
                !question.TryGetProperty("allowFreeText", out var allow) || allow.ValueKind != JsonValueKind.False))
            .ToArray();
        return structured.Length > 0 ? structured
            : [.. ReadOpenPlanQuestions(plan).Select(text => new PlanQuestion(text, [], true))];
    }

    [GeneratedRegex(@"^(?:[0-9]+[\\]?[.)]|[-+*])\s+")]
    private static partial Regex PlanQuestionListMarker();

    internal static IReadOnlyList<string> ReadOpenPlanQuestions(string plan)
    {
        var questions = new List<string>();
        var question = new StringBuilder();
        var inQuestionSection = false;
        var fenceChar = '\0';
        var fenceLength = 0;
        using var lines = new StringReader(plan);
        while (lines.ReadLine() is { } rawLine)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line[0] is '`' or '~')
            {
                var length = line.Length - line.TrimStart(line[0]).Length;
                if (fenceLength == 0 && length >= 3)
                {
                    fenceChar = line[0];
                    fenceLength = length;
                    continue;
                }
                if (line[0] == fenceChar && length >= fenceLength && length == line.Length)
                {
                    fenceLength = 0;
                    continue;
                }
            }
            if (fenceLength != 0) continue;

            line = line.TrimEnd('\\').TrimEnd();
            if (line.Length == 0) continue;
            var label = line.Trim('#', ' ', ':', '*', '_');
            if (label.Equals("Offene Fragen", StringComparison.OrdinalIgnoreCase)
                || label.Equals("Offene Entscheidungen", StringComparison.OrdinalIgnoreCase)
                || label.Equals("Open questions", StringComparison.OrdinalIgnoreCase)
                || label.Equals("Open decisions", StringComparison.OrdinalIgnoreCase))
            {
                FinishQuestion();
                inQuestionSection = true;
                continue;
            }
            if (!inQuestionSection) continue;
            if (line.StartsWith('#'))
            {
                FinishQuestion();
                inQuestionSection = false;
                continue;
            }
            var marker = PlanQuestionListMarker().Match(line);
            if (marker.Success) FinishQuestion();
            if (question.Length > 0) question.Append(' ');
            question.Append(line.AsSpan(marker.Length));
        }
        FinishQuestion();
        return questions;

        void FinishQuestion()
        {
            var text = question.ToString().Trim();
            question.Clear();
            if (text.Length == 0) return;
            if (text.TrimEnd('.', '!', ' ').ToUpperInvariant() is
                "KEINE" or "KEINE OFFENEN FRAGEN" or "KEINE OFFENEN ENTSCHEIDUNGEN" or "ENTFÄLLT" or "NONE" or "NO OPEN QUESTIONS")
                return;
            questions.Add(text);
        }
    }

    private static string PlanTitlePrefix(JsonElement payload, string markdown)
    {
        var title = S(payload, "title").Trim();
        if (title.Length == 0 || markdown.Length == 0) return "";
        var firstLine = markdown.TrimStart().Split('\n', 2)[0].Trim();
        var firstHeading = firstLine.StartsWith('#') ? firstLine.TrimStart('#').Trim() : firstLine;
        return string.Equals(title, firstHeading, StringComparison.OrdinalIgnoreCase) ? "" : "# " + title;
    }

    private string PlanDisplayContent(JsonElement message)
    {
        var content = MessageContent(message);
        if (ActiveSubagent is not null || S(message, "role") != "assistant") return content;
        var start = PlanPayloadStart(content);
        if (start >= 0 && ReadPlanPayload(content[start..], out _) is { } payload && S(payload, "kind") == "plan")
            // All visible plan text moves into the compact card and its full tab.
            // Preserve original offsets for any message attachments/tool rows.
            return new string(' ', content.Length);
        return HidePlanPayload(content);
    }

    private void UpdatePlanInteraction(string messageId, JsonElement message, StackPanel panel, StackPanel footer)
    {
        var old = panel.Children.OfType<Border>().FirstOrDefault(child => child.Name == PlanCardName);
        var content = MessageContent(message);
        var start = ActiveSubagent is null && S(message, "role") == "assistant" ? PlanPayloadStart(content) : -1;
        var length = 0;
        var data = start < 0 ? null : ReadPlanPayload(content[start..], out length);
        if (data is not { } payload)
        {
            if (old is not null) panel.Children.Remove(old);
            return;
        }

        var session = _session;
        var latest = !DisplayMessages.Values.Any(other => MessageCreatedAt(other) > MessageCreatedAt(message)
            || MessageCreatedAt(other) == MessageCreatedAt(message) && S(other, "role") == "assistant"
                && string.CompareOrdinal(S(other, "id"), messageId) > 0);
        var canSubmit = latest && !_discardedPlans.Contains(messageId) && S(message, "status") == "completed"
            && _persistentAction == BuiltInActionIds.PlanMode;
        var isPlan = S(payload, "kind") == "plan";
        var fullPlan = isPlan ? FullPlanText(content, start, length, payload) : "";
        var questions = PlanQuestions(payload, fullPlan);
        var key = (payload.GetRawText(), canSubmit, fullPlan);
        if (old is not null && Equals(old.Tag, key)) return;
        if (old is not null) panel.Children.Remove(old);

        var contentPanel = new StackPanel { Spacing = 8 };
        if (fullPlan.Length > 0)
            contentPanel.Children.Add(PlanSummaryCard(session, messageId, S(payload, "title", "Plan"), fullPlan,
                hasOpenQuestions: questions.Length > 0));
        if (questions.Length > 0)
        {
            var answers = new List<Func<string?>>();
            var interaction = PlanCard();
            var layout = (StackPanel)interaction.Child;
            async Task SendAnswersAsync()
            {
                var selected = answers.Select(answer => answer()).ToArray();
                // Several questions share one submission. A typed free-text
                // answer is complete only after its own send arrow is pressed.
                if (selected.Length == 0 || selected.Any(answer => answer is null)) return;
                await SubmitPlanInteractionAsync(session, messageId, "Meine Antworten zum Plan\n\n" + string.Join("\n\n", selected));
            }
            foreach (var question in questions)
            {
                var questionText = question.Text;
                if (answers.Count > 0) layout.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 4, 0, 4),
                    Background = ThemeBrush("MissumStrokeBrush", 50) });
                layout.Children.Add(PlanLabel(questionText, bold: true));
                var options = question.Options;
                var selectedIndex = -1;
                string? confirmedAnswer = null;
                var confirmation = new TextBlock { Text = "Antwort bestätigt", FontSize = 11,
                    Foreground = ThemeBrush("MissumMutedTextBrush", 180), Visibility = Visibility.Collapsed };
                var optionButtons = new List<Button>();
                var allowFreeText = options.Length == 0 || question.AllowFreeText;
                TextBox? own = null;
                void RefreshOptions()
                {
                    for (var i = 0; i < optionButtons.Count; i++)
                    {
                        optionButtons[i].Background = i == selectedIndex ? ThemeBrush("MissumHoverBrush", 65) : TransparentBrush();
                        AutomationProperties.SetHelpText(optionButtons[i], i == selectedIndex ? "Ausgewählt" : "Auswählen");
                    }
                }
                for (var i = 0; i < options.Length; i++)
                {
                    var index = i;
                    var option = options[index];
                    var label = S(option, "label");
                    var recommended = option.TryGetProperty("recommended", out var flag) && flag.ValueKind == JsonValueKind.True;
                    const string suffix = " (Empfohlen)";
                    if (label.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        recommended = true;
                        label = label[..^suffix.Length];
                    }
                    var row = PlanOptionRow((index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), label, S(option, "description"), recommended: recommended);
                    row.IsEnabled = canSubmit;
                    row.Click += async (_, _) =>
                    {
                        if (own is not null) own.Text = "";
                        confirmedAnswer = null;
                        confirmation.Visibility = Visibility.Collapsed;
                        selectedIndex = index;
                        RefreshOptions();
                        await SendAnswersAsync();
                    };
                    optionButtons.Add(row);
                    layout.Children.Add(row);
                }
                if (allowFreeText)
                {
                    var input = PlanInput("Eigene Antwort", canSubmit, out own, canSubmit ? async value =>
                    {
                        confirmedAnswer = value.Text.Trim();
                        confirmation.Visibility = confirmedAnswer.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                        if (confirmedAnswer.Length > 0) await SendAnswersAsync();
                    } : null);
                    own.TextChanged += (_, _) =>
                    {
                        if (!string.Equals(own.Text.Trim(), confirmedAnswer, StringComparison.Ordinal))
                        {
                            confirmedAnswer = null;
                            confirmation.Visibility = Visibility.Collapsed;
                        }
                        if (!string.IsNullOrWhiteSpace(own.Text)) { selectedIndex = -1; RefreshOptions(); }
                    };
                    layout.Children.Add(input);
                    layout.Children.Add(confirmation);
                }
                answers.Add(() => confirmedAnswer is { Length: > 0 } answer && own?.Text.Trim() == answer
                    ? questionText + "\n" + answer
                    : selectedIndex >= 0 ? questionText + "\n" + S(options[selectedIndex], "label") : null);
            }
            contentPanel.Children.Add(interaction);
        }
        else if (isPlan && canSubmit && fullPlan.Length > 0)
        {
            var interaction = PlanCard();
            var layout = (StackPanel)interaction.Child;
            layout.Children.Add(PlanLabel("Diesen Plan implementieren?", bold: true));
            layout.Children.Add(PlanOptionRow("✓", "Ja, diesen Plan implementieren", "", () =>
                SubmitPlanInteractionAsync(session, messageId, null, leavePlanMode: true)));
            layout.Children.Add(PlanOptionRow("×", "Verwerfen", "", () =>
                SubmitPlanInteractionAsync(session, messageId, null, leavePlanMode: true, discard: true)));
            var input = PlanInput("Änderungswünsche zum Plan", true, out _, async revision =>
            {
                if (revision.Text.Trim() is { Length: > 0 } changes)
                    await SubmitPlanInteractionAsync(session, messageId, "Überarbeite den Plan wie folgt.\n\n" + changes);
            });
            layout.Children.Add(input);
            contentPanel.Children.Add(interaction);
        }
        var card = new Border { Name = PlanCardName, Tag = key, Child = contentPanel };
        panel.Children.Insert(panel.Children.IndexOf(footer), card);
    }

    private static Border PlanCard() => new()
    {
        Child = new StackPanel { Spacing = 8 }, Padding = new Thickness(16, 14, 16, 12),
        CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1),
        BorderBrush = ThemeBrush("MissumStrokeBrush", 50), Background = ThemeBrush("MissumLayerStrongBrush", 44),
    };

    private static TextBlock PlanLabel(string text, bool bold) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("MissumTextBrush", 220),
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
    };

    private static SolidColorBrush TransparentBrush() => new(Microsoft.UI.Colors.Transparent);

    private static Button PlanOptionRow(string number, string label, string description, Func<Task>? invoke = null, bool recommended = false)
    {
        var row = new Grid { ColumnSpacing = 9 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var badge = new Border { Width = 21, Height = 21, CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1), BorderBrush = ThemeBrush("MissumStrokeBrush", 55),
            Background = ThemeBrush("MissumLayerBrush", 50), VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = number, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeBrush("MissumMutedTextBrush", 180) } };
        row.Children.Add(badge);
        var text = new StackPanel { Spacing = 2 };
        var title = new Grid { ColumnSpacing = 6 };
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (recommended) title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(PlanLabel(label, bold: true));
        if (recommended)
        {
            var recommendation = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(5, 1, 5, 1),
                VerticalAlignment = VerticalAlignment.Top, Background = ThemeBrush("MissumHoverBrush", 65),
                Child = new TextBlock { Text = "Empfohlen", FontSize = 11, Foreground = ThemeBrush("MissumMutedTextBrush", 180) } };
            Grid.SetColumn(recommendation, 1);
            title.Children.Add(recommendation);
        }
        text.Children.Add(title);
        if (!string.IsNullOrWhiteSpace(description))
            text.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, FontSize = 12,
                Foreground = ThemeBrush("MissumMutedTextBrush", 180) });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var button = new Button { Content = row, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(7, 5, 7, 5),
            CornerRadius = new CornerRadius(8), Background = TransparentBrush() };
        AutomationProperties.SetName(button, label);
        if (invoke is not null) button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await invoke(); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    private static Border PlanInput(string placeholder, bool enabled, out TextBox textBox, Func<TextBox, Task>? submit = null)
    {
        var grid = new Grid { ColumnSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (submit is not null) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new FontIcon { Glyph = "\uE70F", FontSize = 14, Margin = new Thickness(11, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeBrush("MissumMutedTextBrush", 180) });
        var input = new TextBox { PlaceholderText = placeholder, IsEnabled = enabled, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, MinHeight = 38, MaxHeight = 160, Padding = new Thickness(9, 7, 9, 7),
            Background = TransparentBrush(), BorderThickness = new Thickness(0) };
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused",
            "TextControlBackgroundDisabled", "TextControlBorderBrush", "TextControlBorderBrushPointerOver",
            "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled" }) input.Resources[key] = TransparentBrush();
        AutomationProperties.SetName(input, placeholder);
        textBox = input;
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);
        if (submit is not null)
        {
            var send = new Button { Content = new FontIcon { Glyph = "\uE72A", FontSize = 15 },
                VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(8), Background = TransparentBrush() };
            AutomationProperties.SetName(send, placeholder == "Eigene Antwort" ? "Antworten senden" : "Plan überarbeiten");
            send.Click += async (_, _) =>
            {
                send.IsEnabled = false;
                try { await submit(input); }
                finally { send.IsEnabled = true; }
            };
            input.KeyDown += async (_, args) =>
            {
                if (args.Key != Windows.System.VirtualKey.Enter || Microsoft.UI.Input.InputKeyboardSource
                    .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
                args.Handled = true;
                if (!send.IsEnabled) return;
                send.IsEnabled = false;
                try { await submit(input); }
                finally { send.IsEnabled = true; }
            };
            Grid.SetColumn(send, 2);
            grid.Children.Add(send);
        }
        var border = new Border { Child = grid, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("MissumStrokeBrush", 55), Background = ThemeBrush("MissumInputBrush", 40) };
        input.GotFocus += (_, _) => border.BorderBrush = ThemeBrush("MissumAccentBrush", 180);
        input.LostFocus += (_, _) => border.BorderBrush = ThemeBrush("MissumStrokeBrush", 55);
        return border;
    }

    private async Task SubmitPlanInteractionAsync(Guid session, string messageId, string? prompt, bool leavePlanMode = false, bool discard = false)
    {
        if (_disposed || _submittingPlanInteraction || _session != session || _running || _sendPending
            || _persistentAction != BuiltInActionIds.PlanMode || !DisplayMessages.TryGetValue(messageId, out var message)
            || S(message, "status") != "completed"
            || DisplayMessages.Values.Any(other => MessageCreatedAt(other) > MessageCreatedAt(message))) return;
        if (leavePlanMode && !discard)
        {
            var plan = PlanApprovalText(MessageContent(message));
            if (plan.Length == 0) return;
            prompt = "Implementiere den von mir freigegebenen Plan.\n\n" + plan;
        }
        _submittingPlanInteraction = true;
        try
        {
            if (IsMessageFooterSmoke && _planInteractionSmokeAdapter is { } smokeAdapter)
            {
                await smokeAdapter(prompt, leavePlanMode);
                return;
            }
            if (leavePlanMode)
            {
                if (!await CommandAsync("action.invoke", new { sessionId = session, actionId = BuiltInActionIds.PlanMode, enabled = false })
                    || _session != session) return;
                ClearToolSelection();
            }
            if (prompt is not null)
                await SendAsync(prompt);
            else
            {
                _discardedPlans.Add(messageId);
                await RefreshForExternalActivationAsync();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowError(exception.Message); }
        finally { _submittingPlanInteraction = false; }
    }
}
