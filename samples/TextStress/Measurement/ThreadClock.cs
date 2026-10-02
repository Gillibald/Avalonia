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
    /// <see cref="Stopwatch"/> on a spinning thread. Elsewhere it reports wall time, so a
    /// phase measures elapsed time rather than CPU time.
    /// </summary>
    internal static class ThreadClock
    {
        private static double s_cyclesPerMs = double.NaN;

        /// <summary>True when <see cref="NowMs"/> is thread CPU time, false when it is wall time.</summary>
        public static bool IsThreadCpu { get; private set; }

        public static double CyclesPerMs => s_cyclesPerMs;

        public static void Calibrate()
        {
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
            if (IsThreadCpu && QueryThreadCycleTime(GetCurrentThread(), out var cycles))
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
