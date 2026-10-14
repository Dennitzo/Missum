using System.Text.Json;
using Missum.Core.Research;

namespace Missum.App.Services;

public sealed partial class MissumAiAssistantService
{
    private async Task PersistScientificImageUploadAsync(Guid sessionId, JsonElement result, CancellationToken token)
    {
        if (scientificResearch is not IScientificResearchStateRepository states) return;
        var projectId = "research-" + sessionId.ToString("N");
        var project = await scientificResearch.GetProjectAsync(projectId, token).ConfigureAwait(false);
        if (project?.WorkspacePath is null) return;
        var binding = ScientificVisualInspection.UploadBinding(projectId, project.WorkspacePath, result, DateTimeOffset.UtcNow);
        if (binding is not null) await states.SaveExecutionVerificationAsync(binding, token).ConfigureAwait(false);
    }

    private async Task PersistScientificVisionReviewAsync(Guid sessionId, JsonElement result, CancellationToken token)
    {
        if (scientificResearch is not IScientificResearchStateRepository states) return;
        var projectId = "research-" + sessionId.ToString("N");
        if (await scientificResearch.GetProjectAsync(projectId, token).ConfigureAwait(false) is null) return;
        var bindings = await states.LoadExecutionVerificationsAsync(projectId, token).ConfigureAwait(false);
        var review = ScientificVisualInspection.Review(projectId, bindings, result, DateTimeOffset.UtcNow);
        if (review is not null) await states.SaveExecutionVerificationAsync(review, token).ConfigureAwait(false);
    }
}
