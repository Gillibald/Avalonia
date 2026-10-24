#if PIXELMAN_FRAME_ALLOCATOR
using System;
using System.Collections.Concurrent;
using Avalonia.PixelMan;
using Avalonia.Platform;

namespace Avalonia.Imaging.PixelMan
{
    /// <summary>
    /// Decodes PixelMan frames into rentals from the imaging memory pool, so a frame whose
    /// decoded layout is already the plan's output is installed without a copy. It also
    /// rents the pipeline's own buffers from the same pool.
    /// </summary>
    internal sealed class PixelManFrameAllocator : PmFrameAllocator, IBitmapMemoryAllocator
    {
        private readonly IBitmapMemoryAllocator _pool;
        private readonly ConcurrentDictionary<IntPtr, IBitmapMemory> _unclaimed = new();

        public PixelManFrameAllocator(IBitmapMemoryAllocator pool)
        {
            _pool = pool;
        }

        public IBitmapMemory Rent(long byteCount) => _pool.Rent(byteCount);

        public override PmFrameMemory? Allocate(nuint length)
        {
            if (length > long.MaxValue)
                return null;

            var rental = _pool.Rent((long)length);
            var address = rental.Address;

            _unclaimed[address] = rental;

            return new PmFrameMemory(address, () =>
            {
                _unclaimed.TryRemove(address, out _);
                rental.Dispose();
            });
        }

        /// <summary>
        /// Takes over the rental a frame was decoded into. PixelMan still returns it when the
        /// frame and every lock on it are released, so the caller keeps a lock for as long as
        /// it uses the memory.
        /// </summary>
        public bool TryClaim(IntPtr address, out IBitmapMemory rental) => _unclaimed.TryRemove(address, out rental!);
    }
}
#endif
