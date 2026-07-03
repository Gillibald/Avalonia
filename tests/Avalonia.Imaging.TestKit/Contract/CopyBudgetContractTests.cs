using System;
using System.Threading.Tasks;
using Avalonia.Imaging.TestKit.Fixtures;
using Avalonia.Imaging.TestKit.Instrumentation;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Copy discipline of the decode-to-render path: a plain decode rents exactly the
    /// installed destination plus the manifest's per-format copy budget, the installed
    /// pixels are the pooled rental itself (zero-copy), and the install's lifetime is
    /// counted exactly.
    /// </summary>
    public abstract class CopyBudgetContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        private const string NoPngDecode = "The manifest does not declare PNG decoding.";

        // The 4x4 reference fixtures sit below the frame-sized rent threshold, so the
        // copy accounting runs on the 128x128 gradient. Decodes stay plan-free: with a
        // plan, a backend may legitimately pair a nearest-size decode buffer with the
        // exact target (per its FusedDecodeParts), which the budget does not model.
        private static ImageFixture BudgetFixture => FixtureImages.Gradient128Png;

        [Fact]
        public void DecodeToInstall_MeetsManifestCopyBudget()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true }, NoPngDecode);

            using var scope = BindPlatform();
            using var decoder = BitmapDecoder.Create(BudgetFixture.OpenRead());
            using var frame = decoder.ReadNextFrame()!;
            using var bitmap = frame.ToBitmap();

            Assert.Equal(1, Fixture.RenderInstall.InstallCount);

            var budget = TryGetFormat(FixtureImages.PngFormatName)!.CopyBudget;
            var frameSized = Allocator.FrameSizedRents(FrameSizedThreshold(BudgetFixture));

            // One frame-sized rent is the installed destination itself; the manifest
            // budget covers any extras.
            Assert.True(frameSized <= 1 + budget,
                $"The decode performed {frameSized} frame-sized rents; the destination " +
                $"plus a budget of {budget} allows {1 + budget}.");
        }

        [Fact]
        public void InstalledAddress_InLiveRentalSet()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true }, NoPngDecode);

            using var scope = BindPlatform();
            using var decoder = BitmapDecoder.Create(BudgetFixture.OpenRead());
            using var frame = decoder.ReadNextFrame()!;
            using var bitmap = frame.ToBitmap();

            var address = Fixture.RenderInstall.Address;

            Assert.Contains(address, Allocator.LiveAddresses);

            if (TryGetFormat(FixtureImages.PngFormatName)!.CopyBudget == 0)
            {
                // No plan and no copies: the installed view IS the single frame-sized rent.
                Assert.Equal(1L, Allocator.FrameSizedRents(FrameSizedThreshold(BudgetFixture)));
                Assert.Equal(address, Assert.Single(Allocator.LiveAddresses));
            }
        }

        [Fact]
        public void ReleaseDelegate_FiresOnceOnLastRef()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true }, NoPngDecode);

            using var scope = BindPlatform();
            using var decoder = BitmapDecoder.Create(BudgetFixture.OpenRead());

            var frame = decoder.ReadNextFrame()!;
            var bitmap = frame.ToBitmap();

            frame.Dispose();

            // The realized bitmap still references the install.
            Assert.Equal(1, Fixture.RenderInstall.InstallCount);
            Assert.Equal(0, Fixture.RenderInstall.ReleaseCount);

            bitmap.Dispose();

            Assert.Equal(1, Fixture.RenderInstall.ReleaseCount);
            Allocator.AssertBalanced();
        }

        [Fact]
        public void RenderRefOutlivesFrameDispose()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true }, NoPngDecode);

            using var scope = BindPlatform();
            using var decoder = BitmapDecoder.Create(BudgetFixture.OpenRead());

            Bitmap bitmap;

            using (var frame = decoder.ReadNextFrame()!)
            {
                bitmap = frame.ToBitmap();
            }

            using (bitmap)
            {
                var readable = Assert.IsAssignableFrom<IReadableBitmapImpl>(bitmap.PlatformImpl.Item);

                using var framebuffer = readable.Lock();

                Assert.NotEqual(IntPtr.Zero, framebuffer.Address);
                Assert.Equal(BudgetFixture.ExpectedSize, framebuffer.Size);
            }

            Assert.Equal(1, Fixture.RenderInstall.ReleaseCount);
            Allocator.AssertBalanced();
        }

        [Fact]
        public void PixelBufferSnapshot_ExactlyOneCopy()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true }, NoPngDecode);

            using var scope = BindPlatform();
            using var decoder = BitmapDecoder.Create(BudgetFixture.OpenRead());
            using var frame = decoder.ReadNextFrame()!;

            var buffer = frame.ToPixelBuffer();

            Assert.Equal(BudgetFixture.ExpectedSize, buffer.Size);

            // The snapshot is one managed copy: no frame-sized rent beyond the decode
            // destination (plus budget), and no render install at all.
            var budget = TryGetFormat(FixtureImages.PngFormatName)!.CopyBudget;

            Assert.True(Allocator.FrameSizedRents(FrameSizedThreshold(BudgetFixture)) <= 1 + budget);
            Assert.Equal(0, Fixture.RenderInstall.InstallCount);
        }

        [Fact]
        public void ReRender_DoesNotReDecode()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true }, NoPngDecode);

            using var scope = BindPlatform();
            using var decoder = BitmapDecoder.Create(BudgetFixture.OpenRead());
            using var frame = decoder.ReadNextFrame()!;

            using var first = frame.ToBitmap();

            var rentsAfterFirst = Allocator.RentCount;

            using var second = frame.ToBitmap();

            // The same realized render bitmap is shared; nothing decoded again.
            Assert.Same(first.PlatformImpl.Item, second.PlatformImpl.Item);
            Assert.Equal(1, Fixture.RenderInstall.InstallCount);
            Assert.Equal(rentsAfterFirst, Allocator.RentCount);
        }

        [Fact]
        public void ConcurrentRealizeSameFrame_OneDecode()
        {
            Assert.SkipWhen(TryGetFormat(FixtureImages.PngFormatName) is not { Decode: true }, NoPngDecode);

            using var scope = BindPlatform();
            using var decoder = BitmapDecoder.Create(BudgetFixture.OpenRead());

            var frame = decoder.ReadNextFrame()!;
            var bitmaps = new Bitmap[8];
            var tasks = new Task[bitmaps.Length];

            for (var i = 0; i < tasks.Length; i++)
            {
                var slot = i;

                tasks[i] = Task.Run(() => bitmaps[slot] = frame.ToBitmap());
            }

            Task.WaitAll(tasks);

            foreach (var bitmap in bitmaps)
                Assert.Same(bitmaps[0].PlatformImpl.Item, bitmap.PlatformImpl.Item);

            Assert.Equal(1, Fixture.RenderInstall.InstallCount);

            foreach (var bitmap in bitmaps)
                bitmap.Dispose();

            frame.Dispose();

            Assert.Equal(1, Fixture.RenderInstall.ReleaseCount);
            Allocator.AssertBalanced();
        }

        [Fact(Skip = "Animation playback is deferred project-wide; the steady-state reuse " +
                     "loop needs animated fixtures.")]
        public void SteadyStateAnimLoop_ReusesBuffers()
        {
        }

        private IDisposable BindPlatform() =>
            LocatorScope.With(Backend, new FakeRenderInterface(Fixture.RenderInstall));
    }
}
