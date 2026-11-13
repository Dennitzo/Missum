using System.Text;
using System.Text.Json;
using Missum.App.Controls;
using Missum.Core.Extensions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private string? _composerInstruction;
    private bool _composerToolSelectionBusy;

    private void OnModeMenu(object sender, RoutedEventArgs e)
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.BackgroundProperty, ThemeBrush("MissumLayerStrongBrush", 43)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(18)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, ThemeBrush("MissumStrokeBrush", 49)));
        flyout.FlyoutPresenterStyle = style;
        var panel = new StackPanel { Width = 232, Spacing = 2 };
        foreach (var (mode, title, description) in new[] { ("general", "ChatGPT", "Erstellen, lernen und erkunden"), ("coding", "Codex", "Erstellen, debuggen und ausliefern"), ("claudescience", "Claude Science", "Deep Research, Analysen und Nachweise") })
        {
            var row = new Grid();
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = description, FontSize = 13, Foreground = ThemeBrush("MissumMutedTextBrush", 160) });
            row.Children.Add(text);
            if (_mode == mode) row.Children.Add(new FontIcon { Glyph = "\uE73E", FontSize = 14, Foreground = NativeIconPalette.BrushFor("success"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 5, 0, 0) });
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

    private string _lastToolChipLabel = "";

    private void LayoutComposerChips(double footerWidth)
    {
        // Long model names leave too little room for a usable chip in a narrow composer.
        var useSeparateRow = footerWidth < 545;
        Grid.SetRow(ComposerChipsScroll, useSeparateRow ? 0 : 1);
        Grid.SetColumn(ComposerChipsScroll, useSeparateRow ? 0 : 1);
        Grid.SetColumnSpan(ComposerChipsScroll, useSeparateRow ? 5 : 1);
    }

    private void UpdateSelectedToolChip(string label)
    {
        _lastToolChipLabel = label;
        var action = Items(_snapshot, "actionDescriptors").FirstOrDefault(a => S(a, "actionId") == _selectedAction);
        var glyph = ToolIconGlyph(S(action, "iconKey"));
        var text = _selectedAction == BuiltInActionIds.PlanMode ? "Planen" : label;
        var blenderSelected = _selectedAction == "com.missum.blender/edit";
        if (blenderSelected) text = "Blender";
        var integration = blenderSelected ? null : Services.ProgramIntegrationStatusReader.Find(_selectedAction);
        var integrationSelected = !blenderSelected && (integration is not null
            || S(action, "groupId") == BuiltInExtensionCatalog.GroupIntegrations);
        if (integrationSelected) text = $"{text} · {integration?.ChipSuffix ?? "nicht verbunden"}";
        SelectedToolChip.SetTool(text, glyph, ComposerGroupIconBrush(S(action, "groupId", BuiltInExtensionCatalog.GroupIntegrations)));
        SelectedToolChip.Visibility = Visibility.Visible;
        KeepIntegrationChipFresh(integrationSelected);
    }

    private SolidColorBrush ComposerGroupIconBrush(string groupId)
    {
        var level = groupId switch
        {
            BuiltInExtensionCatalog.GroupFiles => 0,
            BuiltInExtensionCatalog.GroupPlanning => 1,
            BuiltInExtensionCatalog.GroupResearch => 2,
            BuiltInExtensionCatalog.GroupVisual => 3,
            BuiltInExtensionCatalog.GroupSpeech => 4,
            _ => 5,
        };
        var surface = ThemeBrush("MissumLayerStrongBrush", 44).Color;
        var accent = App.ReadableAccentColor(ThemeBrush("MissumAccentBrush", 160).Color,
            [surface, ThemeBrush("MissumInputBrush", 44).Color]);
        var endpoint = ActualTheme == ElementTheme.Light ? 0 : 255;
        var fraction = level * 0.085;
        byte Adjust(byte channel) => (byte)Math.Round(channel + (endpoint - channel) * fraction);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, Adjust(accent.R), Adjust(accent.G), Adjust(accent.B)));
    }

    private Microsoft.UI.Xaml.DispatcherTimer? _integrationChipTimer;

    /// <summary>
    /// An integration chip is live data: the scene name changes when the user saves or opens another
    /// file, and an open visual review has to disappear once the picture was analysed. The composer is
    /// otherwise only redrawn on user input, so while an integration is chosen the chip re-reads its
    /// status on its own. It stops by itself as soon as no integration is selected any more.
    /// </summary>
    private void KeepIntegrationChipFresh(bool integrationSelected)
    {
        if (!integrationSelected)
        {
            _integrationChipTimer?.Stop();
            return;
        }

        _integrationChipTimer ??= CreateIntegrationChipTimer();
        if (!_integrationChipTimer.IsEnabled) _integrationChipTimer.Start();
    }

    private Microsoft.UI.Xaml.DispatcherTimer CreateIntegrationChipTimer()
    {
        var timer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            if (_disposed || _selectedAction is null)
            {
                timer.Stop();
                return;
            }

            UpdateSelectedToolChip(_lastToolChipLabel);
        };
        return timer;
    }

    private Flyout ShowComposerTools()
    {
        var width = Math.Max(280, ComposerSurface.ActualWidth);
        var menu = new Flyout { Placement = FlyoutPlacementMode.TopEdgeAlignedLeft, AreOpenCloseAnimationsEnabled = true };
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.BackgroundProperty, ThemeBrush("MissumLayerStrongBrush", 44)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, ThemeBrush("MissumStrokeBrush", 61)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(20)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 5, 4, 5)));
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, width));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, width));
        style.Setters.Add(new Setter(FrameworkElement.WidthProperty, width));
        menu.FlyoutPresenterStyle = style;
        var rows = new StackPanel { Spacing = 0, Width = width - 10 };
        // Keep text neutral. Use one shade of the native accent for each group of icons.
        var menuForeground = ThemeBrush("MissumTextBrush", 220);
        var entries = new List<ComposerToolEntry>();

        static int IntField(JsonElement value, string name, int fallback) =>
            value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field)
            && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var parsed) ? parsed : fallback;

        // Group headings come from the action catalog, so a package can name its own group.
        // The fallbacks keep the two composer-owned rows (attachments, compaction) in the right group.
        var groupMeta = new Dictionary<string, (int Order, string Label)>(StringComparer.Ordinal);
        foreach (var group in Items(_snapshot, "actionGroups"))
        {
            var declaredId = S(group, "groupId");
            if (declaredId.Length > 0) groupMeta[declaredId] = (IntField(group, "groupOrder", 9_000), S(group, "label", declaredId));
        }
        (int Order, string Label) GroupFor(string groupId, int fallbackOrder, string fallbackLabel) =>
            groupMeta.TryGetValue(groupId, out var known) ? known : (fallbackOrder, fallbackLabel);

        void AddEntry(string groupId, int fallbackGroupOrder, string fallbackGroupLabel, int itemOrder,
            string glyph, string name, string description, Action action, string iconKey,
            string actionId = "", bool enabled = true)
        {
            var group = GroupFor(groupId, fallbackGroupOrder, fallbackGroupLabel);
            var content = new Grid { ColumnSpacing = 8 };
            content.ColumnDefinitions.Add(new() { Width = new(16) });
            content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 15, Foreground = ComposerGroupIconBrush(groupId), VerticalAlignment = VerticalAlignment.Center });
            var label = new TextBlock { Text = name, FontSize = 14, Foreground = menuForeground, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1); content.Children.Add(label);
            var detail = new TextBlock { Text = description, FontSize = 14, Foreground = menuForeground, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(detail, 2); content.Children.Add(detail);
            var button = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new(8, 5, 8, 5), MinHeight = 28, CornerRadius = new(15), IsEnabled = enabled, BorderThickness = new(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
            ToolTipService.SetToolTip(button, description);
            button.Click += (_, _) => { menu.Hide(); action(); };
            _ = iconKey; _ = actionId;
            entries.Add(new ComposerToolEntry(group.Order, groupId, group.Label, itemOrder, button));
        }

        void AddCatalogEntry(JsonElement descriptor, Func<string, Action> invoke, int fallbackItemOrder = 100)
        {
            var actionId = S(descriptor, "actionId");
            // Program integrations report their live connection, so the note tells the user which
            // scene is open or that the editor is still missing. Same text color as every note.
            var note = Services.ProgramIntegrationStatusReader.Find(actionId)?.MenuNote;
            var description = note is null
                ? S(descriptor, "description")
                : $"{S(descriptor, "description")} · {note}";
            AddEntry(S(descriptor, "groupId", BuiltInExtensionCatalog.GroupIntegrations),
                IntField(descriptor, "groupOrder", 60), "Erweiterungen",
                IntField(descriptor, "itemOrder", fallbackItemOrder),
                ToolIconGlyph(S(descriptor, "iconKey")), S(descriptor, "displayName"), description,
                invoke(actionId), S(descriptor, "iconKey"), actionId, S(descriptor, "disabledReason").Length == 0);
        }

        var descriptors = Items(_snapshot, "actionDescriptors");
        var actions = descriptors.Where(a => S(a, "actionKind") == "selectableTool").ToArray();
        var document = actions.FirstOrDefault(a => S(a, "iconKey") == "document");
        var plan = actions.FirstOrDefault(a => S(a, "iconKey") == "plan");
        var attachGroup = S(descriptors.FirstOrDefault(a => S(a, "actionId") == BuiltInActionIds.AttachFilesAndFolders), "groupId", "files");
        var planningGroup = S(plan, "groupId", "planning");

        AddEntry(attachGroup, 10, "Dateien und Dokumente", 10,
            "\uE723", "Dateien und Ordner", "", () => ShowFileChoices(), "attachment");
        if (document.ValueKind == JsonValueKind.Object)
            AddCatalogEntry(document, id => () => SelectComposerTool(id, "Dokumente erstellen"), 20);
        AddEntry(planningGroup, 20, "Planung und Kontext", 20,
            "\uE8EF", "Kontext komprimieren", _running ? "Nach dem Beenden des AI-Laufs verfügbar" : "Aktuelle Sitzung verdichten; Chatverlauf behalten",
            () => _ = CompactCurrentSessionAsync(), "research", "context.compact",
            !_running && !_manualCompactionBusy && Items(_snapshot, "messages").Length > 0);
        if (plan.ValueKind == JsonValueKind.Object)
            AddCatalogEntry(plan, id => () => SelectComposerTool(id, "Planmodus"), 10);
        foreach (var action in actions.Where(a => !S(a, "actionId").StartsWith("builtin.", StringComparison.Ordinal)
                     || (S(a, "actionId") != BuiltInActionIds.DeepResearch && S(a, "iconKey") is not ("plan" or "document"))))
            AddCatalogEntry(action, id => () => SelectComposerTool(id, S(action, "displayName")));
        foreach (var action in descriptors.Where(a =>
                     S(a, "actionKind") == "immediate"
                     && S(a, "actionId") != BuiltInActionIds.AttachFilesAndFolders
                     && (!S(a, "actionId").StartsWith("builtin.", StringComparison.Ordinal)
                         || S(a, "actionId") is BuiltInActionIds.ExportChatPdf or BuiltInActionIds.LiveCaptions)))
            AddCatalogEntry(action, id => () => _ = InvokeImmediateActionAsync(id));

        // A group heading appears only when the current chat mode actually offers an entry for it.
        foreach (var group in entries
                     .GroupBy(static entry => entry.GroupId, StringComparer.Ordinal)
                     .OrderBy(static group => group.Min(static entry => entry.GroupOrder))
                     .ThenBy(static group => group.Key, StringComparer.Ordinal))
        {
            rows.Children.Add(new TextBlock
            {
                Text = group.First().GroupLabel,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = menuForeground,
                Margin = new(10, 7, 10, 3),
            });
            foreach (var entry in group.OrderBy(static item => item.ItemOrder))
                rows.Children.Add(entry.Button);
        }

        menu.Content = new ScrollViewer { Content = rows, MaxHeight = 344, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        menu.ShowAt(ComposerSurface);
        return menu;
    }

    /// <summary>One tool row with the catalog data that places it in a purpose group.</summary>
    private sealed record ComposerToolEntry(
        int GroupOrder,
        string GroupId,
        string GroupLabel,
        int ItemOrder,
        Button Button);

    private bool _manualCompactionBusy;

    private async Task CompactCurrentSessionAsync()
    {
        if (_manualCompactionBusy || _running || _disposed) return;
        var sessionId = _session;
        _manualCompactionBusy = true;
        try { await CommandAsync("chat.compact", new { sessionId }); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowError(exception.Message); }
        finally { _manualCompactionBusy = false; }
    }

    private void ShowFileChoices()
    {
        var menu = new MenuFlyout();
        var menuForeground = ThemeBrush("MissumTextBrush", 220);
        var files = new MenuFlyoutItem { Text = "Dateien hinzufügen", Foreground = menuForeground, Icon = new FontIcon { Glyph = "\uE8A5", Foreground = menuForeground } };
        files.Click += OnAttach;
        var folder = new MenuFlyoutItem { Text = "Projektordner auswählen", Foreground = menuForeground, Icon = new FontIcon { Glyph = "\uE8B7", Foreground = menuForeground } };
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
