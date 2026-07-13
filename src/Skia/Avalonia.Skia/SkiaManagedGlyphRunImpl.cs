using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.TextFormatting;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// The managed glyph run on the Skia backend: mask-path drawing comes from the base class;
    /// this subclass adds a lazily built native <see cref="SKTextBlob"/> so triage-rejected
    /// draws (rotated, oversized, non-solid foreground) and outline-exact
    /// <see cref="GetIntersections"/> keep today's behavior until the managed equivalents land.
    /// </summary>
    internal sealed class SkiaManagedGlyphRunImpl : ManagedGlyphRunImpl
    {
        private readonly SkiaTypeface _glyphTypefaceImpl;
        private SKPoint[]? _blobPositions;

        private readonly TwoLevelCache<TextOptions, SKTextBlob> _textBlobCache =
            new(secondarySize: 3, evictionAction: b => b?.Dispose());

        public SkiaManagedGlyphRunImpl(GlyphTypeface glyphTypeface, double fontRenderingEmSize,
            IReadOnlyList<GlyphInfo> glyphInfos, Point baselineOrigin)
            : base(glyphTypeface, fontRenderingEmSize, glyphInfos, baselineOrigin)
        {
            _glyphTypefaceImpl = (SkiaTypeface)glyphTypeface.PlatformTypeface;
        }

        /// <summary>
        /// The native blob for fallback draws — same options resolution and cache shape as
        /// <see cref="GlyphRunImpl.GetTextBlob"/>.
        /// </summary>
        public SKTextBlob GetTextBlob(TextOptions textOptions, RenderOptions renderOptions)
        {
            if (textOptions.TextRenderingMode == TextRenderingMode.Unspecified)
            {
                textOptions = textOptions with
                {
                    TextRenderingMode = renderOptions.EdgeMode == EdgeMode.Aliased
                        ? TextRenderingMode.Alias
                        : TextRenderingMode.SubpixelAntialias
                };
            }

            return _textBlobCache.GetOrAdd(textOptions, k =>
            {
                using var font = GlyphRunImpl.CreateFont(
                    _glyphTypefaceImpl, (float)FontRenderingEmSize, textOptions);

                if (_blobPositions is null)
                {
                    var positions = GlyphPositions;
                    var points = new SKPoint[GlyphCount];

                    for (var i = 0; i < points.Length; i++)
                    {
                        points[i] = new SKPoint(positions[i * 2], positions[i * 2 + 1]);
                    }

                    _blobPositions = points;
                }

                var builder = SKTextBlobBuilderCache.Shared.Get();

                var runBuffer = builder.AllocatePositionedRun(font, GlyphCount);

                runBuffer.SetPositions(_blobPositions);
                runBuffer.SetGlyphs(GlyphIndices);

                var textBlob = builder.Build()!;
                SKTextBlobBuilderCache.Shared.Return(builder);
                return textBlob;
            });
        }

        public override IReadOnlyList<float> GetIntersections(float lowerLimit, float upperLimit)
        {
            // Outline-exact intercepts via the native blob (identical to GlyphRunImpl) until the
            // managed analytic intercepts replace it — the base box-derived implementation would
            // widen ink-skipping gaps.
            var textBlob = GetTextBlob(default, default);

            return textBlob.GetIntercepts(lowerLimit, upperLimit);
        }

        public override void Dispose()
        {
            _textBlobCache.ClearAndDispose();
            base.Dispose();
        }
    }
}
