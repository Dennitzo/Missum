using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Missum.App.Services.Webdesign;

/// <summary>
/// Job object for a project dev server (npm, node, vite …). Closing the job ends the whole
/// process tree, so a server never outlives its project or Missum. Unlike the extension job it
/// sets no memory or process-count limits: bundlers start many helper processes.
/// </summary>
internal sealed partial class WebdesignProcessJob : IDisposable
{
    private const uint KillOnJobClose = 0x2000;
    private readonly SafeFileHandle _handle;
    private int _disposed;

    private WebdesignProcessJob(SafeFileHandle handle) => _handle = handle;

    internal static WebdesignProcessJob CreateAndAssign(Process process)
    {
        var handle = CreateJobObject(0, null);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, "Das Jobobjekt für den Webdesign-Server konnte nicht erstellt werden.");
        }
        try
        {
            var limits = new ExtendedLimitInformation { Basic = new BasicLimitInformation { LimitFlags = KillOnJobClose } };
            if (SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Die Grenzen des Webdesign-Servers konnten nicht gesetzt werden.");
            if (AssignProcessToJobObject(handle, process.SafeHandle) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Der Webdesign-Server konnte nicht seinem Jobobjekt zugeordnet werden.");
            return new(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal nuint MinimumWorkingSetSize;
        internal nuint MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        internal BasicLimitInformation Basic;
        internal IoCounters IoInfo;
        internal nuint ProcessMemoryLimit;
        internal nuint JobMemoryLimit;
        internal nuint PeakProcessMemoryUsed;
        internal nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateJobObject(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimitInformation info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
