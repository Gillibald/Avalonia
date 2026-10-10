using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using TextStress.Measurement;
using TextStress.Scenarios;

namespace TextStress
{
    /// <summary>The parts of a benchmark process every host shares: process setup and one measured run.</summary>
    internal static class StressRun
    {
        /// <summary>Applies the process-wide options; call once, before Avalonia starts.</summary>
        public static void ConfigureProcess(RunOptions options)
        {
            ThreadClock.Calibrate();

            if (options.PhaseTimers)
            {
                Avalonia.Media.Fonts.Rasterization.GlyphPhaseTimers.Calibrate();
                Avalonia.Media.Fonts.Rasterization.GlyphPhaseTimers.Enabled = true;
            }

            if (options.PendingBatches > 0)
            {
                Avalonia.Skia.DrawingContextImpl.MaxPendingBatches = options.PendingBatches;
            }
        }

        /// <summary>
        /// The font manager options of the measured mode. Each process measures one mode; A/B
        /// comparisons are separate, interleaved processes, so neither mode inherits the other's caches.
        /// </summary>
        public static FontManagerOptions CreateFontManagerOptions(RunOptions options)
        {
            var limitMb = options.GetInt("glyph-cache-mb", 0);

            var fontManagerOptions = new FontManagerOptions
            {
                // Unset keeps the platform default.
                GlyphCacheLimitBytes = limitMb > 0 ? limitMb * 1024L * 1024 : null
            };

            // "default" leaves the mode unset, so the platform picks it once it has chosen how it renders.
            if (options.Mode != "default")
            {
                fontManagerOptions.TextRasterizationMode = options.Mode == "backend"
                    ? TextRasterizationMode.Backend
                    : TextRasterizationMode.Managed;
            }

            return fontManagerOptions;
        }

        /// <summary>Runs the scenario in <paramref name="topLevel"/>; returns the process exit code.</summary>
        public static async Task<int> RunAsync(TopLevel topLevel, RunOptions options)
        {
            ResultWriter? writer = null;

            try
            {
                // Not a frame scenario: times the rasterizer and blitter paths off the UI thread.
                if (options.Scenario == "simd-bench")
                {
                    return await Task.Run(() => SimdBench.Run(options));
                }

                var scenario = Scenario.Create(options);
                writer = options.Out is null ? null : new ResultWriter(options.Out, options);
                await new FrameRunner(topLevel, scenario, options, writer).RunAsync();
                return 0;
            }
            catch (Exception e)
            {
                StressLog.Error(e.ToString());
                return 1;
            }
            finally
            {
                writer?.Dispose();
            }
        }
    }
}
