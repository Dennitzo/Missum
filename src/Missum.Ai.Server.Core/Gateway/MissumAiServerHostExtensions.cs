using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Audio;
using Missum.Ai.Server.Core.Configuration;
using Missum.Ai.Server.Core.Data;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Research;
using Missum.Ai.Server.Core.Runs;
using Missum.Ai.Server.Core.Runtime;
using Missum.Ai.Server.Core.Security;
using Missum.Ai.Server.Core.Status;
using Missum.Ai.Server.Core.Storage;
using Missum.Ai.Server.Core.Workers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Net;

namespace Missum.Ai.Server.Core.Gateway;

public static class MissumAiServerHostExtensions
{
    public static IServiceCollection AddMissumAiServerServices(
        this IServiceCollection services,
        MissumAiServerOptions options,
        bool includeHostedServices)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton<IOptions<MissumAiServerOptions>>(Options.Create(options));
        services.AddSingleton<ServerRuntimeState>();
        services.AddSingleton<MissumAiDatabase>();
        services.AddSingleton<RunEventNotifier>();
        services.AddSingleton<RunRepository>();
        services.AddSingleton<RunWorkChannel>();
        services.AddSingleton<ModelRouter>();
        services.AddSingleton<AgentToolCatalog>();
        services.AddSingleton<AgentToolExecutor>();
        services.AddSingleton<GpuLeaseScheduler>();
        services.AddSingleton<ServiceActivityTracker>();
        services.AddSingleton<GpuStatusService>();
        services.AddSingleton<UploadService>();
        services.AddSingleton<ArtifactService>();
        services.AddSingleton<CapabilityService>();
        services.AddSingleton<ReadinessService>();
        services.AddSingleton<ServiceProbeService>();
        services.AddSingleton<ServerMetricsService>();
        services.AddSingleton<WebResearchService>();
        services.AddSingleton<ScientificMetadataService>();
        services.AddSingleton<UtteranceIntentService>();
        services.AddSingleton<WorkerOrchestrator>();
        services.AddSingleton<LiveCaptionService>();
        services.AddHttpClient();
        services.AddSingleton(static provider => new ModelRuntimeClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ModelRuntimeClient)),
            provider.GetRequiredService<IOptions<MissumAiServerOptions>>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ModelRuntimeClient>>()));
        services.AddHttpClient<WorkerApiClient>();
        services.AddSingleton<RunProcessor>();
        services.AddSingleton<ClientToolDeadlineService>();
        if (includeHostedServices)
        {
            services.AddHostedService<ServerInitializationService>();
            services.AddHostedService<SharedModelWarmupService>();
            services.AddHostedService(static provider => provider.GetRequiredService<RunProcessor>());
            services.AddHostedService(static provider => provider.GetRequiredService<ClientToolDeadlineService>());
            services.AddHostedService<StorageCleanupService>();
            services.AddHostedService(static provider => provider.GetRequiredService<LiveCaptionService>());
        }

        return services;
    }

    public static IHostBuilder ConfigureMissumAiServer(
        this IHostBuilder builder,
        Action<MissumAiServerOptions>? configure = null,
        bool includeHostedServices = true,
        IPAddress? listenAddress = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new MissumAiServerOptions();
        configure?.Invoke(options);

        builder.ConfigureServices(services => services.AddMissumAiServerServices(options, includeHostedServices));

        builder.ConfigureWebHostDefaults(webBuilder =>
        {
            webBuilder.SuppressStatusMessages(true);
            webBuilder.UseKestrel(kestrel =>
            {
                if (listenAddress is null)
                    kestrel.ListenAnyIP(options.GatewayPort, listen => listen.Protocols = HttpProtocols.Http1);
                else
                    kestrel.Listen(listenAddress, options.GatewayPort, listen => listen.Protocols = HttpProtocols.Http1);
                kestrel.Limits.MaxRequestBodySize = MissumAiProtocol.UploadChunkSize + (128 * 1024);
                kestrel.AddServerHeader = false;
            });
            webBuilder.Configure(application =>
            {
                application.UseMiddleware<ProblemDetailsMiddleware>();
                application.UseRouting();
                application.UseMiddleware<ApiRateLimitMiddleware>();
                application.UseEndpoints(GatewayEndpoints.Map);
            });
        });

        return builder;
    }
}
