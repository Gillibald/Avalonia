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
        public long TierMask;
        public long TierTransformed;
        public long TierBlob;
    }
}
