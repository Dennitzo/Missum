using Missum.Core.Chat;
using Missum.Core.Contracts;
using Missum.Infrastructure.Backup;
using Missum.Infrastructure.Documents;
using Missum.Infrastructure.Logging;
using Missum.Infrastructure.Projects;
using Missum.Infrastructure.Repositories;
using Missum.Infrastructure.Research;
using Missum.Infrastructure.Settings;
using Missum.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Missum.Core.Research;

namespace Missum.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddMissumInfrastructure(this IServiceCollection services, Action<MissumInfrastructureOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddLogging();
        var options = new MissumInfrastructureOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<SqliteDatabase>();
        services.AddSingleton<IMissumDatabase>(static provider => provider.GetRequiredService<SqliteDatabase>());
        services.AddSingleton<IChatRepository, SqliteChatRepository>();
        services.AddSingleton<IConversationSnapshotRepository, SqliteConversationSnapshotRepository>();
        services.AddSingleton<IPromptTriggerRepository, SqlitePromptTriggerRepository>();
        services.AddSingleton<IAssistantAttachmentRepository, SqliteAssistantAttachmentRepository>();
        services.AddSingleton<IChatArtifactRepository, SqliteChatArtifactRepository>();
        services.AddSingleton<IGeneratedDocumentRepository, SqliteGeneratedDocumentRepository>();
        services.AddSingleton<IMissumAiRunRepository, SqliteMissumAiRunRepository>();
        services.AddSingleton<IClientToolExecutionRepository, SqliteClientToolExecutionRepository>();
        services.AddSingleton<IScientificResearchRepository, SqliteScientificResearchRepository>();
        services.AddSingleton<IScientificResearchExportService, ScientificResearchExportService>();
        services.AddSingleton<IProjectRepository, SqliteProjectRepository>();
        services.AddSingleton<IProjectAssetWorkingCopyService, ProjectAssetWorkingCopyService>();
        services.AddSingleton<IBinaryObjectStore, SqliteBinaryObjectStore>();
        services.AddSingleton<IDocumentIngestor, DocumentIngestor>();
        services.AddSingleton<IDocumentFileCodec, DocumentFileCodec>();
        services.AddSingleton<IContextAssembler, ContextAssembler>();
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<IBackupService, ZipBackupService>();
        services.AddSingleton<RingBufferLoggerProvider>();
        services.AddSingleton<ISessionLog>(static provider => provider.GetRequiredService<RingBufferLoggerProvider>());
        services.AddSingleton<ILoggerProvider>(static provider => provider.GetRequiredService<RingBufferLoggerProvider>());
        return services;
    }
}
