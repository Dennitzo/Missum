using System.Text.Json;
using Missum.App.Controls;
using Missum.Core.Extensions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private const string PlanMarker = "```assistant-plan";
    private const string PlanCardName = "NativePlanInteraction";
    private bool _submittingPlanInteraction;
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
        return string.Join("\n\n", new[] { content[..start].Trim(), S(payload, "markdown").Trim(), content[(start + length)..].Trim() }
            .Where(part => part.Length > 0));
    }

    private string PlanDisplayContent(JsonElement message) =>
        ActiveSubagent is null && S(message, "role") == "assistant"
            ? HidePlanPayload(MessageContent(message))
            : MessageContent(message);

    private void UpdatePlanInteraction(string messageId, JsonElement message, StackPanel panel, StackPanel footer)
    {
        var old = panel.Children.OfType<Border>().FirstOrDefault(child => child.Name == PlanCardName);
        var content = MessageContent(message);
        var start = ActiveSubagent is null && S(message, "role") == "assistant" ? PlanPayloadStart(content) : -1;
        var data = start < 0 ? null : ReadPlanPayload(content[start..], out _);
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
        var questions = S(payload, "kind") == "questions";
        var plan = canSubmit && !questions ? PlanApprovalText(content) : "";
        var key = (payload.GetRawText(), canSubmit, plan);
        if (old is not null && Equals(old.Tag, key)) return;
        if (old is not null) panel.Children.Remove(old);

        var layout = new StackPanel { Spacing = 10 };
        layout.Children.Add(PlanLabel(questions ? "Rückfragen zum Plan" : S(payload, "title", "Umsetzungsplan"), bold: true));
        if (questions)
        {
            var answers = new List<Func<string?>>();
            foreach (var question in Items(payload, "questions").Take(3))
            {
                var questionText = S(question, "text");
                if (string.IsNullOrWhiteSpace(questionText)) continue;
                layout.Children.Add(PlanLabel(questionText, bold: true));
                if (!latest) continue;
                var options = Items(question, "options").Take(12).ToArray();
                var choices = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch,
                    PlaceholderText = "Antwort auswählen", IsEnabled = canSubmit };
                foreach (var option in options)
                {
                    var label = S(option, "label");
                    var description = S(option, "description");
                    choices.Items.Add(description.Length == 0 ? label : label + " · " + description);
                }
                if (options.Length > 0) layout.Children.Add(choices);
                var own = new TextBox { PlaceholderText = "Eigene Antwort", IsEnabled = canSubmit };
                var allowFreeText = options.Length == 0 || !question.TryGetProperty("allowFreeText", out var allow)
                    || allow.ValueKind != JsonValueKind.False;
                if (allowFreeText) layout.Children.Add(own);
                answers.Add(() => allowFreeText && own.Text.Trim() is { Length: > 0 } answer
                    ? questionText + "\n" + answer
                    : choices.SelectedIndex >= 0 ? questionText + "\n" + S(options[choices.SelectedIndex], "label") : null);
            }
            if (canSubmit && answers.Count > 0)
                AddButton(layout, "Antworten senden", async () =>
                {
                    var selected = answers.Select(answer => answer()).ToArray();
                    if (selected.Any(answer => answer is null))
                    {
                        ShowError("Bitte beantworte alle Rückfragen.");
                        return;
                    }
                    await SubmitPlanInteractionAsync(session, messageId, "Meine Antworten zum Plan\n\n" + string.Join("\n\n", selected));
                });
        }
        else
        {
            var markdown = S(payload, "markdown");
            if (markdown.Length > 0) layout.Children.Add(new NativeStreamingMarkdown(markdown));
            if (canSubmit)
            {
                if (plan.Length > 0)
                    AddButton(layout, "Plan implementieren", () => SubmitPlanInteractionAsync(session, messageId,
                        "Implementiere den von mir freigegebenen Plan.\n\n" + plan, leavePlanMode: true));
                AddButton(layout, "Verwerfen", () => SubmitPlanInteractionAsync(session, messageId, null, leavePlanMode: true));
                var revision = new TextBox { PlaceholderText = "Änderungswünsche zum Plan", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
                layout.Children.Add(revision);
                AddButton(layout, "Plan überarbeiten", async () =>
                {
                    if (revision.Text.Trim() is { Length: > 0 } changes)
                        await SubmitPlanInteractionAsync(session, messageId, "Überarbeite den Plan wie folgt.\n\n" + changes);
                });
            }
        }
        var card = new Border { Name = PlanCardName, Tag = key, Child = layout, Padding = new Thickness(14),
            CornerRadius = new CornerRadius(10), Background = ThemeBrush("MissumLayerStrongBrush", 44) };
        panel.Children.Insert(panel.Children.IndexOf(footer), card);
    }

    private static TextBlock PlanLabel(string text, bool bold) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("MissumTextBrush", 220),
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
    };

    private static void AddButton(StackPanel target, string label, Func<Task> invoke)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await invoke(); }
            finally { button.IsEnabled = true; }
        };
        target.Children.Add(button);
    }

    private async Task SubmitPlanInteractionAsync(Guid session, string messageId, string? prompt, bool leavePlanMode = false)
    {
        if (_disposed || _submittingPlanInteraction || _session != session || _running || _sendPending
            || _persistentAction != BuiltInActionIds.PlanMode || !DisplayMessages.TryGetValue(messageId, out var message)
            || DisplayMessages.Values.Any(other => MessageCreatedAt(other) > MessageCreatedAt(message))) return;
        _submittingPlanInteraction = true;
        try
        {
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
