using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Platform;
using Avalonia.Rendering.Utilities;
using Avalonia.Skia.Helpers;
using Avalonia.Utilities;
using SkiaSharp;
using ISceneBrush = Avalonia.Media.ISceneBrush;

namespace Avalonia.Skia
{
    /// <summary>
    /// Skia based drawing context.
    /// </summary>
    internal partial class DrawingContextImpl : IDrawingContextImpl,
        IDrawingContextWithAcrylicLikeSupport,
        IDrawingContextImplWithEffects,
        IDrawingContextImplWithLayers,
        IAlphaGlyphMaskContext,
        ITransformedGlyphContext
    {
        private IDisposable?[]? _disposables;
        // TODO: Get rid of this value, it's currently used to calculate intermediate sizes for tile brushes
        // but does so ignoring the current transform
        private readonly Vector _intermediateSurfaceDpi;
        private readonly Stack<(SKMatrix matrix, PaintWrapper paint)> _maskStack = new();
        private readonly Stack<double> _opacityStack = new();
        private readonly Stack<RenderOptions> _renderOptionsStack = new();
        private readonly Stack<TextOptions> _textOptionsStack = new();
        private readonly Matrix? _postTransform;
        private double _currentOpacity = 1.0f;
        private readonly bool _disableSubpixelTextRendering;
        private readonly bool _surfaceIsDisplay;
        private readonly LcdMaskGeometry? _lcdMaskGeometry;
        private int _saveLayerDepth;
        private Matrix? _currentTransform;
        private bool _disposed;
        private GRContext? _grContext;
        private readonly GlyphRasterTarget _glyphRasterTarget;
        public GRContext? GrContext => _grContext;
        private readonly ISkiaGpu? _gpu;
        private readonly SKPaint _strokePaint = SKPaintCache.Shared.Get();
        private readonly SKPaint _fillPaint = SKPaintCache.Shared.Get();
        private readonly SKPaint _boxShadowPaint = SKPaintCache.Shared.Get();
        private static SKShader? s_acrylicNoiseShader;
        private readonly ISkiaGpuRenderSession? _session;
        private bool _leased;
        private bool _useOpacitySaveLayer;

        /// <summary>
        /// Context create info.
        /// </summary>
        public struct CreateInfo
        {
            /// <summary>
            /// Canvas to draw to.
            /// </summary>
            public SKCanvas? Canvas;

            /// <summary>
            /// Surface to draw to.
            /// </summary>
            public SKSurface? Surface;

            /// <summary>
            /// Makes DPI to be applied as a hidden matrix transform
            /// </summary>
            public bool ScaleDrawingToDpi;
            
            /// <summary>
            /// Dpi for intermediate surfaces
            /// </summary>
            public Vector Dpi;

            /// <summary>
            /// Render text without subpixel antialiasing.
            /// </summary>
            public bool DisableSubpixelTextRendering;

            /// <summary>
            /// The surface is a display-bound target (window framebuffer or swapchain) whose
            /// declared pixel geometry corresponds to a physical panel. Subpixel (LCD) text is
            /// only eligible here; offscreen targets render grayscale regardless of declared
            /// stripe geometry, because their output may be composed, resampled or read back
            /// with alpha, where per-channel coverage has no valid interpretation.
            /// </summary>
            public bool SurfaceIsDisplay;

            /// <summary>
            /// GPU-accelerated context (optional)
            /// </summary>
            public GRContext? GrContext;

            /// <summary>
            /// Skia GPU provider context (optional)
            /// </summary>
            public ISkiaGpu? Gpu;

            public ISkiaGpuRenderSession? CurrentSession;
        }

        private class SkiaLeaseFeature : ISkiaSharpApiLeaseFeature
        {
            private readonly DrawingContextImpl _context;

            public SkiaLeaseFeature(DrawingContextImpl context)
            {
                _context = context;
            }

            public ISkiaSharpApiLease Lease()
            {
                _context.PrepareCanvas(GlyphBatchFlushReason.Other);
                return new ApiLease(_context);
            }

            private class ApiLease : ISkiaSharpApiLease
            {
                private readonly DrawingContextImpl _context;
                private readonly SKMatrix _revertTransform;
                private bool _isDisposed;
                private bool _leased;

                public ApiLease(DrawingContextImpl context)
                {
                    _revertTransform = context.Canvas.TotalMatrix;
                    _context = context;
                    _context._leased = true;
                }

                void CheckLease()
                {
                    if (_leased)
                        throw new InvalidOperationException("The underlying graphics API is currently leased");
                }

                T CheckLease<T>(T rv)
                {
                    CheckLease();
                    return rv;
                }

                public SKCanvas SkCanvas => CheckLease(_context.Canvas);
                // GrContext is accessible during the lease since one might want to wrap native resources
                // Into Skia ones
                public GRContext? GrContext => _context.GrContext;
                public SKSurface? SkSurface => CheckLease(_context.Surface);
                public double CurrentOpacity => CheckLease(_context._currentOpacity);


                public void Dispose()
                {
                    if (!_isDisposed)
                    {
                        _context.Canvas.SetMatrix(_revertTransform);
                        _context._leased = false;
                        _isDisposed = true;
                    }
                }

                class PlatformApiLease : ISkiaSharpPlatformGraphicsApiLease
                {
                    private readonly ApiLease _parent;

                    public PlatformApiLease(ApiLease parent, IPlatformGraphicsContext context)
                    {
                        _parent = parent;
                        _parent.GrContext?.Flush();
                        Context = context;
                        _parent._leased = true;
                    }
                    
                    public void Dispose()
                    {
                        _parent._leased = false;
                        _parent.GrContext?.ResetContext();
                    }

                    public IPlatformGraphicsContext Context { get; }
                }
                
                public ISkiaSharpPlatformGraphicsApiLease? TryLeasePlatformGraphicsApi()
                {
                    CheckLease();
                    if (_context._gpu?.PlatformGraphicsContext is { } context)
                        return new PlatformApiLease(this, context);
                    return null;
                }
            }
        }
        
        /// <summary>
        /// Create new drawing context.
        /// </summary>
        /// <param name="createInfo">Create info.</param>
        /// <param name="disposables">Array of elements to dispose after drawing has finished.</param>
        public DrawingContextImpl(CreateInfo createInfo, params IDisposable?[]? disposables)
        {
            Canvas = createInfo.Canvas ?? createInfo.Surface?.Canvas
                ?? throw new ArgumentException("Invalid create info - no Canvas provided", nameof(createInfo));

            _intermediateSurfaceDpi = createInfo.Dpi;
            _disposables = disposables;
            _disableSubpixelTextRendering = createInfo.DisableSubpixelTextRendering;
            _surfaceIsDisplay = createInfo.SurfaceIsDisplay;

            // Stripe geometry only means anything on a display-bound surface; offscreen
            // targets declare it too (SurfaceRenderTarget always has), but their output is
            // captured or composed with alpha, so LCD text must not engage there.
            _lcdMaskGeometry = !createInfo.SurfaceIsDisplay
                ? null
                : createInfo.Surface?.SurfaceProperties?.PixelGeometry switch
                {
                    SKPixelGeometry.RgbHorizontal => LcdMaskGeometry.RgbHorizontal,
                    SKPixelGeometry.BgrHorizontal => LcdMaskGeometry.BgrHorizontal,
                    _ => null,
                };
            _grContext = createInfo.GrContext;
            _glyphRasterTarget = _grContext is null
                ? GlyphRasterTarget.Raster
                : SkiaGpuRasterizer.IsSoftware(_grContext)
                    ? GlyphRasterTarget.SoftwareGpu
                    : GlyphRasterTarget.HardwareGpu;
            _gpu = createInfo.Gpu;
            if (_grContext != null)
                Monitor.Enter(_grContext);
            Surface = createInfo.Surface;

            _session = createInfo.CurrentSession;

            
            if (createInfo.ScaleDrawingToDpi && !createInfo.Dpi.NearlyEquals(SkiaPlatform.DefaultDpi))
            {
                _postTransform =
                    Matrix.CreateScale(createInfo.Dpi.X / SkiaPlatform.DefaultDpi.X,
                        createInfo.Dpi.Y / SkiaPlatform.DefaultDpi.Y);
            }

            Transform = Matrix.Identity;

            var options = AvaloniaLocator.Current.GetService<SkiaOptions>();

            if(options != null)
            {
                _useOpacitySaveLayer = options.UseOpacitySaveLayer;
            }
        }
        
        /// <summary>
        /// Skia canvas.
        /// </summary>
        public SKCanvas Canvas { get; }
        public SKSurface? Surface { get; }

        public RenderOptions RenderOptions { get; set; }
        public TextOptions TextOptions { get; set; }

        private void CheckLease()
        {
            if (_leased)
                throw new InvalidOperationException("The underlying graphics API is currently leased");
        }

        /// <summary>
        /// The lease check of every operation that draws to the canvas or changes its clip or
        /// layer state. Pending glyph atlas sprites are drawn first, so they keep their place
        /// in the draw order and the clip and layer they were recorded under.
        /// </summary>
        private void PrepareCanvas() => PrepareCanvas(GlyphBatchFlushReason.CanvasOperation);

        /// <inheritdoc cref="PrepareCanvas()"/>
        /// <param name="reason">Why pending glyph batches are drawn now, for the flush counters.</param>
        private void PrepareCanvas(GlyphBatchFlushReason reason)
        {
            CheckLease();
            FlushGlyphBatch(reason);
        }
        
        /// <inheritdoc />
        public void Clear(Color color)
        {
            PrepareCanvas();
            Canvas.Clear(color.ToSKColor());
        }

        /// <inheritdoc />
        public void DrawBitmap(IBitmapImpl source, double opacity, Rect sourceRect, Rect destRect)
        {
            PrepareCanvas();
            var drawableImage = (IDrawableBitmapImpl)source;
            var s = sourceRect.ToSKRect();
            var d = destRect.ToSKRect();
            var isUpscaling = d.Width > s.Width || d.Height > s.Height;

            var paint = SKPaintCache.Shared.Get();
            var samplingOptions = RenderOptions.BitmapInterpolationMode.ToSKSamplingOptions(isUpscaling);

            paint.Color = new SKColor(255, 255, 255, (byte)(255 * opacity * _currentOpacity));
            paint.BlendMode = RenderOptions.BitmapBlendingMode.ToSKBlendMode();
            paint.IsAntialias = RenderOptions.EdgeMode != EdgeMode.Aliased;

            drawableImage.Draw(this, s, d, samplingOptions, paint);
            SKPaintCache.Shared.ReturnReset(paint);
        }

        bool IAlphaGlyphMaskContext.PrefersAlphaMasks => GrContext is not null;

        /// <inheritdoc cref="ITransformedGlyphContext.RasterTarget"/>
        internal GlyphRasterTarget GlyphRasterTarget => _glyphRasterTarget;

        GlyphRasterTarget ITransformedGlyphContext.RasterTarget => _glyphRasterTarget;

        // Ganesh tiles a raster image larger than the texture limit when drawing it, but a mask
        // that fits uploads as one cached texture and does not depend on that fallback.
        // Read once per context: every text run asks, and the context answers through a native call.
        int IAlphaGlyphMaskContext.MaxRunMaskSize => _maxRunMaskSize ??= GrContext?.MaxTextureSize ?? int.MaxValue;

        private int? _maxRunMaskSize;

        bool IAlphaGlyphMaskContext.TryGetLcdGeometry(out LcdMaskGeometry geometry)
        {
            geometry = _lcdMaskGeometry ?? LcdMaskGeometry.RgbHorizontal;

            // Eligible only on a surface that declares horizontal stripes, outside every
            // composited layer, on targets that permit subpixel text at all. Inside a layer
            // the backdrop is transparent, and per-channel coverage would bake fringes. A GPU
            // context additionally needs the per-channel blender to have compiled; the CPU
            // two-pass blits cannot fold a tracked ambient opacity into their fixed payloads,
            // so those draws degrade to grayscale instead of blending wrong.
            return _lcdMaskGeometry.HasValue && !_disableSubpixelTextRendering && _saveLayerDepth == 0 &&
                   (GrContext is not null ? LcdTextBlender.IsSupported : _currentOpacity >= 1);
        }

        IDisposable IAlphaGlyphMaskContext.CreateLcdMask(ReadOnlySpan<byte> rgba, int width, int height)
        {
            if (TryPlaceLcdMask(rgba, width, height) is { } entry)
            {
                return entry;
            }

            // Alpha carries the channel maximum but stays below every channel only when all
            // channels are equal — declare premul so Skia leaves the payload untouched (each
            // channel is at most the max, which is a valid premultiplied relationship).
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);

            return SKImage.FromPixelCopy(info, rgba, width * 4);
        }

