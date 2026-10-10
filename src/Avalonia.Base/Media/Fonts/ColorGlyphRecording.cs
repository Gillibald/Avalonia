using System.Threading;
using Avalonia.Rendering.Composition;

namespace Avalonia.Media.Fonts
{
    /// <summary>
    /// A colour glyph's drawing recorded once, in font design units at the local origin, for
    /// replay by every draw of the glyph. Cached as a <see cref="GlyphPayloadKind.ColorRecording"/>
    /// payload of the typeface's <see cref="GlyphCache"/>.
    /// </summary>
    /// <remarks>
    /// The cache hands payloads out without a lock, so it cannot dispose one when it evicts it:
    /// another thread may be about to replay it. A draw therefore takes a lease
    /// (<see cref="TryAcquire"/> / <see cref="Release"/>) around the replay, and the cache calls
    /// <see cref="Retire"/> where it gives the payload up; the recording is disposed when it is
    /// retired and no lease is open, exactly once. Render data that drew the recording holds its
    /// own counted reference to the recorded stream and keeps drawing it after that.
    /// </remarks>
    internal sealed class ColorGlyphRecording
    {
        private const int RetiredFlag = 1 << 30;

        private int _state;

        public ColorGlyphRecording(DrawingRecording recording, bool usesForeground)
        {
            Recording = recording;
            UsesForeground = usesForeground;
        }

        /// <summary>The recorded drawing.</summary>
        public DrawingRecording Recording { get; }

        /// <summary>
        /// Whether the paint graph resolves the CPAL foreground sentinel, so the recording made
        /// without a foreground cannot stand in for a draw with one.
        /// </summary>
        public bool UsesForeground { get; }

        /// <summary>
        /// Takes a lease that keeps <see cref="Recording"/> undisposed until the matching
        /// <see cref="Release"/>; <c>false</c> once the recording was retired.
        /// </summary>
        public bool TryAcquire()
        {
            while (true)
            {
                var state = Volatile.Read(ref _state);

                if ((state & RetiredFlag) != 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _state, state + 1, state) == state)
                {
                    return true;
                }
            }
        }

        /// <summary>Ends a lease taken by <see cref="TryAcquire"/>.</summary>
        public void Release()
        {
            if (Interlocked.Decrement(ref _state) == RetiredFlag)
            {
                Recording.Dispose();
            }
        }

        /// <summary>Gives the recording up; it is disposed now, or at the release of the last open lease.</summary>
        public void Retire()
        {
            var previous = Interlocked.Or(ref _state, RetiredFlag);

            if (previous == 0)
            {
                Recording.Dispose();
            }
        }
    }
}
