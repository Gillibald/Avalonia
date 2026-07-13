using System;
using System.Runtime.CompilerServices;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Composes cached per-glyph masks into run-sized buffers — the one deliberate pixel copy of
    /// the managed text pipeline. Two targets: an alpha-only run mask (for opacity-mask drawing
    /// under non-solid foregrounds) and a premultiplied BGRA run bitmap tinted with a solid
    /// foreground (drawn with a plain bitmap blit, no layers). Compose writes are clipped to the
    /// destination; sources are never mutated, so evicting a glyph mask after composing has no
    /// effect on the composed buffer.
    /// </summary>
    internal static class RunMaskComposer
    {
        /// <summary>
        /// Adds a glyph mask's coverage into an A8 run buffer at the snapped pen position,
        /// saturating where glyphs overlap — the same clamp the single-pass rasterizer applies
        /// to accumulated winding, so non-overlapping compose is bit-identical to rasterizing
        /// all contours in one pass.
        /// </summary>
        public static void ComposeAlpha(GlyphMask mask, int penX, int penY,
            Span<byte> destination, int destWidth, int destHeight, int destStride = 0)
        {
            if (mask.IsEmpty)
            {
                return;
            }

            // Stride in bytes per destination row; zero means tightly packed. Non-tight strides
            // let compose write straight into a locked framebuffer — no staging copy.
            if (destStride == 0)
            {
                destStride = destWidth;
            }

            ClipMask(mask, penX, penY, destWidth, destHeight,
                out var srcX, out var srcY, out var dstX, out var dstY, out var width, out var height);

            if (width <= 0 || height <= 0)
            {
                return;   // entirely outside this destination (e.g. the other side of a chunk seam)
            }

            for (var y = 0; y < height; y++)
            {
                var src = mask.Alpha.AsSpan((srcY + y) * mask.Width + srcX, width);
                var dst = destination.Slice((dstY + y) * destStride + dstX, width);

                for (var x = 0; x < width; x++)
                {
                    var sum = dst[x] + src[x];
                    dst[x] = sum > 255 ? (byte)255 : (byte)sum;
                }
            }
        }

        /// <summary>
        /// Draws a glyph mask into a premultiplied BGRA run buffer (byte order B, G, R, A) with a
        /// solid premultiplied tint, source-over. Opacity is deliberately not a parameter — it
        /// rides the eventual bitmap draw call, so animating it reuses the composed buffer.
        /// </summary>
        public static void ComposeTinted(GlyphMask mask, int penX, int penY, uint tintBgra,
            Span<byte> destination, int destWidth, int destHeight, int destStride = 0)
        {
            if (mask.IsEmpty)
            {
                return;
            }

            if (destStride == 0)
            {
                destStride = destWidth * 4;
            }

            ClipMask(mask, penX, penY, destWidth, destHeight,
                out var srcX, out var srcY, out var dstX, out var dstY, out var width, out var height);

            if (width <= 0 || height <= 0)
            {
                return;
            }

            var tintB = (byte)tintBgra;
            var tintG = (byte)(tintBgra >> 8);
            var tintR = (byte)(tintBgra >> 16);
            var tintA = (byte)(tintBgra >> 24);

            for (var y = 0; y < height; y++)
            {
                var src = mask.Alpha.AsSpan((srcY + y) * mask.Width + srcX, width);
                var dst = destination.Slice((dstY + y) * destStride + dstX * 4, width * 4);

                for (var x = 0; x < width; x++)
                {
                    var coverage = src[x];

                    if (coverage == 0)
                    {
                        continue;
                    }

                    var d = x * 4;
                    var sb = Div255(tintB * coverage);
                    var sg = Div255(tintG * coverage);
                    var sr = Div255(tintR * coverage);
                    var sa = Div255(tintA * coverage);
                    var inv = 255 - sa;

                    dst[d] = (byte)(sb + Div255(dst[d] * inv));
                    dst[d + 1] = (byte)(sg + Div255(dst[d + 1] * inv));
                    dst[d + 2] = (byte)(sr + Div255(dst[d + 2] * inv));
                    dst[d + 3] = (byte)(sa + Div255(dst[d + 3] * inv));
                }
            }
        }

        /// <summary>Premultiplies a straight-alpha BGRA color into the compose tint format.</summary>
        public static uint MakeTint(byte alpha, byte red, byte green, byte blue)
        {
            var b = (uint)Div255(blue * alpha);
            var g = (uint)Div255(green * alpha);
            var r = (uint)Div255(red * alpha);

            return b | (g << 8) | (r << 16) | ((uint)alpha << 24);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Div255(int value) => (value + 127) / 255;

        private static void ClipMask(GlyphMask mask, int penX, int penY, int destWidth, int destHeight,
            out int srcX, out int srcY, out int dstX, out int dstY, out int width, out int height)
        {
            dstX = penX + mask.Left;
            dstY = penY + mask.Top;
            srcX = 0;
            srcY = 0;
            width = mask.Width;
            height = mask.Height;

            if (dstX < 0)
            {
                srcX -= dstX;
                width += dstX;
                dstX = 0;
            }

            if (dstY < 0)
            {
                srcY -= dstY;
                height += dstY;
                dstY = 0;
            }

            if (dstX + width > destWidth)
            {
                width = destWidth - dstX;
            }

            if (dstY + height > destHeight)
            {
                height = destHeight - dstY;
            }

            if (width < 0)
            {
                width = 0;
            }

            if (height < 0)
            {
                height = 0;
            }
        }
    }
}
