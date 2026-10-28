using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.App.Controls;
using Microsoft.UI.Xaml;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private readonly Dictionary<Guid, string> _specializedAgentSelections = [];
    private bool _specializedAgentSelectionBusy;

    private SpecializedAgentProfileDescriptor? SelectedSpecializedAgent => _session != Guid.Empty && (_mode is "coding" or "claudescience")
        ? SpecializedAgentProfiles.BuiltIn.FirstOrDefault(profile => profile.Id == _specializedAgentSelections.GetValueOrDefault(_session)) : null;

    private SpecializedAgentOptions? CaptureSpecializedAgentSelection(string originalPrompt) => SelectedSpecializedAgent is { } profile
        ? new(profile.Id, originalPrompt) : null;

    private void ObserveSpecializedAgentSelection(JsonElement snapshot)
    {
        if (snapshot.TryGetProperty("specializedAgentProfile", out var selected))
        {
            var profile = selected.ValueKind == JsonValueKind.String ? selected.GetString() : null;
            if (SpecializedAgentProfiles.BuiltIn.Any(item => item.Id == profile)) _specializedAgentSelections[_session] = profile!;
            else _specializedAgentSelections.Remove(_session);
        }
        RefreshSpecializedAgentChip();
    }

    private void RefreshSpecializedAgentChip()
    {
        var profile = SelectedSpecializedAgent;
        SpecializedAgentChip.Visibility = profile is not null ? Visibility.Visible : Visibility.Collapsed;
        if (profile is null) return;
        SpecializedAgentChip.SetTool(profile.DisplayName, "\uE8EE", NativeIconPalette.BrushFor("research"));
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(SpecializedAgentChip,
            _running ? profile.DisplayName + " aktiv. Entfernen stoppt den gesamten Auftrag." : profile.Description);
    }

    private async void SelectSpecializedAgent(string profileId)
    {
        if (_specializedAgentSelectionBusy || _disposed || _session == Guid.Empty || _mode is not ("coding" or "claudescience")) return;
        if (!SpecializedAgentProfiles.BuiltIn.Any(profile => profile.Id == profileId)) return;
        if (SelectedSpecializedAgent?.Id == profileId) { OnClearSpecializedAgent(this, new RoutedEventArgs()); return; }
        if (_running) return;
        var owner = _session;
        _specializedAgentSelectionBusy = true;
        try
        {
            if (await CommandAsync("session.agentProfile", new { sessionId = owner, profile = profileId, enabled = true })
                && owner == _session && !_disposed)
            {
                _specializedAgentSelections[owner] = profileId;
                RefreshSpecializedAgentChip();
                Composer.Focus(FocusState.Programmatic);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowError(exception.Message); }
        finally { _specializedAgentSelectionBusy = false; }
    }

    private async void OnClearSpecializedAgent(object sender, RoutedEventArgs e)
    {
        if (_specializedAgentSelectionBusy || _disposed || SelectedSpecializedAgent is not { } profile) return;
        var owner = _session;
        _specializedAgentSelectionBusy = true;
        SpecializedAgentChip.IsEnabled = false;
        try
        {
            if (_running && !await CommandAsync("chat.cancel", new { sessionId = owner })) return;
            if (await CommandAsync("session.agentProfile", new { sessionId = owner, profile = profile.Id, enabled = false }))
                _specializedAgentSelections.Remove(owner);
            if (owner == _session && !_disposed) RefreshSpecializedAgentChip();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowError(exception.Message); }
        finally { _specializedAgentSelectionBusy = false; SpecializedAgentChip.IsEnabled = true; }
    }
}
