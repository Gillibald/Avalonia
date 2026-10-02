using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Metal;
using Avalonia.OpenGL;
using Avalonia.Platform;
using Avalonia.Skia.Metal;
using Avalonia.Skia.Vulkan;
using Avalonia.Vulkan;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// Skia platform render interface.
    /// </summary>
    internal class PlatformRenderInterface : IPlatformRenderInterface
    {
        private readonly long? _maxResourceBytes;
        private readonly bool? _useStencilBuffers;

        public PlatformRenderInterface(long? maxResourceBytes = null, bool? useStencilBuffers = null)
        {
            _maxResourceBytes = maxResourceBytes;
            _useStencilBuffers = useStencilBuffers;
            DefaultPixelFormat = SKImageInfo.PlatformColorType.ToPixelFormat();
        }


        public IPlatformRenderInterfaceContext CreateBackendContext(IPlatformGraphicsContext? graphicsContext)
        {
            if (graphicsContext == null)
                return new SkiaContext(null);
            if (graphicsContext is ISkiaGpu skiaGpu)
                return new SkiaContext(skiaGpu);
            if (graphicsContext is IGlContext gl)
                return new SkiaContext(new GlSkiaGpu(gl, _maxResourceBytes, _useStencilBuffers));
            if (graphicsContext is IMetalDevice metal)
                return new SkiaContext(new SkiaMetalGpu(metal, _maxResourceBytes, _useStencilBuffers));
            if (graphicsContext is IVulkanPlatformGraphicsContext vulkanContext)
                return new SkiaContext(new VulkanSkiaGpu(vulkanContext, _maxResourceBytes, _useStencilBuffers));
            throw new ArgumentException("Graphics context of type is not supported");
        }

        public bool SupportsIndividualRoundRects => true;

        public AlphaFormat DefaultAlphaFormat => AlphaFormat.Premul;

        public PixelFormat DefaultPixelFormat { get; }

        public bool IsSupportedBitmapPixelFormat(PixelFormat format) =>
            format == PixelFormats.Rgb565
            || format == PixelFormats.Bgra8888
            || format == PixelFormats.Rgba8888;

        public bool SupportsRegions => true;
        public IPlatformRenderInterfaceRegion CreateRegion() => new SkiaRegionImpl();

        public IGeometryImpl CreateEllipseGeometry(Rect rect) => new EllipseGeometryImpl(rect);

        public IGeometryImpl CreateLineGeometry(Point p1, Point p2) => new LineGeometryImpl(p1, p2);

        public IGeometryImpl CreateRectangleGeometry(Rect rect) => new RectangleGeometryImpl(rect);

        /// <inheritdoc />
        public IStreamGeometryImpl CreateStreamGeometry()
        {
            return new StreamGeometryImpl();
        }

        public IGeometryImpl CreateGeometryGroup(FillRule fillRule, IReadOnlyList<IGeometryImpl> children)
        {
            return new GeometryGroupImpl(fillRule, children);
        }

        public IGeometryImpl CreateCombinedGeometry(GeometryCombineMode combineMode, IGeometryImpl g1, IGeometryImpl g2)
        {
            return CombinedGeometryImpl.ForceCreate(combineMode, g1, g2);
        }

        public IPlatformTypeface CreateTypeface(GlyphTypeface glyphTypeface)
        {
            if (glyphTypeface.FontMemory is SfntFace face)
            {
                // The lease keeps the font file bytes alive until Skia releases the data; the pin
                // handle lives in the release closure so the pointer stays valid for memory that
                // is not natively allocated as well. Disposal order in the closure matters: unpin
                // before the lease release can dispose the underlying memory owner.
                var lease = face.Clone();

                if (!lease.TryGetFontFileData(out var data, out var faceIndex))
                {
                    lease.Dispose();

                    throw new InvalidOperationException(
                        "The glyph typeface's font memory cannot provide the font file data needed to create a render typeface.");
                }

                var handle = data.Pin();

                SKData skData;

                unsafe
                {
                    skData = SKData.Create((IntPtr)handle.Pointer, data.Length, (_, _) =>
                    {
                        handle.Dispose();
                        lease.Dispose();
                    });
                }

                // SkiaSharp cannot create a typeface at variation coordinates, so the face of a
                // varied glyph typeface is the default instance; ManagedGlyphOutlines keeps
                // varied runs and their geometry off it.
                using (skData)
                {
                    if (SKTypeface.FromData(skData, faceIndex) is { } skTypeface)
                    {
                        return new SkiaTypeface(skTypeface);
                    }
                }

                // Some of Skia's ports load only the first face of a collection (the macOS one
                // creates nothing for a later index): a standalone copy of the face's tables is
                // a first face everywhere.
                if (face.TryCreateStandaloneFontData(out var standalone))
                {
                    using var standaloneData = SKData.CreateCopy(standalone);

                    if (SKTypeface.FromData(standaloneData, 0) is { } standaloneTypeface)
                    {
                        return new SkiaTypeface(standaloneTypeface);
                    }
                }

                throw new InvalidOperationException("Skia could not create a typeface from the font data.");
            }

            throw new InvalidOperationException(
                "The glyph typeface's font memory cannot provide font data for the render typeface.");
        }

        public IGeometryImpl BuildGlyphRunGeometry(GlyphRun glyphRun)
        {
            if (ManagedGlyphOutlines.AreRequired(glyphRun.GlyphTypeface) ||
                !glyphRun.GlyphTypeface.TryGetPlatformTypeface(out var platformTypeface))
            {
                return BuildManagedGlyphRunGeometry(glyphRun);
            }

            if (platformTypeface is not SkiaTypeface skiaTypeface)
            {
                throw new InvalidOperationException("PlatformImpl can't be null.");
            }

            var fontRenderingEmSize = (float)glyphRun.FontRenderingEmSize;

            using var skFont = skiaTypeface.CreateSKFont(fontRenderingEmSize, glyphRun.GlyphTypeface.FontSimulations);

            skFont.Hinting = SKFontHinting.None;

            SKPath path = new SKPath();

            var (currentX, currentY) = glyphRun.BaselineOrigin;

            for (var i = 0; i < glyphRun.GlyphInfos.Count; i++)
            {
                var glyph = glyphRun.GlyphInfos[i].GlyphIndex;
                var glyphPath = skFont.GetGlyphPath(glyph);

                if (glyphPath is not null && !glyphPath.IsEmpty)
                {
                    path.AddPath(glyphPath, (float)currentX, (float)currentY);
                }

                currentX += glyphRun.GlyphInfos[i].GlyphAdvance;
            }

            return new StreamGeometryImpl(path, path);
        }

        private static IGeometryImpl BuildManagedGlyphRunGeometry(GlyphRun glyphRun)
        {
            var typeface = glyphRun.GlyphTypeface;
            var path = ManagedGlyphOutlines.CreatePath();
            var (originX, originY) = glyphRun.BaselineOrigin;
            var currentX = 0.0;

            foreach (var info in glyphRun.GlyphInfos)
            {
                ManagedGlyphOutlines.AddGlyph(path, typeface, glyphRun.FontRenderingEmSize, info.GlyphIndex,
                    (float)(originX + currentX + info.GlyphOffset.X), (float)(originY + info.GlyphOffset.Y));

                currentX += info.GlyphAdvance;
            }

            return new StreamGeometryImpl(path, path);
        }

        /// <inheritdoc />
        public IBitmapImpl LoadBitmap(string fileName)
        {
            using (var stream = File.OpenRead(fileName))
            {
                return LoadBitmap(stream);
            }
        }

        /// <inheritdoc />
        public IBitmapImpl LoadBitmap(Stream stream)
        {
            return new ImmutableBitmap(stream);
        }

        public IWriteableBitmapImpl LoadWriteableBitmapToWidth(Stream stream, int width,
            BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality)
        {
            return new WriteableBitmapImpl(stream, width, true, interpolationMode);
        }

        public IWriteableBitmapImpl LoadWriteableBitmapToHeight(Stream stream, int height,
            BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality)
        {
            return new WriteableBitmapImpl(stream, height, false, interpolationMode);
        }

        public IWriteableBitmapImpl LoadWriteableBitmap(string fileName)
        {
            using (var stream = File.OpenRead(fileName))
            {
                return LoadWriteableBitmap(stream);
            }
        }

        public IWriteableBitmapImpl LoadWriteableBitmap(Stream stream)
        {
            return new WriteableBitmapImpl(stream);
        }

        /// <inheritdoc />
        public IBitmapImpl LoadBitmap(PixelFormat format, AlphaFormat alphaFormat, IntPtr data, PixelSize size, Vector dpi, int stride)
        {
            return new ImmutableBitmap(size, dpi, stride, format, alphaFormat, data);
        }

        /// <inheritdoc />
        public IBitmapImpl LoadBitmapToWidth(Stream stream, int width, BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality)
        {
            return new ImmutableBitmap(stream, width, true, interpolationMode);
        }

        /// <inheritdoc />
        public IBitmapImpl LoadBitmapToHeight(Stream stream, int height, BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality)
        {
            return new ImmutableBitmap(stream, height, false, interpolationMode);
        }

        /// <inheritdoc />
        public IBitmapImpl ResizeBitmap(IBitmapImpl bitmapImpl, PixelSize destinationSize, BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality)
        {
            if (bitmapImpl is ImmutableBitmap ibmp)
            {
                return new ImmutableBitmap(ibmp, destinationSize, interpolationMode);
            }
            else
            {
                throw new Exception("Invalid source bitmap type.");
            }
        }

        /// <inheritdoc />
        public IRenderTargetBitmapImpl CreateRenderTargetBitmap(PixelSize size, Vector dpi)
        {
            if (size.Width < 1)
            {
                throw new ArgumentException("Width can't be less than 1", nameof(size));
            }

            if (size.Height < 1)
            {
                throw new ArgumentException("Height can't be less than 1", nameof(size));
            }

            return new RenderTargetBitmapImpl(size, dpi);
        }

        /// <inheritdoc />
        public IWriteableBitmapImpl CreateWriteableBitmap(PixelSize size, Vector dpi, PixelFormat format, AlphaFormat alphaFormat)
        {
            return new WriteableBitmapImpl(size, dpi, format, alphaFormat);
        }

        public IGlyphRunImpl CreateGlyphRun(GlyphTypeface glyphTypeface, double fontRenderingEmSize,
            IReadOnlyList<GlyphInfo> glyphInfos, Point baselineOrigin)
        {
            // Managed-mode runs are created backend-neutrally in GlyphRun itself; what
            // reaches the platform interface is explicit Backend mode and typefaces the
            // managed rasterizer has nothing to rasterize for.
            return new GlyphRunImpl(glyphTypeface, fontRenderingEmSize, glyphInfos, baselineOrigin);
        }
    }
}
