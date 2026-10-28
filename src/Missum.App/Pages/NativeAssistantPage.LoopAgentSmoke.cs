using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Core.Extensions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifySpecializedAgentChipSmokeAsync()
    {
        if (!IsMessageFooterSmoke || Environment.GetEnvironmentVariable("ASSISTANT_DISABLE_RUNTIME_AUTOSTART") != "1")
            throw new InvalidOperationException("Die Loop-Agent-Prüfung benötigt ein isoliertes Profil ohne Modellstart.");
        var owner = _session;
        var mode = _mode;
        var selectedAction = _selectedAction;
        var instruction = _composerInstruction;
        var persistent = _persistentAction;
        var running = _running;
        var selection = _specializedAgentSelections.ToArray();
        Flyout? menu = null;
        try
        {
            _session = Guid.NewGuid(); _mode = "claudescience"; _running = false;
            ObserveSpecializedAgentSelection(JsonSerializer.SerializeToElement(new { specializedAgentProfile = SpecializedAgentProfiles.Loop }));
            _selectedAction = BuiltInActionIds.PlanMode;
            SelectedToolChip.SetTool("Planen", "\uEA80", Missum.App.Controls.NativeIconPalette.BrushFor("plan"));
            SelectedToolChip.Visibility = Visibility.Visible;
            UpdateLayout();
            if (SpecializedAgentChip.Visibility != Visibility.Visible || SelectedToolChip.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Loop-Agent muss als eigener Chip mit anderen Werkzeugen kombinierbar sein.");
            const string originalGoal = "Verallgemeinere die Gleichung \\Box \\psi = 0.\nPrüfe alle offenen Etappen.";
            var request = CaptureSpecializedAgentSelection(originalGoal);
            if (request?.ProfileId != SpecializedAgentProfiles.Loop || request.Goal != originalGoal || _selectedAction != BuiltInActionIds.PlanMode)
                throw new InvalidOperationException("Das Loop-Profil verändert den Prompt oder die bestehende Werkzeugauswahl.");
            menu = ShowComposerTools();
            await WaitForNativePreviewFrameAsync();
            if (menu.Content is not FrameworkElement content)
                throw new InvalidOperationException("Das native Plus-Menü besitzt keinen Inhalt.");
            await WaitForPagingSmokeAsync(() => Descendants(content).OfType<Button>()
                .Count(button => AutomationProperties.GetName(button) == "Loop-Agent") == 1,
                "Das native Plus-Menü enthält Loop-Agent nicht genau einmal.");
            menu.Hide();
            ClearToolSelection();
            if (SpecializedAgentChip.Visibility != Visibility.Visible || CaptureSpecializedAgentSelection(originalGoal) is null)
                throw new InvalidOperationException("Das Abwählen eines anderen Werkzeugs entfernt den unabhängigen Loop-Chip.");
            var selectedSession = _session;
            _session = Guid.NewGuid(); RefreshSpecializedAgentChip();
            if (SpecializedAgentChip.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Die Agentenauswahl ist in eine fremde Sitzung übergelaufen.");
            _session = selectedSession; RefreshSpecializedAgentChip();
            if (SpecializedAgentChip.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Die Agentenauswahl ging beim Sitzungswechsel verloren.");
            _mode = "general"; RefreshSpecializedAgentChip();
            if (CaptureSpecializedAgentSelection(originalGoal) is not null || SpecializedAgentChip.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Loop-Agent ist außerhalb von Codex/Claude Science aktiv.");
            _mode = "claudescience";
            ObserveSpecializedAgentSelection(JsonSerializer.SerializeToElement(new { specializedAgentProfile = (string?)null }));
            if (SpecializedAgentChip.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Die gespeicherte Abwahl wurde nicht angezeigt.");
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-loop-agent-chip-validation.json"),
                JsonSerializer.Serialize(new { passed = true, independentChip = true, otherToolPreserved = true,
                    originalGoalPreserved = true, sessionScoped = true, supportedModes = true, menuEntry = true,
                    modelRequests = 0, userDataModified = false }), _lifetime.Token);
        }
        finally
        {
            menu?.Hide();
            _session = owner; _mode = mode; _running = running; _selectedAction = selectedAction; _composerInstruction = instruction; _persistentAction = persistent;
            _specializedAgentSelections.Clear();
            foreach (var pair in selection) _specializedAgentSelections[pair.Key] = pair.Value;
            SelectedToolChip.Visibility = selectedAction is null ? Visibility.Collapsed : Visibility.Visible;
            if (selectedAction is not null)
            {
                var action = Items(_snapshot, "actionDescriptors").FirstOrDefault(item => S(item, "actionId") == selectedAction);
                UpdateSelectedToolChip(S(action, "displayName", selectedAction));
            }
            RefreshSpecializedAgentChip();
        }
    }
}
