using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Missum.App.Services;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private Guid? _renderedComputerRequest;

    private void OnComputerApprovalsChanged()
    {
        if (!_disposed) DispatcherQueue.TryEnqueue(() => { if (!_disposed) RenderComputerApprovals(); });
    }

    private void RenderComputerApprovals()
    {
        if (_disposed) return;
        var state = _computerApprovals.Snapshot();
        var pending = state.Pending?.SessionId == _session ? state.Pending : null;
        var active = state.ActiveSession == _session && state.ActiveRun is { Length: > 0 };
        ComputerControlHost.Children.Clear();
        ComputerControlStatusHost.Children.Clear();
        ComputerControlHost.Visibility = pending is not null ? Visibility.Visible : Visibility.Collapsed;
        ComputerControlStatusHost.Visibility = active || pending is not null ? Visibility.Visible : Visibility.Collapsed;
        if (active || pending is not null)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = pending is not null ? "Computersteuerung · Bestätigung erforderlich" : "Computersteuerung aktiv · Desktop nicht isoliert", VerticalAlignment = VerticalAlignment.Center,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (pending is not null)
            {
                var show = new Button { Content = "Anzeigen", CornerRadius = new CornerRadius(8) };
                show.Click += (_, _) => OnScrollToBottom(this, new RoutedEventArgs());
                actions.Children.Add(show);
            }
            var stop = new Button { Content = "Stoppen", CornerRadius = new CornerRadius(8) };
            AutomationProperties.SetName(stop, "Computersteuerung und Lauf stoppen");
            var runId = state.ActiveRun ?? pending!.RunId;
            var session = _session;
            stop.Click += async (_, _) => await StopComputerUseAsync(session, runId);
            actions.Children.Add(stop);
            Grid.SetColumn(actions, 1); row.Children.Add(actions);
            ComputerControlStatusHost.Children.Add(new Border { Child = row, CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12), BorderThickness = new Thickness(1),
                BorderBrush = ThemeBrush("MissumStrokeBrush", 55), Background = ThemeBrush("MissumLayerStrongBrush", 44) });
        }
        if (pending is null) { _renderedComputerRequest = null; return; }

        // Share the same card and choice rows as plan-mode interactions so
        // borders, spacing, typography and hover states stay in sync.
        var card = PlanCard();
        var content = (StackPanel)card.Child;
        content.Children.Add(PlanLabel(pending.IsActivation ? "Computersteuerung für diesen Lauf aktivieren?" : "Computeraktion bestätigen", bold: true));
        var description = pending.IsActivation
            ? "Missum darf den gesamten Windows-Desktop für diesen Lauf sehen und bedienen. Der Desktop ist keine isolierte Umgebung. Du kannst den Lauf jederzeit stoppen."
            : DescribeComputerAction(pending.Request!);
        content.Children.Add(PlanLabel(description, bold: false));
        if (pending.IsActivation)
            content.Children.Add(ComputerDecisionButton("✓", "Computersteuerung aktivieren", pending, ComputerActionDecision.Execute));
        else
        {
            content.Children.Add(ComputerDecisionButton("1", "Ich habe es erledigt", pending, ComputerActionDecision.ManualDone));
            content.Children.Add(ComputerDecisionButton("2", "Missum ausführen lassen", pending, ComputerActionDecision.Execute));
        }
        content.Children.Add(ComputerDecisionButton("×", "Abbrechen", pending, ComputerActionDecision.Cancel));
        ComputerControlHost.Children.Add(card);
        if (_renderedComputerRequest != pending.Id)
        {
            _renderedComputerRequest = pending.Id;
            if (CanFollowConversationTail) OnScrollToBottom(this, new RoutedEventArgs());
        }
    }

    private Button ComputerDecisionButton(string marker, string text, PendingComputerApproval pending, ComputerActionDecision decision)
    {
        return PlanOptionRow(marker, text, "", async () =>
        {
            if (_session != pending.SessionId || !_computerApprovals.Decide(pending.Id, pending.SessionId, pending.RunId, decision)) return;
            if (decision == ComputerActionDecision.Cancel) await StopComputerUseAsync(pending.SessionId, pending.RunId);
        });
    }

    private async Task StopComputerUseAsync(Guid sessionId, string runId)
    {
        App.Current.GetService<ComputerDesktopBroker>().End(sessionId, runId);
        _computerApprovals.EndRun(runId);
        await CommandAsync("chat.cancel", new { sessionId });
    }

    private static string DescribeComputerAction(ComputerApprovalRequest request)
    {
        var target = string.IsNullOrWhiteSpace(request.TargetTitle) ? "Windows-Desktop" : request.TargetTitle.Trim();
        var position = request.X is int x && request.Y is int y ? $" · Position {x}, {y}" : "";
        var text = string.IsNullOrWhiteSpace(request.Text) ? "" : "\nEingabe: " + request.Text;
        var action = request.Action switch
        {
            "click" => "Klick", "double_click" => "Doppelklick", "drag" => "Ziehen",
            "scroll" => "Scrollen", "type" => "Text eingeben", "keypress" => "Taste drücken",
            "focus" => "Fenster wechseln", _ => request.Action,
        };
        return $"Ziel: {target}\nAktion: {action}{position}{text}";
    }
}