        void IAlphaGlyphMaskContext.DrawLcdMask(IDisposable mask, Rect sourceRect, Rect destRect, uint tintArgb)
        {
            if (mask is LcdAtlasEntry entry && sourceRect == new Rect(0, 0, entry.Width, entry.Height) &&
                destRect.Size == sourceRect.Size)
            {
                DrawLcdAtlasEntry(entry, destRect, tintArgb);
                return;
            }

            DrawLcdMask(mask, sourceRect, destRect, tintArgb, new SKSamplingOptions());
        }

        void ITransformedGlyphContext.DrawMaskStretched(IDisposable mask, Rect sourceRect, Rect destRect,
            uint tintArgb, bool lcd)
        {
            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);

            if (lcd)
            {
                DrawLcdMask(mask, sourceRect, destRect, tintArgb, sampling);
            }
            else
            {
                DrawAlphaMask(mask, sourceRect, destRect, tintArgb, sampling);
            }
        }

        private void DrawLcdMask(IDisposable mask, Rect sourceRect, Rect destRect, uint tintArgb,
            SKSamplingOptions sampling)
        {
            PrepareCanvas(GlyphBatchFlushReason.OtherTextPath);

            var source = sourceRect.ToSKRect();
            SKImage image;
            SKImage? transient = null;

            if (mask is LcdAtlasEntry entry)
            {
                // An entry draws from its page, offset to its place there; a page the atlas has
                // dropped still holds the entry's coverage, but no image of its own.
                transient = entry.IsEvicted ? CreateLcdPageImage(entry.Page) : null;
                image = transient ?? GetLcdPageImage(entry.Page);
                source.Offset(entry.X, entry.Y);
            }
            else
            {
                image = (SKImage)mask;
            }

            var alpha = (byte)((tintArgb >> 24) * _currentOpacity);
            var blender = LcdTextBlender.Get(((uint)alpha << 24) | (tintArgb & 0x00FFFFFF));

            if (blender is null)
            {
                // Unreachable by policy — eligibility requires the compiled blender — but a
                // driver losing the effect must not erase text silently.
                DrawAlphaMask(image, source, destRect, tintArgb, sampling);
            }
            else
            {
                var paint = SKPaintCache.Shared.Get();

                paint.Blender = blender;

                // Masks are drawn 1:1 at device pixels, where nearest sampling keeps coverage
                // exact; only a stretched mask samples bilinearly.
                Canvas.DrawImage(image, source, destRect.ToSKRect(), sampling, paint);
                SKPaintCache.Shared.ReturnReset(paint);
            }

            transient?.Dispose();
        }

        IDisposable IAlphaGlyphMaskContext.CreateAlphaMask(ReadOnlySpan<byte> alpha, int width, int height)
        {
            // An immutable alpha-only image owning a copy of the pixels: its identity is stable,
            // so the GPU texture cache uploads it once, and drawing modulates it by paint color.
            var info = new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul);

            return SKImage.FromPixelCopy(info, alpha, width);
        }

        void IAlphaGlyphMaskContext.DrawAlphaMask(IDisposable mask, Rect sourceRect, Rect destRect, uint tintArgb)
            => DrawAlphaMask(mask, sourceRect, destRect, tintArgb, new SKSamplingOptions());

        private void DrawAlphaMask(IDisposable mask, Rect sourceRect, Rect destRect, uint tintArgb,
            SKSamplingOptions sampling)
        {
            PrepareCanvas(GlyphBatchFlushReason.OtherTextPath);
            DrawAlphaMask((SKImage)mask, sourceRect.ToSKRect(), destRect, tintArgb, sampling);
        }

        private void DrawAlphaMask(SKImage image, SKRect sourceRect, Rect destRect, uint tintArgb,
            SKSamplingOptions sampling)
        {
            var paint = SKPaintCache.Shared.Get();

            paint.Color = new SKColor(
                (byte)(tintArgb >> 16),
                (byte)(tintArgb >> 8),
                (byte)tintArgb,
                (byte)((tintArgb >> 24) * _currentOpacity));

            // Coverage lands in the alpha channel here (A8 image modulated by the paint), so
            // the gamma/contrast table rides a color filter. Exact for opaque foregrounds; a
            // translucent one folds its alpha into the table input, the platform-typical
            // approximation.
            paint.ColorFilter = MaskGammaFilters.Get(
                (byte)(tintArgb >> 16), (byte)(tintArgb >> 8), (byte)tintArgb);

            // Masks are drawn 1:1 at device pixels, where nearest sampling keeps coverage exact;
            // only a stretched mask samples bilinearly.
            Canvas.DrawImage(image, sourceRect, destRect.ToSKRect(), sampling, paint);
            SKPaintCache.Shared.ReturnReset(paint);
        }

        /// <inheritdoc />
        public void DrawBitmap(IBitmapImpl source, IBrush opacityMask, Rect opacityMaskRect, Rect destRect)
        {
            PrepareCanvas();
            PushOpacityMask(opacityMask, opacityMaskRect);
            DrawBitmap(source, 1, new Rect(0, 0, source.PixelSize.Width, source.PixelSize.Height), destRect);
            PopOpacityMask();
        }

        /// <inheritdoc />
        public void DrawLine(IPen? pen, Point p1, Point p2)
        {
            PrepareCanvas();

            if (pen is not null
                && TryCreatePaint(_strokePaint, pen, new Rect(p1, p2).Normalize()) is { } stroke)
            {
                using (stroke)
                {
                    Canvas.DrawLine((float)p1.X, (float)p1.Y, (float)p2.X, (float)p2.Y, stroke.Paint);
                }
            }
        }

        /// <inheritdoc />
        public void DrawGeometry(IBrush? brush, IPen? pen, IGeometryImpl geometry)
        {
            PrepareCanvas();
            var impl = (GeometryImpl) geometry;
            var rect = geometry.Bounds;

            if (brush is not null && impl.FillPath != null)
            {
                using (var fill = CreatePaint(_fillPaint, brush, rect))
                {
                    Canvas.DrawPath(impl.FillPath, fill.Paint);
                }
            }

            if (pen is not null
                && impl.StrokePath != null
                && TryCreatePaint(_strokePaint, pen, rect.Inflate(new Thickness(pen.Thickness / 2))) is { } stroke)
            {
                using (stroke)
                {
                    Canvas.DrawPath(impl.StrokePath, stroke.Paint);
                }
            }
        }

        private static float SkBlurRadiusToSigma(double radius) {
            if (radius <= 0)
                return 0.0f;
            return 0.288675f * (float)radius + 0.5f;
        }
        
        private struct BoxShadowFilter : IDisposable
        {
            public readonly SKPaint Paint;
            private readonly SKImageFilter? _filter;
            public readonly SKClipOperation ClipOperation;

            private BoxShadowFilter(SKPaint paint, SKImageFilter? filter, SKClipOperation clipOperation)
            {
                Paint = paint;
                _filter = filter;
                ClipOperation = clipOperation;
            }

            public static BoxShadowFilter Create(SKPaint paint, BoxShadow shadow, double opacity)
            {
                var ac = shadow.Color;

                var filter = SKImageFilter.CreateBlur(SkBlurRadiusToSigma(shadow.Blur), SkBlurRadiusToSigma(shadow.Blur));
                var color = new SKColor(ac.R, ac.G, ac.B, (byte)(ac.A * opacity));

                paint.Reset();
                paint.IsAntialias = true;
                paint.Color = color;
                paint.ImageFilter = filter;

                var clipOperation = shadow.IsInset ? SKClipOperation.Intersect : SKClipOperation.Difference;

                return new BoxShadowFilter(paint, filter, clipOperation);
            }

            public void Dispose()
            {
                Paint?.Reset();
                _filter?.Dispose();
            }
        }

        private static SKRect AreaCastingShadowInHole(
            SKRect hole_rect,
            float shadow_blur,
            float shadow_spread,
            float offsetX, float offsetY)
        {
            // Adapted from Chromium
            var bounds = hole_rect;

            bounds.Inflate(shadow_blur, shadow_blur);

            if (shadow_spread < 0)
                bounds.Inflate(-shadow_spread, -shadow_spread);

            var offset_bounds = bounds;
            offset_bounds.Offset(-offsetX, -offsetY);
            bounds.Union(offset_bounds);
            return bounds;
        }

        /// <inheritdoc />
        public void DrawRectangle(IExperimentalAcrylicMaterial? material, RoundedRect rect)
        {
            if (rect.Rect.Height <= 0 || rect.Rect.Width <= 0)
                return;
            PrepareCanvas();
            
            var rc = rect.Rect.ToSKRect();
            SKRoundRect? skRoundRect = null;

            if (rect.IsRounded)
            {
                skRoundRect = SKRoundRectCache.Shared.Get();
                skRoundRect.SetRectRadii(rc,
                    new[]
                    {
                        rect.RadiiTopLeft.ToSKPoint(),
                        rect.RadiiTopRight.ToSKPoint(),
                        rect.RadiiBottomRight.ToSKPoint(),
                        rect.RadiiBottomLeft.ToSKPoint(),
                    });
            }

            if (material != null)
            {
                using (var paint = CreateAcrylicPaint(_fillPaint, material))
                {
                    if (skRoundRect is not null)
                    {
                        Canvas.DrawRoundRect(skRoundRect, paint.Paint);
                        SKRoundRectCache.Shared.Return(skRoundRect);
                    }
                    else
                    {
                        Canvas.DrawRect(rc, paint.Paint);
                    }

                }
            }
        }

        /// <inheritdoc />
        public void DrawRectangle(IBrush? brush, IPen? pen, RoundedRect rect, BoxShadows boxShadows = default)
        {
            if (rect.Rect.Height <= 0 || rect.Rect.Width <= 0)
                return;

            // A transparent background, which every templated item container fills by default,
            // changes no pixel under source-over blending, so it leaves the pending glyph batches
            // and the deferred clips alone.
            if (pen is null && boxShadows.Count == 0 && IsInvisibleFill(brush))
            {
                CheckLease();
                return;
            }

            PrepareCanvas();
            // Arbitrary chosen values
            // On OSX Skia breaks OpenGL context when asked to draw, e. g. (0, 0, 623, 6666600) rect
            if (rect.Rect.Height > 8192 || rect.Rect.Width > 8192)
                boxShadows = default;

            var rc = rect.Rect.ToSKRect();
            var isRounded = rect.IsRounded;
            var needRoundRect = rect.IsRounded || (boxShadows.HasInsetShadows);
            SKRoundRect? skRoundRect = null;
            if (needRoundRect)
            {
                skRoundRect = SKRoundRectCache.Shared.GetAndSetRadii(rc, rect);
            }

            foreach (var boxShadow in boxShadows)
            {
                if (boxShadow != default && !boxShadow.IsInset)
                {
                    using (var shadow = BoxShadowFilter.Create(_boxShadowPaint, boxShadow, _currentOpacity))
                    {
                        var spread = (float)boxShadow.Spread;
                        if (boxShadow.IsInset)
                            spread = -spread;

                        Canvas.Save();
                        if (isRounded)
                        {
                            var shadowRect = SKRoundRectCache.Shared.GetAndSetRadii(skRoundRect!.Rect, skRoundRect.Radii);
                            if (spread != 0)
                                shadowRect.Inflate(spread, spread);
                            Canvas.ClipRoundRect(skRoundRect,
                                shadow.ClipOperation, true);
                            
                            var oldTransform = Transform;
                            Transform = oldTransform * Matrix.CreateTranslation(boxShadow.OffsetX, boxShadow.OffsetY);
                            Canvas.DrawRoundRect(shadowRect, shadow.Paint);
                            Transform = oldTransform;
                            SKRoundRectCache.Shared.Return(shadowRect);
                        }
                        else
                        {
                            var shadowRect = rc;
                            if (spread != 0)
                                shadowRect.Inflate(spread, spread);
                            Canvas.ClipRect(rc, shadow.ClipOperation);
                            var oldTransform = Transform;
                            Transform = oldTransform * Matrix.CreateTranslation(boxShadow.OffsetX, boxShadow.OffsetY);
                            Canvas.DrawRect(shadowRect, shadow.Paint);
                            Transform = oldTransform;
                        }

                        RestoreCanvas();
                    }
                }
            }

            if (brush != null)
            {
                using (var fill = CreatePaint(_fillPaint, brush, rect.Rect))
                {
                    if (isRounded)
                    {
                        Canvas.DrawRoundRect(skRoundRect, fill.Paint);
                    }
                    else
                    {
                        Canvas.DrawRect(rc, fill.Paint);
                    }
                }
            }

            foreach (var boxShadow in boxShadows)
            {
                if (boxShadow != default && boxShadow.IsInset)
                {
                    using (var shadow = BoxShadowFilter.Create(_boxShadowPaint, boxShadow, _currentOpacity))
                    {
                        var spread = (float)boxShadow.Spread;
                        var offsetX = (float)boxShadow.OffsetX;
                        var offsetY = (float)boxShadow.OffsetY;
                        var outerRect = AreaCastingShadowInHole(rc, (float)boxShadow.Blur, spread, offsetX, offsetY);

                        Canvas.Save();
                        var shadowRect = SKRoundRectCache.Shared.GetAndSetRadii(skRoundRect!.Rect, skRoundRect.Radii);
                        if (spread != 0)
                            shadowRect.Deflate(spread, spread);
                        Canvas.ClipRoundRect(skRoundRect,
                            shadow.ClipOperation, true);
                        
                        var oldTransform = Transform;
                        Transform = oldTransform * Matrix.CreateTranslation(boxShadow.OffsetX, boxShadow.OffsetY);
                        using (var outerRRect = new SKRoundRect(outerRect))
                            Canvas.DrawRoundRectDifference(outerRRect, shadowRect, shadow.Paint);
                        Transform = oldTransform;
                        RestoreCanvas();
                        SKRoundRectCache.Shared.Return(shadowRect);
                    }
                }
            }

            if (pen is not null
                && TryCreatePaint(_strokePaint, pen, rect.Rect.Inflate(new Thickness(pen.Thickness / 2))) is { } stroke)
            {
                using (stroke)
                {
                    if (isRounded)
                    {
                        Canvas.DrawRoundRect(skRoundRect, stroke.Paint);
                    }
                    else
                    {
                        Canvas.DrawRect(rc, stroke.Paint);
                    }
                }
            }

            if (skRoundRect is not null)
                SKRoundRectCache.Shared.Return(skRoundRect);
        }

        private static bool IsInvisibleFill(IBrush? brush) =>
            brush is null or ISolidColorBrush { Color.A: 0 } or ISolidColorBrush { Opacity: 0 };

        /// <inheritdoc />
        public void DrawRegion(IBrush? brush, IPen? pen, IPlatformRenderInterfaceRegion region)
        {
            var r = (SkiaRegionImpl)region;
            if(r.IsEmpty)
                return;
            PrepareCanvas();
            
            if (brush != null)
            {
                using (var fill = CreatePaint(_fillPaint, brush, r.Bounds.ToRectUnscaled()))
                {
                    Canvas.DrawRegion(r.Region, fill.Paint);
                }
            }

            if (pen is not null
                && TryCreatePaint(_strokePaint, pen, r.Bounds.ToRectUnscaled().Inflate(new Thickness(pen.Thickness / 2))) is { } stroke)
            {
                using (stroke)
                {
                    Canvas.DrawRegion(r.Region, stroke.Paint);
                }
            }
        }

        /// <inheritdoc />
        public void DrawEllipse(IBrush? brush, IPen? pen, Rect rect)
        {
            if (rect.Height <= 0 || rect.Width <= 0)
                return;
            PrepareCanvas();
            
            var rc = rect.ToSKRect();

            if (brush != null)
            {
                using (var fill = CreatePaint(_fillPaint, brush, rect))
                {
                    Canvas.DrawOval(rc, fill.Paint);
                }
            }

            if (pen is not null
                && TryCreatePaint(_strokePaint, pen, rect.Inflate(new Thickness(pen.Thickness / 2))) is { } stroke)
            {
                using (stroke)
                {
                    Canvas.DrawOval(rc, stroke.Paint);
                }
            }
        }
       
        /// <inheritdoc />
        public void DrawGlyphRun(IBrush? foreground, IGlyphRunImpl glyphRun)
        {
            var timer = GlyphPhaseTimers.Start();

            DrawGlyphRunCore(foreground, glyphRun);
            GlyphPhaseTimers.Stop(GlyphTimerPhase.GlyphRun, timer);
        }

        private void DrawGlyphRunCore(IBrush? foreground, IGlyphRunImpl glyphRun)
        {
            CheckLease();

            if (foreground is null)
            {
                return;
            }

            // Determine effective TextOptions for text rendering. Start with current pushed TextOptions.
            var effectiveTextOptions = TextOptions;

            // Map subpixel modes to grayscale when subpixel rendering is disabled globally or
            // the target is not a display surface. Offscreen output is captured, composed or
            // read back with alpha, where per-channel coverage is meaningless, so even an
            // explicit SubpixelAntialias request degrades there (DirectWrite semantics).
            if (_disableSubpixelTextRendering || !_surfaceIsDisplay)
            {
                var mode = effectiveTextOptions.TextRenderingMode;

                if (mode == TextRenderingMode.SubpixelAntialias ||
                    (mode == TextRenderingMode.Unspecified && (RenderOptions.EdgeMode == EdgeMode.Antialias || RenderOptions.EdgeMode == EdgeMode.Unspecified)))
                {
                    effectiveTextOptions = effectiveTextOptions with { TextRenderingMode = TextRenderingMode.Antialias };
                }
            }

            var renderOptions = RenderOptions;

            // If TextRenderingMode is unspecified in TextOptions, use the one from RenderOptions.
#pragma warning disable CS0618
            if (effectiveTextOptions.TextRenderingMode == TextRenderingMode.Unspecified && renderOptions.TextRenderingMode != TextRenderingMode.Unspecified)
            {
                effectiveTextOptions = effectiveTextOptions with { TextRenderingMode = renderOptions.TextRenderingMode };
            }
#pragma warning restore CS0618

            // Managed rasterization path: decided before any Skia paint exists, so mask-path
            // draws build no unused paint. Falls back to the run's own native blob when the
            // triage rejects this draw (transform class, size, foreground kind).
            if (glyphRun is ManagedGlyphRunImpl managedRun)
            {
                // Masks carry no COLR v1 paint: those glyphs draw from their drawings, and the
                // stretches between them come back through here as runs of their own, so they
                // take the tiers below and the native fallbacks only for their own reasons.
                if (managedRun.ColorGlyphSegments is { } colorSegments)
                {
                    ColorGlyphRunSplitter.DrawSegments(this, colorSegments, foreground);

                    return;
                }

                var maskTimer = GlyphPhaseTimers.Start();
                var drawn = MaskGlyphRunRenderer.TryDraw(this, managedRun, foreground,
                    effectiveTextOptions.TextRenderingMode, effectiveTextOptions.TextHintingMode);

                GlyphPhaseTimers.Stop(GlyphTimerPhase.MaskRunDraw, maskTimer);

                if (drawn)
                {
                    if (TextTierDiagnostics.CountTiers)
                    {
                        System.Threading.Interlocked.Increment(ref TextTierDiagnostics.MaskTierDraws);
                    }

                    if (TextTierDiagnostics.TintTiers)
                    {
                        FlushGlyphBatch(GlyphBatchFlushReason.Other);
                        TextTierDiagnostics.DrawBadge(Canvas, glyphRun.Bounds, TextTierDiagnostics.MaskTierColor);
                    }

                    return;
                }

                // The draws the upright triage rejected (rotation, skew, anisotropic scale,
                // sizes past the upright ceiling) take transformed masks on every context.
                // Declines fall through to the native blob.
                if (MaskGlyphRunRenderer.TryDrawTransformed(this, managedRun, foreground,
                        effectiveTextOptions.TextRenderingMode))
                {
                    if (TextTierDiagnostics.CountTiers)
                    {
                        System.Threading.Interlocked.Increment(ref TextTierDiagnostics.TransformedMaskTierDraws);
                    }

                    if (TextTierDiagnostics.TintTiers)
                    {
                        FlushGlyphBatch(GlyphBatchFlushReason.Other);
                        TextTierDiagnostics.DrawBadge(Canvas, glyphRun.Bounds,
                            TextTierDiagnostics.TransformedMaskTierColor);
                    }

                    return;
                }

                // The native fallbacks below draw straight to the canvas.
                FlushGlyphBatch(GlyphBatchFlushReason.OtherTextPath);

                if (ManagedGlyphOutlines.AreRequired(managedRun.GlyphTypeface))
                {
                    using (var outlinePaint = CreatePaint(_fillPaint, foreground, managedRun.BrushBounds))
                    {
                        Canvas.DrawPath(NativeTextBlob.GetOutlinePath(managedRun), outlinePaint.Paint);
                    }

                    // Outlines carry no colour: the path leaves colour glyphs out, and they draw
                    // from their own drawings on top, at the run's variation.
                    if (managedRun.GlyphTypeface.ColorTable is not null ||
                        managedRun.GlyphTypeface.BitmapSource is not null)
                    {
                        using var colorContext = new PlatformDrawingContext(this, ownsImpl: false);

                        ColorGlyphRunSplitter.DrawColorGlyphs(colorContext, managedRun, foreground);
                    }

                    return;
                }

                // Null without a native face (synthetic typeface) or when the colour split
                // below takes every glyph.
                var fallbackBlob = NativeTextBlob.TryGetTextBlob(managedRun, effectiveTextOptions, RenderOptions);

                if (fallbackBlob is not null)
                {
                    using var fallbackPaint = CreatePaint(_fillPaint, foreground, managedRun.BrushBounds);

                    Canvas.DrawText(fallbackBlob, (float)glyphRun.BaselineOrigin.X,
                        (float)glyphRun.BaselineOrigin.Y, fallbackPaint.Paint);
                }

                if (NativeTextBlob.SplitsColorGlyphs(managedRun.GlyphTypeface))
                {
                    using var colorContext = new PlatformDrawingContext(this, ownsImpl: false);

                    ColorGlyphRunSplitter.DrawColorGlyphs(colorContext, managedRun, foreground);
                }

                if (TextTierDiagnostics.CountTiers)
                {
                    System.Threading.Interlocked.Increment(ref TextTierDiagnostics.BlobTierDraws);
                }

                if (TextTierDiagnostics.TintTiers)
                {
                    TextTierDiagnostics.DrawBadge(Canvas, glyphRun.Bounds, TextTierDiagnostics.BlobTierColor);
                }

                return;
            }

            FlushGlyphBatch(GlyphBatchFlushReason.OtherTextPath);

            // The native blob applies the face's simulations to every glyph, colour glyphs
            // included (Skia's skew slants them). Text layout splits colour glyphs out of the
            // run before it gets here, so only a direct glyph run draw of an oblique colour
            // face shows the slant; see ColorGlyphRunSplitter.
            var setupTimer = GlyphPhaseTimers.Start();

            using (var paintWrapper = CreatePaint(_fillPaint, foreground, glyphRun.Bounds))
            {
                var glyphRunImpl = (GlyphRunImpl)glyphRun;

                var textBlob = glyphRunImpl.GetTextBlob(effectiveTextOptions, RenderOptions);

                GlyphPhaseTimers.Stop(GlyphTimerPhase.BackendTextSetup, setupTimer);

                var drawTimer = GlyphPhaseTimers.Start();

                Canvas.DrawText(textBlob, (float)glyphRun.BaselineOrigin.X,
                    (float)glyphRun.BaselineOrigin.Y, paintWrapper.Paint);
                GlyphPhaseTimers.Stop(GlyphTimerPhase.BackendDrawText, drawTimer);
            }
        }

        /// <inheritdoc />
        public IDrawingContextLayerImpl CreateLayer(PixelSize size)
        {
            CheckLease();
            return CreateRenderTarget(size, true, false);
        }

        /// <inheritdoc />
        public void PushClip(Rect clip)
        {
            if (TryDeferRectClip(clip))
            {
                return;
            }

            PrepareCanvas(GlyphBatchFlushReason.Clip);
            Canvas.Save();
            Canvas.ClipRect(clip.ToSKRect());
            TrackRectClip(clip);
        }

        public void PushClip(RoundedRect clip)
        {
            PrepareCanvas(GlyphBatchFlushReason.Clip);
            Canvas.Save();

            // Get the rounded rectangle
            var rc = clip.Rect.ToSKRect();

            // Get a round rect from the cache.
            var roundRect = SKRoundRectCache.Shared.Get();

            roundRect.SetRectRadii(rc,
                new[]
                {
                    clip.RadiiTopLeft.ToSKPoint(), clip.RadiiTopRight.ToSKPoint(),
                    clip.RadiiBottomRight.ToSKPoint(), clip.RadiiBottomLeft.ToSKPoint(),
                });

            Canvas.ClipRoundRect(roundRect, antialias:true);
            TrackShapeClip(clip.Rect);

            // Should not need to reset as SetRectRadii overrides the values.
            SKRoundRectCache.Shared.Return(roundRect);
        }

        public void PushClip(IPlatformRenderInterfaceRegion region)
        {
            var r = ((SkiaRegionImpl)region).Region;
            PrepareCanvas(GlyphBatchFlushReason.Clip);
            Canvas.Save();
            Canvas.ClipRegion(r);
            TrackRegionClip(r);
        }

        private void RestoreCanvas()
        {
            _currentTransform = null;
            Canvas.Restore();
        }
        
        /// <inheritdoc />
        public void PopClip() => PopClipLevel();

        public void PushLayer(Rect bounds)
        {
            PrepareCanvas(GlyphBatchFlushReason.Layer);
            _saveLayerDepth++;
            Canvas.SaveLayer(bounds.ToSKRect(), null!);
        }

        public void PopLayer()
        {
            PrepareCanvas(GlyphBatchFlushReason.Layer);
            _saveLayerDepth--;
            RestoreCanvas();
        }

        /// <inheritdoc />
        public void PushOpacity(double opacity, Rect? bounds)
        {
            PrepareCanvas(GlyphBatchFlushReason.Layer);

            _opacityStack.Push(_currentOpacity);

            var useOpacitySaveLayer = _useOpacitySaveLayer || RenderOptions.RequiresFullOpacityHandling == true;

            if (useOpacitySaveLayer)
            {
                opacity = _currentOpacity * opacity; //Take current multiplied opacity

                _currentOpacity = 1; //Opacity is applied via layering

                _saveLayerDepth++;

                if (bounds.HasValue)
                {
                    var rect = bounds.Value.ToSKRect();
                    Canvas.SaveLayer(rect, new SKPaint { ColorF = new SKColorF(0, 0, 0, (float)opacity) });
                }
                else
                {
                    Canvas.SaveLayer(new SKPaint { ColorF = new SKColorF(0, 0, 0, (float)opacity) });
                }
            }
            else
            {
                _currentOpacity *= opacity;
            }
        }

        /// <inheritdoc />
        public void PopOpacity()
        {
            PrepareCanvas(GlyphBatchFlushReason.Layer);

            var useOpacitySaveLayer = _useOpacitySaveLayer || RenderOptions.RequiresFullOpacityHandling == true;

            if (useOpacitySaveLayer)
            {
                _saveLayerDepth--;
                RestoreCanvas();
            }

            _currentOpacity = _opacityStack.Pop();
        }

        /// <inheritdoc />
        public void PushRenderOptions(RenderOptions renderOptions)
        {
            CheckLease();

            _renderOptionsStack.Push(RenderOptions);

            RenderOptions = RenderOptions.MergeWith(renderOptions);
        }

        public void PushTextOptions(TextOptions textOptions)
        {
            CheckLease();

            _textOptionsStack.Push(TextOptions);

            TextOptions = TextOptions.MergeWith(textOptions);
        }

        public void PopRenderOptions()
        {
            RenderOptions = _renderOptionsStack.Pop();
        }

        public void PopTextOptions()
        {
            TextOptions = _textOptionsStack.Pop();
        }

        /// <inheritdoc />
        public virtual void Dispose()
        {
            if(_disposed)
                return;
            PrepareCanvas(GlyphBatchFlushReason.EndOfSession);
            try
            {
                // Return leased paints.
                SKPaintCache.Shared.ReturnReset(_strokePaint);
                SKPaintCache.Shared.ReturnReset(_fillPaint);
                SKPaintCache.Shared.ReturnReset(_boxShadowPaint);

                if (_grContext != null)
                {
                    Monitor.Exit(_grContext);
                    _grContext = null;
                }

                if (_disposables != null)
                {
                    foreach (var disposable in _disposables)
                        disposable?.Dispose();
                    _disposables = null;
                }
            }
            finally
            {
                _disposed = true;
            }
        }

        /// <inheritdoc />
        public void PushGeometryClip(IGeometryImpl clip)
        {
            PrepareCanvas(GlyphBatchFlushReason.Clip);
            Canvas.Save();
            Canvas.ClipPath(((GeometryImpl)clip).FillPath, SKClipOperation.Intersect, true);
            TrackShapeClip(clip.Bounds);
        }

        /// <inheritdoc />
        public void PopGeometryClip() => PopClipLevel();

        /// <inheritdoc />
        public void PushOpacityMask(IBrush mask, Rect bounds)
        {
            PrepareCanvas(GlyphBatchFlushReason.Layer);

            var paint = SKPaintCache.Shared.Get();

            _saveLayerDepth++;
            Canvas.SaveLayer(bounds.ToSKRect(), paint);
            _maskStack.Push((Canvas.TotalMatrix, CreatePaint(paint, mask, bounds)));
        }

        /// <inheritdoc />
        public void PopOpacityMask()
        {
            PrepareCanvas(GlyphBatchFlushReason.Layer);

            var paint = SKPaintCache.Shared.Get();
            paint.BlendMode = SKBlendMode.DstIn;
            
            Canvas.SaveLayer(paint);
            SKPaintCache.Shared.ReturnReset(paint);
            
            var (transform, paintWrapper) = _maskStack.Pop();
            Canvas.SetMatrix(transform);
            using (paintWrapper)
            {
                Canvas.DrawPaint(paintWrapper.Paint);
            }
            // Return the paint wrapper's paint less the reset since the paint is already reset in the Dispose method above.
            SKPaintCache.Shared.Return(paintWrapper.Paint);

            RestoreCanvas();

            _saveLayerDepth--;
            RestoreCanvas();
        }

        /// <inheritdoc />
        public Matrix Transform
        {
            // There is a Canvas.TotalMatrix (non 4x4 overload), but internally it still uses 4x4 matrix.
            // We want to avoid SKMatrix4x4 -> SKMatrix -> Matrix conversion by directly going SKMatrix4x4 -> Matrix.
            get { return _currentTransform ??= Canvas.TotalMatrix44.ToAvaloniaMatrix(); }
            set
            {
                CheckLease();
                if (_currentTransform == value)
                    return;

                _currentTransform = value;

                var transform = value;

                if (_postTransform.HasValue)
                {
                    transform *= _postTransform.Value;
                }

                // Canvas.SetMatrix internally uses 4x4 matrix, even with SKMatrix(3x3) overload.
                // We want to avoid Matrix -> SKMatrix -> SKMatrix4x4 conversion by directly going Matrix -> SKMatrix4x4.
                Canvas.SetMatrix(transform.ToSKMatrix44());
            }
        }

        public object? GetFeature(Type t)
        {
            if (t == typeof(ISkiaSharpApiLeaseFeature))
                return new SkiaLeaseFeature(this);
            return null;
        }

        /// <summary>
        /// The relative transform of a brush conjugated into target space: the
        /// unit square maps onto <paramref name="targetRect"/>, the matrix acts
        /// inside that space, before the absolute brush transform.
        /// </summary>
        private static Matrix? GetRelativeTransform(IBrush brush, Rect targetRect)
        {
            if (brush.RelativeTransform is not { } relativeTransform
                || targetRect.Width <= 0 || targetRect.Height <= 0)
            {
                return null;
            }

            var matrix = relativeTransform.Value;
            if (matrix.IsIdentity)
                return null;

            return Matrix.CreateTranslation(-targetRect.X, -targetRect.Y)
                   * Matrix.CreateScale(1 / targetRect.Width, 1 / targetRect.Height)
                   * matrix
                   * Matrix.CreateScale(targetRect.Width, targetRect.Height)
                   * Matrix.CreateTranslation(targetRect.X, targetRect.Y);
        }

        /// <summary>
        /// Configure paint wrapper for using gradient brush.
        /// </summary>
        /// <param name="paintWrapper">Paint wrapper.</param>
        /// <param name="targetRect">Target rect.</param>
        /// <param name="gradientBrush">Gradient brush.</param>
        private static void ConfigureGradientBrush(ref PaintWrapper paintWrapper, Rect targetRect, IGradientBrush gradientBrush)
        {
            var tileMode = gradientBrush.SpreadMethod.ToSKShaderTileMode();
            var stopColors = gradientBrush.GradientStops.Select(s => s.Color.ToSKColor()).ToArray();
            var stopOffsets = gradientBrush.GradientStops.Select(s => (float)s.Offset).ToArray();

            switch (gradientBrush)
            {
                case ILinearGradientBrush linearGradient:
                    {
                        var start = linearGradient.StartPoint.ToPixels(targetRect).ToSKPoint();
                        var end = linearGradient.EndPoint.ToPixels(targetRect).ToSKPoint();

                        var transform = GetRelativeTransform(linearGradient, targetRect);
                        if (linearGradient.Transform is { } absoluteTransform)
                        {
                            var transformOrigin = linearGradient.TransformOrigin.ToPixels(targetRect);
                            var offset = Matrix.CreateTranslation(transformOrigin);
                            var absolute = (-offset) * absoluteTransform.Value * (offset);
                            transform = transform.HasValue ? transform.Value * absolute : absolute;
                        }

                        // would be nice to cache these shaders possibly?
                        using (var shader = transform.HasValue
                                   ? SKShader.CreateLinearGradient(start, end, stopColors, stopOffsets, tileMode,
                                       transform.Value.ToSKMatrix())
                                   : SKShader.CreateLinearGradient(start, end, stopColors, stopOffsets, tileMode))
                        {
                            paintWrapper.Paint.Shader = shader;
                        }

                        break;
                    }
                case IRadialGradientBrush radialGradient:
                    {
                        var centerPoint = radialGradient.Center.ToPixels(targetRect);
                        var center = centerPoint.ToSKPoint();

                        var radiusX = (radialGradient.RadiusX.ToValue(targetRect.Width));
                        var radiusY = (radialGradient.RadiusY.ToValue(targetRect.Height));

                        var originPoint = radialGradient.GradientOrigin.ToPixels(targetRect);

                        Matrix? transform = null;

                        if (radiusX != radiusY)
                            transform =
                                Matrix.CreateTranslation(-centerPoint)
                                * Matrix.CreateScale(1, radiusY / radiusX)
                                * Matrix.CreateTranslation(centerPoint);

                        if (GetRelativeTransform(radialGradient, targetRect) is { } relative)
                            transform = transform.HasValue ? transform * relative : relative;

                        if (radialGradient.Transform != null)
                        {
                            var transformOrigin = radialGradient.TransformOrigin.ToPixels(targetRect);
                            var offset = Matrix.CreateTranslation(transformOrigin);
                            var brushTransform = (-offset) * radialGradient.Transform.Value * (offset);
                            transform = transform.HasValue ? transform * brushTransform : brushTransform;
                        }

                        if (originPoint.Equals(centerPoint))
                        {
                            // when the origin is the same as the center the Skia RadialGradient acts the same as D2D
                            using (var shader =
                                       transform.HasValue
                                           ? SKShader.CreateRadialGradient(center, (float)radiusX, stopColors, stopOffsets, tileMode,
                                               transform.Value.ToSKMatrix())
                                           : SKShader.CreateRadialGradient(center, (float)radiusX, stopColors, stopOffsets, tileMode)
                                      )
                            {
                                paintWrapper.Paint.Shader = shader;
                            }
                        }
                        else
                        {
                            // when the origin is different to the center use a two point ConicalGradient to match the behaviour of D2D
                            if (radiusX != radiusY)
                                // Adjust the origin point for radiusX/Y transformation by reversing it
                                originPoint = originPoint.WithY(
                                    (originPoint.Y - centerPoint.Y) * radiusX / radiusY + centerPoint.Y);

                            var origin = originPoint.ToSKPoint();

                            var endOffset = stopOffsets[stopOffsets.Length - 1];

                            var start = origin;
                            var radiusStart = 0f;
                            var end = center;
                            var radiusEnd = (float)radiusX;
                            var reverse = (centerPoint.X != originPoint.X  || centerPoint.Y != originPoint.Y) && endOffset == 1;

                            if (reverse)
                            {
                                // reverse the order of the stops to match D2D
                                (start, radiusStart, end, radiusEnd) = (end, radiusEnd, start, radiusStart);

                                var count = stopOffsets.Length;

                                var reversedColors = new SKColor[stopColors.Length];
                                // and then reverse the reference point of the stops
                                var reversedStops = new float[count];

                                for (var i = 0; i < count; i++)
                                {
                                    var offset = radialGradient.GradientStops[i].Offset;

                                    offset = 1 - offset;

                                    if (MathUtilities.IsZero(offset))
                                    {
                                        offset = 0;
                                    }

                                    var reversedIndex = count - 1 - i;

                                    reversedStops[reversedIndex] = (float)offset;
                                    reversedColors[reversedIndex] = stopColors[i];
                                }
                               
                                stopColors = reversedColors;
                                stopOffsets = reversedStops;
                            }

                            // compose with a background colour of the final stop to match D2D's behaviour of filling with the final color
                            using (var shader = SKShader.CreateCompose(
                                       SKShader.CreateColor(stopColors[0]),
                                       transform.HasValue
                                           ? SKShader.CreateTwoPointConicalGradient(start, radiusStart, end, radiusEnd,
                                              stopColors, stopOffsets, tileMode, transform.Value.ToSKMatrix())
                                           : SKShader.CreateTwoPointConicalGradient(start, radiusStart, end, radiusEnd,
                                              stopColors, stopOffsets, tileMode)
                                        )
                                    )
                            {
                                paintWrapper.Paint.Shader = shader;
                            }
                        }

                        break;
                    }
                case IConicGradientBrush conicGradient:
                    {
                        var center = conicGradient.Center.ToPixels(targetRect).ToSKPoint();

                        // Skia's default is that angle 0 is from the right hand side of the center point
                        // but we are matching CSS where the vertical point above the center is 0.
                        var angle = (float)(conicGradient.Angle - 90);
                        var rotation = SKMatrix.CreateRotationDegrees(angle, center.X, center.Y);

                        var transform = GetRelativeTransform(conicGradient, targetRect);
                        if (conicGradient.Transform is { } absoluteTransform)
                        {
                            var transformOrigin = conicGradient.TransformOrigin.ToPixels(targetRect);
                            var offset = Matrix.CreateTranslation(transformOrigin);
                            var absolute = (-offset) * absoluteTransform.Value * (offset);
                            transform = transform.HasValue ? transform.Value * absolute : absolute;
                        }

                        if (transform.HasValue)
                            rotation = rotation.PostConcat(transform.Value.ToSKMatrix());

                        using (var shader =
                            SKShader.CreateSweepGradient(center, stopColors, stopOffsets, rotation))
                        {
                            paintWrapper.Paint.Shader = shader;
                        }

                        break;
                    }
            }
        }

        /// <summary>
        /// Configure paint wrapper for using tile brush.
        /// </summary>
        /// <param name="paintWrapper">Paint wrapper.</param>
        /// <param name="targetBox">Target bounding box.</param>
        /// <param name="tileBrush">Tile brush to use.</param>
        /// <param name="tileBrushImage">Tile brush image.</param>
        private void ConfigureTileBrush(ref PaintWrapper paintWrapper, Rect targetBox, ITileBrush tileBrush, IDrawableBitmapImpl tileBrushImage)
        {
            var calc = new TileBrushCalculator(tileBrush, tileBrushImage.PixelSize.ToSizeWithDpi(_intermediateSurfaceDpi), targetBox.Size);
            var intermediate = CreateRenderTarget(
                PixelSize.FromSizeWithDpi(calc.IntermediateSize, _intermediateSurfaceDpi), false, true);

            paintWrapper.AddDisposable(intermediate);

            using (var context = intermediate.CreateDrawingContext())
            {
                var sourceRect = new Rect(tileBrushImage.PixelSize.ToSizeWithDpi(96));
                var targetRect = new Rect(tileBrushImage.PixelSize.ToSizeWithDpi(_intermediateSurfaceDpi));

                context.Clear(Colors.Transparent);
                context.PushClip(calc.IntermediateClip);
                context.PushRenderOptions(RenderOptions);
                
                context.Transform = calc.IntermediateTransform;
                
                context.DrawBitmap(
                    tileBrushImage,
                    1,
                    sourceRect,
                    targetRect);

                context.PopRenderOptions();
                context.PopClip();
            }

            var tileTransform =
                tileBrush.TileMode != TileMode.None
                    ? SKMatrix.CreateTranslation(-(float)calc.DestinationRect.X, -(float)calc.DestinationRect.Y)
                    : SKMatrix.CreateIdentity();

            SKShaderTileMode tileX =
                tileBrush.TileMode == TileMode.None
                    ? SKShaderTileMode.Decal
                    : tileBrush.TileMode == TileMode.FlipX || tileBrush.TileMode == TileMode.FlipXY
                        ? SKShaderTileMode.Mirror
                        : SKShaderTileMode.Repeat;

            SKShaderTileMode tileY =
                tileBrush.TileMode == TileMode.None
                    ? SKShaderTileMode.Decal
                    : tileBrush.TileMode == TileMode.FlipY || tileBrush.TileMode == TileMode.FlipXY
                        ? SKShaderTileMode.Mirror
                        : SKShaderTileMode.Repeat;


            var image = intermediate.SnapshotImage();
            paintWrapper.AddDisposable(image);

            var paintTransform = default(SKMatrix);

            SKMatrix.Concat(
                ref paintTransform,
                tileTransform,
                SKMatrix.CreateScale((float)(96.0 / _intermediateSurfaceDpi.X), (float)(96.0 / _intermediateSurfaceDpi.Y)));

            if (tileBrush.DestinationRect.Unit == RelativeUnit.Relative)
                paintTransform =
                    paintTransform.PreConcat(SKMatrix.CreateTranslation((float)targetBox.X, (float)targetBox.Y));

            // Both brush transforms act on the tile once it sits in target space, the relative one
            // first.
            if (GetRelativeTransform(tileBrush, targetBox) is { } relativeTransform)
                paintTransform = paintTransform.PostConcat(relativeTransform.ToSKMatrix());

            if (tileBrush.Transform is { })
            {
                var origin = tileBrush.TransformOrigin.ToPixels(targetBox);
                var offset = Matrix.CreateTranslation(origin);
                var transform = (-offset) * tileBrush.Transform.Value * (offset);

                paintTransform = paintTransform.PostConcat(transform.ToSKMatrix());
            }

            using (var shader = image.ToShader(tileX, tileY, paintTransform))
            {
                paintWrapper.Paint.Shader = shader;
            }
        }

        private void ConfigureSceneBrushContent(ref PaintWrapper paintWrapper, ISceneBrushContent content,
            Rect targetRect)
        {
            if(content.UseScalableRasterization)
                ConfigureSceneBrushContentWithPicture(ref paintWrapper, content, targetRect);
            else
                ConfigureSceneBrushContentWithSurface(ref paintWrapper, content, targetRect);
        }
        
        private void ConfigureSceneBrushContentWithSurface(ref PaintWrapper paintWrapper, ISceneBrushContent content,
            Rect targetRect)
        {
            var rect = content.Rect;
            var intermediateSize = rect.Size;

            if (intermediateSize.Width >= 1 && intermediateSize.Height >= 1)
            {
                using var intermediate = CreateRenderTarget(
                    PixelSize.FromSizeWithDpi(intermediateSize, _intermediateSurfaceDpi), false, true);

                using (var ctx = intermediate.CreateDrawingContext())
                {
                    ctx.PushRenderOptions(RenderOptions);
                    ctx.Clear(Colors.Transparent);
                    content.Render(ctx, rect.TopLeft == default ? null : Matrix.CreateTranslation(-rect.X, -rect.Y));
                    ctx.PopRenderOptions();
                }

                ConfigureTileBrush(ref paintWrapper, targetRect, content.Brush, intermediate);
            }
        }
        
        private void ConfigureSceneBrushContentWithPicture(ref PaintWrapper paintWrapper, ISceneBrushContent content,
            Rect targetRect)
        {
            // To understand what happens here, read
            // https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.tilebrush
            // and the rest of the docs
            
            // Avalonia follows WPF and WPF's brushes completely ignore whatever layout bounds visuals have, 
            // and instead are using content bounds, e. g.
            // ╔════════════════════════════════════╗  <--- target control
            // ║                                    ║       layout bounds
            // ║  ╔═════╗───────────┐ <--- content  ║
            // ║  ║     ║<- content │      bounds   ║
            // ║  ╚═════╝        ╔══╗               ║
            // ║  │  ^ content   ╚══╝               ║
            // ║  │ ╔═════╗content^ │               ║
            // ║  └─╚═════╝─────────┘               ║
            // ║                                    ║
            // ╚════════════════════════════════════╝
            //
            // Source Rect (aka ViewBox) is relative to the content bounds, not to the visual/drawing
            
            var contentRect = content.Rect;
            var sourceRect = content.Brush.SourceRect.ToPixels(contentRect);
            
            // Early escape
            if (contentRect.Size.Width <= 0 || contentRect.Size.Height <= 0
                || sourceRect.Size.Width <= 0 || sourceRect.Size.Height <= 0)
            {
                paintWrapper.Paint.Color = SKColor.Empty;
                return;
            }
            
            // We are moving the render area to make the top-left corner of the SourceRect (ViewBox) to be at (0,0)
            // of the tile
            var contentRenderTransform = Matrix.CreateTranslation(-sourceRect.X, -sourceRect.Y);
            
            // DestinationRect (aka Viewport) is specified relative to the target rect
            var destinationRect = content.Brush.DestinationRect.ToPixels(targetRect);
            
            // Tile size matches the destination rect size
            var tileSize = destinationRect.Size;
            
            // Apply transforms to stretch content to match the tile
            if (sourceRect.Size != tileSize)
            {
                // Stretch the content rect to match the tile size
                var scale = content.Brush.Stretch.CalculateScaling(tileSize, sourceRect.Size);

                // And move the resulting rect according to alignment rules
                var alignmentTranslate = TileBrushCalculator.CalculateTranslate(
                    content.Brush.AlignmentX,
                    content.Brush.AlignmentY, sourceRect.Size * scale, tileSize);

                contentRenderTransform = contentRenderTransform * Matrix.CreateScale(scale) *
                                         Matrix.CreateTranslation(alignmentTranslate);
            }
            
            // Pre-rasterize the tile into SKPicture
            using var pictureTarget = new PictureRenderTarget(_gpu, _grContext, _intermediateSurfaceDpi);
            using (var ctx = pictureTarget.CreateDrawingContext(tileSize, false))
            {
                ctx.PushRenderOptions(RenderOptions);
                content.Render(ctx, contentRenderTransform);
                ctx.PopRenderOptions();
            }
            using var tile = pictureTarget.GetPicture();
            
            // If there is no BrushTransform and destinationRect is at (0,0) we don't need any transforms
            Matrix shaderTransform = Matrix.Identity;

            // Apply destinationRect position
            if (destinationRect.Position != default)
                shaderTransform = Matrix.CreateTranslation(destinationRect.X, destinationRect.Y);

            // Apply Brush.RelativeTransform and Brush.Transform to SKShader, in that order
            if (GetRelativeTransform(content, targetRect) is { } relativeTransform)
                shaderTransform *= relativeTransform;

            if (content.Transform != null)
            {
                
                var transformOrigin = content.TransformOrigin.ToPixels(targetRect);
                var offset = Matrix.CreateTranslation(transformOrigin);
                shaderTransform *= (-offset) * content.Transform.Value * (offset);
            }
            
            // Create shader
            var (tileX, tileY) = GetTileModes(content.Brush.TileMode);
            using(var shader = tile.ToShader(tileX, tileY, shaderTransform.ToSKMatrix(), 
                      new SKRect(0, 0, tile.CullRect.Width, tile.CullRect.Height)))
            {
                paintWrapper.Paint.Shader = shader;
            }
        }

        (SKShaderTileMode x, SKShaderTileMode y) GetTileModes(TileMode mode)
        {
            return (
                mode == TileMode.None
                    ? SKShaderTileMode.Decal
                    : mode == TileMode.FlipX || mode == TileMode.FlipXY
                        ? SKShaderTileMode.Mirror
                        : SKShaderTileMode.Repeat,


                mode == TileMode.None
                    ? SKShaderTileMode.Decal
                    : mode == TileMode.FlipY || mode == TileMode.FlipXY
                        ? SKShaderTileMode.Mirror
                        : SKShaderTileMode.Repeat);
        }

        private static SKColorFilter CreateAlphaColorFilter(double opacity)
        {
            if (opacity > 1)
                opacity = 1;
            var c = new byte[256];
            var a = new byte[256];
            for (var i = 0; i < 256; i++)
            {
                c[i] = (byte)i;
                a[i] = (byte)(i * opacity);
            }

            return SKColorFilter.CreateTable(a, c, c, c);
        }

        private static byte Blend(byte leftColor, byte leftAlpha, byte rightColor, byte rightAlpha)
        {
            var ca = leftColor / 255d;
            var aa = leftAlpha / 255d;
            var cb = rightColor / 255d;
            var ab = rightAlpha / 255d;
            var r = (ca * aa + cb * ab * (1 - aa)) / (aa + ab * (1 - aa));
            return (byte)(r * 255);
        }

        internal PaintWrapper CreateAcrylicPaint (SKPaint paint, IExperimentalAcrylicMaterial material)
        {
            var paintWrapper = new PaintWrapper(paint);

            paint.IsAntialias = true;

            var tintOpacity =
                material.BackgroundSource == AcrylicBackgroundSource.Digger ?
                material.TintOpacity : 1;

            const double noiseOpcity = 0.0225;

            var tintColor = material.TintColor;
            var tint = new SKColor(tintColor.R, tintColor.G, tintColor.B, tintColor.A);

            if (s_acrylicNoiseShader == null)
            {
                using (var stream = typeof(DrawingContextImpl).Assembly.GetManifestResourceStream("Avalonia.Skia.Assets.NoiseAsset_256X256_PNG.png"))
                using (var bitmap = SKBitmap.Decode(stream))
                {
                    s_acrylicNoiseShader = SKShader.CreateBitmap(bitmap, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat)
                        .WithColorFilter(CreateAlphaColorFilter(noiseOpcity));
                }
            }

            using (var backdrop = SKShader.CreateColor(new SKColor(material.MaterialColor.R, material.MaterialColor.G, material.MaterialColor.B, material.MaterialColor.A)))
            using (var tintShader = SKShader.CreateColor(tint))
            using (var effectiveTint = SKShader.CreateCompose(backdrop, tintShader))
            using (var compose = SKShader.CreateCompose(effectiveTint, s_acrylicNoiseShader))
            {
                paint.Shader = compose;

                if (material.BackgroundSource == AcrylicBackgroundSource.Digger)
                {
                    paint.BlendMode = SKBlendMode.Src;
                }

                return paintWrapper;
            }
        }

        /// <summary>
        /// Creates paint wrapper for given brush.
        /// </summary>
        /// <param name="paint">The paint to wrap.</param>
        /// <param name="brush">Source brush.</param>
        /// <param name="targetRect">Target rect.</param>
        /// <returns>Paint wrapper for given brush.</returns>
        internal PaintWrapper CreatePaint(SKPaint paint, IBrush brush, Rect targetRect)
        {
            var paintWrapper = new PaintWrapper(paint);

            paint.IsAntialias = RenderOptions.EdgeMode != EdgeMode.Aliased;

            double opacity = brush.Opacity * (_useOpacitySaveLayer ? 1 :_currentOpacity);

            if (brush is ISolidColorBrush solid)
            {
                paint.Color = new SKColor(solid.Color.R, solid.Color.G, solid.Color.B, (byte) (solid.Color.A * opacity));

                return paintWrapper;
            }

            paint.Color = new SKColor(255, 255, 255, (byte) (255 * opacity));

            if (brush is IGradientBrush gradient)
            {
                ConfigureGradientBrush(ref paintWrapper, targetRect, gradient);

                return paintWrapper;
            }

            var tileBrush = brush as ITileBrush;
            var tileBrushImage = default(IDrawableBitmapImpl);

            if (brush is ISceneBrush sceneBrush)
            {
                using (var content = sceneBrush.CreateContent())
                {
                    if (content != null)
                    {
                        ConfigureSceneBrushContent(ref paintWrapper, content, targetRect);
                        return paintWrapper;
                    }
                    else
                        paint.Color = default;
                }
            }
            else if (brush is ISceneBrushContent sceneBrushContent)
            {
                ConfigureSceneBrushContent(ref paintWrapper, sceneBrushContent, targetRect);
                return paintWrapper;
            }
            else
            {
                tileBrushImage = (tileBrush as IImageBrush)?.Source?.GetBitmap() as IDrawableBitmapImpl;
            }

            if (tileBrush != null && tileBrushImage != null)
            {
                ConfigureTileBrush(ref paintWrapper, targetRect, tileBrush, tileBrushImage);
            }
            else
            {
                paint.Color = new SKColor(255, 255, 255, 0);
            }

            return paintWrapper;
        }

        /// <summary>
        /// Creates paint wrapper for given pen.
        /// </summary>
        /// <param name="paint">The paint to wrap.</param>
        /// <param name="pen">Source pen.</param>
        /// <param name="targetRect">Target rect.</param>
        /// <returns></returns>
        private PaintWrapper? TryCreatePaint(SKPaint paint, IPen pen, Rect targetRect)
        {
            // In Skia 0 thickness means - use hairline rendering
            // and for us it means - there is nothing rendered.
            if (pen.Brush is not { } brush || pen.Thickness == 0d)
            {
                return null;
            }

            var rv = CreatePaint(paint, brush, targetRect);

            paint.IsStroke = true;
            paint.StrokeWidth = (float) pen.Thickness;

            // Need to modify dashes due to Skia modifying their lengths
            // https://docs.microsoft.com/en-us/xamarin/xamarin-forms/user-interface/graphics/skiasharp/paths/dots
            // TODO: Still something is off, dashes are now present, but don't look the same as D2D ones.

            paint.StrokeCap = pen.LineCap.ToSKStrokeCap();
            paint.StrokeJoin = pen.LineJoin.ToSKStrokeJoin();

            paint.StrokeMiter = (float) pen.MiterLimit;

            if (DrawingContextHelper.TryCreateDashEffect(pen, out var dashEffect))
            {
                paint.PathEffect = dashEffect;
                rv.AddDisposable(dashEffect);
            }

            return rv;
        }

        /// <summary>
        /// Create new render target compatible with this drawing context.
        /// </summary>
        /// <param name="pixelSize">The size of the render target.</param>
        /// <param name="isLayer">Whether the render target is being created for a layer.</param>
        /// <param name="useScaledDrawing">Auto-scales to DPI</param>
        /// <param name="format">Pixel format.</param>
        /// <returns></returns>
        private SurfaceRenderTarget CreateRenderTarget(PixelSize pixelSize, bool isLayer, bool useScaledDrawing, PixelFormat? format = null)
        {
            var createInfo = new SurfaceRenderTarget.CreateInfo
            {
                Width = pixelSize.Width,
                Height = pixelSize.Height,
                Dpi = _intermediateSurfaceDpi,
                Format = format,
                DisableTextLcdRendering = isLayer ? _disableSubpixelTextRendering : true,
                GrContext = _grContext,
                Gpu = _gpu,
                Session = _session,
                DisableManualFbo = !isLayer,
                UseScaledDrawing = useScaledDrawing
            };

            return new SurfaceRenderTarget(createInfo);
        }        

        /// <summary>
        /// Skia cached paint state.
        /// </summary>
        private readonly struct PaintState : IDisposable
        {
            private readonly SKColor _color;
            private readonly SKShader _shader;
            private readonly SKPaint _paint;
            
            public PaintState(SKPaint paint, SKColor color, SKShader shader)
            {
                _paint = paint;
                _color = color;
                _shader = shader;
            }

            /// <inheritdoc />
            public void Dispose()
            {
                _paint.Color = _color;
                _paint.Shader = _shader;
            }
        }

        /// <summary>
        /// Skia paint wrapper.
        /// </summary>
        internal struct PaintWrapper : IDisposable
        {
            //We are saving memory allocations there
            public readonly SKPaint Paint;

            private IDisposable? _disposable1;
            private IDisposable? _disposable2;
            private IDisposable? _disposable3;

            public PaintWrapper(SKPaint paint)
            {
                Paint = paint;

                _disposable1 = null;
                _disposable2 = null;
                _disposable3 = null;
            }

            /// <summary>
            /// Add new disposable to a wrapper.
            /// </summary>
            /// <param name="disposable">Disposable to add.</param>
            public void AddDisposable(IDisposable disposable)
            {
                if (_disposable1 == null)
                {
                    _disposable1 = disposable;
                }
                else if (_disposable2 == null)
                {
                    _disposable2 = disposable;
                }
                else if (_disposable3 == null)
                {
                    _disposable3 = disposable;
                }
                else
                {
                    Debug.Assert(false);

                    // ReSharper disable once HeuristicUnreachableCode
                    throw new InvalidOperationException(
                        "PaintWrapper disposable object limit reached. You need to add extra struct fields to support more disposables.");
                }
            }
            
            /// <inheritdoc />
            public void Dispose()
            {
                Paint?.Reset();
                _disposable1?.Dispose();
                _disposable2?.Dispose();
                _disposable3?.Dispose();
            }
        }
    }
}
