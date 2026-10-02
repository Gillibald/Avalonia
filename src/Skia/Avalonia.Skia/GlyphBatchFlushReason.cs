namespace Avalonia.Skia
{
    /// <summary>
    /// Why pending glyph atlas batches were drawn before the drawing session ended them, as
    /// counted by <see cref="DrawingContextImpl.GetBatchesFlushedOnThread"/>.
    /// </summary>
    internal enum GlyphBatchFlushReason
    {
        /// <summary>A run overlaps a pending batch of another atlas page.</summary>
        PageChange,

        /// <summary>A run overlaps a pending batch of its page in another colour.</summary>
        ColorChange,

        /// <summary>Every pending batch slot is taken and a run needs a batch of a new page and colour.</summary>
        SlotPressure,

        /// <summary>The runs pending in other batches reached the limit a joining run is tested against.</summary>
        RunLimit,

        /// <summary>A batch holds as many sprites as one atlas draw takes.</summary>
        SpriteCap,

        /// <summary>A subpixel entry overlaps an entry of its own batch, which one draw cannot blend.</summary>
        Overlap,

        /// <summary>A canvas draw other than glyph atlas text: fills, strokes, bitmaps, clears.</summary>
        CanvasOperation,

        /// <summary>A clip pushed or popped.</summary>
        Clip,

        /// <summary>A layer, opacity, opacity mask or effect pushed or popped.</summary>
        Layer,

        /// <summary>The drawing session ended.</summary>
        EndOfSession,

        /// <summary>
        /// Text that takes another path: a subpixel batch after a grayscale one or the reverse, an
        /// atlas draw that cannot be batched, a run mask image, the native fallback.
        /// </summary>
        OtherTextPath,

        /// <summary>A surface read back or blitted, a lease of the canvas, a diagnostic overlay.</summary>
        Other,
    }
}
