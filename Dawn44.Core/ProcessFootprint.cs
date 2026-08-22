using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dawn44.Core;

/// <summary>
/// Hands memory back to the OS once a burst of work is over. Only the headless resident calls this.
/// </summary>
/// <remarks>
/// <para>
/// A process that sits idle for hours between bursts is judged by the one number Task Manager shows, and
/// neither half of the runtime has any reason to volunteer it back: a workstation GC with a two-megabyte
/// heap and no allocation pressure will not run, and pages the burst touched stay in the working set
/// until something else on the machine wants them.
/// </para>
/// <para>
/// So after a burst settles, collect and then trim. The cost is a few soft page faults on the next
/// keypress, which is invisible next to the 100-500ms the HID read on that same press already takes.
/// </para>
/// </remarks>
public static class ProcessFootprint
{
    /// <summary>Collects, compacts, and empties the working set. Never throws.</summary>
    public static void Trim()
    {
        // Compacting because the point is to give pages back rather than to mark them reusable.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        // -1/-1 is documented as "empty the working set"; whatever the process still needs is faulted
        // straight back in. A failure here is ignored — this is a courtesy, not a correctness step.
        SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
    }

    /// <summary>
    /// One line describing where the memory is, for <c>background.log</c>.
    /// </summary>
    /// <remarks>
    /// A NativeAOT process has no CLR, so none of <c>dotnet-dump</c>, <c>dotnet-gcdump</c> or SOS can
    /// walk its heap, and the resident normally runs elevated where a medium-integrity shell cannot
    /// even open it. Self-reporting is therefore the only cheap way to tell a managed leak (managed
    /// climbing) from native growth (private climbing while managed does not) from nothing at all
    /// (working set climbing while both hold).
    /// </remarks>
    public static string Describe()
    {
        using var self = Process.GetCurrentProcess();
        var info = GC.GetGCMemoryInfo();

        return $"footprint: ws={self.WorkingSet64 / 1024}KB private={self.PrivateMemorySize64 / 1024}KB "
            + $"managed={GC.GetTotalMemory(false) / 1024}KB heap={info.HeapSizeBytes / 1024}KB "
            + $"committed={info.TotalCommittedBytes / 1024}KB "
            + $"gc0={GC.CollectionCount(0)} gc1={GC.CollectionCount(1)} gc2={GC.CollectionCount(2)} "
            + $"threads={self.Threads.Count} handles={self.HandleCount}";
    }

    /// <summary>Returns a pseudo-handle that does not need closing.</summary>
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(
        IntPtr process,
        IntPtr minimumWorkingSetSize,
        IntPtr maximumWorkingSetSize);
}
