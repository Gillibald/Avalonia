using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.OpenGL;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Transport;
using Avalonia.Skia;
using TextStress.Scenarios;

namespace TextStress.Measurement
{
    /// <summary>
    /// Drives a scenario frame by frame through the top level's real compositor (a desktop
    /// window or a mobile view). For frame N
    /// the scenario mutates the tree on the UI thread, the runner takes the composition batch
    /// that will carry the change and waits until the render thread has applied it and finished
    /// the render pass that drew it; only then does frame N+1 start. Frames therefore never
    /// overlap, and every reading belongs to exactly one frame.
    /// </summary>
    /// <remarks>
    /// The batch is <see cref="Compositor.RequestCompositionBatchCommitAsync"/>'s pending
    /// batch: the UI thread's layout and render-recording pass commits it after the mutation.
    /// Its <see cref="CompositionBatch.Processed"/> and <see cref="CompositionBatch.Rendered"/>
    /// tasks complete synchronously on the render thread, at the start of the render pass
    /// (after the batch is deserialized) and after every target has rendered and presented,
    /// so continuations attached with <see cref="TaskContinuationOptions.ExecuteSynchronously"/>
    /// read that thread's clocks. The compositor's <c>AfterCommit</c> event closes the UI phase.
    /// GPU execution is asynchronous to all of this: a GPU-bound frame shows up only once the
    /// driver blocks the present.
    /// </remarks>
    internal sealed class FrameRunner
    {
        private readonly TopLevel _topLevel;
        private readonly Scenario _scenario;
        private readonly RunOptions _options;
        private readonly ResultWriter? _writer;
        private Compositor? _compositor;
        private FrameSample? _awaitingCommit;

        /// <summary>
        /// Frames between heap size readings. Mono computes <see cref="GC.GetTotalMemory"/> by
        /// walking every block of the major heap, milliseconds per call on a phone, which would
        /// load the UI thread and the CPU clocks between measured frames; other runtimes keep a
        /// running total and read it every frame.
        /// </summary>
        private static readonly int s_heapReadPeriod = Type.GetType("Mono.RuntimeStructs") is null ? 1 : 60;

        private int _snapshots;
        private long _heapBytes;

        public FrameRunner(TopLevel topLevel, Scenario scenario, RunOptions options, ResultWriter? writer)
        {
            _topLevel = topLevel;
            _scenario = scenario;
            _options = options;
            _writer = writer;
        }

        public async Task RunAsync()
        {
            _compositor = ElementComposition.GetElementVisual(_topLevel)?.Compositor
                          ?? throw new InvalidOperationException("The top level has no compositor.");
            _compositor.AfterCommit += OnAfterCommit;
            TextTierDiagnostics.CountTiers = true;

            var scaling = _topLevel.RenderScaling;
            var environmentWritten = false;

            if (_options.Frames > 0 && _options.PrewarmMs > 0)
            {
                await PrewarmAsync(_options.N[0], scaling);
            }

            foreach (var n in _options.N)
            {
                _topLevel.Content = _scenario.CreateView(n, scaling);

                // Let layout, template application and first rendering of the new content settle.
                for (var i = 0; i < 3; i++)
                {
                    await RunFrameAsync(-1, apply: false);
                }

                if (_options.PrewarmPass && n == _options.N[0])
                {
                    for (var frame = 0; frame < _options.Warmup + _options.Frames; frame++)
                    {
                        await RunFrameAsync(frame, apply: true);
                    }
                }

                if (_writer is not null && !environmentWritten)
                {
                    _writer.WriteEnvironment(DescribeEnvironment(scaling));
                    environmentWritten = true;
                }

                GlyphCacheBudget.Shared.ResetPeak();
                var interactive = _options.Frames <= 0;
                var total = _options.Warmup + _options.Frames;
                var previous = new FrameSample();
                Snapshot(previous);
                long previousRenderEnd = 0;
                var recent = new List<double>();
                var watch = Stopwatch.StartNew();

                for (var frame = 0; interactive || frame < total; frame++)
                {
                    if (interactive && !_topLevel.IsVisible)
                    {
                        break;
                    }

                    var sample = await RunFrameAsync(frame, apply: true);
                    var current = new FrameSample();
                    Snapshot(current);
                    FillDeltas(sample, previous, current);
                    previous = current;

                    var measured = frame >= _options.Warmup;

                    if (measured && _writer is not null)
                    {
                        _writer.WriteFrame(_scenario.Name, n, frame - _options.Warmup, sample,
                            previousRenderEnd == 0 ? double.NaN : Ms(sample.RenderEnd - previousRenderEnd));
                    }

                    previousRenderEnd = sample.RenderEnd;

                    if (interactive && _topLevel is Window window)
                    {
                        recent.Add(Ms(sample.RenderEnd - sample.RenderStart));

                        if (recent.Count == 60)
                        {
                            recent.Sort();
                            window.Title = string.Format(CultureInfo.InvariantCulture,
                                "TextStress {0} {1} | render p50 {2:F2} ms p95 {3:F2} ms | {4:F0} fps",
                                _scenario.Name, _options.Mode, recent[30], recent[57], 60 / watch.Elapsed.TotalSeconds);
                            recent.Clear();
                            watch.Restart();
                        }
                    }
                }

                StressLog.Info(string.Format(CultureInfo.InvariantCulture, "{0} n={1} {2} {3}: {4} frames",
                    _scenario.Name, n, _options.Mode, _options.Render, _options.Frames));
                StressLog.Info(string.Format(CultureInfo.InvariantCulture, "budget n={0} end {1}", n, DescribeBudget()));
            }

            _compositor.AfterCommit -= OnAfterCommit;

            var settleMs = _options.GetInt("settle-ms", 0);

            if (settleMs > 0)
            {
                // No frames run meanwhile, so the idle trim after the last frame takes effect.
                await Task.Delay(settleMs);
                StressLog.Info(string.Format(CultureInfo.InvariantCulture, "budget settled after {0} ms {1}",
                    settleMs, DescribeBudget()));
            }
        }

