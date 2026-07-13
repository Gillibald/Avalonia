using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Fonts.Tables.Bitmaps;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Avalonia.Media
{
    /// <summary>
    /// A bitmap-strike glyph drawing: the decoded strike image placed by its bearings, exposed
    /// in font design units like every <see cref="IGlyphDrawing"/> (callers scale to the em size
    /// and land the origin on the pen; there is no Y-flip — strike bearings already describe
    /// the y-down placement relative to the pen).
    /// </summary>
    internal sealed class BitmapGlyphDrawing : IGlyphDrawing
    {
        private readonly Bitmap _bitmap;
        private readonly Rect _sourceRect;
        private readonly Rect _bounds;

        public unsafe BitmapGlyphDrawing(GlyphTypeface glyphTypeface, in DecodedGlyphBitmap decoded,
            in BitmapGlyphImage metrics, int strikePpem)
        {
            // Font units per strike pixel: the strike is a fixed-ppem raster of the em square.
            var unitsPerPixel = glyphTypeface.Metrics.DesignEmHeight / (double)strikePpem;

            _bounds = new Rect(
                metrics.BearingX * unitsPerPixel,
                -metrics.BearingY * unitsPerPixel,
                decoded.Width * unitsPerPixel,
                decoded.Height * unitsPerPixel);

            _sourceRect = new Rect(0, 0, decoded.Width, decoded.Height);

            fixed (byte* pixels = decoded.Bgra)
            {
                // The Bitmap constructor copies, so the pinned span may go out of scope.
                _bitmap = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, (nint)pixels,
                    new PixelSize(decoded.Width, decoded.Height), new Vector(96, 96), decoded.Width * 4);
            }
        }

        public GlyphDrawingType Type => GlyphDrawingType.Bitmap;

        public Rect Bounds => _bounds;

        public void Draw(DrawingContext context, Point origin)
        {
            context.DrawImage(_bitmap, _sourceRect, _bounds.Translate(new Vector(origin.X, origin.Y)));
        }
    }
}
