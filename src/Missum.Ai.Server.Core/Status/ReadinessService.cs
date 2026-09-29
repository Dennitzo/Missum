using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Configuration;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runtime;
using Microsoft.Extensions.Options;

namespace Missum.Ai.Server.Core.Status;

public sealed class ReadinessService
{
    private readonly MissumAiServerOptions _options;
    private readonly ModelRuntimeClient _modelRuntime;
    private readonly ServerRuntimeState _runtime;

    public ReadinessService(
        IOptions<MissumAiServerOptions> options,
        ModelRuntimeClient modelRuntime,
        ServerRuntimeState runtime)
    {
        _options = options.Value;
        _modelRuntime = modelRuntime;
        _runtime = runtime;
    }

    public async Task<HealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var modelStatus = await _modelRuntime.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return await GetSnapshotAsync(modelStatus, cancellationToken).ConfigureAwait(false);
    }

    public Task<HealthSnapshot> GetSnapshotAsync(
        ModelStatusSnapshot modelStatus,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelStatus);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateSnapshot(modelStatus));
    }

    private HealthSnapshot CreateSnapshot(ModelStatusSnapshot modelStatus)
    {
        if (!modelStatus.ProviderReachable)
        {
            return NotReady(
                $"native llama ist über {_options.ModelRuntimeUri} nicht erreichbar.",
                "Den nativen Missum-Llama-Prozess starten und Port 8081 für das Docker-Gateway freigeben.");
        }

        if (!modelStatus.Models.Any(static model => model.Role == "general" && model.Downloaded))
            return NotReady("Kein lokales Sprachmodell verfügbar.", "Unsloth-Modellordner und nativen llama-Katalog prüfen.");

        var selectableModels = modelStatus.Models
            .Where(static model => model.Role == "general")
            .ToArray();
        var loading = selectableModels.FirstOrDefault(model =>
            !model.Loaded && string.Equals(model.State, "loading", StringComparison.OrdinalIgnoreCase));
        if (loading is not null)
        {
            _runtime.SetGatewayState("Modell wird geladen", loading.Id);
            return new HealthSnapshot("modelLoading", MissumAiProtocol.Version, DateTimeOffset.UtcNow, loading.Id);
        }
        if (!selectableModels.Any(static model => model.Loaded))
        {
            _runtime.SetGatewayState("Bereit", "Gateway bereit; Modell wird beim ersten AI-Lauf geladen.");
            return new HealthSnapshot(
                "modelNotLoaded",
                MissumAiProtocol.Version,
                DateTimeOffset.UtcNow,
                "Kein General-Modell ist geladen.");
        }
        _runtime.SetGatewayState("Bereit", "Gateway und Modellruntime sind bereit.");
        return new HealthSnapshot("ready", MissumAiProtocol.Version, DateTimeOffset.UtcNow);
    }

    private HealthSnapshot NotReady(string reason, string repair)
    {
        _runtime.SetGatewayState("Nicht bereit", reason);
        return new HealthSnapshot("notReady", MissumAiProtocol.Version, DateTimeOffset.UtcNow, reason, repair);
    }
}
