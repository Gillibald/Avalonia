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
        private readonly Action<SharedFontData>? _released;
        private int _refCount;

        /// <summary>
        /// Initializes a new instance over the specified memory owner with a reference count of one.
        /// </summary>
        /// <param name="owner">The object owning the font file bytes; disposed when the last reference is released.</param>
        /// <param name="memory">The font file bytes.</param>
        /// <param name="released">Invoked once when the last reference is released, before the owner is disposed.</param>
        public SharedFontData(IDisposable owner, ReadOnlyMemory<byte> memory, Action<SharedFontData>? released = null)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _released = released;
            Memory = memory;
            _refCount = 1;
        }

        /// <summary>
        /// Gets the font file bytes. Only valid while at least one reference is held.
        /// </summary>
        public ReadOnlyMemory<byte> Memory { get; }

        /// <summary>
        /// Gets the object owning the font file bytes.
        /// </summary>
        public IDisposable Owner => _owner;

        /// <summary>
        /// Adds a reference. Throws if the data has already been released.
        /// </summary>
        public void AddRef()
        {
            if (!TryAddRef())
            {
                throw new ObjectDisposedException(nameof(SharedFontData));
            }
        }

        /// <summary>
        /// Adds a reference unless the last one has already been released. A cache that hands out
        /// data it does not own a reference to uses this to tell a live entry from a dying one.
        /// </summary>
        /// <returns><see langword="true"/> if a reference was added; otherwise, <see langword="false"/>.</returns>
        public bool TryAddRef()
        {
            while (true)
            {
                var current = Volatile.Read(ref _refCount);

                if (current <= 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
                {
                    return true;
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
                _released?.Invoke(this);
                _owner.Dispose();
            }
        }
    }
}
