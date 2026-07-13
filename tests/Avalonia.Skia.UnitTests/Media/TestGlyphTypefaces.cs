using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Builds glyph typefaces over raw font bytes, so their render typeface derives from the same
    /// data a test also hands to Skia directly.
    /// </summary>
    internal static class TestGlyphTypefaces
    {
        public static GlyphTypeface FromBytes(byte[] data, int faceIndex = 0)
        {
            Assert.True(SfntFace.TryLoad(new MemoryStream(data), faceIndex, out var face));

            return new GlyphTypeface(face);
        }

        public static GlyphTypeface FromSKTypeface(SKTypeface typeface)
        {
            using var asset = typeface.OpenStream(out var faceIndex);
            var data = new byte[asset.Length];

            asset.Read(data, data.Length);

            return FromBytes(data, faceIndex);
        }
    }
}
