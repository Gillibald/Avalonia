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
    /// A failed try that cost per glyph no more than <see cref="WarmingDrop"/> less than the
    /// failed try before it shows the scene does not fit, rather than code still compiling: until
    /// the hold resets, a try then also needs the scene estimated within the budget rather than
    /// twice it. A scene that ran out of budget and whose estimate then keeps it from trying goes
    /// on doubling its hold, and tries once more after the first hold of
    /// <see cref="RetryHoldFrames"/>, whatever its estimate: tries on code still compiling can
    /// cost alike per glyph, and an estimate measured on that code can stay past the gate long
    /// after the code runs warm. After that try it follows the rules above until the hold resets.
    /// A stop is a draw that repeats the run's transform, a pause more than two frames without
    /// moving text. Frames a moving run draws from a raster it has cached, such as a zoom jumping
    /// back to its start or reversing through scales it drew before, keep the motion going.
    /// </para>
    /// <para>
    /// Each thread of an application that draws text has one (<see cref="TextRasterThreadState.Motion"/>),
    /// so what it learned ends with the application.
    /// </para>
    /// </remarks>
    internal sealed class MotionRasterBudget
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
        /// The hold after which a scene its estimate keeps from trying is tried once anyway, 2 s at
        /// 60 Hz. The first tries of a process run on code still compiling, often three in a row
        /// at several times the warm cost per glyph, and leave an estimate measured on that code;
        /// by the end of this hold, in the first gesture, the code runs warm.
        /// </summary>
        internal const int RetryHoldFrames = 4 * HoldFrames;

        /// <summary>
        /// What a run costs beyond its glyphs, in glyphs: laying out its masks and handing them to
        /// the backend.
        /// </summary>
        internal const int RunOverheadGlyphs = 10;

        private static readonly double s_microsecondsPerTick = 1e6 / Stopwatch.Frequency;

        private long _frame;
        private double _spent;
        private double _estimate;
        private double _previousEstimate;
        private long _holdUntil;
        private long _lastMotionFrame;
        private int _hold;
        private bool _tried;
        private bool _ranOut;
        private int _triedFrames;
        private long _chargedGlyphs;
        private double _failedCostPerGlyph;
        private bool _strict;
        private bool _retried;
        private long _retryFrame;

        /// <summary>
        /// How much less per glyph a failed try must cost than the failed try before it to count as
        /// code still warming up, which keeps retrying under twice the budget: more than a warm
        /// scene's cost per glyph varies from try to try, far less than the several-fold drop of
        /// code that compiled between them.
        /// </summary>
        internal const double WarmingDrop = 0.25;

        /// <summary>
        /// The frames in which a moving run tried the budget and drew sharp, a running total.
        /// </summary>
        internal int TriedFrames => _triedFrames;

        private double _budget;
        private double _nonText;

        private static double Cap(GlyphRasterTuning tuning) => tuning.MotionBudgetMicroseconds ?? FrameBudgetMicroseconds;

        /// <summary>
        /// What a frame keeps free besides its other work, in microseconds: about the spread of the
        /// time frames of a busy desktop scene spend outside text, between their median and their
        /// 95th percentile.
        /// </summary>
        internal const double SafetyMarginMicroseconds = 1000;

        /// <summary>The interval between frames under <paramref name="tuning"/>, in microseconds.</summary>
        internal static double FrameIntervalMicroseconds(GlyphRasterTuning tuning)
            => tuning.MotionFrameIntervalMicroseconds ?? 1e6 / 120;

        /// <summary>
        /// The time recent frames that drew moving text spent outside text, in microseconds, each
        /// frame weighing a quarter, unless <paramref name="tuning"/> sets it.
        /// </summary>
        internal double NonTextMicroseconds(GlyphRasterTuning tuning) => tuning.MotionNonTextMicroseconds ?? _nonText;

        /// <summary>
        /// Records that a frame took <paramref name="renderMicroseconds"/> to render, of which
        /// <paramref name="textMicroseconds"/> went to rasterizing text.
        /// </summary>
        internal void RecordFrame(double renderMicroseconds, double textMicroseconds)
        {
            var sample = Math.Max(0, renderMicroseconds - textMicroseconds);

            _nonText = _nonText > 0 ? _nonText + (sample - _nonText) / 4 : sample;
        }

        /// <summary>
        /// Records a frame the render thread finished: it took <paramref name="elapsedTicks"/>, of
        /// which <paramref name="rasterTicks"/> rasterized glyph masks (<see cref="Stopwatch"/>
        /// ticks). Only frames that drew moving text, or text about to move, count.
        /// </summary>
        public void FrameRendered(long elapsedTicks, long rasterTicks)
        {
            var frame = GlyphCacheBudget.Shared.CurrentThreadFrame;

            if (frame == 0 || frame != _frame)
            {
                return;
            }

            RecordFrame(elapsedTicks * s_microsecondsPerTick,
                Math.Max(_spent, rasterTicks * s_microsecondsPerTick));
        }

        /// <summary>
        /// Whether this motion frame of <paramref name="run"/> at <paramref name="pixelsPerEm"/>
        /// rasterizes sharp. <paramref name="missShare"/> is the share of its glyph masks the sharp
        /// frame has to rasterize rather than find cached: 1 for transient masks, 0 when all it
        /// draws is cached.
        /// </summary>
        public bool PrefersSharp(ManagedGlyphRunImpl run, double pixelsPerEm, double missShare,
            GlyphRasterTuning tuning)
        {
            var frame = BeginFrame(tuning);
            var estimate = Estimate(run, pixelsPerEm, missShare, tuning);

            run.MovesOnGpu = true;
            _estimate += estimate;
            Continue(frame);

            if (frame < _holdUntil)
            {
                return false;
            }

            if (_previousEstimate >= (_strict ? 1 : 2) * _budget && (frame == 0 || frame != _retryFrame))
            {
                // A scene that ran out of budget before goes on doubling its hold while its estimate
                // keeps it from trying, and tries once after the first hold of RetryHoldFrames,
                // whatever its estimate: the estimate and the costs of the tries that led there may
                // all have been measured on code still compiling.
                if (_hold == 0)
                {
                    return false;
                }

                if (_hold < RetryHoldFrames || _retried)
                {
                    _hold = Math.Min(2 * _hold, MaxHoldFrames);
                    _holdUntil = frame + 1 + _hold;

                    return false;
                }

                _retried = true;
                _retryFrame = frame;
            }

            if (_spent + estimate < _budget)
            {
                if (!_tried)
                {
                    _triedFrames++;
                }

                _tried = true;
                return true;
            }

            _hold = _hold == 0 ? HoldFrames : Math.Min(2 * _hold, MaxHoldFrames);
            _holdUntil = frame + 1 + _hold;
            _ranOut = true;

            return false;
        }

        /// <summary>
        /// Records a motion frame of <paramref name="run"/> at <paramref name="pixelsPerEm"/> drawn
        /// from a raster it has cached, which leaves nothing to decide: the motion goes on, and the
        /// run counts towards the frame's estimate with the share of its masks its next motion frame
        /// would rasterize, <paramref name="missShare"/>, as a run that tries does.
        /// </summary>
        public void Moving(ManagedGlyphRunImpl run, double pixelsPerEm, double missShare,
            GlyphRasterTuning tuning)
        {
            var frame = BeginFrame(tuning);

            run.MovesOnGpu = true;
            _estimate += Estimate(run, pixelsPerEm, missShare, tuning);
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
        public void NotMoving(ManagedGlyphRunImpl run, double pixelsPerEm, bool changing, bool held,
            double missShare, GlyphRasterTuning tuning)
        {
            if (changing)
            {
                BeginFrame(tuning);
                _estimate += Estimate(run, pixelsPerEm, missShare, tuning);
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
                Continue(BeginFrame(tuning));
            }
        }

        /// <summary>
        /// Notes that text moves in <paramref name="frame"/>; motion more than two frames after the
        /// last one follows a pause, which ends a hold.
        /// </summary>
        private void Continue(long frame)
        {
            if (frame - _lastMotionFrame > 2)
            {
                EndHold();
            }

            _lastMotionFrame = frame;
        }

        private void EndHold()
        {
            _holdUntil = 0;
            _hold = 0;
            _failedCostPerGlyph = 0;
            _strict = false;
            _retried = false;
        }

        /// <summary>A timestamp for <see cref="Charge"/>, taken before a motion frame rasterizes.</summary>
        public static long Start() => Stopwatch.GetTimestamp();

        /// <summary>
        /// Charges the frame's budget for a motion frame of <paramref name="run"/> that rasterized
        /// since <paramref name="start"/>.
        /// </summary>
        public void Charge(ManagedGlyphRunImpl run, long start, GlyphRasterTuning tuning)
        {
            var elapsed = (Stopwatch.GetTimestamp() - start) * s_microsecondsPerTick;

            BeginFrame(tuning);

            _spent += tuning.MotionGlyphCostMicroseconds is { } cost ? run.GlyphCount * cost : elapsed;
            _chargedGlyphs += run.GlyphCount;
        }

        private static double Estimate(ManagedGlyphRunImpl run, double pixelsPerEm, double missShare,
            GlyphRasterTuning tuning)
            => tuning.MotionGlyphCostMicroseconds is { } cost
                ? run.GlyphCount * cost
                : (run.GlyphCount * missShare + RunOverheadGlyphs) *
                  run.GlyphTypeface.MaskCache.UnhintedRasterCost.PerGlyph(pixelsPerEm);

        /// <summary>
        /// Starts a new budget when the thread draws a frame other than the last one it drew
        /// moving text in, keeping that frame's estimate as the previous one, and returns the
        /// frame. A last frame that tried and did not run out of budget ends the doubling of the
        /// hold. Drawing outside a frame decides every draw on its own.
        /// </summary>
        private long BeginFrame(GlyphRasterTuning tuning)
        {
            var frame = GlyphCacheBudget.Shared.CurrentThreadFrame;

            if (frame != _frame || frame == 0)
            {
                if (_tried && !_ranOut)
                {
                    _hold = 0;
                    _failedCostPerGlyph = 0;
                    _strict = false;
                    _retried = false;
                }
                else if (_tried && _chargedGlyphs > 0)
                {
                    // A failed try: compare what it cost per glyph with the failed try before it.
                    var cost = _spent / _chargedGlyphs;

                    _strict = _failedCostPerGlyph > 0 && cost > (1 - WarmingDrop) * _failedCostPerGlyph;
                    _failedCostPerGlyph = cost;
                }

                _tried = false;
                _ranOut = false;
                _previousEstimate = _estimate;
                _budget = Math.Clamp(FrameIntervalMicroseconds(tuning) - NonTextMicroseconds(tuning) -
                    SafetyMarginMicroseconds, 0, Math.Max(0, Cap(tuning)));
                _frame = frame;
                _spent = 0;
                _chargedGlyphs = 0;
                _estimate = 0;
            }

            return frame;
        }
    }
}
