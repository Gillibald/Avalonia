using System;
using System.Threading;

namespace Avalonia.Platform.Internal
{
    /// <summary>
    /// A reference-counted native pixel buffer shared between a frame, its outstanding
    /// framebuffer views and a render install. The memory returns to its allocator when
    /// the last reference is released.
    /// </summary>
    internal sealed class SharedBitmapMemory
    {
        private readonly IBitmapMemory _memory;
        private int _refCount = 1;   // the creator's reference

        public SharedBitmapMemory(IBitmapMemory memory)
        {
            _memory = memory;
        }

        public IntPtr Address => _memory.Address;

        public void AddRef()
        {
            int current;

            do
            {
                current = Volatile.Read(ref _refCount);

                if (current == 0)
                    throw new ObjectDisposedException(nameof(SharedBitmapMemory));
            }
            while (Interlocked.CompareExchange(ref _refCount, current + 1, current) != current);
        }

        public void Release()
        {
            var remaining = Interlocked.Decrement(ref _refCount);

            if (remaining == 0)
            {
                _memory.Dispose();
            }
            else if (remaining < 0)
            {
                throw new InvalidOperationException(
                    "The shared bitmap memory was released more often than it was referenced.");
            }
        }

        /// <summary>
        /// Creates a framebuffer view holding one reference. Disposing the view releases
        /// it; disposing more than once is safe (a render install disposes the view it is
        /// handed, and callers may also dispose defensively).
        /// </summary>
        public ILockedFramebuffer CreateView(PixelSize size, int rowBytes, Vector dpi,
            PixelFormat format, AlphaFormat alphaFormat)
        {
            AddRef();

            return new View(this, size, rowBytes, dpi, format, alphaFormat);
        }

        private sealed class View : ILockedFramebuffer
        {
            private readonly SharedBitmapMemory _owner;
            private int _disposed;

            public View(SharedBitmapMemory owner, PixelSize size, int rowBytes, Vector dpi,
                PixelFormat format, AlphaFormat alphaFormat)
            {
                _owner = owner;
                Size = size;
                RowBytes = rowBytes;
                Dpi = dpi;
                Format = format;
                AlphaFormat = alphaFormat;
            }

            public IntPtr Address => _disposed == 0
                ? _owner.Address
                : throw new ObjectDisposedException(nameof(ILockedFramebuffer));

            public PixelSize Size { get; }

            public int RowBytes { get; }

            public Vector Dpi { get; }

            public PixelFormat Format { get; }

            public AlphaFormat AlphaFormat { get; }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    _owner.Release();
            }
        }
    }
}
