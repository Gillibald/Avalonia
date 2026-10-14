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
    /// its estimate fits what is left; once the budget is spent the remaining runs stretch. The
    /// budget is what the frame interval leaves after the time recent frames spent outside text
    /// and <see cref="SafetyMarginMicroseconds"/>, at most <see cref="FrameBudgetMicroseconds"/>:
    /// moving text in an otherwise idle frame gets the whole cap, and a busy frame keeps its
    /// interval by stretching. The interval is a 120 Hz frame, the shortest common one, since the
    /// render timers do not tell theirs.
    /// </para>
    /// <para>
    /// A run is estimated at the glyphs whose masks it has to rasterize (all of them for transient
    /// masks; for upright sprites the share recent sprite builds of the typeface missed the mask
    /// cache), plus <see cref="RunOverheadGlyphs"/> for laying out and handing over its masks,
    /// times its typeface's measured cost per unhinted glyph at its size
    /// (<see cref="GlyphMaskCache.UnhintedRasterCost"/>), since motion frames rasterize their
    /// glyphs unhinted. The runs that moved, or were about to move, in the previous frame give
    /// the estimate of the scene; a scene estimated at twice the budget or more stretches
    /// every run without trying, so a screen of CJK text or a dashboard of thousands of glyphs
    /// draws as it would without the budget. The frames before an animation is recognized draw
    /// its runs under changing transforms, which estimates the scene before its first moving frame.
    /// </para>
    /// <para>
    /// A frame that runs out of budget stretches every moving run for the next
    /// <see cref="HoldFrames"/> frames, or until the motion stops or pauses: a scene near the
    /// budget does not alternate between sharp and stretched lines, and the frame after the hold
    /// tries again, which also lets a scene first estimated on code still compiling become sharp
    /// once it runs warm. Each try that runs out of budget again doubles the hold, up to
    /// <see cref="MaxHoldFrames"/>, so a scene whose sharp frames do not fit, though its estimate
    /// does, rarely costs a frame over budget; a try that fits, a stop or a pause starts over.
    /// Until then a try also needs the scene estimated within the budget rather than twice it.
    /// A stop is a draw that repeats the run's transform, a pause more than two frames without
    /// moving text. Frames a moving run draws from a raster it has cached, such as a zoom jumping
    /// back to its start or reversing through scales it drew before, keep the motion going.
    /// </para>
    /// </remarks>
    internal static class MotionRasterBudget
    {
        /// <summary>
        /// The most time a frame may spend rasterizing motion frames sharp, in microseconds: about a
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

        /// <summary>The longest hold after repeated tries that ran out of budget, 8 s at 60 Hz.</summary>
        internal const int MaxHoldFrames = 16 * HoldFrames;

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

        [ThreadStatic]
        private static int t_hold;

        [ThreadStatic]
        private static bool t_tried;

        [ThreadStatic]
        private static bool t_ranOut;

        [ThreadStatic]
        private static int t_triedFrames;

        /// <summary>
        /// The frames of the calling thread in which a moving run tried the budget and drew sharp,
        /// a running total.
        /// </summary>
        internal static int TriedFrames => t_triedFrames;

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

        [ThreadStatic]
        private static double t_budget;

        [ThreadStatic]
        private static double t_nonText;

        private static double Cap => BudgetOverride ?? FrameBudgetMicroseconds;

        /// <summary>Replaces the frame interval, in microseconds, for tests and benchmarks.</summary>
        internal static double? FrameIntervalOverride { get; set; }

        /// <summary>Replaces the time frames spend outside text, in microseconds, for tests.</summary>
        internal static double? NonTextOverride { get; set; }

        /// <summary>
        /// What a frame keeps free besides its other work, in microseconds: about the spread of the
        /// time frames of a busy desktop scene spend outside text, between their median and their
        /// 95th percentile.
        /// </summary>
        internal const double SafetyMarginMicroseconds = 1000;

        /// <summary>The interval between frames, in microseconds.</summary>
        internal static double FrameIntervalMicroseconds => FrameIntervalOverride ?? 1e6 / 120;

        /// <summary>
        /// The time recent frames of the thread that drew moving text spent outside text, in
        /// microseconds, each frame weighing a quarter.
        /// </summary>
        internal static double NonTextMicroseconds => NonTextOverride ?? t_nonText;

        /// <summary>
        /// Records that a frame took <paramref name="renderMicroseconds"/> to render, of which
        /// <paramref name="textMicroseconds"/> went to rasterizing text.
        /// </summary>
        internal static void RecordFrame(double renderMicroseconds, double textMicroseconds)
        {
            var sample = Math.Max(0, renderMicroseconds - textMicroseconds);

            t_nonText = t_nonText > 0 ? t_nonText + (sample - t_nonText) / 4 : sample;
        }

        /// <summary>
        /// Records a frame the render thread finished: it took <paramref name="elapsedTicks"/>, of
        /// which <paramref name="rasterTicks"/> rasterized glyph masks (<see cref="Stopwatch"/>
        /// ticks). Only frames that drew moving text, or text about to move, count.
        /// </summary>
        public static void FrameRendered(long elapsedTicks, long rasterTicks)
        {
            var frame = GlyphCacheBudget.Shared.CurrentThreadFrame;

            if (frame == 0 || frame != t_frame)
            {
                return;
            }

            RecordFrame(elapsedTicks * s_microsecondsPerTick,
                Math.Max(t_spent, rasterTicks * s_microsecondsPerTick));
        }

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
            Continue(frame);

            // After a try ran out, the scene must be estimated within the budget itself, not twice
            // it, to try again before the hold resets: an estimate that fit twice the budget
            // already proved too low once.
            if (frame < t_holdUntil || t_previousEstimate >= (t_hold > 0 ? 1 : 2) * t_budget)
            {
                return false;
            }

            if (t_spent + estimate < t_budget)
            {
                if (!t_tried)
                {
                    t_triedFrames++;
                }

                t_tried = true;
                return true;
            }

            t_hold = t_hold == 0 ? HoldFrames : Math.Min(2 * t_hold, MaxHoldFrames);
            t_holdUntil = frame + 1 + t_hold;
            t_ranOut = true;

            return false;
        }

        /// <summary>
        /// Records a motion frame of <paramref name="run"/> at <paramref name="pixelsPerEm"/> drawn
        /// from a raster it has cached, which leaves nothing to decide: the motion goes on, and the
        /// run counts towards the frame's estimate with the share of its masks its next motion frame
        /// would rasterize, <paramref name="missShare"/>, as a run that tries does.
        /// </summary>
        public static void Moving(ManagedGlyphRunImpl run, double pixelsPerEm, double missShare)
        {
            var frame = BeginFrame();

            run.MovesOnGpu = true;
            t_estimate += Estimate(run, pixelsPerEm, missShare);
            Continue(frame);
        }

        /// <summary>
        /// Records a draw of <paramref name="run"/> that is not a motion frame. A draw under a
        /// changed transform, <paramref name="changing"/>, may start a motion, so it counts
        /// towards the frame's estimate with the share of its masks a motion frame would rasterize,
        /// <paramref name="missShare"/>. A run that was moving and now repeats its transform,
        /// <paramref name="held"/>, has stopped: that ends the hold, and the next motion is weighed
        /// anew. One that was moving and draws a static frame under a new transform, after a cache
        /// hit started its count of changes over, is still moving.
        /// </summary>
        public static void NotMoving(ManagedGlyphRunImpl run, double pixelsPerEm, bool changing, bool held,
            double missShare)
        {
            if (changing)
            {
                BeginFrame();
                t_estimate += Estimate(run, pixelsPerEm, missShare);
            }

            if (!run.MovesOnGpu)
            {
                return;
            }

            if (held)
            {
                run.MovesOnGpu = false;
                EndHold();
            }
            else
            {
                Continue(BeginFrame());
            }
        }

        /// <summary>
        /// Notes that text moves in <paramref name="frame"/>; motion more than two frames after the
        /// last one follows a pause, which ends a hold.
        /// </summary>
        private static void Continue(long frame)
        {
            if (frame - t_lastMotionFrame > 2)
            {
                EndHold();
            }

            t_lastMotionFrame = frame;
        }

        private static void EndHold()
        {
            t_holdUntil = 0;
            t_hold = 0;
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
                  run.GlyphTypeface.MaskCache.UnhintedRasterCost.PerGlyph(pixelsPerEm);

        /// <summary>
        /// Starts a new budget when the thread draws a frame other than the last one it drew
        /// moving text in, keeping that frame's estimate as the previous one, and returns the
        /// frame. A last frame that tried and did not run out of budget ends the doubling of the
        /// hold. Drawing outside a frame decides every draw on its own.
        /// </summary>
        private static long BeginFrame()
        {
            var frame = GlyphCacheBudget.Shared.CurrentThreadFrame;

            if (frame != t_frame || frame == 0)
            {
                if (t_tried && !t_ranOut)
                {
                    t_hold = 0;
                }

                t_tried = false;
                t_ranOut = false;
                t_previousEstimate = t_estimate;
                t_budget = Math.Clamp(FrameIntervalMicroseconds - NonTextMicroseconds - SafetyMarginMicroseconds, 0,
                    Math.Max(0, Cap));
                t_frame = frame;
                t_spent = 0;
                t_estimate = 0;
            }

            return frame;
        }
    }
}