        /// <summary>
        /// Runs the first sweep value's warmup frames over and over for a fixed wall time and
        /// discards them. Tiered compilation promotes hot methods only after they have run a
        /// while, so without this the first measured value of a process runs partly on
        /// unoptimized code and reads slower than the values after it.
        /// </summary>
        private async Task PrewarmAsync(int n, double scaling)
        {
            _topLevel.Content = _scenario.CreateView(n, scaling);

            var watch = Stopwatch.StartNew();
            var period = Math.Max(1, _options.Warmup);

            for (var frame = 0; watch.ElapsedMilliseconds < _options.PrewarmMs; frame++)
            {
                await RunFrameAsync(frame % period, apply: true);
            }
        }

        private async Task<FrameSample> RunFrameAsync(int frame, bool apply)
        {
            var compositor = _compositor!;
            var sample = new FrameSample { Frame = frame };

            sample.UiStart = Stopwatch.GetTimestamp();
            sample.UiCpuStart = ThreadClock.NowMs();
            sample.UiAllocStart = GC.GetAllocatedBytesForCurrentThread();

            if (apply)
            {
                _scenario.Apply(frame);
            }

            _awaitingCommit = sample;

            var batch = compositor.RequestCompositionBatchCommitAsync();
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            _ = batch.Processed.ContinueWith(_ =>
            {
                sample.RenderStartThread = Environment.CurrentManagedThreadId;
                sample.RenderCpuStart = ThreadClock.NowMs();
                sample.RenderAllocStart = GC.GetAllocatedBytesForCurrentThread();
                sample.AtlasDrawsStart = DrawingContextImpl.AtlasDrawsOnThread;
                sample.PageUploadsStart = DrawingContextImpl.PageImagesCreatedOnThread;
                sample.AtlasGeometryStart = DrawingContextImpl.AtlasGeometrySubmittedOnThread;
                RenderCounters.Read(sample.CountersStart);
                PhaseTimes.Read(sample.PhaseTicksStart, sample.PhaseCountsStart);
                DrawingContextImpl.TakeMaxRunsPerBatchOnThread();
                sample.RenderStart = Stopwatch.GetTimestamp();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            _ = batch.Rendered.ContinueWith(_ =>
            {
                sample.RenderEnd = Stopwatch.GetTimestamp();
                sample.RenderEndThread = Environment.CurrentManagedThreadId;
                sample.RenderCpuEnd = ThreadClock.NowMs();
                sample.RenderAllocEnd = GC.GetAllocatedBytesForCurrentThread();
                sample.AtlasDrawsEnd = DrawingContextImpl.AtlasDrawsOnThread;
                sample.PageUploadsEnd = DrawingContextImpl.PageImagesCreatedOnThread;
                sample.AtlasGeometryEnd = DrawingContextImpl.AtlasGeometrySubmittedOnThread;
                RenderCounters.Read(sample.CountersEnd);
                PhaseTimes.Read(sample.PhaseTicksEnd, sample.PhaseCountsEnd);
                sample.MaxRunsPerBatch = DrawingContextImpl.TakeMaxRunsPerBatchOnThread();
                rendered.TrySetResult();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            await rendered.Task;

            if (_awaitingCommit == sample)
            {
                // No commit was observed (the change needed none); close the UI phase here.
                OnAfterCommit();
            }

            return sample;
        }

        private void OnAfterCommit()
        {
            if (_awaitingCommit is not { } sample)
            {
                return;
            }

            sample.UiEnd = Stopwatch.GetTimestamp();
            sample.UiCpuEnd = ThreadClock.NowMs();
            sample.UiAllocEnd = GC.GetAllocatedBytesForCurrentThread();
            _awaitingCommit = null;
        }

        /// <summary>Cumulative process readings; frames store the differences.</summary>
        private void Snapshot(FrameSample target)
        {
            target.Gc0 = GC.CollectionCount(0);
            target.Gc1 = GC.CollectionCount(1);
            target.Gc2 = GC.CollectionCount(2);
            target.GcPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds;
            if (_snapshots++ % s_heapReadPeriod == 0)
            {
                _heapBytes = GC.GetTotalMemory(false);
            }

            target.HeapBytes = _heapBytes;
            target.PrivateBytes = ThreadClock.PrivateBytes();

            // A simulated face shares its unsimulated face's cache and atlas, so count each once.
            // A GL 3 or GLES 3 context places the masks of every face in the shared atlas, others in
            // each face's own atlas.
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var shared = GlyphMaskAtlas.Shared;
            var sharedPages = shared.GetPages().Length;

            seen.Add(shared);
            target.AtlasBytes += shared.AllocatedBytes;
            target.AtlasEvictions += shared.Evictions;
            target.AtlasPages += sharedPages;
            target.AtlasPagesShared = sharedPages;

            foreach (var typeface in _scenario.Typefaces)
            {
                var cache = typeface.MaskCache;

                if (seen.Add(cache))
                {
                    target.MaskCacheBytes += cache.TotalCost;
                    target.MaskEvictions += cache.Evictions;
                }

                var atlas = typeface.MaskAtlas;

                if (seen.Add(atlas))
                {
                    var pages = atlas.GetPages().Length;

                    target.AtlasBytes += atlas.AllocatedBytes;
                    target.AtlasEvictions += atlas.Evictions;
                    target.AtlasPages += pages;
                    target.AtlasFaces += pages > 0 ? 1 : 0;
                }
            }

            var budget = GlyphCacheBudget.Shared;
            target.BudgetUsedBytes = budget.UsedBytes;
            target.BudgetPeakBytes = budget.PeakBytes;
            target.BudgetEvictedBytes = budget.EvictedBytes;

            target.TierMask = Interlocked.Read(ref TextTierDiagnostics.MaskTierDraws);
            target.TierTransformed = Interlocked.Read(ref TextTierDiagnostics.TransformedMaskTierDraws);
            target.TierBlob = Interlocked.Read(ref TextTierDiagnostics.BlobTierDraws);
        }

        private static void FillDeltas(FrameSample sample, FrameSample before, FrameSample after)
        {
            sample.Gc0 = after.Gc0 - before.Gc0;
            sample.Gc1 = after.Gc1 - before.Gc1;
            sample.Gc2 = after.Gc2 - before.Gc2;
            sample.GcPauseMs = after.GcPauseMs - before.GcPauseMs;
            sample.HeapBytes = after.HeapBytes;
            sample.PrivateBytes = after.PrivateBytes;
            sample.MaskCacheBytes = after.MaskCacheBytes;
            sample.AtlasBytes = after.AtlasBytes;
            sample.MaskEvictions = after.MaskEvictions - before.MaskEvictions;
            sample.AtlasEvictions = after.AtlasEvictions - before.AtlasEvictions;
            sample.AtlasPages = after.AtlasPages;
            sample.AtlasPagesShared = after.AtlasPagesShared;
            sample.AtlasFaces = after.AtlasFaces;
            sample.BudgetUsedBytes = after.BudgetUsedBytes;
            sample.BudgetPeakBytes = after.BudgetPeakBytes;
            sample.BudgetEvictedBytes = after.BudgetEvictedBytes - before.BudgetEvictedBytes;
            sample.TierMask = after.TierMask - before.TierMask;
            sample.TierTransformed = after.TierTransformed - before.TierTransformed;
            sample.TierBlob = after.TierBlob - before.TierBlob;
        }

        /// <summary>The glyph cache budget's limits and the bytes each pool kind holds, in MB.</summary>
        private static string DescribeBudget()
        {
            var budget = GlyphCacheBudget.Shared;
            var line = new System.Text.StringBuilder();

            line.Append(FormattableString.Invariant(
                $"limit={Mb(budget.LimitBytes):F1} retain={Mb(budget.RetainBytes):F1} used={Mb(budget.UsedBytes):F2} peak={Mb(budget.PeakBytes):F2} evicted_total={Mb(budget.EvictedBytes):F2}"));

            foreach (var kind in Enum.GetValues<GlyphCachePoolKind>())
            {
                line.Append(FormattableString.Invariant($" {kind}={Mb(budget.BytesOf(kind)):F2}"));
            }

            return line.ToString();
        }

        private static double Mb(long bytes) => bytes / 1048576.0;

        internal static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private IEnumerable<(string Key, string Value)> DescribeEnvironment(double scaling)
        {
            var graphics = AvaloniaLocator.Current.GetService<IPlatformGraphics>();
            var renderer = "none";

            if (graphics is not null)
            {
                try
                {
                    var shared = graphics.UsesSharedContext;
                    var context = shared ? graphics.GetSharedContext() : graphics.CreateContext();

                    if (context is IGlContext gl)
                    {
                        renderer = gl.GlInterface.Renderer ?? "unknown";
                    }
                    else
                    {
                        renderer = context.GetType().Name;
                    }

                    if (!shared)
                    {
                        context.Dispose();
                    }
                }
                catch (Exception e)
                {
                    renderer = "error: " + e.GetType().Name;
                }
            }

            var fonts = new List<string>();

            foreach (var typeface in _scenario.Typefaces)
            {
                fonts.Add(typeface.FamilyName + " " + typeface.Weight);
            }

            yield return ("tag", _options.Tag);
            yield return ("date_utc", DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture));
            yield return ("os", System.Runtime.InteropServices.RuntimeInformation.OSDescription);
            yield return ("arch", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
            yield return ("runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            yield return ("aot", (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported).ToString());
            yield return ("cpu", Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown");
            yield return ("cores", Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));
            yield return ("gc", System.Runtime.GCSettings.IsServerGC ? "server" : "workstation");
            yield return ("graphics", graphics?.GetType().Name ?? "software (no IPlatformGraphics)");
            yield return ("gpu_renderer", renderer);
            yield return ("render_scaling", scaling.ToString(CultureInfo.InvariantCulture));
            yield return ("client_size", FormattableString.Invariant($"{_topLevel.ClientSize.Width}x{_topLevel.ClientSize.Height}"));
            yield return ("thread_clock", ThreadClock.Description);
            yield return ("phase_timers", GlyphPhaseTimers.Enabled
                ? FormattableString.Invariant(
                    $"on, stopwatch at {GlyphPhaseTimers.Frequency} Hz, probe pair {PhaseTimes.TicksToMicroseconds(1) * GlyphPhaseTimers.ProbeTicks * 1000:F1} ns")
                : "off");
            yield return ("ui_thread", Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture));
            yield return ("scenario", _scenario.Name);
            yield return ("sweep", _scenario.SweepDimension ?? "-");
            yield return ("params", _scenario.Describe() +
                             (_options.PrewarmPass ? ";prewarm-pass" : "") +
                             (_options.PhaseTimers ? ";phase-timers" : "") +
                             (_options.PendingBatches > 0
                                 ? FormattableString.Invariant($";pending-batches={_options.PendingBatches}")
                                 : ""));
            yield return ("pending_batches",
                DrawingContextImpl.MaxPendingBatches.ToString(CultureInfo.InvariantCulture));
            yield return ("prewarm_pass", _options.PrewarmPass.ToString());
            yield return ("fonts", string.Join(", ", fonts));
            yield return ("mode", _options.Mode);
            yield return ("resolved_mode",
                (AvaloniaLocator.Current.GetService<FontManagerOptions>() ?? new FontManagerOptions())
                .TextRasterizationMode.ToString());
            yield return ("render", _options.Render);
            yield return ("pass", _options.Pass.ToString(CultureInfo.InvariantCulture));
            yield return ("seed", _options.Seed.ToString(CultureInfo.InvariantCulture));
            yield return ("frames", _options.Frames.ToString(CultureInfo.InvariantCulture));
            yield return ("warmup", _options.Warmup.ToString(CultureInfo.InvariantCulture));
            yield return ("prewarm_ms", _options.PrewarmMs.ToString(CultureInfo.InvariantCulture));
            yield return ("glyph_cache_limit_mb",
                (GlyphCacheBudget.Shared.LimitBytes / 1048576.0).ToString(CultureInfo.InvariantCulture));
            yield return ("heap_read_period", s_heapReadPeriod.ToString(CultureInfo.InvariantCulture));

            foreach (var entry in StressLog.HostEnvironment)
            {
                yield return entry;
            }
        }
    }
}
