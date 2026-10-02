using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;

namespace Avalonia.Skia
{
    internal partial class DrawingContextImpl
    {
        private static readonly SKSamplingOptions s_nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);
        private static readonly SKSamplingOptions s_bilinear = new(SKFilterMode.Linear, SKMipmapMode.None);

        // The release callback only exists to keep the page array reachable until Skia drops
        // its last reference to an image wrapping it; the array is its context object.
        private static readonly SKImageRasterReleaseDelegate s_releasePage = static (_, _) => { };

        [ThreadStatic]
        private static SKPixmap? t_blitPixmap;

        // The surface's pixel format, read once: reading a pixmap's image info marshals and
        // looks up its colour space on every call. The format of a surface never changes.
        private BlitSurfaceFormat _blitSurfaceFormat;

        // The direct-write target as the canvas and the surface last reported it, kept until
        // the next canvas operation: asking Skia for the clip and the pixels costs about as
        // much as blending a small label, once per run. Only a canvas operation changes the
        // clip, and only drawing through the canvas can move the surface's pixels (copy on
        // write after a snapshot), and every canvas operation of this context flushes the
        // glyph batch first, which drops the kept target.
        private GlyphBlitTarget _blitTarget;
        private BlitTargetState _blitTargetState;

        /// <summary>
        /// Whether text may be blended straight into this context's raster surface. On by
        /// default; tests turn it off to compare with drawing through the canvas.
        /// </summary>
        internal bool AllowsDirectSurfaceWrites { get; set; } = true;

        bool ITransformedGlyphContext.TryGetBlitTarget(out GlyphBlitTarget target)
        {
            target = default;

            if (!AllowsDirectSurfaceWrites)
            {
                return false;
            }

            // Writing the surface directly is only equivalent to drawing through the canvas
            // when the canvas would write the same pixels 1:1: no layer or opacity between the
            // draw and the surface, source-over blending, device coordinates equal to canvas
            // coordinates, and a clip that is exactly its bounds.
            if (_grContext is not null || Surface is null || _saveLayerDepth != 0 || _currentOpacity != 1 ||
                _postTransform.HasValue ||
                RenderOptions.BitmapBlendingMode is not (BitmapBlendingMode.Unspecified or BitmapBlendingMode.SourceOver))
            {
                return false;
            }

            if (_blitTargetState == BlitTargetState.Unread)
            {
                _blitTargetState = ReadBlitTarget(Surface, out _blitTarget)
                    ? BlitTargetState.Available
                    : BlitTargetState.Unavailable;
            }

            target = _blitTarget;

            return _blitTargetState == BlitTargetState.Available;
        }

        /// <summary>Drops the kept direct-write target; the next request asks Skia again.</summary>
        private void ForgetBlitTarget() => _blitTargetState = BlitTargetState.Unread;

        private bool ReadBlitTarget(SKSurface surface, out GlyphBlitTarget target)
        {
            target = default;

            if (!Canvas.IsClipRect)
            {
                return false;
            }

            var pixmap = t_blitPixmap ??= new SKPixmap();

            if (!surface.PeekPixels(pixmap))
            {
                return false;
            }

            if (_blitSurfaceFormat.Kind == BlitSurfaceKind.Unknown)
            {
                var info = pixmap.Info;

                _blitSurfaceFormat = new BlitSurfaceFormat(
                    info.AlphaType is not (SKAlphaType.Premul or SKAlphaType.Opaque) ? BlitSurfaceKind.Unsupported
                    : info.ColorType == SKColorType.Bgra8888 ? BlitSurfaceKind.Bgra
                    : info.ColorType == SKColorType.Rgba8888 ? BlitSurfaceKind.Rgba
                    : BlitSurfaceKind.Unsupported,
                    info.Width, info.Height);
            }

            var format = _blitSurfaceFormat;

            if (format.Kind == BlitSurfaceKind.Unsupported)
            {
                return false;
            }

            var clip = Canvas.DeviceClipBounds;

            target = new GlyphBlitTarget(pixmap.GetPixels(), pixmap.RowBytes, format.Width, format.Height,
                new PixelRect(clip.Left, clip.Top, Math.Max(0, clip.Width), Math.Max(0, clip.Height)),
                format.Kind == BlitSurfaceKind.Rgba, GetBlitArithmetic(format.Kind));

            return true;
        }

