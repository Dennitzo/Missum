using Missum.Core.Models;

namespace Missum.App.Services;

internal static class ModelContextSelection
{
    internal static int Resolve(AppSettings settings, string modelId, int maximum) =>
        settings.ContextLimitsByModel.TryGetValue(modelId, out var selected)
            ? Math.Clamp(selected, 2048, Math.Max(2048, maximum)) : Math.Max(2048, maximum);
}
