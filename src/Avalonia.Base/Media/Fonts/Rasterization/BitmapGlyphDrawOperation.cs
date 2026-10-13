using System;
using Avalonia.Rendering.SceneGraph;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Draws one bitmap strike glyph, choosing the strike where it is drawn: text records its colour
    /// glyphs without knowing the device scale, and only the transform the draw replays under, with the
    /// scale its target applies after it (<see cref="GlyphTargetSurface"/>), tells the device pixels
    /// per em. The strike is the smallest at or above that size, as on the managed
    /// mask path, and its image comes from the typeface's recording for the size
    /// (<see cref="GlyphTypeface.GetBitmapGlyphRecording"/>), so render data that holds this
    /// operation stays valid when the scale changes and draws at an unchanged scale reuse one
    /// recording without decoding again.
    /// </summary>
    /// <remarks>
    /// Under an upright, uniformly scaled transform the image is filtered to the device size and
    /// copied with the pen rounded to a device pixel. Under any other transform it is filtered to the
    /// size the transform's area scale gives and the backend resamples it. Above the largest strike
    /// the backend enlarges that strike's image.
    /// </remarks>
    internal sealed class BitmapGlyphDrawOperation : ICustomDrawOperation
    {
        // Bounds are known before the device scale: the drawn image may extend past the exact image
        // rectangle by the rounding of the pen, the image offsets and the image size, which stays
        // below two device pixels; at device scales of one and above that is within two DIPs.
        private const double BoundsSlack = 2;

        private readonly GlyphTypeface _typeface;
        private readonly ushort _glyph;
        private readonly double _emSize;
        private readonly Point _pen;

        /// <param name="typeface">The typeface; <see cref="GlyphTypeface.IsBitmapGlyph"/> holds for the glyph.</param>
        /// <param name="glyph">The glyph.</param>
        /// <param name="emSize">The em size in DIPs.</param>
        /// <param name="pen">The glyph origin on the baseline, in DIPs.</param>
        public BitmapGlyphDrawOperation(GlyphTypeface typeface, ushort glyph, double emSize, Point pen)
        {
            _typeface = typeface;
            _glyph = glyph;
            _emSize = emSize;
            _pen = pen;
            Bounds = ComputeBounds(typeface, glyph, emSize).Translate(new Vector(pen.X, pen.Y));
        }

        public Rect Bounds { get; }

        public bool HitTest(Point p) => Bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            if (_typeface.BitmapSource is not { } source)
            {
                return;
            }

            var impl = context.PlatformImpl;
            var transform = impl.Transform;
            var surface = GlyphTargetSurface.Of(impl);
            var device = surface.ToDevice(transform);
            var upright = device.M12 == 0 && device.M21 == 0 && device.M11 > 0 && device.M22 > 0 &&
                          Math.Abs(device.M11 - device.M22) <= device.M11 * 0.001;
            var deviceScale = upright ? device.M11 : Math.Sqrt(Math.Abs(device.GetDeterminant()));
            var pixelsPerEm = _emSize * deviceScale;

            if (!(pixelsPerEm > 0) || double.IsInfinity(pixelsPerEm))
            {
                return;
            }

            // Above the largest strike the recording keeps that strike's own size, so enlarging
            // the view costs no image memory beyond the strike's.
            var largest = source.SelectStrike(float.MaxValue).PpemY;
            var copies = upright && pixelsPerEm <= largest;
            var pixelsPerEmQ = GlyphMaskKey.QuantizeScale((float)Math.Min(pixelsPerEm, largest));

            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (_typeface.GetBitmapGlyphRecording(_glyph, pixelsPerEmQ) is not { } recorded)
                {
                    return;
                }

                // A recording the cache retired between the lookup and the lease is rebuilt by the
                // next lookup.
                if (!recorded.TryAcquire())
                {
                    continue;
                }

                try
                {
                    if (copies)
                    {
                        var pen = _pen.Transform(device);

                        impl.Transform = Matrix.CreateTranslation(Math.Round(pen.X), Math.Round(pen.Y)) *
                                         surface.DevicePixels;
                    }
                    else
                    {
                        // The recording is in pixels of its own size: DIPs per pixel scale it back.
                        var unit = _emSize * GlyphMaskKey.ScaleQuantum / pixelsPerEmQ;

                        impl.Transform = Matrix.CreateScale(unit, unit) *
                                         Matrix.CreateTranslation(_pen.X, _pen.Y) * transform;
                    }

                    recorded.Recording.Render(impl);
                }
                finally
                {
                    impl.Transform = transform;
                    recorded.Release();
                }

                return;
            }
        }

        public bool Equals(ICustomDrawOperation? other)
            => other is BitmapGlyphDrawOperation operation && ReferenceEquals(operation._typeface, _typeface) &&
               operation._glyph == _glyph && operation._emSize == _emSize && operation._pen == _pen;

        public override bool Equals(object? obj) => obj is ICustomDrawOperation other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(_typeface, _glyph, _emSize, _pen);

        public void Dispose()
        {
        }

        // The union of the glyph's image rectangles over every strike, in DIPs at the origin, read
        // from the strike metrics. An image whose size is only known once decoded falls back to the
        // largest strike's decoded placement.
        private static Rect ComputeBounds(GlyphTypeface typeface, ushort glyph, double emSize)
        {
            if (typeface.BitmapSource is not { } source)
            {
                return default;
            }

            Rect? bounds = null;

            for (var i = 0; i < source.StrikeCount; i++)
            {
                var strike = source.GetStrike(i);

                if (source.TryGetImageBounds(strike, glyph, out var image))
                {
                    var rect = ToDips(image, emSize / strike.PpemY);

                    bounds = bounds is { } union ? union.Union(rect) : rect;
                }
            }

            if (bounds is null &&
                AvaloniaLocator.Current.GetService<IBitmapGlyphDecoder>() is { } decoder &&
                source.SelectStrike(float.MaxValue) is var largest &&
                source.TryGetPlacement(largest, glyph, decoder, out var placement))
            {
                bounds = ToDips(new PixelRect(placement.Left, placement.Top, placement.Bitmap.Width,
                    placement.Bitmap.Height), emSize / largest.PpemY);
            }

            return bounds?.Inflate(BoundsSlack) ?? default;
        }

        private static Rect ToDips(PixelRect image, double dipsPerPixel)
            => new(image.X * dipsPerPixel, image.Y * dipsPerPixel, image.Width * dipsPerPixel,
                image.Height * dipsPerPixel);
    }
}
