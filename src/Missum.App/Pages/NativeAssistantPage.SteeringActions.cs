using System.Text.Json;
using Missum.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private MessageActionView SteeringPromptActions(string ownerId, JsonElement message, JsonElement step)
    {
        var id = MissumAiAssistantService.SteeringPromptMessageId(Guid.Parse(ownerId), S(step, "id")).ToString("D");
        if (!_messageActionViews.TryGetValue(id, out var view))
            _messageActionViews[id] = view = CreateMessageActionView(id);
        view.OwnerMessageId = ownerId;
        view.SteeringStepId = S(step, "id");
        view.SessionId = Guid.TryParse(S(message, "sessionId"), out var session) ? session : _session;
        view.Text = S(step, "detail");
        view.IsAssistant = view.IsStreaming = false;
        view.CanRead = !string.IsNullOrWhiteSpace(view.Text);
        RefreshMessageActionView(view);
        return view;
    }

    private Border CreateSteeringPromptView(string ownerId, JsonElement message, JsonElement step)
    {
        var bubble = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new(16, 12, 16, 12), CornerRadius = new(18), Background = ThemeBrush("MissumLayerStrongBrush", 45),
            Child = new StackPanel { Spacing = 8, Children =
                { new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 16 } } },
        };
        UpdateSteeringPromptView(bubble, ownerId, message, step);
        return bubble;
    }

    private void UpdateSteeringPromptView(Border bubble, string ownerId, JsonElement message, JsonElement step)
    {
        var actions = SteeringPromptActions(ownerId, message, step);
        var body = (StackPanel)bubble.Child;
        var text = (TextBlock)body.Children[0];
        if (text.Text != actions.Text) text.Text = actions.Text;
        // Recycled blocks and the transition to virtualization reuse footer
        // actions, but WinUI panels can only belong to one visual parent.
        if (!body.Children.Contains(actions.Panel))
        {
            // Parent may still be null before the first layout, even though
            // the element already belongs to an unloaded container.
            actions.SteeringHost?.Children.Remove(actions.Panel);
            body.Children.Add(actions.Panel);
        }
        actions.SteeringHost = body;
        bubble.Tag = actions.MessageId;
    }

    private void ReleaseSteeringPromptActions(string ownerId, FrameworkElement element)
    {
        if (element is Border { Tag: string id } && _messageActionViews.TryGetValue(id, out var action)
            && action.OwnerMessageId == ownerId && action.SteeringStepId is not null)
            _messageActionViews.Remove(id);
    }
}
