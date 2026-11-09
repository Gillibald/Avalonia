using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Avalonia.Imaging.Benchmarks
{
    /// <summary>
    /// Keeps the benchmark on full-speed cores. A hybrid CPU otherwise schedules part of the
    /// work on its efficiency cores, and Windows also throttles a long-running process it
    /// considers background (EcoQoS); either makes every timing, both backends', up to about
    /// 1.5 times slower and noisy between runs.
    /// </summary>
    internal static class ProcessScheduling
    {
        private const int ProcessPowerThrottling = 4;
        private const uint ProcessPowerThrottlingCurrentVersion = 1;
        private const uint ProcessPowerThrottlingExecutionSpeed = 1;
        private const int QosClassUserInteractive = 0x21;

        /// <summary>
        /// Moves the process to its fastest cores and out of throttling, as far as the OS allows
        /// without elevated rights. Call it before any other thread starts: threads created
        /// afterwards inherit the placement on Linux and macOS. Returns a note for the report.
        /// </summary>
        public static string RequestFullSpeed()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return Windows();
                if (OperatingSystem.IsLinux())
                    return Linux();
                if (OperatingSystem.IsMacOS())
                    return MacOS();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or EntryPointNotFoundException
                                          or DllNotFoundException)
            {
                return "default scheduling (" + e.GetType().Name + ")";
            }

            return "default scheduling";
        }

        private static string Windows()
        {
            var notes = new List<string>();

            // A control mask with the bit set and a state mask without it turns throttling off.
            var state = new PowerThrottlingState
            {
                Version = ProcessPowerThrottlingCurrentVersion,
                ControlMask = ProcessPowerThrottlingExecutionSpeed,
                StateMask = 0,
            };
            if (SetProcessInformation(Process.GetCurrentProcess().Handle, ProcessPowerThrottling, ref state,
                    (uint)Marshal.SizeOf<PowerThrottlingState>()))
                notes.Add("power throttling off");

            if (WindowsFastestCpuSets() is { } sets &&
                SetProcessDefaultCpuSets(Process.GetCurrentProcess().Handle, sets.Ids, (uint)sets.Ids.Length))
                notes.Add($"{sets.Ids.Length} of {sets.Total} logical processors (highest efficiency class)");

            try
            {
                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
                notes.Add("high priority");
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            return notes.Count == 0 ? "default scheduling" : string.Join(", ", notes);
        }

        /// <summary>
        /// The CPU set ids of the highest efficiency class, or null when every logical processor
        /// is in the same class (no hybrid CPU) and there is nothing to choose.
        /// </summary>
        private static (uint[] Ids, int Total)? WindowsFastestCpuSets()
        {
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out var length, IntPtr.Zero, 0);
            if (length == 0)
                return null;

            var buffer = new byte[length];
            if (!GetSystemCpuSetInformation(buffer, length, out length, IntPtr.Zero, 0))
                return null;

            // SYSTEM_CPU_SET_INFORMATION: Size, Type, then the CpuSet member: Id at 8, EfficiencyClass at 18.
            var sets = new List<(uint Id, byte EfficiencyClass)>();
            for (var offset = 0; offset + 19 <= length;)
            {
                var size = BitConverter.ToInt32(buffer, offset);
                if (size <= 0)
                    break;
                if (BitConverter.ToInt32(buffer, offset + 4) == 0)
                    sets.Add((BitConverter.ToUInt32(buffer, offset + 8), buffer[offset + 18]));
                offset += size;
            }

            if (sets.Count == 0)
                return null;
            var fastest = sets.Max(s => s.EfficiencyClass);
            var ids = sets.Where(s => s.EfficiencyClass == fastest).Select(s => s.Id).ToArray();
            return ids.Length == sets.Count ? null : (ids, sets.Count);
        }

        private static string Linux()
        {
            var notes = new List<string>();
            var cpus = Directory.GetDirectories("/sys/devices/system/cpu", "cpu*")
                .Select(d => Path.GetFileName(d)!)
                .Where(n => n.Length > 3 && n.Skip(3).All(char.IsAsciiDigit))
                .Select(n => int.Parse(n.AsSpan(3), CultureInfo.InvariantCulture))
                .Where(c => c < 1024)
                .Order()
                .ToList();

            // ARM reports relative capacity, x86 hybrids a lower maximum frequency on E-cores.
            var speed = cpus.ToDictionary(c => c,
                c => ReadLong($"/sys/devices/system/cpu/cpu{c}/cpu_capacity")
                     ?? ReadLong($"/sys/devices/system/cpu/cpu{c}/cpufreq/cpuinfo_max_freq") ?? 0);
            var fastest = speed.Count == 0 ? 0 : speed.Values.Max();
            var chosen = cpus.Where(c => speed[c] == fastest).ToList();

            if (chosen.Count > 0 && chosen.Count < cpus.Count)
            {
                var mask = new byte[128];
                foreach (var c in chosen)
                    mask[c / 8] |= (byte)(1 << (c % 8));
                if (sched_setaffinity(0, mask.Length, mask) == 0)
                    notes.Add($"{chosen.Count} of {cpus.Count} cores (fastest)");
            }

            if (File.Exists("/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor"))
            {
                var governor = File.ReadAllText("/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor").Trim();
                notes.Add(governor == "performance" ? "governor performance" : $"governor {governor} (not performance)");
            }

            return notes.Count == 0 ? "default scheduling" : string.Join(", ", notes);
        }

        private static string MacOS()
        {
            // macOS has no affinity API; the user-interactive QoS class keeps the thread on the
            // performance cores. Threads that PixelMan or the thread pool create pick their own.
            return pthread_set_qos_class_self_np(QosClassUserInteractive, 0) == 0
                ? "QoS user-interactive (main thread)"
                : "default scheduling";
        }

        private static long? ReadLong(string path) =>
            File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var value)
                ? value
                : null;

        [StructLayout(LayoutKind.Sequential)]
        private struct PowerThrottlingState
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessInformation(IntPtr process, int informationClass,
            ref PowerThrottlingState information, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemCpuSetInformation(IntPtr information, uint bufferLength,
            out uint returnedLength, IntPtr process, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemCpuSetInformation(byte[] information, uint bufferLength,
            out uint returnedLength, IntPtr process, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessDefaultCpuSets(IntPtr process, uint[] cpuSetIds, uint cpuSetIdCount);

        [DllImport("libc", SetLastError = true)]
        private static extern int sched_setaffinity(int pid, nint cpuSetSize, byte[] mask);

        [DllImport("/usr/lib/libSystem.dylib")]
        private static extern int pthread_set_qos_class_self_np(int qosClass, int relativePriority);
    }
}
