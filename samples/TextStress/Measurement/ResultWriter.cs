using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TextStress.Measurement
{
    /// <summary>
    /// Tab-separated results: <c>#</c> lines with the environment, one column header line,
    /// then one row per measured frame. Times are milliseconds, sizes bytes.
    /// </summary>
    internal sealed class ResultWriter : IDisposable
    {
        public static readonly string Columns =
            "scenario\tn\tmode\trender\tpass\tframe\tinterval_ms\trender_ms\trender_cpu_ms\tui_ms\tui_cpu_ms\t" +
            "latency_ms\tui_alloc\trender_alloc\tgc0\tgc1\tgc2\tgc_pause_ms\theap_bytes\tprivate_bytes\t" +
            "mask_cache_bytes\tatlas_bytes\tmask_evictions\tatlas_evictions\ttier_mask\ttier_transformed\ttier_blob\t" +
            "atlas_draws\tpage_uploads\tatlas_geometry\tmax_runs_per_batch\tatlas_pages\tatlas_pages_shared\t" +
            "atlas_faces\tbudget_used_bytes\tbudget_peak_bytes\tbudget_evicted_bytes\t" + string.Join("\t", RenderCounters.Columns) + "\t" + string.Join("\t", PhaseTimes.Columns);

        private readonly StreamWriter _writer;
        private readonly RunOptions _options;
        private readonly StringBuilder _line = new();

        public ResultWriter(string path, RunOptions options)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _writer = new StreamWriter(path, false, new UTF8Encoding(false));
            _options = options;
        }

        public void WriteEnvironment(IEnumerable<(string Key, string Value)> entries)
        {
            foreach (var (key, value) in entries)
            {
                _writer.Write("# ");
                _writer.Write(key);
                _writer.Write('\t');
                _writer.WriteLine(value.Replace('\t', ' ').Replace('\n', ' '));
            }

            _writer.WriteLine(Columns);
        }

        public void WriteFrame(string scenario, int n, int frame, FrameSample s, double intervalMs)
        {
            var sameRenderThread = s.RenderStartThread == s.RenderEndThread;

            _line.Clear();
            Append(scenario).Append(n).Append(_options.Mode).Append(_options.Render).Append(_options.Pass).Append(frame)
                .Append(intervalMs)
                .Append(FrameRunner.Ms(s.RenderEnd - s.RenderStart))
                .Append(sameRenderThread ? s.RenderCpuEnd - s.RenderCpuStart : double.NaN)
                .Append(FrameRunner.Ms(s.UiEnd - s.UiStart))
                .Append(s.UiCpuEnd - s.UiCpuStart)
                .Append(FrameRunner.Ms(s.RenderEnd - s.UiStart))
                .Append(s.UiAllocEnd - s.UiAllocStart)
                .Append(sameRenderThread ? s.RenderAllocEnd - s.RenderAllocStart : -1)
                .Append(s.Gc0).Append(s.Gc1).Append(s.Gc2).Append(s.GcPauseMs)
                .Append(s.HeapBytes).Append(s.PrivateBytes).Append(s.MaskCacheBytes).Append(s.AtlasBytes)
                .Append(s.MaskEvictions).Append(s.AtlasEvictions)
                .Append(s.TierMask).Append(s.TierTransformed).Append(s.TierBlob)
                .Append(sameRenderThread ? s.AtlasDrawsEnd - s.AtlasDrawsStart : -1)
                .Append(sameRenderThread ? s.PageUploadsEnd - s.PageUploadsStart : -1)
                .Append(sameRenderThread ? s.AtlasGeometryEnd - s.AtlasGeometryStart : -1)
                .Append(sameRenderThread ? s.MaxRunsPerBatch : -1)
                .Append(s.AtlasPages).Append(s.AtlasPagesShared).Append(s.AtlasFaces)
                .Append(s.BudgetUsedBytes).Append(s.BudgetPeakBytes).Append(s.BudgetEvictedBytes);

            for (var i = 0; i < RenderCounters.Count; i++)
            {
                Append(sameRenderThread ? s.CountersEnd[i] - s.CountersStart[i] : -1);
            }

            for (var i = 0; i < PhaseTimes.Count; i++)
            {
                Append(sameRenderThread
                    ? PhaseTimes.TicksToMicroseconds(s.PhaseTicksEnd[i] - s.PhaseTicksStart[i])
                    : double.NaN);
            }

            for (var i = 0; i < PhaseTimes.Count; i++)
            {
                Append(sameRenderThread ? s.PhaseCountsEnd[i] - s.PhaseCountsStart[i] : -1);
            }

            _line.Length--;
            _writer.WriteLine(_line);
        }

        private ResultWriter Append(string value)
        {
            _line.Append(value).Append('\t');
            return this;
        }

        private ResultWriter Append(long value)
        {
            _line.Append(value.ToString(CultureInfo.InvariantCulture)).Append('\t');
            return this;
        }

        private ResultWriter Append(double value)
        {
            _line.Append(double.IsNaN(value) ? "nan" : value.ToString("0.####", CultureInfo.InvariantCulture))
                .Append('\t');
            return this;
        }

        public void Dispose() => _writer.Dispose();
    }
}
