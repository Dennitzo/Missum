using System.Text.Json;
using Missum.App.Services;
using Microsoft.UI.Xaml;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private DateTimeOffset _workingPlanUpdatedAt;

    private void ObserveWorkingPlan(JsonElement data, bool sessionChanged = false)
    {
        if (sessionChanged)
        {
            _workingPlanUpdatedAt = default;
            WorkingPlanView.Update(new([], []));
            WorkingPlanView.SetExpanded(true);
        }
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("workingPlan", out var plan))
        {
            var updated = DateTimeOffset.TryParse(S(plan, "updatedAt"), out var time) ? time : default;
            // An asynchronous durable read can finish after a newer live receipt.
            if (sessionChanged || updated >= _workingPlanUpdatedAt)
            {
                _workingPlanUpdatedAt = updated;
                WorkingPlanView.Update(NativeWorkingPlanProjection.Read(plan));
            }
        }
        RefreshWorkingPlanVisibility();
    }

    private void RefreshWorkingPlanVisibility()
    {
        WorkingPlanView.Visibility = _settings.Current.ShowWorkingPlan && WorkingPlanView.StepCount > 0
            && ActiveSubagent is null && BodyGrid.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        WorkingPlanView.SetMaximumBodyHeight(BodyGrid.ActualHeight);
    }

    private void OnWorkingPlanSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Current.ShowWorkingPlan == e.Previous.ShowWorkingPlan) return;
        DispatcherQueue.TryEnqueue(() => RunUiCallback("WorkingPlan.SettingsChanged", RefreshWorkingPlanVisibility));
    }
}
