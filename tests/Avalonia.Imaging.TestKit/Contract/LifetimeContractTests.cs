using System;
using Avalonia.Imaging.TestKit.Fixtures;
using Xunit;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Lifetime rules at the SPI seam: pooled frame memory returns only after the frame
    /// and every locked view are disposed, disposed objects reject further use, and
    /// dispose order is forgiving.
    /// </summary>
    public abstract class LifetimeContractTests<TFixture> : ImagingContractTests<TFixture>
        where TFixture : CodecBackendFixture, new()
    {
        [Fact]
        public void PooledBuffer_ReturnedAfterFrameAndViewsDispose()
        {
            var fixture = RequireAnyDecodableReferenceFixture();

            using var decoder = CreateDecoder(fixture);

            var frame = ReadFirstFrame(decoder);
            var view = frame.Lock();

            Assert.True(Allocator.OutstandingRentals > 0, "Locking a frame must hold pooled memory.");

            // An open view keeps the pixels alive across the frame's dispose.
            frame.Dispose();

            Assert.True(Allocator.OutstandingRentals > 0, "An open view must keep the frame memory rented.");
            Assert.NotEqual(IntPtr.Zero, view.Address);

            view.Dispose();

            Allocator.AssertBalanced();
        }

        [Fact]
        public void UseAfterDispose_Throws()
        {
            var fixture = RequireAnyDecodableReferenceFixture();

            var decoder = CreateDecoder(fixture);
            var frame = ReadFirstFrame(decoder);

            frame.Dispose();

            Assert.Throws<ObjectDisposedException>(() => frame.Lock());

            decoder.Dispose();

            Assert.Throws<ObjectDisposedException>(() => decoder.ReadNextFrame());
        }

        [Fact]
        public void DisposeDuringOutstandingLock_Safe()
        {
            var fixture = RequireAnyDecodableReferenceFixture();

            var decoder = CreateDecoder(fixture);
            var frame = ReadFirstFrame(decoder);
            var view = frame.Lock();

            // Decoder first, then frame, with the view still open: every order works
            // and the pixels stay readable until the last view closes.
            decoder.Dispose();
            frame.Dispose();

            AssertRgbaMatchesReference(fixture, FramebufferPixels.ReadRgba(view), view.AlphaFormat);

            view.Dispose();
            view.Dispose(); // double dispose is safe

            Allocator.AssertBalanced();
        }
    }
}
