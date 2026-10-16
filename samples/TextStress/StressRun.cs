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

            // The Skia options are bound in the application's locator, where the platform setup
            // finds them; they reach every drawing context it creates.
            var skiaOptions = new Avalonia.SkiaOptions();

            if (options.StretchVerticesMinSprites >= 0)
            {
                skiaOptions.StretchVerticesMinSprites = options.StretchVerticesMinSprites;
            }

            if (options.PendingBatches > 0)
            {
                skiaOptions.MaxPendingGlyphBatches = options.PendingBatches;
            }

            // Unset keeps the built-in choice.
            switch (options.GetString("atlas-cpu-copy", ""))
            {
                case "on":
                    skiaOptions.ResidentGlyphPages = false;
                    break;
                case "off":
                    skiaOptions.ResidentGlyphPages = true;
                    break;
            }

            Avalonia.AvaloniaLocator.CurrentMutable.Bind<Avalonia.SkiaOptions>().ToConstant(skiaOptions);
        }

        /// <summary>
        /// Applies the run's options to the glyph rasterization context the platform setup registered;
        /// call once the application runs, before the scenario draws.
        /// </summary>
        private static void ConfigureRasterization(RunOptions options)
        {
            if (GlyphRasterizationContext.Current is not { } context)
            {
                return;
            }

            if (options.PhaseTimers)
            {
                context.Diagnostics.Calibrate(context.ForCurrentThread());
                context.Diagnostics.PhaseTimersEnabled = true;
            }

            // A negative value keeps the built-in budget.
            var tuning = context.Tuning;
            var motionBudget = options.GetDouble("motion-budget-us", -1);

            if (motionBudget >= 0)
            {
                tuning = tuning with { MotionBudgetMicroseconds = motionBudget };
            }

            var motionInterval = options.GetDouble("motion-interval-us", -1);

            if (motionInterval > 0)
            {
                tuning = tuning with { MotionFrameIntervalMicroseconds = motionInterval };
            }

            context.Tuning = tuning;
        }

        /// <summary>
        /// The glyph rasterization options of the measured mode. Each process measures one mode; A/B
        /// comparisons are separate, interleaved processes, so neither mode inherits the other's caches.
        /// </summary>
        public static GlyphRasterizationOptions CreateGlyphRasterizationOptions(RunOptions options)
        {
            var limitMb = options.GetInt("glyph-cache-mb", 0);

            var rasterizationOptions = new GlyphRasterizationOptions
            {
                // Unset keeps the platform default.
                GlyphCacheLimitBytes = limitMb > 0 ? limitMb * 1024L * 1024 : null
            };

            // "default" leaves the mode unset, so the platform picks it once it has chosen how it renders.
            if (options.Mode != "default")
            {
                rasterizationOptions.TextRasterizationMode = options.Mode == "backend"
                    ? TextRasterizationMode.Backend
                    : TextRasterizationMode.Managed;
            }

            return rasterizationOptions;
        }

        /// <summary>Runs the scenario in <paramref name="topLevel"/>; returns the process exit code.</summary>
        public static async Task<int> RunAsync(TopLevel topLevel, RunOptions options)
        {
            ResultWriter? writer = null;

            ConfigureRasterization(options);

            try
            {
                // Not a frame scenario: times the rasterizer and blitter paths off the UI thread.
                if (options.Scenario == "simd-bench")
                {
                    SetupLabel.Show(topLevel, options, null);
                    return await Task.Run(() => SimdBench.Run(options));
                }

                var scenario = Scenario.Create(options);
                var label = SetupLabel.Show(topLevel, options, scenario);
                var stage = Stage.Current ?? Stage.Attach(topLevel, options);
                await stage.ShowAsync(label);

                if (stage.Check() is { } problem)
                {
                    StressLog.Error(problem);
                    return Stage.ContentFailedExitCode;
                }

                writer = options.Out is null ? null : new ResultWriter(options.Out, options);
                await new FrameRunner(topLevel, stage, scenario, options, writer).RunAsync();
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
