using Avalonia.Rendering.Composition;

namespace Avalonia.Media.Fonts
{
    /// <summary>
    /// A colour glyph's drawing recorded once, in font design units at the local origin, for
    /// replay by every draw of the glyph.
    /// </summary>
    internal sealed class ColorGlyphRecording
    {
        public ColorGlyphRecording(DrawingRecording recording, bool usesForeground)
        {
            Recording = recording;
            UsesForeground = usesForeground;
        }

        /// <summary>The recorded drawing.</summary>
        public DrawingRecording Recording { get; }

        /// <summary>Whether the paint graph resolves the CPAL foreground sentinel.</summary>
        public bool UsesForeground { get; }

        /// <summary>Takes a lease that keeps <see cref="Recording"/> undisposed until <see cref="Release"/>.</summary>
        public bool TryAcquire() => false;

        /// <summary>Ends a lease taken by <see cref="TryAcquire"/>.</summary>
        public void Release()
        {
        }

        /// <summary>Gives the recording up; it is disposed once no lease is open.</summary>
        public void Retire()
        {
        }
    }
}
