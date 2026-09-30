using System;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// The native-blob fallback for managed glyph runs: builds and caches an
    /// <see cref="SKTextBlob"/> on the run (disposal-tied via the run's artifact slot) for
    /// exactly the draws the managed tiers decline: non-solid foregrounds and anything both
    /// the upright and the transformed triage reject. Managed runs are created
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

        /// <summary>
        /// The fallback for runs whose Skia face draws the wrong outlines
        /// (<see cref="ManagedGlyphOutlines.AreRequired"/>): the run's managed outlines as one
        /// path in run coordinates, built once per run. Colour glyphs are left out; they draw
        /// through <see cref="ColorGlyphRunSplitter.DrawColorGlyphs"/>.
        /// </summary>
        public static SKPath GetOutlinePath(ManagedGlyphRunImpl run)
        {
            if (run.NativeTextArtifact is not Cache cache)
            {
                cache = new Cache();
                run.NativeTextArtifact = cache;
            }

            return cache.GetOrBuildOutlinePath(run);
        }

        private sealed class Cache : IDisposable
        {
            private readonly TwoLevelCache<TextOptions, SKTextBlob> _blobs =
                new(secondarySize: 3, evictionAction: b => b?.Dispose());

            private SKPoint[]? _positions;
            private SKPath? _outlinePath;

            public SKPath GetOrBuildOutlinePath(ManagedGlyphRunImpl run)
            {
                if (_outlinePath is not null)
                {
                    return _outlinePath;
                }

                var path = ManagedGlyphOutlines.CreatePath();
                var indices = run.GlyphIndices;
                var positions = run.GlyphPositions;
                var origin = run.BaselineOrigin;

                var typeface = run.GlyphTypeface;
                var hasColor = typeface.ColorTable is not null || typeface.BitmapSource is not null;

                for (var i = 0; i < indices.Length; i++)
                {
                    if (hasColor && ColorGlyphRunSplitter.IsDrawnAsColor(typeface, indices[i]))
                    {
                        continue;
                    }

                    ManagedGlyphOutlines.AddGlyph(path, run.GlyphTypeface, run.FontRenderingEmSize, indices[i],
                        (float)(origin.X + positions[i * 2]), (float)(origin.Y + positions[i * 2 + 1]));
                }

                return _outlinePath = path;
            }

            public SKTextBlob GetOrBuild(ManagedGlyphRunImpl run, TextOptions textOptions)
            {
                return _blobs.GetOrAdd(textOptions, _ =>
                {
                    using var font = GlyphRunImpl.CreateFont(
                        (SkiaTypeface)run.GlyphTypeface.PlatformTypeface,
                        (float)run.FontRenderingEmSize, run.GlyphTypeface.FontSimulations, textOptions);

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

            public void Dispose()
            {
                _blobs.ClearAndDispose();
                _outlinePath?.Dispose();
                _outlinePath = null;
            }
        }
    }
}
