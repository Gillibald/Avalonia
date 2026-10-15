using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Phases of glyph drawing that <see cref="GlyphPhaseTimers"/> times. Phases nest: a phase
    /// includes the time of every phase that runs inside it, as noted per phase.
    /// </summary>
    internal enum GlyphTimerPhase
    {
        /// <summary>A whole glyph run draw on a Skia context, either text path; includes every run-level phase below it.</summary>
        GlyphRun,

        /// <summary>Native text path: making the paint and looking up the run's text blob.</summary>
        BackendTextSetup,

        /// <summary>Native text path: the canvas's draw text call.</summary>
        BackendDrawText,

        /// <summary>
        /// The managed upright renderer's attempt at a run, from its triage to handing the batches
        /// to the context; includes <see cref="SpriteSetBuild"/>, <see cref="AtlasBatchBuild"/> and
        /// <see cref="AtlasAppend"/>.
        /// </summary>
        MaskRunDraw,

        /// <summary>Laying out an upright run's sprite set; includes <see cref="Rasterize"/>.</summary>
        SpriteSetBuild,

        /// <summary>Building a glyph mask the glyph mask cache did not hold.</summary>
        Rasterize,

        /// <summary>Placing a sprite set's masks in the atlas and splitting it into per-page batches; includes <see cref="AtlasWrite"/>.</summary>
        AtlasBatchBuild,

        /// <summary>Allocating an atlas slot and writing a mask into the page.</summary>
        AtlasWrite,

        /// <summary>
        /// Handing one atlas batch of a run to the context: appending it to a pending batch, or
        /// drawing it at once when it cannot be batched; includes any batch draw it forces.
        /// </summary>
        AtlasAppend,

        /// <summary>Drawing one pending glyph batch; includes every batch-level phase below.</summary>
        BatchDraw,

        /// <summary>Wrapping a new page version's pixels as an image, and releasing the older version's image.</summary>
        PageRewrap,

        /// <summary>Making the image shader that kept vertices sample a page image through.</summary>
        PageShader,

        /// <summary>Matching a batch against the batch its first run began last time, and building its vertices once it repeats.</summary>
        BatchVertices,

        /// <summary>Renting and setting up the paint (colour, shader) and setting the batch's transform.</summary>
        DrawSetup,

        /// <summary>The native vertices or atlas draw calls of a batch whose page image existed before the batch draw.</summary>
        NativeDraw,

        /// <summary>The native draw calls of a batch whose page image was made by the same batch draw.</summary>
        NativeDrawFresh,

        /// <summary>Restoring the transform and returning the paint.</summary>
        DrawTeardown,

        /// <summary>The end of a GPU rendering session: flushing the surface and submitting the context's work.</summary>
        SurfaceFlush,

        /// <summary>Ending the platform's rendering session after the flush, which presents.</summary>
        /// <remarks>Stays the last phase: <see cref="GlyphPhaseTimers"/> sizes its totals by it.</remarks>
        Present,
    }

    /// <summary>
    /// Opt-in phase timers of glyph drawing on one thread of one application, for profiling tools:
    /// running totals of the elapsed time and the number of timed spans of each
    /// <see cref="GlyphTimerPhase"/> (<see cref="TextRasterThreadState.PhaseTimers"/>). Off by default
    /// (<see cref="TextRasterDiagnostics.PhaseTimersEnabled"/>); while off a probe is one test of a
    /// field.
    /// </summary>
    /// <remarks>
    /// Spans are read with <see cref="Stopwatch.GetTimestamp"/>, about 15 ns per read. The
    /// thread's own cycle counter (<c>QueryThreadCycleTime</c>) costs about 140 ns per read,
    /// more than several of the phases it would time. The stopwatch ticks at 10 MHz on
    /// Windows, so a single span is only good to 100 ns, but the boundaries of spans fall at
    /// random within a tick and a mean over many spans has no such error. Spans are wall time:
    /// a span in which the thread was descheduled or blocked counts that time too. A probe
    /// pair adds <see cref="TextRasterDiagnostics.ProbeTicks"/> to the phase it times and to every
    /// phase around it.
    /// </remarks>
    internal sealed class GlyphPhaseTimers
    {
        // A constant rather than the enum's values: enum reflection allocates.
        private const int PhaseCount = (int)GlyphTimerPhase.Present + 1;

        private readonly TextRasterDiagnostics? _diagnostics;
        private readonly long[] _ticks = new long[PhaseCount];
        private readonly long[] _counts = new long[PhaseCount];

        /// <param name="diagnostics">The application's switch, or <c>null</c> for timers that never record.</param>
        internal GlyphPhaseTimers(TextRasterDiagnostics? diagnostics)
        {
            _diagnostics = diagnostics;
        }

        /// <summary>Ticks per second of the recorded totals.</summary>
        public static long Frequency => Stopwatch.Frequency;

        /// <summary>The start of a span: a timestamp while enabled, otherwise 0, which the matching <see cref="Stop"/> ignores.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long Start() => _diagnostics is { PhaseTimersEnabled: true } ? Stopwatch.GetTimestamp() : 0;

        /// <summary>Ends a span begun by <see cref="Start()"/> and adds it to <paramref name="phase"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Stop(GlyphTimerPhase phase, long start)
        {
            if (start != 0)
            {
                Record(phase, start);
            }
        }

        /// <summary>A span start of timers that may be absent: 0 without timers.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Start(GlyphPhaseTimers? timers) => timers?.Start() ?? 0;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Record(GlyphTimerPhase phase, long start)
        {
            _ticks[(int)phase] += Stopwatch.GetTimestamp() - start;
            _counts[(int)phase]++;
        }

        /// <summary>The ticks recorded for <paramref name="phase"/>.</summary>
        public long GetTicks(GlyphTimerPhase phase) => _ticks[(int)phase];

        /// <summary>The spans recorded for <paramref name="phase"/>.</summary>
        public long GetCount(GlyphTimerPhase phase) => _counts[(int)phase];

        /// <summary>
        /// Measures the mean ticks an empty probe pair records, timing <paramref name="pairs"/>
        /// pairs whether or not recording is on; the totals are left as they were.
        /// </summary>
        internal double MeasureProbe(int pairs)
        {
            const GlyphTimerPhase phase = GlyphTimerPhase.GlyphRun;
            var ticks = _ticks[(int)phase];
            var count = _counts[(int)phase];

            for (var i = 0; i < pairs; i++)
            {
                Stop(phase, Stopwatch.GetTimestamp());
            }

            var probe = (double)(_ticks[(int)phase] - ticks) / pairs;

            _ticks[(int)phase] = ticks;
            _counts[(int)phase] = count;

            return probe;
        }
    }
}
