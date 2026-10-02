using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Transport;
using Avalonia.Skia;
using TextStress.Scenarios;

namespace TextStress.Measurement
{
    /// <summary>
    /// Drives a scenario frame by frame through the window's real compositor. For frame N
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
        private readonly Window _window;
        private readonly Scenario _scenario;
        private readonly RunOptions _options;
        private readonly ResultWriter? _writer;
        private Compositor? _compositor;
        private FrameSample? _awaitingCommit;

        public FrameRunner(Window window, Scenario scenario, RunOptions options, ResultWriter? writer)
        {
            _window = window;
            _scenario = scenario;
            _options = options;
            _writer = writer;
        }

        public async Task RunAsync()
        {
            _compositor = ElementComposition.GetElementVisual(_window)?.Compositor
                          ?? throw new InvalidOperationException("The window has no compositor.");
            _compositor.AfterCommit += OnAfterCommit;
            TextTierDiagnostics.CountTiers = true;

            var scaling = _window.RenderScaling;
            var environmentWritten = false;

            foreach (var n in _options.N)
            {
                _window.Content = _scenario.CreateView(n, scaling);

                // Let layout, template application and first rendering of the new content settle.
                for (var i = 0; i < 3; i++)
                {
                    await RunFrameAsync(-1, apply: false);
                }

                if (_writer is not null && !environmentWritten)
                {
                    _writer.WriteEnvironment(DescribeEnvironment(scaling));
                    environmentWritten = true;
                }

                var interactive = _options.Frames <= 0;
                var total = _options.Warmup + _options.Frames;
                var previous = new FrameSample();
                Snapshot(previous);
                long previousRenderEnd = 0;
                var recent = new List<double>();
                var watch = Stopwatch.StartNew();

                for (var frame = 0; interactive || frame < total; frame++)
                {
                    if (interactive && !_window.IsVisible)
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

                    if (interactive)
                    {
                        recent.Add(Ms(sample.RenderEnd - sample.RenderStart));

                        if (recent.Count == 60)
                        {
                            recent.Sort();
                            _window.Title = string.Format(CultureInfo.InvariantCulture,
                                "TextStress {0} {1} | render p50 {2:F2} ms p95 {3:F2} ms | {4:F0} fps",
                                _scenario.Name, _options.Mode, recent[30], recent[57], 60 / watch.Elapsed.TotalSeconds);
                            recent.Clear();
                            watch.Restart();
                        }
                    }
                }

                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0} n={1} {2} {3}: {4} frames",
                    _scenario.Name, n, _options.Mode, _options.Render, _options.Frames));
            }

            _compositor.AfterCommit -= OnAfterCommit;
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
                sample.RenderStart = Stopwatch.GetTimestamp();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            _ = batch.Rendered.ContinueWith(_ =>
            {
                sample.RenderEnd = Stopwatch.GetTimestamp();
                sample.RenderEndThread = Environment.CurrentManagedThreadId;
                sample.RenderCpuEnd = ThreadClock.NowMs();
                sample.RenderAllocEnd = GC.GetAllocatedBytesForCurrentThread();
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
            target.HeapBytes = GC.GetTotalMemory(false);
            target.PrivateBytes = ThreadClock.PrivateBytes();

            // A simulated face shares its unsimulated face's cache and atlas, so count each once.
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

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
                    target.AtlasBytes += atlas.AllocatedBytes;
                    target.AtlasEvictions += atlas.Evictions;
                }
            }

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
            sample.TierMask = after.TierMask - before.TierMask;
            sample.TierTransformed = after.TierTransformed - before.TierTransformed;
            sample.TierBlob = after.TierBlob - before.TierBlob;
        }

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
            yield return ("client_size", FormattableString.Invariant($"{_window.ClientSize.Width}x{_window.ClientSize.Height}"));
            yield return ("thread_clock", ThreadClock.IsThreadCpu
                ? FormattableString.Invariant($"QueryThreadCycleTime at {ThreadClock.CyclesPerMs:F0} cycles/ms")
                : "wall");
            yield return ("ui_thread", Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture));
            yield return ("scenario", _scenario.Name);
            yield return ("sweep", _scenario.SweepDimension ?? "-");
            yield return ("params", _scenario.Describe());
            yield return ("fonts", string.Join(", ", fonts));
            yield return ("mode", _options.Mode);
            yield return ("render", _options.Render);
            yield return ("pass", _options.Pass.ToString(CultureInfo.InvariantCulture));
            yield return ("seed", _options.Seed.ToString(CultureInfo.InvariantCulture));
            yield return ("frames", _options.Frames.ToString(CultureInfo.InvariantCulture));
            yield return ("warmup", _options.Warmup.ToString(CultureInfo.InvariantCulture));
        }
    }
}
