using System;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// The native-blob fallback for managed glyph runs: builds and caches an
    /// <see cref="SKTextBlob"/> on the run (disposal-tied via the run's artifact slot) for
    /// exactly the draws the managed tiers decline — non-solid foregrounds and anything the
    /// triage rejects on a context without the vector tier. Managed runs are created
    /// backend-neutrally in Avalonia.Base, so this is where the Skia dependency lives now;
    /// synthetic typefaces without a Skia platform face simply have no native fallback.
    /// </summary>
    internal static class NativeTextBlob
    {
        public static SKTextBlob? TryGetTextBlob(ManagedGlyphRunImpl run,
            TextOptions textOptions, RenderOptions renderOptions)
        {
            if (run.GlyphTypeface.PlatformTypeface is not SkiaTypeface)
            {
                return null;
            }

            if (textOptions.TextRenderingMode == TextRenderingMode.Unspecified)
            {
                textOptions = textOptions with
                {
                    TextRenderingMode = renderOptions.EdgeMode == EdgeMode.Aliased
                        ? TextRenderingMode.Alias
                        : TextRenderingMode.SubpixelAntialias
                };
            }

            if (run.NativeTextArtifact is not Cache cache)
            {
                cache = new Cache();
                run.NativeTextArtifact = cache;
            }

            return cache.GetOrBuild(run, textOptions);
        }

        private sealed class Cache : IDisposable
        {
            private readonly TwoLevelCache<TextOptions, SKTextBlob> _blobs =
                new(secondarySize: 3, evictionAction: b => b?.Dispose());

            private SKPoint[]? _positions;

            public SKTextBlob GetOrBuild(ManagedGlyphRunImpl run, TextOptions textOptions)
            {
                return _blobs.GetOrAdd(textOptions, _ =>
                {
                    using var font = GlyphRunImpl.CreateFont(
                        (SkiaTypeface)run.GlyphTypeface.PlatformTypeface,
                        (float)run.FontRenderingEmSize, textOptions);

                    if (_positions is null)
                    {
                        var positions = run.GlyphPositions;
                        var points = new SKPoint[run.GlyphCount];

                        for (var i = 0; i < points.Length; i++)
                        {
                            points[i] = new SKPoint(positions[i * 2], positions[i * 2 + 1]);
                        }

                        _positions = points;
                    }

                    var builder = SKTextBlobBuilderCache.Shared.Get();
                    var runBuffer = builder.AllocatePositionedRun(font, run.GlyphCount);

                    runBuffer.SetPositions(_positions);
                    runBuffer.SetGlyphs(run.GlyphIndices);

                    var textBlob = builder.Build()!;

                    SKTextBlobBuilderCache.Shared.Return(builder);
                    return textBlob;
                });
            }

            public void Dispose() => _blobs.ClearAndDispose();
        }
    }
}
