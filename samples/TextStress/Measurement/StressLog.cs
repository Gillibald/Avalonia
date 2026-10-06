using System;
using System.Collections.Generic;

namespace TextStress.Measurement
{
    /// <summary>
    /// Where a run reports progress and errors, and what the host adds to the result header.
    /// The desktop host writes to the console, which driver scripts read; hosts without a
    /// console (Android) replace the writers.
    /// </summary>
    internal static class StressLog
    {
        public static Action<string> Info { get; set; } = Console.WriteLine;

        public static Action<string> Error { get; set; } = Console.Error.WriteLine;

        /// <summary>Header entries the host appends after the common ones, such as its runtime configuration.</summary>
        public static IReadOnlyList<(string Key, string Value)> HostEnvironment { get; set; } =
            Array.Empty<(string, string)>();
    }
}
