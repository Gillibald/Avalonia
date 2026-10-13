using System;
using System.Diagnostics;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Decides how a GPU draws a frame of a run whose transform keeps changing (a zoom, an
    /// animated rotation): rasterized sharp at the frame's transform, as a static frame would draw
    /// it, or stretched from an earlier raster. Moving text draws sharp while the frame's motion
    /// fits a per-frame budget: every run the thread draws in one frame shares the budget, the
    /// time actually spent rasterizing motion frames (band crossings included) uses it up, and
    /// once it is spent the remaining runs stretch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The estimate of a run is its glyph count times the typeface's measured cost per glyph at
    /// the run's size (<see cref="GlyphRasterCost"/>), times how the thread's sharp motion
    /// frames compared with such estimates: glyph masks shared between runs or cached, and the
    /// layout, composition and upload around them.
    /// </para>
    /// <para>
    /// A frame draws its moving runs sharp only when the estimates of all the moving runs of the
    /// thread's previous frame fit the budget, so a scene that cannot be sharp as a whole (a
    /// screen of CJK text, a dashboard of thousands of glyphs) stretches every run as before
    /// rather than spending the budget on a few of them. A frame that runs out of budget, or
    /// finds the previous frame's motion past it, stretches every moving run for the next
    /// <see cref="HoldFrames"/> frames or until the motion stops, so a scene near the budget does
    /// not alternate between sharp and stretched frames. The first frame after a hold tries
    /// again, measuring what the estimates may have got wrong, when the motion was estimated at
    /// less than <see cref="ProbeFactor"/> budgets, and after every <see cref="ProbeHolds"/>th
    /// hold otherwise.
    /// </para>
    /// </remarks>
    internal static class MotionRasterBudget
    {
        /// <summary>
        /// The time a frame may spend rasterizing motion frames sharp, in microseconds: about a
        /// third of a 120 Hz frame. A page of a few hundred Latin glyphs rotating or zooming takes
        /// 1-3 ms a frame on a desktop once the rasterizer is compiled, so it stays sharp, while a
        /// screen of CJK text or a dashboard of thousands of glyphs, 10-20 ms, stretches.
        /// </summary>
        internal const double FrameBudgetMicroseconds = 3000;

        /// <summary>
        /// The frames a thread stretches all moving text after a frame did not fit the budget,
        /// half a second at 60 Hz: moving text switches between sharp and stretched frames at
        /// most this rarely, and recovers this soon from a frame slowed by compiling or
        /// preemption.
        /// </summary>
        internal const int HoldFrames = 30;

        /// <summary>
        /// The multiple of the budget below which motion estimated past the budget is tried again
        /// after every hold.
        /// </summary>
        internal const double ProbeFactor = 2;

        /// <summary>The holds after which motion estimated further past the budget is tried again.</summary>
        internal const int ProbeHolds = 2;

        // Before any sharp frame was measured, a sharp frame counts as twice the time of its glyph
        // masks: on the safe side, so motion that starts out of reach stretches.
        private const double InitialCorrection = 2;

        private const double MinCorrection = 1.0 / 16;
        private const double MaxCorrection = 16;

        private static readonly double s_microsecondsPerTick = 1e6 / Stopwatch.Frequency;

        [ThreadStatic]
        private static long t_frame;

        [ThreadStatic]
        private static double t_spent;

        [ThreadStatic]
        private static double t_demand;

        [ThreadStatic]
        private static double t_previousDemand;

        [ThreadStatic]
        private static long t_holdUntil;

        [ThreadStatic]
        private static int t_holds;

        [ThreadStatic]
        private static double t_correction;

        /// <summary>
        /// Replaces <see cref="FrameBudgetMicroseconds"/>, for tests; <c>null</c> keeps it.
        /// </summary>
        internal static double? BudgetOverride { get; set; }

        /// <summary>
        /// Replaces the measured cost per glyph, in microseconds, for tests: estimates are the
        /// glyph count times it, and a frame that rasterizes is charged its estimate rather than
        /// the time it took, so decisions do not depend on the machine. <c>null</c> measures.
        /// </summary>
        internal static double? GlyphCostOverride { get; set; }

        private static double Budget => BudgetOverride ?? FrameBudgetMicroseconds;

        private static double Correction => t_correction > 0 ? t_correction : InitialCorrection;

        /// <summary>
        /// Whether this motion frame of <paramref name="run"/> at <paramref name="pixelsPerEm"/>
        /// rasterizes sharp. <paramref name="cached"/> tells that everything the sharp frame
        /// draws is cached, so it costs nothing.
        /// </summary>
        public static bool PrefersSharp(ManagedGlyphRunImpl run, double pixelsPerEm, bool cached)
        {
            var frame = BeginFrame();
            var estimate = cached ? 0 : Estimate(run, pixelsPerEm);

            run.MovesOnGpu = true;
            t_demand += estimate;

            if (frame < t_holdUntil)
            {
                return false;
            }

            var fits = t_previousDemand < Budget ||
                       frame == t_holdUntil &&
                       (t_previousDemand < ProbeFactor * Budget || t_holds % ProbeHolds == 0);

            if (fits && t_spent + estimate < Budget)
            {
                return true;
            }

            t_holdUntil = frame + 1 + HoldFrames;
            t_holds++;

            return false;
        }

        /// <summary>A timestamp for <see cref="Charge"/>, taken before a motion frame rasterizes.</summary>
        public static long Start() => Stopwatch.GetTimestamp();

        /// <summary>
        /// Charges the frame's budget for a motion frame of <paramref name="run"/> that
        /// rasterized since <paramref name="start"/>. A <paramref name="sharp"/> frame also
        /// teaches the thread how its cost compares with the estimate; a band crossing builds
        /// cached sprites rather than a sharp frame and teaches nothing.
        /// </summary>
        public static void Charge(ManagedGlyphRunImpl run, double pixelsPerEm, long start, bool sharp)
        {
            var elapsed = (Stopwatch.GetTimestamp() - start) * s_microsecondsPerTick;

            BeginFrame();

            if (GlyphCostOverride is { } cost)
            {
                t_spent += run.GlyphCount * cost;
                return;
            }

            t_spent += elapsed;

            var plain = run.GlyphCount * run.GlyphTypeface.MaskCache.RasterCost.PerGlyph(pixelsPerEm);

            if (sharp && plain > 0)
            {
                // One run raises the correction by a quarter at most, so a frame that compiled the
                // sharp path or was preempted does not stretch the motion after it on its own; it
                // lowers it faster, since nothing makes a frame run faster than its code allows.
                var correction = Correction;

                t_correction = Math.Clamp((correction + Math.Clamp(elapsed / plain, correction / 8,
                    correction * 1.5)) / 2, MinCorrection, MaxCorrection);
            }
        }

        /// <summary>
        /// Records that <paramref name="run"/> drew a frame under an unchanged transform. When it
        /// was moving, its motion stopped, and the next motion is weighed anew.
        /// </summary>
        public static void EndMotion(ManagedGlyphRunImpl run)
        {
            if (run.MovesOnGpu)
            {
                run.MovesOnGpu = false;
                t_holdUntil = 0;
                t_holds = 0;
            }
        }

        private static double Estimate(ManagedGlyphRunImpl run, double pixelsPerEm)
            => GlyphCostOverride is { } cost
                ? run.GlyphCount * cost
                : run.GlyphCount * run.GlyphTypeface.MaskCache.RasterCost.PerGlyph(pixelsPerEm) * Correction;

        /// <summary>
        /// Starts a new budget when the thread draws a frame other than the last one it drew
        /// moving text in, and returns the frame. What that frame's motion was estimated at, and
        /// a hold it started, count for this one when it was one of the two frames before; a
        /// motion that starts after a pause is weighed by its own frames. Drawing outside a frame
        /// decides every draw on its own.
        /// </summary>
        private static long BeginFrame()
        {
            var frame = GlyphCacheBudget.Shared.CurrentThreadFrame;

            if (frame != t_frame || frame == 0)
            {
                var follows = frame - t_frame <= 2;

                t_previousDemand = follows ? t_demand : 0;

                if (!follows)
                {
                    t_holdUntil = 0;
                    t_holds = 0;
                }

                t_frame = frame;
                t_spent = 0;
                t_demand = 0;
            }

            return frame;
        }
    }
}