        private enum BlitTargetState : byte
        {
            Unread,
            Available,
            Unavailable,
        }

        private enum BlitSurfaceKind : byte
        {
            Unknown,
            Unsupported,
            Bgra,
            Rgba,
        }

        private readonly record struct BlitSurfaceFormat(BlitSurfaceKind Kind, int Width, int Height);

        /// <summary>
        /// How Skia rounds a premultiplied BGRA bitmap drawn 1:1 onto a surface of
        /// <paramref name="kind"/>. Its ARM64 code divides by 255 with rounding in the sprite
        /// blitter and in the 8-bit raster pipeline alike. Elsewhere the sprite blitter takes the
        /// bitmap when the surface holds the platform's native BGRA order, and the raster
        /// pipeline converts onto any other surface.
        /// </summary>
        private static GlyphBlitArithmetic GetBlitArithmetic(BlitSurfaceKind kind)
        {
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            {
                return GlyphBlitArithmetic.Rounded;
            }

            return kind == BlitSurfaceKind.Bgra && SKImageInfo.PlatformColorType == SKColorType.Bgra8888
                ? GlyphBlitArithmetic.Sprite
                : GlyphBlitArithmetic.Pipeline;
        }

        IDisposable ITransformedGlyphContext.CreateAtlasBatch(ReadOnlySpan<GlyphAtlasSprite> sprites,
            GlyphMask? standalone)
        {
            var sources = new SKRect[sprites.Length];
            var placements = new SKRotationScaleMatrix[sprites.Length];

            for (var i = 0; i < sprites.Length; i++)
            {
                var sprite = sprites[i];

                sources[i] = SKRect.Create(sprite.SourceX, sprite.SourceY, sprite.Width, sprite.Height);
                placements[i] = SKRotationScaleMatrix.CreateTranslation(sprite.X, sprite.Y);
            }

            SKImage? image = null;

            if (standalone is not null)
            {
                var info = new SKImageInfo(standalone.Width, standalone.Height, SKColorType.Alpha8, SKAlphaType.Premul);

                image = SKImage.FromPixelCopy(info, standalone.Alpha, standalone.Width);
            }

            return new SkiaGlyphAtlasBatch(sources, placements, image);
        }

        void ITransformedGlyphContext.DrawAtlasBatch(GlyphAtlasBatch batch, in Matrix transform, uint tintArgb,
            bool bilinear)
        {
            CheckLease();

            var backend = (SkiaGlyphAtlasBatch)batch.Backend;

            // The A8 page holds coverage already corrected for this colour's luminance, and
            // drawing modulates it by the paint colour.
            var color = new SKColor((byte)(tintArgb >> 16), (byte)(tintArgb >> 8), (byte)tintArgb,
                (byte)((tintArgb >> 24) * _currentOpacity));

            if (!bilinear && backend.Image is null && batch.Page is { } page &&
                TryAppendToGlyphBatch(page, backend, transform, color))
            {
                return;
            }

            FlushGlyphBatch(GlyphBatchFlushReason.OtherTextPath);

            var image = backend.Image ?? GetPageImage(batch.Page!).Image;
            var paint = SKPaintCache.Shared.Get();

            paint.Color = color;

            var oldTransform = Transform;

            Transform = transform;
            t_atlasDraws += DrawAtlasSprites(image, backend.Sources, backend.Placements,
                bilinear ? s_bilinear : s_nearest, paint);
            t_atlasGeometry += backend.Sources.Length;
            Transform = oldTransform;

            SKPaintCache.Shared.ReturnReset(paint);
        }

