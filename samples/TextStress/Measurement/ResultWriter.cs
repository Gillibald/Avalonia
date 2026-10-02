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
        public const string Columns =
            "scenario\tn\tmode\trender\tpass\tframe\tinterval_ms\trender_ms\trender_cpu_ms\tui_ms\tui_cpu_ms\t" +
            "latency_ms\tui_alloc\trender_alloc\tgc0\tgc1\tgc2\tgc_pause_ms\theap_bytes\tprivate_bytes\t" +
            "mask_cache_bytes\tatlas_bytes\tmask_evictions\tatlas_evictions\ttier_mask\ttier_transformed\ttier_blob";

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
                .Append(s.TierMask).Append(s.TierTransformed).Append(s.TierBlob);

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
