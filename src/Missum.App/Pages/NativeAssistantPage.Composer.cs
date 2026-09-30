using System.Text;
using System.Text.Json;
using Missum.App.Controls;
using Missum.Core.Extensions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private string? _composerInstruction;
    private bool _composerToolSelectionBusy;

    private void OnModeMenu(object sender, RoutedEventArgs e)
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brush(43)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(18)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(49)));
        flyout.FlyoutPresenterStyle = style;
        var panel = new StackPanel { Width = 232, Spacing = 2 };
        foreach (var (mode, title, description) in new[] { ("general", "ChatGPT", "Erstellen, lernen und erkunden"), ("coding", "Codex", "Erstellen, debuggen und ausliefern"), ("claudescience", "Claude Science", "Deep Research, Analysen und Nachweise") })
        {
            var row = new Grid();
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = description, FontSize = 13, Foreground = Brush(160) });
            row.Children.Add(text);
            if (_mode == mode) row.Children.Add(new FontIcon { Glyph = "\uE73E", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 5, 0, 0) });
            var button = new Button { Content = row, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new(8, 8, 8, 8), CornerRadius = new(12) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, title);
            button.Click += async (_, _) => { flyout.Hide(); await NavigateAsync("mode.switch", new { chatMode = mode }); };
            panel.Children.Add(button);
        }
        flyout.Content = panel;
        NativeDropdownChevron.Bind(flyout, ModeButton);
        flyout.ShowAt(ModeButton);
    }

    private async void SelectComposerTool(string? actionId, string label, string? instruction = null)
    {
        if (_composerToolSelectionBusy || _disposed) return;
        var sessionId = _session;
        _composerToolSelectionBusy = true;
        try
        {
            // Persist mode-like tools before sending so switching chats keeps their selection.
            // Selecting another tool also releases an earlier persistent Plan/Hörbuch mode.
            if (!string.IsNullOrEmpty(_persistentAction) && _persistentAction != actionId
                && !await CommandAsync("action.invoke", new { sessionId, actionId = _persistentAction, enabled = false })) return;
            if (actionId is not null && !await CommandAsync("action.invoke", new { sessionId, actionId, enabled = true })) return;
            if (_session != sessionId || _disposed) return;
            _selectedAction = actionId;
            _composerInstruction = instruction;
            UpdateSelectedToolChip(label);
            if (!await BeginSelectedMediaCaptureAsync(actionId, sessionId))
            {
                ClearToolSelection();
                return;
            }
            Composer.Focus(FocusState.Programmatic);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ClearToolSelection();
            ShowError(exception.Message);
        }
        finally { _composerToolSelectionBusy = false; }
    }

    private void UpdateSelectedToolChip(string label)
    {
        var action = Items(_snapshot, "actionDescriptors").FirstOrDefault(a => S(a, "actionId") == _selectedAction);
        var glyph = S(action, "iconKey") switch
        {
            "plan" => "\uEA80", "web" => "\uE774", "research" => "\uE721", "document" => "\uE8A5",
            "image" => "\uEB9F", "speech" or "audio" => "\uE767", _ => "\uEA86",
        };
        SelectedToolChip.SetTool(_selectedAction == BuiltInActionIds.PlanMode ? "Planen" : label, glyph);
        SelectedToolChip.Visibility = Visibility.Visible;
    }

    private void ShowComposerTools()
    {
        var width = Math.Max(280, ComposerSurface.ActualWidth);
        var menu = new Flyout { Placement = FlyoutPlacementMode.TopEdgeAlignedLeft, AreOpenCloseAnimationsEnabled = true };
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brush(44)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(61)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(20)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 5, 4, 5)));
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, width));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, width));
        style.Setters.Add(new Setter(FrameworkElement.WidthProperty, width));
        menu.FlyoutPresenterStyle = style;
        var rows = new StackPanel { Spacing = 0, Width = width - 10 };
        void Heading(string text) => rows.Children.Add(new TextBlock { Text = text, FontSize = 13, Foreground = Brush(155), Margin = new(8, 6, 8, 5) });
        void Row(string glyph, string name, string description, Action action, bool enabled = true, Color? color = null)
        {
            var content = new Grid { ColumnSpacing = 8 };
            content.ColumnDefinitions.Add(new() { Width = new(16) });
            content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 15, Foreground = color is { } c ? new SolidColorBrush(c) : Brush(205), VerticalAlignment = VerticalAlignment.Center });
            var label = new TextBlock { Text = name, FontSize = 14, Foreground = Brush(220), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1); content.Children.Add(label);
            var detail = new TextBlock { Text = description, FontSize = 14, Foreground = Brush(145), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(detail, 2); content.Children.Add(detail);
            var button = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new(8, 5, 8, 5), MinHeight = 28, CornerRadius = new(15), IsEnabled = enabled, BorderThickness = new(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
            ToolTipService.SetToolTip(button, description);
            button.Click += (_, _) => { menu.Hide(); action(); };
            rows.Children.Add(button);
        }
        var actions = Items(_snapshot, "actionDescriptors").Where(a => S(a, "actionKind") == "selectableTool").ToArray();
        var document = actions.FirstOrDefault(a => S(a, "iconKey") == "document");
        var plan = actions.FirstOrDefault(a => S(a, "iconKey") == "plan");
        Heading("Hinzufügen");
        Row("\uE723", "Dateien und Ordner", "", () => ShowFileChoices(), color: Color.FromArgb(255, 91, 156, 246));
        var workspace = S(_snapshot, "workspacePath");
        if (_mode == "claudescience")
            Row("\uE9CE", "Forschung öffnen", "Vorhaben, Quellen und Ergebnisse", () => OpenResearchView(), color: Color.FromArgb(255, 168, 132, 246));
        if (plan.ValueKind == JsonValueKind.Object)
            Row("\uEA80", "Planmodus", "Planmodus einschalten", () => SelectComposerTool(S(plan, "actionId"), "Planmodus"), S(plan, "disabledReason").Length == 0, Color.FromArgb(255, 241, 157, 56));
        if (document.ValueKind == JsonValueKind.Object)
            Row("\uE8A5", "Dokumente erstellen", "Word, PDF, Tabellen und Präsentationen", () => SelectComposerTool(S(document, "actionId"), "Dokumente erstellen"), S(document, "disabledReason").Length == 0, Color.FromArgb(255, 55, 143, 234));
        foreach (var action in actions.Where(a => !S(a, "actionId").StartsWith("builtin.", StringComparison.Ordinal)))
            Row("\uEA86", S(action, "displayName"), S(action, "description"), () => SelectComposerTool(S(action, "actionId"), S(action, "displayName")), S(action, "disabledReason").Length == 0, Color.FromArgb(255, 151, 116, 225));
        foreach (var action in actions.Where(a => S(a, "actionId").StartsWith("builtin.", StringComparison.Ordinal) && S(a, "actionId") != BuiltInActionIds.DeepResearch && S(a, "iconKey") is not ("plan" or "document")))
            Row(S(action, "iconKey") switch { "web" => "\uE774", "research" => "\uE721", "image" => "\uEB9F", "audio" or "speech" => "\uE767", _ => "\uEA86" }, S(action, "displayName"), S(action, "description"), () => SelectComposerTool(S(action, "actionId"), S(action, "displayName")), S(action, "disabledReason").Length == 0, ToolGlyphColor(S(action, "iconKey"), S(action, "actionId")));
        foreach (var action in Items(_snapshot, "actionDescriptors").Where(a =>
                     S(a, "actionKind") == "immediate"
                     && S(a, "actionId") != BuiltInActionIds.AttachFilesAndFolders
                     && (!S(a, "actionId").StartsWith("builtin.", StringComparison.Ordinal)
                         || S(a, "actionId") is BuiltInActionIds.ExportChatPdf or BuiltInActionIds.LiveCaptions)))
            Row(S(action, "iconKey") switch { "pdf" => "\uEA90", "captions" => "\uE8F2", _ => "\uEA86" }, S(action, "displayName"), S(action, "description"), () => _ = InvokeImmediateActionAsync(S(action, "actionId")), S(action, "disabledReason").Length == 0, ToolGlyphColor(S(action, "iconKey"), S(action, "actionId")));
        menu.Content = new ScrollViewer { Content = rows, MaxHeight = 308, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        menu.ShowAt(ComposerSurface);
    }

    private void ShowFileChoices()
    {
        var menu = new MenuFlyout();
        var files = new MenuFlyoutItem { Text = "Dateien hinzufügen", Icon = new FontIcon { Glyph = "\uE8A5" } };
        files.Click += OnAttach;
        var folder = new MenuFlyoutItem { Text = "Projektordner auswählen", Icon = new FontIcon { Glyph = "\uE8B7" } };
        folder.Click += OnAddProject;
        menu.Items.Add(files); menu.Items.Add(folder); menu.ShowAt(ComposerSurface);
    }

    private async void AttachWorkspaceContext()
    {
        var workspace = S(_snapshot, "workspacePath");
        if (!Directory.Exists(workspace)) { OnAddProject(this, new RoutedEventArgs()); return; }
        try
        {
            var names = Directory.EnumerateFileSystemEntries(workspace).Select(Path.GetFileName).Take(100);
            var context = $"Projekt: {Path.GetFileName(workspace)}\nPfad: {workspace}\nEinträge im Projektordner (ohne Dateiinhalte):\n" + string.Join('\n', names);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(context));
            await _coordinator.ImportDocumentAsync(_session, "Projektkontext.txt", stream, _lifetime.Token);
            await SetInspectorVisibleAsync(true); await RefreshForExternalActivationAsync();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private static Color ToolGlyphColor(string iconKey, string actionId) => iconKey switch
    {
        "web" => Color.FromArgb(255, 76, 148, 242),
        "research" => Color.FromArgb(255, 160, 124, 246),
        "image" => Color.FromArgb(255, 231, 104, 171),
        "audio" => Color.FromArgb(255, 70, 188, 133),
        "speech" or "captions" => Color.FromArgb(255, 51, 184, 207),
        "pdf" => Color.FromArgb(255, 218, 80, 82),
        _ when actionId.Contains("audiobook", StringComparison.Ordinal) => Color.FromArgb(255, 241, 157, 56),
        _ => Color.FromArgb(255, 151, 116, 225),
    };

    private async void AttachSketch()
    {
        var sessionId = _session;
        if (sessionId == Guid.Empty) return;
        using var editor = new NativeSketchEditor();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Skizze zeichnen",
            Content = editor.View,
            PrimaryButtonText = "An Chat anhängen",
            CloseButtonText = "Abbrechen",
            IsPrimaryButtonEnabled = false,
            DefaultButton = ContentDialogButton.Primary,
        };
        editor.Changed += (_, _) => dialog.IsPrimaryButtonEnabled = editor.HasStrokes;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || !editor.HasStrokes) return;
            var path = await editor.SaveAsync(_lifetime.Token);
            await using var stream = File.OpenRead(path);
            await _coordinator.ImportAttachmentAsync(sessionId, Path.GetFileName(path), "image/png", stream, _lifetime.Token);
            if (_session == sessionId)
            {
                await SetInspectorVisibleAsync(true);
                await RefreshForExternalActivationAsync();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowError(exception.Message);
        }
    }

}
