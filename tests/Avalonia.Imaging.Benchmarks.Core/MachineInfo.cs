using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Avalonia.Imaging.Benchmarks
{
    /// <summary>Describes the machine a report comes from, so runs on other people's hardware can be read.</summary>
    public static class MachineInfo
    {
        /// <summary>The processor's marketing name, or null where the platform does not expose it (the browser).</summary>
        public static string? ProcessorName()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return WindowsName() ?? System.Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
                if (OperatingSystem.IsMacOS())
                    return Sysctl("machdep.cpu.brand_string");
                if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
                    return CpuInfo();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException
                                          or EntryPointNotFoundException)
            {
            }

            return null;
        }

        private static string? WindowsName()
        {
            if (!OperatingSystem.IsWindows())
                return null;
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim();
        }

        /// <summary>x86 kernels report "model name"; ARM ones "Hardware" or nothing but the part number.</summary>
        private static string? CpuInfo()
        {
            if (!File.Exists("/proc/cpuinfo"))
                return null;
            var lines = File.ReadAllLines("/proc/cpuinfo");
            foreach (var field in new[] { "model name", "Hardware", "Model" })
            {
                var line = lines.FirstOrDefault(l => l.StartsWith(field, StringComparison.Ordinal) && l.Contains(':'));
                if (line is not null)
                    return line[(line.IndexOf(':') + 1)..].Trim();
            }

            return null;
        }

        private static string? Sysctl(string name)
        {
            nint size = 0;
            if (sysctlbyname(name, null, ref size, IntPtr.Zero, 0) != 0 || size <= 0)
                return null;
            var buffer = new byte[size];
            if (sysctlbyname(name, buffer, ref size, IntPtr.Zero, 0) != 0)
                return null;
            return Encoding.UTF8.GetString(buffer, 0, (int)size).TrimEnd('\0').Trim();
        }

        [DllImport("/usr/lib/libSystem.dylib")]
        private static extern int sysctlbyname(string name, byte[]? value, ref nint size, IntPtr newValue, nint newSize);
    }
}
