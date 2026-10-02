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
    /// Opt-in phase timers of glyph drawing, for profiling tools: per-thread running totals of
    /// the elapsed time and the number of timed spans of each <see cref="GlyphTimerPhase"/>.
    /// Off by default; while off a probe is one test of a static field.
    /// </summary>
    /// <remarks>
    /// Spans are read with <see cref="Stopwatch.GetTimestamp"/>, about 15 ns per read. The
    /// thread's own cycle counter (<c>QueryThreadCycleTime</c>) costs about 140 ns per read,
    /// more than several of the phases it would time. The stopwatch ticks at 10 MHz on
    /// Windows, so a single span is only good to 100 ns, but the boundaries of spans fall at
    /// random within a tick and a mean over many spans has no such error. Spans are wall time:
    /// a span in which the thread was descheduled or blocked counts that time too. A probe
    /// pair adds <see cref="ProbeTicks"/> to the phase it times and to every phase around it.
    /// </remarks>
    internal static class GlyphPhaseTimers
    {
        // A constant rather than the enum's values: the first probe of a process runs the type
        // initializer, and enum reflection there would allocate inside whatever is being measured.
        private const int PhaseCount = (int)GlyphTimerPhase.Present + 1;

        private static bool s_enabled;

        [ThreadStatic]
        private static long[]? t_ticks;

        [ThreadStatic]
        private static long[]? t_counts;

        /// <summary>Whether probes record. Set before the spans to measure begin; spans already running when it changes are dropped.</summary>
        public static bool Enabled
        {
            get => s_enabled;
            set => s_enabled = value;
        }

        /// <summary>Ticks per second of the recorded totals.</summary>
        public static long Frequency => Stopwatch.Frequency;

        /// <summary>The mean ticks an empty probe pair records, from <see cref="Calibrate"/>; 0 before it runs.</summary>
        public static double ProbeTicks { get; private set; }

        /// <summary>The start of a span: a timestamp while enabled, otherwise 0, which the matching <see cref="Stop"/> ignores.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Start() => s_enabled ? Stopwatch.GetTimestamp() : 0;

        /// <summary>Ends a span begun by <see cref="Start"/> and adds it to <paramref name="phase"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Stop(GlyphTimerPhase phase, long start)
        {
            if (start != 0)
            {
                Record(phase, start);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Record(GlyphTimerPhase phase, long start)
        {
            var elapsed = Stopwatch.GetTimestamp() - start;

            (t_ticks ??= new long[PhaseCount])[(int)phase] += elapsed;
            (t_counts ??= new long[PhaseCount])[(int)phase]++;
        }

        /// <summary>The ticks recorded for <paramref name="phase"/> on the calling thread.</summary>
        public static long GetTicksOnThread(GlyphTimerPhase phase) => t_ticks?[(int)phase] ?? 0;

        /// <summary>The spans recorded for <paramref name="phase"/> on the calling thread.</summary>
        public static long GetCountOnThread(GlyphTimerPhase phase) => t_counts?[(int)phase] ?? 0;

        /// <summary>
        /// Measures what an empty probe pair records into <see cref="ProbeTicks"/>, timing
        /// <paramref name="pairs"/> pairs; the thread's own totals are left as they were.
        /// </summary>
        public static void Calibrate(int pairs = 1_000_000)
        {
            var wasEnabled = s_enabled;
            var phase = GlyphTimerPhase.GlyphRun;
            var ticks = GetTicksOnThread(phase);
            var count = GetCountOnThread(phase);

            s_enabled = true;

            for (var i = 0; i < pairs; i++)
            {
                Stop(phase, Start());
            }

            ProbeTicks = (double)(GetTicksOnThread(phase) - ticks) / pairs;
            t_ticks![(int)phase] = ticks;
            t_counts![(int)phase] = count;
            s_enabled = wasEnabled;
        }
    }
}
