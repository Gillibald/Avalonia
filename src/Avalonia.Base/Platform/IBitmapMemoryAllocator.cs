using System;
using Avalonia.Metadata;

namespace Avalonia.Platform
{
    /// <summary>
    /// Allocates native memory for decoded frames, pipeline staging and encode buffers.
    /// The default implementation is a bucketed pool honoring
    /// <see cref="Avalonia.Media.Imaging.ImagingOptions.DecodePoolMaxBytes"/>; tests bind a
    /// counting decorator to assert copy budgets and pool balance.
    /// </summary>
    [Unstable]
    public interface IBitmapMemoryAllocator
    {
        /// <summary>
        /// Rents a native buffer of at least <paramref name="byteCount"/> bytes. The
        /// buffer contents are undefined - a recycled buffer still holds the previous
        /// rental's bytes - so a renter must fully overwrite every byte it exposes.
        /// </summary>
        IBitmapMemory Rent(long byteCount);
    }

    /// <summary>
    /// A rented native buffer. Disposing returns it to its allocator; the allocator's
    /// finalizer backstop reclaims leaked rentals, but deterministic disposal is the contract.
    /// </summary>
    [Unstable]
    public interface IBitmapMemory : IDisposable
    {
        /// <summary>
        /// Gets the address of the buffer. Stable for the lifetime of the rental.
        /// </summary>
        IntPtr Address { get; }

        /// <summary>
        /// Gets the usable size of the buffer in bytes (at least the requested count).
        /// </summary>
        long ByteCount { get; }
    }
}
