using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Avalonia.Imaging.Benchmarks
{
    /// <summary>
    /// Keeps the benchmark on full-speed cores. Windows marks a long-running process it
    /// considers background for power throttling (EcoQoS) and schedules it on the efficiency
    /// cores of a hybrid CPU, which makes every timing, both backends', about 1.5 times slower.
    /// </summary>
    internal static class ProcessScheduling
    {
        private const int ProcessPowerThrottling = 4;
        private const uint ProcessPowerThrottlingCurrentVersion = 1;
        private const uint ProcessPowerThrottlingExecutionSpeed = 1;

        /// <summary>
        /// Opts the process out of execution-speed throttling and raises its priority.
        /// Returns a note for the report.
        /// </summary>
        public static string RequestFullSpeed()
        {
            if (!OperatingSystem.IsWindows())
                return "default scheduling";

            // A control mask with the bit set and a state mask without it turns throttling off.
            var state = new PowerThrottlingState
            {
                Version = ProcessPowerThrottlingCurrentVersion,
                ControlMask = ProcessPowerThrottlingExecutionSpeed,
                StateMask = 0,
            };
            var throttlingOff = SetProcessInformation(Process.GetCurrentProcess().Handle, ProcessPowerThrottling,
                ref state, (uint)Marshal.SizeOf<PowerThrottlingState>());

            try
            {
                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return throttlingOff ? "power throttling off" : "default scheduling";
            }

            return throttlingOff ? "power throttling off, high priority" : "high priority";
        }

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
    }
}
