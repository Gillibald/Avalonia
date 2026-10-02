using System;
using System.Text;
using Avalonia.Media.Fonts.Rasterization;

namespace TextStress.Measurement
{
    /// <summary>
    /// The render thread's glyph phase timers (<see cref="GlyphPhaseTimers"/>): total time and
    /// span count per phase. The runner reads them at both ends of a render pass and writes the
    /// differences, the time in microseconds. All zero unless <c>--phase-timers</c> is given.
    /// </summary>
    internal static class PhaseTimes
    {
        private static readonly GlyphTimerPhase[] s_phases = Enum.GetValues<GlyphTimerPhase>();

        public static int Count => s_phases.Length;

        /// <summary>Time column names (<c>us_</c>), then count column names (<c>n_</c>), in phase order.</summary>
        public static readonly string[] Columns = BuildColumns();

        private static string[] BuildColumns()
        {
            var columns = new string[s_phases.Length * 2];

            for (var i = 0; i < s_phases.Length; i++)
            {
                var name = Snake(s_phases[i].ToString());

                columns[i] = "us_" + name;
                columns[s_phases.Length + i] = "n_" + name;
            }

            return columns;
        }

        /// <summary>Reads the calling thread's ticks and span counts per phase.</summary>
        public static void Read(long[] ticks, long[] counts)
        {
            for (var i = 0; i < s_phases.Length; i++)
            {
                ticks[i] = GlyphPhaseTimers.GetTicksOnThread(s_phases[i]);
                counts[i] = GlyphPhaseTimers.GetCountOnThread(s_phases[i]);
            }
        }

        public static double TicksToMicroseconds(long ticks) => ticks * 1e6 / GlyphPhaseTimers.Frequency;

        private static string Snake(string name)
        {
            var builder = new StringBuilder(name.Length + 4);

            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];

                if (char.IsUpper(c) && i > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }

            return builder.ToString();
        }
    }
}
