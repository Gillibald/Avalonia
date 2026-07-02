using System.Threading.Tasks;
using Avalonia.Imaging.TestKit.Fixtures;
using Avalonia.Media.Imaging;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Pool discipline: parallel decodes stay within the memory ceiling implied by
    /// <see cref="ImagingOptions.MaxConcurrentDecodes"/> and always return balanced.
    /// </summary>
    public abstract class PoolContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        /// <summary>Why the remaining pool tests are skipped for now.</summary>
        protected const string SkipReason =
            "Needs the render-install seam and playback frame reuse (animation is deferred " +
            "project-wide), so per-decode copy budgets and pooled retention become " +
            "observable end to end.";

        [Fact]
        public void ParallelDecodes_PeakBounded_ByMaxConcurrentDecodes()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true },
                "The manifest does not declare PNG decoding.");

            const int maxConcurrent = 2;
            const int workerCount = 8;

            var fixture = FixtureImages.Gradient128Png;

            // Materialize the backend and the encoded fixture before the workers race.
            _ = Backend;
            _ = fixture.EncodedBytes;

            using var scope = AvaloniaLocator.EnterScope();

            AvaloniaLocator.CurrentMutable.Bind<ImagingOptions>()
                .ToConstant(new ImagingOptions { MaxConcurrentDecodes = maxConcurrent });

            var tasks = new Task[workerCount];

            for (var i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    using var decoder = CreateDecoder(fixture);
                    using var frame = ReadFirstFrame(decoder);
                    using var view = frame.Lock();

                    Assert.Equal(fixture.ExpectedSize, view.Size);
                });
            }

            Task.WaitAll(tasks);

            // Each gated decode may hold its destination plus pipeline staging while it
            // runs, and a finished frame lives for a moment between leaving the gate
            // and its dispose. Grant each slot three frame-sized buffers (at four
            // bytes per pixel) plus fixed scratch; without the gate, eight workers
            // would trend toward eight live frames and blow through this ceiling.
            var frameBytes = (long)fixture.ExpectedSize.Width * fixture.ExpectedSize.Height * 4;
            var perSlot = 3 * frameBytes + 64 * 1024;

            Assert.True(Allocator.PeakLiveBytes <= maxConcurrent * perSlot,
                $"Peak pooled bytes {Allocator.PeakLiveBytes} exceed {maxConcurrent} decode slots " +
                $"of {perSlot} bytes each.");

            Allocator.AssertBalanced();
        }

        [Fact(Skip = SkipReason)]
        public void Decode_StaysWithinDeclaredCopyBudget()
        {
        }

        [Fact(Skip = SkipReason)]
        public void InstallDestination_IsTheOnlyFrameSizedRent()
        {
        }

        [Fact(Skip = SkipReason)]
        public void PoolRetention_HonorsDecodePoolMaxBytes()
        {
        }
    }
}