        IDisposable ITransformedGlyphContext.CreateTransientImage(ReadOnlySpan<byte> coverage, int width, int height,
            int rowBytes)
        {
            // A copy: Skia uploads a raster image when the GPU work is flushed, after the
            // caller has reused its buffer for the next run.
            var info = new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul);

            return SKImage.FromPixelCopy(info, coverage, rowBytes) ??
                   throw new InvalidOperationException("Could not create a transient glyph image.");
        }

        void ITransformedGlyphContext.DrawTransientSprites(IDisposable image, ReadOnlySpan<GlyphAtlasSprite> sprites,
            in Matrix transform, uint tintArgb)
        {
            PrepareCanvas(GlyphBatchFlushReason.OtherTextPath);

            var paint = SKPaintCache.Shared.Get();

            paint.Color = new SKColor((byte)(tintArgb >> 16), (byte)(tintArgb >> 8), (byte)tintArgb,
                (byte)((tintArgb >> 24) * _currentOpacity));

            var oldTransform = Transform;

            Transform = transform;

            for (var start = 0; start < sprites.Length; start += MaxSpritesPerAtlasDraw)
            {
                var slice = sprites.Slice(start, Math.Min(MaxSpritesPerAtlasDraw, sprites.Length - start));

                // DrawAtlas takes the sprite count from the array lengths, so the arrays are exact
                // fits, kept per length: a steady animation draws the same sprite counts frame
                // after frame and allocates none. Skia copies the geometry during the call.
                var (sources, placements) = GetTransientSpriteArrays(slice.Length);

                for (var i = 0; i < slice.Length; i++)
                {
                    var sprite = slice[i];

                    sources[i] = SKRect.Create(sprite.SourceX, sprite.SourceY, sprite.Width, sprite.Height);
                    placements[i] = SKRotationScaleMatrix.CreateTranslation(sprite.X, sprite.Y);
                }

                Canvas.DrawAtlas((SKImage)image, sources, placements, s_nearest, paint);
            }

            t_atlasGeometry += sprites.Length;
            Transform = oldTransform;

            SKPaintCache.Shared.ReturnReset(paint);
        }

        /// <summary>
        /// The most sprites one atlas draw hands to Skia. Skia's GPU atlas op sizes its vertex data
        /// in a 32-bit int at 64 bytes per sprite, so a draw of 2^25 sprites overflows it and writes
        /// out of bounds. A draw stays at the 65536 vertices one kept-vertices part addresses with
        /// 16-bit indices; a frame of ordinary text fits in one.
        /// </summary>
        /// <remarks>
        /// Skia also merges consecutive atlas draws of one image and colour into one op until the
        /// surface is flushed, and that op overflows the same way past 2^25 sprites. Surfaces are
        /// flushed every frame, far below that.
        /// </remarks>
        internal const int MaxSpritesPerAtlasDraw = 16384;

        /// <summary>
        /// Draws the sprites in order, in calls of at most <see cref="MaxSpritesPerAtlasDraw"/>
        /// sprites, and returns the number of calls.
        /// </summary>
        private int DrawAtlasSprites(SKImage image, SKRect[] sources, SKRotationScaleMatrix[] placements,
            SKSamplingOptions sampling, SKPaint paint)
        {
            if (sources.Length <= MaxSpritesPerAtlasDraw)
            {
                Canvas.DrawAtlas(image, sources, placements, sampling, paint);
                return 1;
            }

            var draws = 0;

            for (var start = 0; start < sources.Length; start += MaxSpritesPerAtlasDraw)
            {
                var length = Math.Min(MaxSpritesPerAtlasDraw, sources.Length - start);
                var (sliceSources, slicePlacements) = GetTransientSpriteArrays(length);

                Array.Copy(sources, start, sliceSources, 0, length);
                Array.Copy(placements, start, slicePlacements, 0, length);
                Canvas.DrawAtlas(image, sliceSources, slicePlacements, sampling, paint);
                draws++;
            }

            return draws;
        }

        private const int MaxTransientSpriteArrayLengths = 64;

        [ThreadStatic]
        private static Dictionary<int, (SKRect[] Sources, SKRotationScaleMatrix[] Placements)>? t_transientSprites;

        private static (SKRect[] Sources, SKRotationScaleMatrix[] Placements) GetTransientSpriteArrays(int length)
        {
            var arrays = t_transientSprites ??= new Dictionary<int, (SKRect[], SKRotationScaleMatrix[])>();

            if (arrays.TryGetValue(length, out var pair))
            {
                return pair;
            }

            // Lengths that only appeared once, during a relayout say, do not pile up.
            if (arrays.Count >= MaxTransientSpriteArrayLengths)
            {
                arrays.Clear();
            }

            pair = (new SKRect[length], new SKRotationScaleMatrix[length]);
            arrays.Add(length, pair);

            return pair;
        }

        /// <summary>
        /// The image of an atlas page at its current version. The image wraps the page's
        /// pinned array without copying; a GPU context uploads it once per version and draws
        /// every batch on the page from that texture.
        /// </summary>
        private static GlyphPageImage GetPageImage(GlyphAtlasPage page)
        {
            if (page.Realized is GlyphPageImage current && page.RealizedVersion == page.Version)
            {
                return current;
            }

            if (page.Realized is not null)
            {
                t_pageImagesReplaced++;
            }

            page.Realized?.Dispose();

            var image = new GlyphPageImage(CreatePageImage(page));

            page.Realized = image;
            page.RealizedVersion = page.Version;

            return image;
        }

        [ThreadStatic]
        private static int t_pageImagesCreated;

        [ThreadStatic]
        private static int t_pageImagesReplaced;

        [ThreadStatic]
        private static long t_pageImageBytes;

        [ThreadStatic]
        private static int t_atlasDraws;

        /// <summary>
        /// The number of atlas page images made on this thread, each a texture upload on a GPU
        /// context; for tests.
        /// </summary>
        internal static int PageImagesCreatedOnThread => t_pageImagesCreated;

        /// <summary>
        /// The atlas page images made on this thread that replaced the image of an older version
        /// of their page; for profiling tools.
        /// </summary>
        internal static int PageImagesReplacedOnThread => t_pageImagesReplaced;

        /// <summary>
        /// The bytes of the atlas page images made on this thread. A page image wraps the page's
        /// whole array, and a GPU context uploads a raster image as a whole texture the first
        /// time it draws it, so this is the texture upload volume; for profiling tools.
        /// </summary>
        internal static long PageImageBytesOnThread => t_pageImageBytes;

        /// <summary>The number of atlas draw calls issued on this thread; for tests.</summary>
        internal static int AtlasDrawsOnThread => t_atlasDraws;

        [ThreadStatic]
        private static int t_atlasGeometry;

        /// <summary>
        /// The number of glyph atlas sprites whose geometry was handed to Skia on this thread;
        /// sprites drawn from geometry kept from an earlier frame do not count. For tests.
        /// </summary>
        internal static int AtlasGeometrySubmittedOnThread => t_atlasGeometry;

        private static unsafe SKImage CreatePageImage(GlyphAtlasPage page)
        {
            var pixels = page.Pixels;
            var info = new SKImageInfo(GlyphMaskAtlas.PageWidth, page.Height, SKColorType.Alpha8, SKAlphaType.Premul);
            var address = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(pixels));

            using var pixmap = new SKPixmap(info, address, GlyphMaskAtlas.PageWidth);

            t_pageImagesCreated++;
            t_pageImageBytes += info.BytesSize64;

            // Writes after this point only fill rows and columns no sprite of this version
            // samples, and growth moves the page to a new array, so the wrapped pixels stay
            // what this version's sprites expect for as long as Skia holds the image.
            return SKImage.FromPixels(pixmap, s_releasePage, pixels);
        }

        /// <summary>An atlas page's image at one version, and the shader that samples it texel for pixel.</summary>
        private sealed class GlyphPageImage : IDisposable
        {
            private SKShader? _shader;

            public GlyphPageImage(SKImage image) => Image = image;

            public SKImage Image { get; }

            /// <summary>Made on first use: only batches drawn from kept vertices sample the page through it.</summary>
            public SKShader Shader =>
                _shader ??= Image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, s_nearest);

            public void Dispose()
            {
                _shader?.Dispose();
                Image.Dispose();
            }
        }

        /// <summary>The exact-length sprite arrays of one batch, plus its own image for a standalone glyph.</summary>
        internal sealed partial class SkiaGlyphAtlasBatch : IDisposable
        {
            // Four vertices per sprite, addressed by 16-bit indices.
            private const int MaxSpritesPerVertices = (ushort.MaxValue + 1) / 4;

            // Vertex positions are floats: offsets this far apart still add up exactly.
            private const int MaxRelativeOffset = 1 << 22;

            // The runs of the last merged batch that began with these sprites, placed relative to
            // them, and the vertices built for that batch once it was seen again. A count of -1
            // lets no batch repeat: none was recorded, or its runs lie too far apart to place
            // exactly. The recorded length counts the entries in use, released on replacement.
            private BatchedRun[]? _batchRuns;
            private int _batchRunCount = -1;
            private int _recordedLength;
            private SKVertices[]? _batchVertices;

            // The sprites' device rectangle at the origin, made on first use.
            private SKRect? _bounds;

            public SkiaGlyphAtlasBatch(SKRect[] sources, SKRotationScaleMatrix[] placements, SKImage? image)
            {
                Sources = sources;
                Placements = placements;
                Image = image;
            }

            public SKRect[] Sources { get; }

            public SKRotationScaleMatrix[] Placements { get; }

            public SKImage? Image { get; }

            /// <summary>
            /// The device rectangle the sprites cover when the batch is drawn at the origin, made
            /// on first use; an empty batch covers nothing.
            /// </summary>
            public SKRect Bounds
            {
                get
                {
                    if (_bounds is { } bounds)
                    {
                        return bounds;
                    }

                    var result = SKRect.Empty;

                    for (var i = 0; i < Sources.Length; i++)
                    {
                        var placement = Placements[i];
                        var sprite = SKRect.Create(placement.TX, placement.TY, Sources[i].Width, Sources[i].Height);

                        result = i == 0 ? sprite : SKRect.Union(result, sprite);
                    }

                    _bounds = result;

                    return result;
                }
            }

            /// <summary>
            /// Vertices for a pending batch that begins with these sprites, placed relative to
            /// the first run, when the batch repeats the last one they began: the same runs, in
            /// order, at the same offsets from the first. They are built the first time the batch
            /// repeats (<paramref name="built"/>) and kept while it keeps repeating. Otherwise
            /// records the batch for the next frame and returns <c>false</c>, and the caller draws
            /// the sprites through an atlas draw.
            /// </summary>
            /// <remarks>
            /// A run's sprite arrays never change, so the runs identify the batch's geometry; the
            /// page they sample may grow or gain glyphs without moving theirs.
            /// </remarks>
            public bool TryGetBatchVertices(BatchedRun[] runs, int count, int spriteCount,
                out SKVertices[] vertices, out bool built)
            {
                vertices = null!;
                built = false;

                if (!Repeats(runs, count))
                {
                    Record(runs, count);
                    return false;
                }

                built = _batchVertices is null;
                vertices = _batchVertices ??= BuildVertices(runs, count, spriteCount);

                return true;
            }

            private bool Repeats(BatchedRun[] runs, int count)
            {
                if (_batchRunCount != count)
                {
                    return false;
                }

                var recorded = _batchRuns;
                var first = runs[0];

                for (var i = 1; i < count; i++)
                {
                    var run = runs[i];
                    var expected = recorded![i];

                    if (!ReferenceEquals(run.Backend, expected.Backend) || run.X - first.X != expected.X ||
                        run.Y - first.Y != expected.Y)
                    {
                        return false;
                    }
                }

                return true;
            }

            private void Record(BatchedRun[] runs, int count)
            {
                ReleaseVertices();

                var first = runs[0];
                var recorded = _batchRuns;
                var repeatable = true;

                if (count > 1 && (recorded is null || recorded.Length < count))
                {
                    recorded = _batchRuns = new BatchedRun[Math.Max(count, (recorded?.Length ?? 4) * 2)];
                }

                for (var i = 1; i < count; i++)
                {
                    var run = runs[i];
                    var x = run.X - first.X;
                    var y = run.Y - first.Y;

                    repeatable &= Math.Abs(x) <= MaxRelativeOffset && Math.Abs(y) <= MaxRelativeOffset;
                    recorded![i] = new BatchedRun(run.Backend, x, y);
                }

                // Entries past the batch would keep runs that left it alive.
                if (recorded is not null && _recordedLength > count)
                {
                    Array.Clear(recorded, count, _recordedLength - count);
                }

                _recordedLength = count;
                _batchRunCount = repeatable ? count : -1;
            }

            private static SKVertices[] BuildVertices(BatchedRun[] runs, int count, int spriteCount)
            {
                var parts = new SKVertices[(spriteCount + MaxSpritesPerVertices - 1) / MaxSpritesPerVertices];
                var first = runs[0];
                var run = 0;
                var sprite = 0;
                var remaining = spriteCount;

                for (var part = 0; part < parts.Length; part++)
                {
                    var sprites = Math.Min(remaining, MaxSpritesPerVertices);
                    var positions = new SKPoint[sprites * 4];
                    var coordinates = new SKPoint[sprites * 4];
                    var indices = new ushort[sprites * 6];

                    for (var i = 0; i < sprites; i++)
                    {
                        while (sprite == runs[run].Backend.Sources.Length)
                        {
                            run++;
                            sprite = 0;
                        }

                        var backend = runs[run].Backend;
                        var source = backend.Sources[sprite];
                        var placement = backend.Placements[sprite];
                        var x = placement.TX + (runs[run].X - first.X);
                        var y = placement.TY + (runs[run].Y - first.Y);
                        var v = i * 4;

                        // The quad and texture coordinates an atlas draw makes for an unrotated,
                        // unscaled sprite.
                        positions[v] = new SKPoint(x, y);
                        positions[v + 1] = new SKPoint(x + source.Width, y);
                        positions[v + 2] = new SKPoint(x, y + source.Height);
                        positions[v + 3] = new SKPoint(x + source.Width, y + source.Height);
                        coordinates[v] = new SKPoint(source.Left, source.Top);
                        coordinates[v + 1] = new SKPoint(source.Right, source.Top);
                        coordinates[v + 2] = new SKPoint(source.Left, source.Bottom);
                        coordinates[v + 3] = new SKPoint(source.Right, source.Bottom);

                        var index = i * 6;

                        indices[index] = (ushort)v;
                        indices[index + 1] = (ushort)(v + 1);
                        indices[index + 2] = (ushort)(v + 2);
                        indices[index + 3] = (ushort)(v + 1);
                        indices[index + 4] = (ushort)(v + 3);
                        indices[index + 5] = (ushort)(v + 2);

                        sprite++;
                    }

                    parts[part] = SKVertices.CreateCopy(SKVertexMode.Triangles, positions, coordinates, null, indices);
                    remaining -= sprites;
                }

                return parts;
            }

            private void ReleaseVertices()
            {
                if (_batchVertices is { } parts)
                {
                    foreach (var part in parts)
                    {
                        part.Dispose();
                    }

                    _batchVertices = null;
                }
            }

            public void Dispose()
            {
                ReleaseVertices();
                DisposeTrimmed();

                if (_batchRuns is { } recorded)
                {
                    Array.Clear(recorded);
                }

                _batchRunCount = -1;
                _recordedLength = 0;
                Image?.Dispose();
            }
        }
    }
}
