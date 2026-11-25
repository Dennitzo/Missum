namespace Missum.App.Services.ModelHub;

public enum ModelFitLevel
{
    /// <summary>Fits completely into one GPU's memory.</summary>
    SingleGpu,
    /// <summary>Fits completely into VRAM when spread across several GPUs (layer split).</summary>
    MultiGpu,
    /// <summary>Runs with part of the layers in system RAM.</summary>
    Offload,
    /// <summary>Does not fit even with VRAM and RAM together.</summary>
    Insufficient,
}

public sealed record ModelFit(ModelFitLevel Level, long NeededBytes, long VramBytes, long RamPartBytes, int GpuCount, string Label, string Detail);

/// <summary>
/// Estimates whether a quantization runs on this PC, in the spirit of the native runtime's
/// placement (catalog.py estimate_replica_bytes / choose_single_gpu): the weights plus about 5 %
/// overhead, the q8_0 KV cache for a 32k context and a reserve per GPU. Totals, not free memory,
/// are compared, because the runtime unloads other models before it loads one.
/// </summary>
public static class ModelFitEstimator
{
    public const int DefaultContext = 32_768;
    private const long Gib = 1L << 30;
    internal const long GpuReserve = 2 * Gib;
    internal const long SystemReserve = 8 * Gib;
    private const double UsableRamShare = .85;

    /// <summary>Memory a quantization needs at the default context.</summary>
    public static long Needed(HubQuantization quantization, long? parameters)
    {
        var weights = (long)(quantization.TotalBytes * 1.05);
        // Without layer metadata before the download, the q8_0 KV cache for 32k tokens is
        // approximated from the parameter count (about 1.6 MB per billion parameters and 1k tokens),
        // never below 6 % of the files.
        var kv = parameters is > 0
            ? (long)(parameters.Value / 1e9 * 1.6e6 * (DefaultContext / 1024.0))
            : 0;
        kv = Math.Max(kv, (long)(quantization.ModelBytes * .06));
        return weights + kv + 512L * 1024 * 1024;
    }

    public static ModelFit Estimate(HubQuantization quantization, long? parameters, SystemResourceSnapshot system)
    {
        var needed = Needed(quantization, parameters);
        var gpus = system.Gpus;
        var largest = gpus.Count == 0 ? 0 : gpus.Max(g => g.TotalBytes);
        if (gpus.Count > 0 && needed <= largest - GpuReserve)
            return new ModelFit(ModelFitLevel.SingleGpu, needed, needed, 0, 1, "Passt in VRAM",
                $"Braucht etwa {ModelHubFormat.Gigabytes(needed)} – passt auf eine GPU ({ModelHubFormat.Gigabytes(largest)}).");

        var vram = Math.Max(0, gpus.Sum(g => g.TotalBytes - GpuReserve));
        if (gpus.Count > 1 && needed <= vram)
            return new ModelFit(ModelFitLevel.MultiGpu, needed, needed, 0, gpus.Count, $"Passt in VRAM · {gpus.Count} GPUs",
                $"Braucht etwa {ModelHubFormat.Gigabytes(needed)} – wird auf {gpus.Count} GPUs verteilt ({ModelHubFormat.Gigabytes(system.VramTotalBytes)} VRAM).");

        var ram = Math.Max(0, (long)(system.RamTotalBytes * UsableRamShare) - SystemReserve);
        if (needed <= vram + ram)
        {
            var inRam = needed - vram;
            return new ModelFit(ModelFitLevel.Offload, needed, vram, inRam, gpus.Count, "Offload VRAM + RAM",
                gpus.Count == 0
                    ? $"Braucht etwa {ModelHubFormat.Gigabytes(needed)} – läuft ohne GPU nur im Arbeitsspeicher (langsam)."
                    : $"Braucht etwa {ModelHubFormat.Gigabytes(needed)} – {ModelHubFormat.Gigabytes(vram)} im VRAM, etwa {ModelHubFormat.Gigabytes(inRam)} im RAM (langsamer).");
        }
        return new ModelFit(ModelFitLevel.Insufficient, needed, vram, Math.Max(0, needed - vram), gpus.Count, "Zu groß",
            $"Braucht etwa {ModelHubFormat.Gigabytes(needed)} – mehr als VRAM ({ModelHubFormat.Gigabytes(system.VramTotalBytes)}) und nutzbarer RAM ({ModelHubFormat.Gigabytes(ram)}) zusammen.");
    }
}
