using System;
using Avalonia.Platform;

namespace Avalonia.Imaging.TestKit.Instrumentation
{
    /// <summary>
    /// A decorating <see cref="IBitmapFrameSource"/> counting <see cref="Lock"/> calls,
    /// distinguishing the first call - the one that pays for the decode - from repeats.
    /// </summary>
    public sealed class CountingFrameSource : IBitmapFrameSource
    {
        private readonly IBitmapFrameSource _inner;

        public CountingFrameSource(IBitmapFrameSource inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        /// <summary>Gets the wrapped frame.</summary>
        public IBitmapFrameSource Inner => _inner;

        /// <summary>Gets the total number of <see cref="Lock"/> calls on this instance.</summary>
        public int TotalLockCount { get; private set; }

        /// <summary>
        /// Gets 1 once the decode-triggering first <see cref="Lock"/> happened, 0 before.
        /// </summary>
        public int FirstLockCount { get; private set; }

        public PixelSize PixelSize => _inner.PixelSize;

        public Vector Dpi => _inner.Dpi;

        public PixelFormat PixelFormat => _inner.PixelFormat;

        public AlphaFormat AlphaFormat => _inner.AlphaFormat;

        public ILockedFramebuffer Lock()
        {
            if (TotalLockCount == 0)
                FirstLockCount = 1;

            TotalLockCount++;

            return _inner.Lock();
        }

        public void Dispose() => _inner.Dispose();
    }
}
