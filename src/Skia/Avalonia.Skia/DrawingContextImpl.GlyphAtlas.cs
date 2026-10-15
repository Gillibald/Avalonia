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
            // draw and the surface, source-over blending, and a clip that is exactly its bounds.
            if (_grContext is not null || Surface is null || _saveLayerDepth != 0 || _currentOpacity != 1 ||
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
                TryAppendToGlyphBatch(page, backend, transform, color, batch.DisjointRun))
            {
                return;
            }

            if (bilinear && backend.Image is null && batch.Page is { } stretchedPage &&
                TryAppendStretchedToGlyphBatch(stretchedPage, backend, transform, color, batch.DisjointRun))
            {
                return;
            }

            FlushGlyphBatch(GlyphBatchFlushReason.OtherTextPath);

            var image = backend.Image ?? GetPageImage(batch.Page!).Image;
            var paint = SKPaintCache.Shared.Get();

            paint.Color = color;

            var saved = _currentTransform;
            var savedMatrix = saved is null ? Canvas.TotalMatrix44 : default;

            SetDeviceTransform(transform);
            t_atlasDraws += DrawAtlasSprites(image, backend.Sources, backend.Placements,
                bilinear ? s_bilinear : s_nearest, paint);
            t_atlasGeometry += backend.Sources.Length;
            SetCanvasTransform(saved, savedMatrix);

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

            var saved = _currentTransform;
            var savedMatrix = saved is null ? Canvas.TotalMatrix44 : default;

            SetDeviceTransform(transform);

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
            SetCanvasTransform(saved, savedMatrix);

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

        [ThreadStatic]
        private static Dictionary<int, SKColor[]>? t_transientSpriteColors;

        /// <summary>An exact-length per-sprite colour array, kept per length like the sprite arrays.</summary>
        private static SKColor[] GetTransientSpriteColors(int length)
        {
            var arrays = t_transientSpriteColors ??= new Dictionary<int, SKColor[]>();

            if (arrays.TryGetValue(length, out var colors))
            {
                return colors;
            }

            if (arrays.Count >= MaxTransientSpriteArrayLengths)
            {
                arrays.Clear();
            }

            colors = new SKColor[length];
            arrays.Add(length, colors);

            return colors;
        }

        /// <summary>
        /// The image of an atlas page at its current version.
        /// </summary>
        /// <remarks>
        /// A context with updatable textures keeps the page in a texture of its own, uploaded
        /// whole once and then updated with only the rectangle the atlas wrote since, so a new
        /// glyph moves its own pixels to the GPU instead of the page. Elsewhere the image wraps
        /// the page's pinned array without copying, once per version, and a GPU context uploads
        /// that image whole, up to 2 MB (8 MB for a colour page), the first time it draws it.
        /// </remarks>
        private GlyphPageImage GetPageImage(GlyphAtlasPage page)
        {
            var grContext = _grContext;

            if (page.Realized is GlyphPageTexture texture && texture.CanDraw(page, grContext))
            {
                if (texture.Version != page.Version && !texture.UpdatePending)
                {
                    texture.UpdatePending = true;
                    (t_pendingPageTextures ??= new List<(GlyphAtlasPage, GlyphPageTexture)>()).Add((page, texture));
                }

                return texture.Image;
            }

            // A texture the page outgrew still backs the draws recorded so far: they get what the
            // atlas wrote for them before it is replaced.
            if (page.Realized is GlyphPageTexture outgrown && outgrown.UpdatePending)
            {
                UploadPageTexture(page, outgrown);
            }

            var textures = grContext is null ? null : SkiaUpdatableTextures.Get(grContext)?.Feature;

            if (textures is null && page.Realized is GlyphPageImage current && page.RealizedVersion == page.Version)
            {
                return current;
            }

            var timer = GlyphPhaseTimers.Start();

            if (page.Realized is not null)
            {
                t_pageImagesReplaced++;
            }

            page.Realized?.Dispose();
            page.Realized = null;

            GlyphPageImage image;

            if (textures is not null && GlyphPageTexture.TryCreate(grContext!, textures, page) is { } created)
            {
                page.Realized = created;
                image = created.Image;
            }
            else
            {
                image = new GlyphPageImage(CreatePageImage(page));
                page.Realized = image;
                page.RealizedVersion = page.Version;
            }

            GlyphPhaseTimers.Stop(GlyphTimerPhase.PageRewrap, timer);

            return image;
        }

        // The page textures drawn on this thread since the atlas wrote to their pages, uploaded
        // once before the GPU runs the draws recorded so far. Contexts drawn inside another on the
        // same GPU context share the list, so whichever ends first uploads for both.
        [ThreadStatic]
        private static List<(GlyphAtlasPage Page, GlyphPageTexture Texture)>? t_pendingPageTextures;

        /// <summary>
        /// Uploads what the atlas wrote to the pages drawn from page textures on this thread since
        /// the last upload. A texture update takes effect before the draws recorded earlier in the frame,
        /// which the GPU runs when the surface is flushed, so a page drawn many times in a frame,
        /// a page batch per clip or per stretched run in between writes, is uploaded once. The
        /// context uploads before anything else can make the GPU run its draws: when it ends,
        /// when the graphics API is leased, and when a caller flushes it to read the surface.
        /// </summary>
        private static void UploadPendingPageTextures()
        {
            if (t_pendingPageTextures is not { Count: > 0 } pending)
            {
                return;
            }

            foreach (var (page, texture) in pending)
            {
                UploadPageTexture(page, texture);
            }

            pending.Clear();
        }

        private static void UploadPageTexture(GlyphAtlasPage page, GlyphPageTexture texture)
        {
            if (!texture.UpdatePending)
            {
                return;
            }

            var timer = GlyphPhaseTimers.Start();

            texture.Update(page);
            GlyphPhaseTimers.Stop(GlyphTimerPhase.PageRewrap, timer);
        }

        /// <summary>The image this context draws <paramref name="page"/> from at its current version; for tests.</summary>
        internal SKImage GetAtlasPageImage(GlyphAtlasPage page) => GetPageImage(page).Image;

        [ThreadStatic]
        private static int t_pageImagesCreated;

        [ThreadStatic]
        private static int t_pageImagesReplaced;

        [ThreadStatic]
        private static long t_pageImageBytes;

        [ThreadStatic]
        private static int t_pageTextureUpdates;

        [ThreadStatic]
        private static int t_atlasDraws;

        /// <summary>
        /// The number of atlas page images made on this thread, each holding the whole page: a
        /// page texture, or an image wrapping the page's array, which a GPU context uploads
        /// whole; for tests.
        /// </summary>
        internal static int PageImagesCreatedOnThread => t_pageImagesCreated;

        /// <summary>
        /// The atlas page images made on this thread that replaced the image or texture of their
        /// page; for profiling tools.
        /// </summary>
        internal static int PageImagesReplacedOnThread => t_pageImagesReplaced;

        /// <summary>
        /// The atlas page bytes handed to the GPU on this thread: whole pages for new page
        /// images, written rectangles for page texture updates; for profiling tools.
        /// </summary>
        internal static long PageImageBytesOnThread => t_pageImageBytes;

        /// <summary>
        /// The page texture updates on this thread, each uploading what the atlas wrote to the
        /// page since the last; for profiling tools.
        /// </summary>
        internal static int PageTextureUpdatesOnThread => t_pageTextureUpdates;

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
            var info = new SKImageInfo(GlyphMaskAtlas.PageWidth, page.Height,
                page.BytesPerPixel == 4 ? SKColorType.Rgba8888 : SKColorType.Alpha8, SKAlphaType.Premul);
            var address = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(pixels));

            using var pixmap = new SKPixmap(info, address, page.RowBytes);

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
            private SKPaint? _paint;

            private readonly IDisposable _owner;

            /// <param name="image">The page's image.</param>
            /// <param name="owner">What releases the image, when not the image itself.</param>
            public GlyphPageImage(SKImage image, IDisposable? owner = null)
            {
                Image = image;
                _owner = owner ?? image;
            }

            public SKImage Image { get; }

            /// <summary>Made on first use: only batches drawn from kept vertices sample the page through it.</summary>
            public SKShader Shader => _shader ?? CreateShader();

            private SKShader CreateShader()
            {
                var timer = GlyphPhaseTimers.Start();

                _shader = Image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, s_nearest);
                GlyphPhaseTimers.Stop(GlyphTimerPhase.PageShader, timer);

                return _shader;
            }

            /// <summary>
            /// The paint batches drawn from kept vertices sample the page with, made on first use
            /// and kept with the image; each draw sets its colour.
            /// </summary>
            public SKPaint Paint => _paint ??= new SKPaint { Shader = Shader };

            private SKShader? _bilinearShader;
            private SKPaint? _bilinearPaint;

            /// <summary>
            /// The paint stretched runs drawn from kept vertices sample the page with, bilinearly
            /// as their atlas draws do, made on first use and kept with the image; each draw sets
            /// its colour.
            /// </summary>
            public SKPaint BilinearPaint => _bilinearPaint ??= new SKPaint
            {
                Shader = _bilinearShader = Image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, s_bilinear)
            };

            public void Dispose()
            {
                _paint?.Dispose();
                _shader?.Dispose();
                _bilinearPaint?.Dispose();
                _bilinearShader?.Dispose();
                _owner.Dispose();
            }
        }

        /// <summary>An atlas page held in a texture of one GPU context, updated in place as the atlas writes.</summary>
        /// <remarks>
        /// <para>
        /// The texture is a single-channel or RGBA texture, as the page holds, of the context's
        /// <see cref="ISkiaUpdatableTextureFeature"/>, made with the whole page and wrapped for
        /// Skia once. An update hands the rectangle the atlas wrote since to the backend straight
        /// from the page's array, so the page's image, shader and paint stay the same across
        /// versions. SkiaSharp offers no partial upload of its own: drawing the rectangle into a
        /// page surface instead makes Skia create and fill a texture for every rectangle, 40-60 us
        /// of render-thread time each. The array stays the source of truth: a texture that no
        /// longer fits its page (the page outgrew it, another context draws it, the context was
        /// lost) is dropped and the page uploaded whole again.
        /// </para>
        /// <para>
        /// The texture has the rows of the largest page from the start, the ones the page does not
        /// use yet clear, so the page grows within it. A texture made anew at every growth uploaded
        /// the whole page each time: a frame that places many glyphs, a zoom's band crossing of a
        /// screen of CJK text say, grows a page by a few dozen rows at a time, and its pages made
        /// 20-35 textures in one frame on a phone. The rows a coverage page does not use yet cost
        /// at most one page's texture of GPU memory per atlas, the page still growing.
        /// </para>
        /// <para>
        /// The upload takes effect before the draws recorded earlier in the frame, which Skia
        /// runs at its next flush, so those draws see the new pixels too. They only sample
        /// entries the atlas wrote before them, and the atlas never writes over an entry, only
        /// into rows and columns no entry uses yet. So the contexts of a thread upload a page once
        /// before the GPU runs their draws rather than each time they draw it.
        /// </para>
        /// <para>
        /// GPU resources are released on the thread that drives their context. Disposing a page
        /// texture, which eviction may do on any thread, only marks it; the thread that made it
        /// frees it when it next makes a texture for that context, together with the textures
        /// of collected pages and of lost contexts. The backend frees the texture itself once no
        /// recorded draw uses it any more.
        /// </para>
        /// </remarks>
        private sealed class GlyphPageTexture : IDisposable
        {
            [ThreadStatic]
            private static List<GlyphPageTexture>? t_textures;

            private readonly GRContext _context;
            private readonly ISkiaUpdatableTexture _texture;
            private readonly WeakReference<GlyphAtlasPage> _page;
            private volatile bool _released;

            private GlyphPageTexture(GRContext context, ISkiaUpdatableTexture texture, GlyphAtlasPage page)
            {
                _context = context;
                _texture = texture;
                _page = new WeakReference<GlyphAtlasPage>(page);
                Height = TextureHeight(page);
                Image = new GlyphPageImage(texture.Image, texture);
            }

            /// <summary>The rows the texture has; the page uses its first <see cref="GlyphAtlasPage.Height"/>.</summary>
            public int Height { get; }

            /// <summary>The page version the texture holds.</summary>
            public int Version { get; private set; }

            /// <summary>
            /// Whether a context drew from the texture since the atlas wrote to its page and will
            /// upload the writes before its draws run.
            /// </summary>
            public bool UpdatePending { get; set; }

            /// <summary>The texture wrapped for Skia; the same image at every version.</summary>
            public GlyphPageImage Image { get; }

            /// <summary>Whether this texture can stand for <paramref name="page"/> on <paramref name="context"/>.</summary>
            public bool CanDraw(GlyphAtlasPage page, GRContext? context) =>
                !_released && ReferenceEquals(context, _context) && !SkiaUpdatableTextures.IsLost(_context) &&
                Height >= page.Height;

            /// <summary>The rows of the texture made for <paramref name="page"/>: the largest page's.</summary>
            private static int TextureHeight(GlyphAtlasPage page) =>
                Math.Max(GlyphMaskAtlas.MaxPageHeight, page.Height);

            /// <summary>Makes a texture of the whole page, or returns <c>null</c> when the backend or Skia refuse it.</summary>
            public static unsafe GlyphPageTexture? TryCreate(GRContext context, ISkiaUpdatableTextureFeature textures,
                GlyphAtlasPage page)
            {
                Collect(context);

                // The whole page goes up, so earlier writes need no upload of their own.
                var version = page.Version;

                page.TakeWritten(out _);

                var height = TextureHeight(page);
                var used = page.RowBytes * page.Height;
                var length = page.RowBytes * height;
                ISkiaUpdatableTexture? texture;

                // The rows past the page's go up clear, so the texture draws as the page's array at
                // every height; a texture's initial contents are undefined on every backend. The
                // padded staging copy lives in native memory, freed as soon as the backend has
                // taken it: it is the full texture's size and a zoom opens page after page, so on
                // the managed heap each copy would be garbage, and a pooled one would keep the
                // largest page's bytes for the life of the process.
                if (length > used)
                {
                    var staging = (byte*)NativeMemory.AllocZeroed((nuint)length);

                    try
                    {
                        var padded = new Span<byte>(staging, length);

                        page.Pixels.AsSpan(0, used).CopyTo(padded);
                        texture = Create(textures, page, height, padded);
                    }
                    finally
                    {
                        NativeMemory.Free(staging);
                    }
                }
                else
                {
                    texture = Create(textures, page, height, page.Pixels.AsSpan(0, used));
                }

                if (texture is null)
                {
                    return null;
                }

                var created = new GlyphPageTexture(context, texture, page) { Version = version };

                (t_textures ??= new List<GlyphPageTexture>()).Add(created);
                t_pageImagesCreated++;
                t_pageImageBytes += length;

                return created;
            }

            private static ISkiaUpdatableTexture? Create(ISkiaUpdatableTextureFeature textures, GlyphAtlasPage page,
                int height, ReadOnlySpan<byte> pixels) =>
                page.BytesPerPixel == 4
                    ? textures.TryCreateRgba8(GlyphMaskAtlas.PageWidth, height, pixels, page.RowBytes)
                    : textures.TryCreateAlpha8(GlyphMaskAtlas.PageWidth, height, pixels, page.RowBytes);

            /// <summary>
            /// Uploads what the atlas wrote to the page since the texture's version, within the
            /// texture's rows: a page that outgrew it gets a texture of its own afterwards, made
            /// from the whole page.
            /// </summary>
            public void Update(GlyphAtlasPage page)
            {
                var version = page.Version;

                UpdatePending = false;

                if (_released || SkiaUpdatableTextures.IsLost(_context))
                {
                    return;
                }

                if (page.TakeWritten(out var written))
                {
                    var bottom = Math.Min(written.Bottom, Height);

                    if (bottom > written.Y)
                    {
                        written = new PixelRect(written.X, written.Y, written.Width, bottom - written.Y);
                        _texture.Update(written, page.Pixels, page.RowBytes);
                        t_pageTextureUpdates++;
                        t_pageImageBytes += (long)written.Width * written.Height * page.BytesPerPixel;
                    }
                }

                Version = version;
            }

            /// <summary>Marks the texture released; the thread that made it frees it.</summary>
            public void Dispose() => _released = true;

            /// <summary>
            /// Frees this thread's released textures of <paramref name="context"/>, those whose
            /// page was collected, and those of lost contexts, which release nothing on the GPU.
            /// </summary>
            private static void Collect(GRContext context)
            {
                if (t_textures is not { } textures)
                {
                    return;
                }

                for (var i = textures.Count - 1; i >= 0; i--)
                {
                    var texture = textures[i];

                    if (!SkiaUpdatableTextures.IsLost(texture._context) &&
                        (!ReferenceEquals(texture._context, context) ||
                         !texture._released && texture._page.TryGetTarget(out _)))
                    {
                        continue;
                    }

                    texture.Image.Dispose();
                    textures.RemoveAt(i);
                }
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
            // The run colours only belong to the batch when it has several: vertices of one
            // colour take it from the paint, so a recoloured batch still repeats.
            private BatchedRun[]? _batchRuns;
            private int _batchRunCount = -1;
            private int _recordedLength;
            private bool _batchColored;
            private SKVertices[]? _batchVertices;

            // The sprites' device rectangle at the origin, made on first use.
            private SKRect? _bounds;

            // The vertices a stretched draw of these sprites alone draws from, built on its first
            // stretched draw and kept while the sprites are.
            private SKVertices[]? _stretchVertices;

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
            /// order, at the same offsets from the first, and in the same colours when the vertices
            /// carry them (<paramref name="colored"/>). They are built the first time the batch
            /// repeats (<paramref name="built"/>) and kept while it keeps repeating. Otherwise
            /// records the batch for the next frame and returns <c>false</c>, and the caller draws
            /// the sprites through an atlas draw, unless <paramref name="required"/>: then the
            /// vertices are built for the recorded batch at once.
            /// </summary>
            /// <remarks>
            /// A run's sprite arrays never change, so the runs identify the batch's geometry; the
            /// page they sample may grow or gain glyphs without moving theirs.
            /// </remarks>
            public bool TryGetBatchVertices(BatchedRun[] runs, int count, int spriteCount, bool colored,
                bool required, out SKVertices[] vertices, out bool built)
            {
                vertices = null!;
                built = false;

                if (!Repeats(runs, count, colored))
                {
                    Record(runs, count, colored);

                    if (!required)
                    {
                        return false;
                    }
                }

                built = _batchVertices is null;
                vertices = _batchVertices ??= BuildVertices(runs, count, spriteCount, colored);

                return true;
            }

            private bool Repeats(BatchedRun[] runs, int count, bool colored)
            {
                if (_batchRunCount != count || _batchColored != colored)
                {
                    return false;
                }

                var recorded = _batchRuns;
                var first = runs[0];

                if (colored && recorded![0].Color != first.Color)
                {
                    return false;
                }

                for (var i = 1; i < count; i++)
                {
                    var run = runs[i];
                    var expected = recorded![i];

                    if (!ReferenceEquals(run.Backend, expected.Backend) || run.X - first.X != expected.X ||
                        run.Y - first.Y != expected.Y || (colored && run.Color != expected.Color))
                    {
                        return false;
                    }
                }

                return true;
            }

            private void Record(BatchedRun[] runs, int count, bool colored)
            {
                ReleaseVertices();

                var first = runs[0];
                var recorded = _batchRuns;
                var repeatable = true;

                if (count > 1 && (recorded is null || recorded.Length < count))
                {
                    recorded = _batchRuns = new BatchedRun[Math.Max(count, (recorded?.Length ?? 4) * 2)];
                }

                if (count > 1)
                {
                    // The first run is these sprites; only its colour is recorded.
                    recorded![0] = new BatchedRun(null!, 0, 0, first.Color);
                }

                for (var i = 1; i < count; i++)
                {
                    var run = runs[i];
                    var x = run.X - first.X;
                    var y = run.Y - first.Y;

                    repeatable &= Math.Abs(x) <= MaxRelativeOffset && Math.Abs(y) <= MaxRelativeOffset;
                    recorded![i] = new BatchedRun(run.Backend, x, y, run.Color);
                }

                // Entries past the batch would keep runs that left it alive.
                if (recorded is not null && _recordedLength > count)
                {
                    Array.Clear(recorded, count, _recordedLength - count);
                }

                _recordedLength = count;
                _batchRunCount = repeatable ? count : -1;
                _batchColored = colored;
            }

            private static SKVertices[] BuildVertices(BatchedRun[] runs, int count, int spriteCount, bool colored)
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
                    var colors = colored ? new SKColor[sprites * 4] : null;

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

                        if (colors is not null)
                        {
                            colors.AsSpan(v, 4).Fill(runs[run].Color);
                        }

                        var index = i * 6;

                        indices[index] = (ushort)v;
                        indices[index + 1] = (ushort)(v + 1);
                        indices[index + 2] = (ushort)(v + 2);
                        indices[index + 3] = (ushort)(v + 1);
                        indices[index + 4] = (ushort)(v + 3);
                        indices[index + 5] = (ushort)(v + 2);

                        sprite++;
                    }

                    parts[part] = SKVertices.CreateCopy(SKVertexMode.Triangles, positions, coordinates, colors, indices);
                    remaining -= sprites;
                }

                return parts;
            }

            /// <summary>Whether <see cref="GetStretchVertices"/> has built the vertices.</summary>
            public bool HasStretchVertices => _stretchVertices is not null;

            /// <summary>
            /// The vertices of these sprites drawn alone, placed as an atlas draw at the origin places
            /// them, built on first use and kept: a zoom stretches the same crossing's sprites frame
            /// after frame, and drawing them from vertices skips building their quads each time.
            /// </summary>
            public SKVertices[] GetStretchVertices(SKColor color)
                => _stretchVertices ??= BuildVertices(new[] { new BatchedRun(this, 0, 0, color) }, 1, Sources.Length,
                    colored: false);

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

                if (_stretchVertices is { } stretch)
                {
                    foreach (var part in stretch)
                    {
                        part.Dispose();
                    }

                    _stretchVertices = null;
                }

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
