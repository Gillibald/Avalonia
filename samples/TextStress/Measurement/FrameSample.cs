namespace TextStress.Measurement
{
    /// <summary>
    /// One measured frame. Timestamps are <see cref="System.Diagnostics.Stopwatch"/> ticks;
    /// CPU readings are <see cref="ThreadClock.NowMs"/> on the thread that took them.
    /// </summary>
    internal sealed class FrameSample
    {
        public int Frame;

        // UI thread: from the scenario's mutation to the end of the composition commit.
        public long UiStart;
        public long UiEnd;
        public double UiCpuStart;
        public double UiCpuEnd;
        public long UiAllocStart;
        public long UiAllocEnd;

        // Render thread: from the batch being applied to the end of the render pass that drew it.
        public int RenderStartThread;
        public int RenderEndThread;
        public long RenderStart;
        public long RenderEnd;
        public double RenderCpuStart;
        public double RenderCpuEnd;
        public long RenderAllocStart;
        public long RenderAllocEnd;

        // Render-thread counters of the GPU glyph atlas path, read at both ends of the pass.
        public int AtlasDrawsStart;
        public int AtlasDrawsEnd;
        public int PageUploadsStart;
        public int PageUploadsEnd;
        public int AtlasGeometryStart;
        public int AtlasGeometryEnd;

        // The render thread's text counters (RenderCounters), read at both ends of the pass, and
        // the most runs one glyph batch of the pass held.
        public readonly long[] CountersStart = new long[RenderCounters.Count];
        public readonly long[] CountersEnd = new long[RenderCounters.Count];
        public int MaxRunsPerBatch;

        // The render thread's glyph phase timers (PhaseTimes), ticks and span counts per phase,
        // read at both ends of the pass.
        public readonly long[] PhaseTicksStart = new long[PhaseTimes.Count];
        public readonly long[] PhaseTicksEnd = new long[PhaseTimes.Count];
        public readonly long[] PhaseCountsStart = new long[PhaseTimes.Count];
        public readonly long[] PhaseCountsEnd = new long[PhaseTimes.Count];

        // Process state after the frame, as deltas from the previous frame where cumulative.
        public int Gc0;
        public int Gc1;
        public int Gc2;
        public double GcPauseMs;
        public long HeapBytes;
        public long PrivateBytes;
        public long MaskCacheBytes;
        public long AtlasBytes;
        public long MaskEvictions;
        public long AtlasEvictions;
        public int AtlasPages;
        public int AtlasPagesShared;
        public int AtlasFaces;
        public long TierMask;
        public long TierTransformed;
        public long TierBlob;

        // The process-wide glyph cache budget: bytes charged after the frame, the peak since the
        // sweep value started, and bytes evicted during the frame.
        public long BudgetUsedBytes;
        public long BudgetPeakBytes;
        public long BudgetEvictedBytes;
    }
}
