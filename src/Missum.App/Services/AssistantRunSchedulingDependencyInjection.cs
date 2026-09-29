using Missum.Core.Chat;
using Microsoft.Extensions.DependencyInjection;

namespace Missum.App.Services;

public static class AssistantRunSchedulingDependencyInjection
{
    /// <summary>
    /// Registers one serial model execution lane for the local assistant.
    /// </summary>
    public static IServiceCollection AddAssistantRunScheduling(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IAssistantRunScheduler, ProfileAssistantRunScheduler>();
        return services;
    }
}
