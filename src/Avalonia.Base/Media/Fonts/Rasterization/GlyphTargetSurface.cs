using Avalonia.Platform;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// The surface a drawing context draws into, as managed text sees it: how
    /// <see cref="IDrawingContextImpl.Transform"/> maps to device pixels, what produces the
    /// pixels, and what the surface lets glyph masks be. Text makes every size, strike, snapping
    /// and subpixel decision in device pixels, from <see cref="ToDevice"/>.
    /// </summary>
    /// <param name="DeviceScale">
    /// The scale the target applies after <see cref="IDrawingContextImpl.Transform"/>: (1, 1) on
    /// window and compositor targets, which carry their render scaling in the transform; the DPI
    /// scale on targets that scale their drawing to their DPI, such as a render target bitmap or
    /// the intermediate surface of a tile brush; on a picture recorded in DIPs, such as a scene
    /// brush's, the scale the picture is drawn at on the device.
    /// </param>
    /// <param name="RasterTarget">What rasterizes the target's pixels.</param>
    /// <param name="PrefersAlphaMasks">
    /// Whether the context benefits from untinted alpha masks tinted per draw
    /// (<see cref="IAlphaGlyphMaskContext"/>). On a GPU-backed canvas, modulating an A8 texture
    /// by the paint colour is the native mechanism; on a CPU raster pipeline the same draw is
    /// several times slower than a pre-tinted BGRA blit, so such contexts keep the portable floor.
    /// </param>
    /// <param name="MaxRunMaskSize">
    /// The largest width or height, in device pixels, of one realized run mask: a GPU target's
    /// maximum texture dimension, <see cref="int.MaxValue"/> on a raster target, which has no
    /// texture to fit and leaves only the renderer's memory bound.
    /// </param>
    /// <param name="PixelGeometry">
    /// The stripe order of a display surface that can show subpixel text, <c>null</c> on any other
    /// surface. Whether a particular draw may use it also depends on the context's state at that
    /// moment (see <see cref="IAlphaGlyphMaskContext.TryGetLcdGeometry"/>).
    /// </param>
    internal readonly record struct GlyphTargetSurface(
        Vector DeviceScale,
        GlyphRasterTarget RasterTarget,
        bool PrefersAlphaMasks,
        int MaxRunMaskSize,
        LcdMaskGeometry? PixelGeometry)
    {
        /// <summary>
        /// The description of a context that describes no surface: no scale after the transform,
        /// pre-tinted masks bounded by <see cref="MaskGlyphRunRenderer.DefaultMaxRunMaskSize"/>
        /// and no subpixel text.
        /// </summary>
        public static GlyphTargetSurface Default { get; } = new(new Vector(1, 1), GlyphRasterTarget.Raster, false,
            MaskGlyphRunRenderer.DefaultMaxRunMaskSize, null);

        /// <summary>The surface <paramref name="context"/> draws into.</summary>
        public static GlyphTargetSurface Of(IDrawingContextImpl context)
            => context is IGlyphTargetContext target ? target.TargetSurface : Default;

        /// <summary>Whether the target scales what is drawn after the transform.</summary>
        public bool ScalesDrawing => DeviceScale != new Vector(1, 1);

        /// <summary>The map from local coordinates under <paramref name="transform"/> to device pixels.</summary>
        public Matrix ToDevice(in Matrix transform)
            => ScalesDrawing ? transform * Matrix.CreateScale(DeviceScale.X, DeviceScale.Y) : transform;

        /// <summary>
        /// The transform under which coordinates are device pixels, for draws placed 1:1 on the
        /// device grid: the identity on a target that does not scale its drawing.
        /// </summary>
        public Matrix DevicePixels
            => ScalesDrawing ? Matrix.CreateScale(1 / DeviceScale.X, 1 / DeviceScale.Y) : Matrix.Identity;
    }

    /// <summary>
    /// A drawing context that describes its target surface to managed text; the base of the
    /// backend seams text draws through (<see cref="IAlphaGlyphMaskContext"/>,
    /// <see cref="ITransformedGlyphContext"/>).
    /// </summary>
    internal interface IGlyphTargetContext
    {
        /// <summary>The surface this context draws into.</summary>
        GlyphTargetSurface TargetSurface { get; }

        /// <summary>
        /// The text raster state of the thread drawing this context, of the services of the
        /// application the context was created in.
        /// </summary>
        TextRasterThreadState RasterState { get; }

        /// <summary>The tuning of the context's application, read once, before the context draws text.</summary>
        GlyphRasterTuning RasterTuning { get; }
    }
}
