using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Missum.App.Services.ModelHub;

public sealed record GpuInfo(int Index, string Name, long TotalBytes, long UsedBytes);

public sealed record SystemResourceSnapshot(IReadOnlyList<GpuInfo> Gpus, long RamTotalBytes, long RamAvailableBytes)
{
    public long VramTotalBytes => Gpus.Sum(g => g.TotalBytes);

    /// <summary>"2 × RTX 6000 · 96 GB VRAM · 224 GB RAM".</summary>
    public string Summary
    {
        get
        {
            var gpus = Gpus.Count == 0 ? "Keine NVIDIA-GPU erkannt"
                : string.Join(" + ", Gpus.GroupBy(g => g.Name).Select(group => (group.Count() > 1 ? group.Count() + " × " : "") + ShortName(group.Key)))
                    + " · " + ModelHubFormat.Gigabytes(VramTotalBytes) + " VRAM";
            return gpus + " · " + ModelHubFormat.Gigabytes(RamTotalBytes) + " RAM";
        }
    }

    private static string ShortName(string name) => name.Replace("NVIDIA ", "", StringComparison.OrdinalIgnoreCase).Replace("GeForce ", "", StringComparison.OrdinalIgnoreCase).Trim();
}

/// <summary>
/// Reads this PC's GPUs (nvidia-smi, as the native runtime does) and RAM (GlobalMemoryStatusEx).
/// Runs on the host, so it works without the Docker gateway. The result is cached briefly.
/// </summary>
public static partial class SystemResources
{
    private static SystemResourceSnapshot? _cached;
    private static DateTimeOffset _cachedAt;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<SystemResourceSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromSeconds(30)) return _cached;
            var gpus = await ReadGpusAsync(cancellationToken).ConfigureAwait(false);
            var (total, available) = ReadMemory();
            _cached = new SystemResourceSnapshot(gpus, total, available);
            _cachedAt = DateTimeOffset.UtcNow;
            return _cached;
        }
        finally { Gate.Release(); }
    }

    private static async Task<IReadOnlyList<GpuInfo>> ReadGpusAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=index,name,memory.total,memory.used --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return [];
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? ParseGpus(output) : [];
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException) { return []; }
    }

    internal static IReadOnlyList<GpuInfo> ParseGpus(string output)
    {
        var gpus = new List<GpuInfo>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 4) continue;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)) continue;
            long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var used);
            gpus.Add(new GpuInfo(index, parts[1], total * 1024 * 1024, used * 1024 * 1024));
        }
        return gpus.OrderBy(g => g.Index).ToList();
    }

    private static (long Total, long Available) ReadMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? ((long)status.TotalPhysical, (long)status.AvailablePhysical) : (0, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}

public static class ModelHubFormat
{
    public static string Gigabytes(long bytes) => bytes < 1e9
        ? (bytes / 1e6).ToString("0", CultureInfo.GetCultureInfo("de-DE")) + " MB"
        : (bytes / 1e9).ToString(bytes >= 100e9 ? "0" : "0.#", CultureInfo.GetCultureInfo("de-DE")) + " GB";

    /// <summary>"vor 2 Tagen", "vor 3 Std.", "gerade eben".</summary>
    public static string Ago(DateTimeOffset date, DateTimeOffset now)
    {
        var span = now - date;
        if (span < TimeSpan.FromMinutes(1)) return "gerade eben";
        if (span < TimeSpan.FromHours(1)) return $"vor {(int)span.TotalMinutes} Min.";
        if (span < TimeSpan.FromDays(1)) return $"vor {(int)span.TotalHours} Std.";
        if (span < TimeSpan.FromDays(2)) return "gestern";
        if (span < TimeSpan.FromDays(31)) return $"vor {(int)span.TotalDays} Tagen";
        if (span < TimeSpan.FromDays(365)) return (int)(span.TotalDays / 30) is var months && months <= 1 ? "vor 1 Monat" : $"vor {months} Monaten";
        return (int)(span.TotalDays / 365) is var years && years <= 1 ? "vor 1 Jahr" : $"vor {years} Jahren";
    }
}
