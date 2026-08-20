using System;
using System.Threading;

namespace Avalonia.Media.Fonts
{
    /// <summary>
    /// Reference-counted holder for the raw bytes of one font file, shared between all
    /// <see cref="SfntFace"/> views over that file (multiple faces of a collection, synthetic
    /// clones of the same face). The underlying memory owner is disposed when the last
    /// reference is released.
    /// </summary>
    internal sealed class SharedFontData
    {
        private readonly IDisposable _owner;
        private int _refCount;

        /// <summary>
        /// Initializes a new instance over the specified memory owner with a reference count of one.
        /// </summary>
        /// <param name="owner">The object owning the font file bytes; disposed when the last reference is released.</param>
        /// <param name="memory">The font file bytes.</param>
        public SharedFontData(IDisposable owner, ReadOnlyMemory<byte> memory)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            Memory = memory;
            _refCount = 1;
        }

        /// <summary>
        /// Gets the font file bytes. Only valid while at least one reference is held.
        /// </summary>
        public ReadOnlyMemory<byte> Memory { get; }

        /// <summary>
        /// Adds a reference. Throws if the data has already been released.
        /// </summary>
        public void AddRef()
        {
            while (true)
            {
                var current = Volatile.Read(ref _refCount);

                if (current <= 0)
                {
                    throw new ObjectDisposedException(nameof(SharedFontData));
                }

                if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Releases a reference, disposing the underlying memory owner when the count reaches zero.
        /// </summary>
        public void Release()
        {
            if (Interlocked.Decrement(ref _refCount) == 0)
            {
                _owner.Dispose();
            }
        }
    }
}
