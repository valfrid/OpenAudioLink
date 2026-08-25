using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenAudioLink.Hub.Services;

/// <summary>
/// Makes sure librespot processes die with the Hub, and clears any that
/// did not.
/// </summary>
/// <remarks>
/// Windows does not kill a process's children when it exits. Unix reaps a
/// process group; here every child outlives its parent unless something
/// explicitly says otherwise, and <c>LibrespotInstance.Dispose</c> only runs
/// on a graceful shutdown — not when the service is killed, crashes, or is
/// stopped hard by an installer.
///
/// The cost was measured rather than imagined. A Hub found in the field had
/// **eighteen** librespot processes for three cast points, the oldest twelve
/// days old, left behind by ordinary stop/start cycles during development.
/// All fifteen orphans were still announcing themselves over zeroconf under
/// the cast points' names, so a phone choosing "AOL-Matsal" could perfectly
/// well connect to one the Hub was not reading samples from. The Hub's own
/// instance then sat at <c>playing: false</c> for ever, the stream had no
/// audio, and a cast stopped a second after it started.
///
/// That is a nasty shape of bug: nothing errors, every process looks
/// healthy, and the symptom is somewhere else entirely.
///
/// Two mechanisms, because they cover different failures:
///
/// <list type="bullet">
/// <item>A <b>job object</b> with kill-on-close ties every child's lifetime
/// to the Hub's. However the Hub dies, the kernel takes the children with
/// it. This is the real fix and it needs no cleanup pass.</item>
/// <item>A <b>startup sweep</b> for strays, which covers the processes an
/// older Hub already leaked and any that escape the job — a Hub upgraded in
/// place inherits a machine, not a clean slate.</item>
/// </list>
/// </remarks>
public static class LibrespotReaper
{
    /// <summary>
    /// Kills librespot processes left over from a previous Hub.
    /// </summary>
    /// <param name="executablePath">
    /// The binary this Hub launches. Only processes running *this* image are
    /// touched: a librespot somebody started themselves, from elsewhere, is
    /// not this Hub's to end.
    /// </param>
    /// <returns>How many were killed.</returns>
    public static int SweepStrays(string executablePath, ILogger logger)
    {
        var mine = Path.GetFullPath(executablePath);
        var self = Environment.ProcessId;
        var killed = 0;

        foreach (var process in SafeGetProcessesByName("librespot"))
        {
            using (process)
            {
                if (process.Id == self)
                {
                    continue;
                }
                try
                {
                    // MainModule throws for a process this one may not open,
                    // which on a service host is common and not interesting.
                    var path = process.MainModule?.FileName;
                    if (path is null
                        || !string.Equals(Path.GetFullPath(path), mine,
                                          StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                    killed++;
                }
                catch (Exception ex) when (ex is InvalidOperationException
                                              or System.ComponentModel.Win32Exception
                                              or NotSupportedException)
                {
                    // Gone already, or not ours to kill. Either way the next
                    // line is the useful one.
                    logger.LogDebug("Could not sweep librespot {Pid}: {Message}",
                                    process.Id, ex.Message);
                }
            }
        }

        if (killed > 0)
        {
            // Loud on purpose. This should be zero on a healthy machine, and
            // a number that keeps growing across restarts means the job
            // object is not holding and the leak is still open.
            logger.LogWarning(
                "Cleared {Count} librespot process(es) left by a previous Hub. " +
                "Orphans keep announcing themselves to Spotify, so a phone can " +
                "connect to one this Hub is not reading.", killed);
        }

        return killed;
    }

    private static Process[] SafeGetProcessesByName(string name)
    {
        try
        {
            return Process.GetProcessesByName(name);
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    // ---------------------------------------------------------------------
    // The job object.
    //
    // Windows-only, and guarded rather than compiled out, because the Hub
    // builds and its tests run on Linux in CI. On anything else this is a
    // no-op and the sweep above carries the load alone.
    // ---------------------------------------------------------------------

    private static IntPtr _job = IntPtr.Zero;

    /// <summary>
    /// Creates the job every librespot will be assigned to. Safe to call
    /// more than once; safe to call on a platform without job objects.
    /// </summary>
    public static void EnsureJob(ILogger logger)
    {
        if (!OperatingSystem.IsWindows() || _job != IntPtr.Zero)
        {
            return;
        }

        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                logger.LogDebug("CreateJobObject failed; relying on the startup sweep");
                return;
            }

            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                },
            };

            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, buffer, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation,
                                             buffer, (uint)size))
                {
                    logger.LogDebug("SetInformationJobObject failed; relying on the sweep");
                    CloseHandle(job);
                    return;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            // Deliberately never closed. The handle's lifetime *is* the
            // mechanism: when the Hub's process ends, however it ends, the
            // last handle closes and the kernel terminates everything in the
            // job. Closing it here would kill the children immediately.
            _job = job;
            logger.LogInformation(
                "librespot processes will be terminated with the Hub, however it exits");
        }
        catch (DllNotFoundException)
        {
            // Not Windows after all, or an unusual host. The sweep covers it.
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    /// <summary>
    /// Puts a freshly started librespot into the job, so it cannot outlive
    /// the Hub. Failure is logged and survived: the process still runs, and
    /// the next Hub's sweep will find it.
    /// </summary>
    public static void Adopt(Process process, ILogger logger)
    {
        if (!OperatingSystem.IsWindows() || _job == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (!AssignProcessToJobObject(_job, process.Handle))
            {
                logger.LogDebug("Could not put librespot {Pid} in the job object", process.Id);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                      or System.ComponentModel.Win32Exception
                                      or DllNotFoundException)
        {
            logger.LogDebug("Could not put librespot in the job object: {Message}", ex.Message);
        }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /*
     * The shape of these three is dictated by kernel32, not by this code:
     * every field has to be present and in order for the marshalled struct
     * to be the right size, and only LimitFlags and BasicLimitInformation
     * are ever set. The compiler is right that the rest are never assigned
     * — that is what "use the OS default" looks like — and this project
     * treats warnings as errors, so it is said here rather than argued with.
     */
#pragma warning disable CS0649

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

#pragma warning restore CS0649
}
