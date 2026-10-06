using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace TextStress.Measurement
{
    /// <summary>
    /// CPU time of the calling thread. On Windows it reads the thread's cycle counter, which
    /// is exact; the thread times the OS also keeps advance only at scheduler ticks (about
    /// 15.6 ms), coarser than a frame. Cycles convert to time at a rate calibrated against
    /// <see cref="Stopwatch"/> on a spinning thread. On Linux and Android it reads
    /// <c>clock_gettime(CLOCK_THREAD_CPUTIME_ID)</c>, which the kernel keeps in nanoseconds of
    /// running time. Elsewhere it reports wall time, so a phase measures elapsed time rather than
    /// CPU time.
    /// </summary>
    internal static class ThreadClock
    {
        private const int ClockThreadCpuTime = 3;

        private static double s_cyclesPerMs = double.NaN;
        private static bool s_posixThreadClock;

        /// <summary>True when <see cref="NowMs"/> is thread CPU time, false when it is wall time.</summary>
        public static bool IsThreadCpu { get; private set; }

        public static double CyclesPerMs => s_cyclesPerMs;

        /// <summary>How <see cref="NowMs"/> reads the time, for the result header.</summary>
        public static string Description =>
            !IsThreadCpu ? "wall"
            : s_posixThreadClock ? "clock_gettime(CLOCK_THREAD_CPUTIME_ID)"
            : FormattableString.Invariant($"QueryThreadCycleTime at {s_cyclesPerMs:F0} cycles/ms");

        public static void Calibrate()
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
            {
                try
                {
                    if (clock_gettime(ClockThreadCpuTime, out _) == 0)
                    {
                        s_posixThreadClock = true;
                        IsThreadCpu = true;
                    }
                }
                catch (EntryPointNotFoundException)
                {
                }
                catch (DllNotFoundException)
                {
                }

                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                // Spin on a fresh thread so the cycles counted are all running time; best of a
                // few rounds discards rounds the scheduler interrupted.
                var best = 0.0;

                for (var round = 0; round < 5; round++)
                {
                    var rate = 0.0;
                    var thread = new Thread(() =>
                    {
                        var handle = GetCurrentThread();
                        QueryThreadCycleTime(handle, out var c0);
                        var start = Stopwatch.GetTimestamp();

                        while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 40)
                        {
                        }

                        QueryThreadCycleTime(handle, out var c1);
                        rate = (c1 - c0) / Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    });

                    thread.Start();
                    thread.Join();
                    best = Math.Max(best, rate);
                }

                if (best > 0)
                {
                    s_cyclesPerMs = best;
                    IsThreadCpu = true;
                }
            }
            catch (EntryPointNotFoundException)
            {
            }
            catch (DllNotFoundException)
            {
            }
        }

        /// <summary>Monotonic milliseconds of the calling thread, CPU or wall per <see cref="IsThreadCpu"/>.</summary>
        public static double NowMs()
        {
            if (s_posixThreadClock && clock_gettime(ClockThreadCpuTime, out var time) == 0)
            {
                return time.Seconds * 1000.0 + time.Nanoseconds / 1_000_000.0;
            }

            if (IsThreadCpu && !s_posixThreadClock && QueryThreadCycleTime(GetCurrentThread(), out var cycles))
            {
                return cycles / s_cyclesPerMs;
            }

            return Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
        }

        /// <summary>Private bytes of the process (commit charge on Windows, working set elsewhere).</summary>
        public static long PrivateBytes()
        {
            if (OperatingSystem.IsWindows())
            {
                var counters = new ProcessMemoryCountersEx { Size = (uint)Marshal.SizeOf<ProcessMemoryCountersEx>() };

                if (K32GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Size))
                {
                    return (long)counters.PrivateUsage;
                }
            }

            return Environment.WorkingSet;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool K32GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCountersEx counters, uint size);

        [DllImport("libc", SetLastError = false)]
        private static extern int clock_gettime(int clock, out TimeSpec time);

        /// <summary><c>struct timespec</c>: both fields are <c>long</c>, pointer-sized on Linux and Android.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct TimeSpec
        {
            public nint Seconds;
            public nint Nanoseconds;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCountersEx
        {
            public uint Size;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivateUsage;
        }
    }
}
