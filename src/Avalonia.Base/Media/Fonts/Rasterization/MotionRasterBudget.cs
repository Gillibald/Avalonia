using System;
using System.Diagnostics;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Decides how a GPU draws a frame of a run whose transform keeps changing (a zoom, an
    /// animated rotation): rasterized sharp at the frame's transform, as a static frame would draw
    /// it, or stretched from an earlier raster.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every run the thread draws in one frame shares a budget, which the time actually spent
    /// rasterizing motion frames, band crossings included, uses up. A moving run draws sharp while
    /// its estimate fits what is left; once the budget is spent the remaining runs stretch.
    /// </para>
    /// <para>
    /// A run is estimated at the glyphs whose masks it has to rasterize (all of them for transient
    /// masks; for upright sprites the share recent sprite builds of the typeface missed the mask
    /// cache), plus <see cref="RunOverheadGlyphs"/> for laying out and handing over its masks,
    /// times its typeface's measured cost per glyph at its size (<see cref="GlyphRasterCost"/>). The runs that moved, or were about to move, in the previous
    /// frame give the estimate of the scene; a scene estimated at twice the budget or more stretches
    /// every run without trying, so a screen of CJK text or a dashboard of thousands of glyphs
    /// draws as it would without the budget. The frames before an animation is recognized draw
    /// its runs under changing transforms, which estimates the scene before its first moving frame.
    /// </para>
    /// <para>
    /// A frame that runs out of budget stretches every moving run for the next
    /// <see cref="HoldFrames"/> frames, or until the motion stops or pauses: a scene near the
    /// budget does not alternate between sharp and stretched lines, and the frame after the hold
    /// tries again, which also lets a scene first estimated on code still compiling become sharp
    /// once it runs warm.
    /// </para>
    /// </remarks>
    internal static class MotionRasterBudget
    {
        /// <summary>
        /// The time a frame may spend rasterizing motion frames sharp, in microseconds: about a
        /// third of a 120 Hz frame. A page of a few hundred Latin glyphs rotating or zooming takes
        /// 1-3 ms a frame on a desktop once the rasterizer is compiled, so it stays sharp, while a
        /// screen of CJK text or a dashboard of thousands of glyphs, 7-20 ms, stretches.
        /// </summary>
        internal const double FrameBudgetMicroseconds = 3000;

        /// <summary>
        /// The frames a thread stretches all moving text after a frame ran out of budget, half a
        /// second at 60 Hz.
        /// </summary>
        internal const int HoldFrames = 30;

        /// <summary>
        /// What a run costs beyond its glyphs, in glyphs: laying out its masks and handing them to
        /// the backend.
        /// </summary>
        internal const int RunOverheadGlyphs = 10;

        private static readonly double s_microsecondsPerTick = 1e6 / Stopwatch.Frequency;

        [ThreadStatic]
        private static long t_frame;

        [ThreadStatic]
        private static double t_spent;

        [ThreadStatic]
        private static double t_estimate;

        [ThreadStatic]
        private static double t_previousEstimate;

        [ThreadStatic]
        private static long t_holdUntil;

        [ThreadStatic]
        private static long t_lastMotionFrame;

        /// <summary>
        /// Replaces <see cref="FrameBudgetMicroseconds"/>, for tests; <c>null</c> keeps it.
        /// </summary>
        internal static double? BudgetOverride { get; set; }

        /// <summary>
        /// Replaces the measured cost per glyph, in microseconds, for tests: a run is estimated at
        /// its glyph count times it, whatever its masks find cached, and a frame that rasterizes is
        /// charged its estimate rather than the time it took, so decisions depend neither on the
        /// machine nor on what earlier tests cached. <c>null</c> measures.
        /// </summary>
        internal static double? GlyphCostOverride { get; set; }

        private static double Budget => BudgetOverride ?? FrameBudgetMicroseconds;

        /// <summary>
        /// Whether this motion frame of <paramref name="run"/> at <paramref name="pixelsPerEm"/>
        /// rasterizes sharp. <paramref name="missShare"/> is the share of its glyph masks the sharp
        /// frame has to rasterize rather than find cached: 1 for transient masks, 0 when all it
        /// draws is cached.
        /// </summary>
        public static bool PrefersSharp(ManagedGlyphRunImpl run, double pixelsPerEm, double missShare)
        {
            var frame = BeginFrame();
            var estimate = Estimate(run, pixelsPerEm, missShare);

            run.MovesOnGpu = true;
            t_estimate += estimate;

            // A motion more than two frames after the last one follows a pause, which ends a hold.
            if (frame - t_lastMotionFrame > 2)
            {
                t_holdUntil = 0;
            }

            t_lastMotionFrame = frame;

            if (frame < t_holdUntil || t_previousEstimate >= 2 * Budget)
            {
                return false;
            }

            if (t_spent + estimate < Budget)
            {
                return true;
            }

            t_holdUntil = frame + 1 + HoldFrames;

            return false;
        }

        /// <summary>
        /// Records a draw of <paramref name="run"/> that is not a motion frame. A draw under a
        /// changed transform, <paramref name="changing"/>, may start a motion, so it counts
        /// towards the frame's estimate with the share of its masks a motion frame would rasterize,
        /// <paramref name="missShare"/>; a run that was moving and now holds its transform ends
        /// the hold, and the next motion is weighed anew.
        /// </summary>
        public static void NotMoving(ManagedGlyphRunImpl run, double pixelsPerEm, bool changing, double missShare)
        {
            if (changing)
            {
                BeginFrame();
                t_estimate += Estimate(run, pixelsPerEm, missShare);
            }

            if (run.MovesOnGpu)
            {
                run.MovesOnGpu = false;
                t_holdUntil = 0;
            }
        }

        /// <summary>A timestamp for <see cref="Charge"/>, taken before a motion frame rasterizes.</summary>
        public static long Start() => Stopwatch.GetTimestamp();

        /// <summary>
        /// Charges the frame's budget for a motion frame of <paramref name="run"/> that rasterized
        /// since <paramref name="start"/>.
        /// </summary>
        public static void Charge(ManagedGlyphRunImpl run, long start)
        {
            var elapsed = (Stopwatch.GetTimestamp() - start) * s_microsecondsPerTick;

            BeginFrame();

            t_spent += GlyphCostOverride is { } cost ? run.GlyphCount * cost : elapsed;
        }

        private static double Estimate(ManagedGlyphRunImpl run, double pixelsPerEm, double missShare)
            => GlyphCostOverride is { } cost
                ? run.GlyphCount * cost
                : (run.GlyphCount * missShare + RunOverheadGlyphs) *
                  run.GlyphTypeface.MaskCache.RasterCost.PerGlyph(pixelsPerEm);

        /// <summary>
        /// Starts a new budget when the thread draws a frame other than the last one it drew
        /// moving text in, keeping that frame's estimate as the previous one, and returns the
        /// frame. Drawing outside a frame decides every draw on its own.
        /// </summary>
        private static long BeginFrame()
        {
            var frame = GlyphCacheBudget.Shared.CurrentThreadFrame;

            if (frame != t_frame || frame == 0)
            {
                t_previousEstimate = t_estimate;
                t_frame = frame;
                t_spent = 0;
                t_estimate = 0;
            }

            return frame;
        }
    }
}
