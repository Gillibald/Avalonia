using System;
using System.Buffers;
using Avalonia.Media.Imaging;
using Avalonia.Media.Fonts.Tables.Colr;
using Avalonia.Platform;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Draws a <see cref="ManagedGlyphRunImpl"/> through the mask pipeline: per-glyph masks from
    /// the typeface's cache are composed once into an immutable run bitmap (cached per run) and
    /// every subsequent frame is a single bitmap blit. Backend-independent — it uses only
    /// mandatory <see cref="IDrawingContextImpl"/> capabilities plus writeable-bitmap creation.
    /// </summary>
    internal static partial class MaskGlyphRunRenderer
    {
        /// <summary>Above this device size the D4 triage sends the run to the caller's fallback.</summary>
        internal const double MaxPixelsPerEm = 160;

        /// <summary>
        /// The run-mask dimension bound for contexts that do not report their own through
        /// <see cref="IAlphaGlyphMaskContext.MaxRunMaskSize"/>: the OpenGL ES 3.0 guaranteed
        /// minimum of GL_MAX_TEXTURE_SIZE, safe on any backend that uploads masks as textures.
        /// </summary>
        internal const int DefaultMaxRunMaskSize = 2048;

        /// <summary>
        /// The memory bound on one composed run mask, in bytes. An 8K-wide line at
        /// <see cref="MaxPixelsPerEm"/> (7680 x ~210 px) in the heaviest format, the portable
        /// subpixel pair at 8 bytes per pixel, needs about 13 MB, so realistic lines stay well
        /// inside it; runs beyond it (an unwrapped paragraph, a huge single-line document) fall
        /// back rather than hold tens of megabytes of mostly offscreen pixels.
        /// </summary>
        internal const long MaxRunMaskBytes = 32L * 1024 * 1024;

        /// <summary>
        /// Slack between the scaled ink bounds and the composed union: glyph mask aprons plus
        /// pen phase and snapping.
        /// </summary>
        private const int RunMaskMargin = 8;

        [ThreadStatic]
        private static GlyphPathBuilder? t_scratch;

        private static readonly Func<GlyphMaskKey, (GlyphTypeface, GlyphPathBuilder), GlyphMask> s_buildMask =
            static (key, state) => GlyphMasks.Build(state.Item1, state.Item2, key);

        /// <summary>
        /// Attempts to draw the run through the mask path. Returns <c>false</c> when this draw
        /// cannot take it — non-axis-aligned or non-uniform transform, oversized glyphs or run,
        /// or a non-solid foreground — and the caller falls back to its native path. Returns
        /// <c>true</c> when handled, including the nothing-to-draw cases.
        /// </summary>
        public static bool TryDraw(IDrawingContextImpl context, ManagedGlyphRunImpl run,
            IBrush? foreground, TextRenderingMode textRenderingMode,
            TextHintingMode textHintingMode = TextHintingMode.Unspecified)
        {
            var transform = context.Transform;

            if (transform.M12 != 0 || transform.M21 != 0)
            {
                return false;
            }

            var scaleX = transform.M11;
            var scaleY = transform.M22;

            if (scaleX <= 0 || scaleY <= 0 || Math.Abs(scaleX - scaleY) > scaleX * 0.001)
            {
                return false;
            }

            var pixelsPerEm = run.FontRenderingEmSize * scaleX;

            if (pixelsPerEm <= 0 || pixelsPerEm > MaxPixelsPerEm)
            {
                return false;
            }

            if (foreground is not ISolidColorBrush solid)
            {
                // Non-solid foregrounds stay on the caller's native path for now; the
                // opacity-mask floor and backend shader tinting widen this later.
                return false;
            }

            if (HasColrV1OnlyGlyph(run))
            {
                return false;
            }

            var alpha = (byte)Math.Clamp(solid.Color.A * solid.Opacity + 0.5, 0, 255);

            if (alpha == 0)
            {
                return true;   // fully transparent — nothing to draw, but handled
            }

            // Backend fast path: untinted alpha masks, tinted per draw — color leaves the cache
            // identity, so a foreground animation reuses one mask. Typefaces with intrinsic
            // color (COLR layers, bitmap strikes) stay on the pre-tinted BGRA floor, and so do
            // contexts that report no benefit (CPU raster: see PrefersAlphaMasks).
            var alphaContext = run.GlyphTypeface.ColorTable is null &&
                run.GlyphTypeface.BitmapSource is null &&
                context is IAlphaGlyphMaskContext { PrefersAlphaMasks: true } preferring
                    ? preferring
                    : null;

            var tint = alphaContext is null
                ? RunMaskComposer.MakeTint(alpha, solid.Color.R, solid.Color.G, solid.Color.B)
                : 0u;   // the documented alpha-variant sentinel

            var deviceX = (float)(run.BaselineOrigin.X * scaleX + transform.M31);
            var deviceY = run.BaselineOrigin.Y * scaleY + transform.M32;

            var mode = ResolveMaskMode(textRenderingMode, context, run.GlyphTypeface, out var lcdGeometry);

            if (!FitsRunMaskBounds(context, run.Bounds, scaleX, scaleY, BytesPerPixel(mode, alphaContext),
                    out var maxSize))
            {
                return false;
            }

            // TextHintingMode drives the grid fit: None means outlines scaled only, Light
            // takes the natural fit (bytecode in the v40 compatibility class when the font
            // is instructed, the light auto-fit otherwise), and Strong adds whole-pixel pens
            // with full program interpretation — the GDI-classic positioning trade.
            // Unspecified resolves through the font's gasp table: a grid-fit range without
            // smoothing flags is the legacy bi-level signature (Courier New) and gets the
            // Strong treatment outright; a grid-fit range whose only smoothing flag is
            // SYMMETRIC_GRIDFIT (Tahoma and Verdana at text sizes) is the ClearType-era
            // strong-hinting request, honored only when a bytecode interpreter actually
            // stands behind it — the auto-hinter cannot deliver what that range promises.
            // A size below the font's own hinted floor renders unhinted, the designer's
            // small-size veto that DirectWrite honors too. An explicit choice always wins.
            var hinting = textHintingMode;

            if (hinting == TextHintingMode.Unspecified)
            {
                var gasp = run.GlyphTypeface.Gasp;

                if (gasp.IsBelowHintingFloor(pixelsPerEm))
                {
                    hinting = TextHintingMode.None;
                }
                else if (gasp.WantsFullGridFit(pixelsPerEm) ||
                         (gasp.WantsBytecodeGridFit(pixelsPerEm) && run.GlyphTypeface.HasTrueTypeHinting))
                {
                    hinting = TextHintingMode.Strong;
                }
            }

            var gridFit = hinting != TextHintingMode.None;
            var penSnap = hinting == TextHintingMode.Strong;

            int originX;
            byte originPhase;

            if (penSnap)
            {
                originX = (int)MathF.Round(deviceX);
                originPhase = 0;
            }
            else
            {
                GlyphMaskKey.SnapPen(deviceX, out originX, out originPhase);
            }

            var originY = (int)Math.Round(deviceY);


            var key = new RunMaskKey(GlyphMaskKey.QuantizeScale((float)pixelsPerEm), originPhase, mode, tint, gridFit, penSnap);

            // A hardware GPU draws grayscale glyph masks from the typeface's atlas, as it draws
            // transformed text: no run-sized mask, coverage stored corrected for the colour's
            // luminance, and the context can merge consecutive runs into one draw. Glyphs
            // composite one over the other there, as the raster compose does, where a run mask
            // sums overlapping coverage before correcting it. Software GPUs keep the run mask:
            // they gain least from fewer draw calls, and llvmpipe shades the run mask's
            // correction filter a level apart from the atlas modulation at some pixels. A zoom
            // gesture, whose scale changes every frame, keeps run masks too: laid out as sprites,
            // every frame's glyph masks would fill the atlas and push static text out of it.
            if (alphaContext is not null && mode != GlyphMaskMode.Subpixel &&
                context is ITransformedGlyphContext { RasterTarget: GlyphRasterTarget.HardwareGpu } atlasContext &&
                !run.UprightChurn.Record(key.ScaleQ, default, run.TransformedSprites.TryGet(key, out _)))
            {
                DrawUprightFromAtlas(atlasContext, run, key, transform, (float)scaleX, (float)scaleY, originX, originY,
                    ToArgb(alpha, solid.Color));

                return true;
            }

            var cache = run.RunMasks;

            // A raster surface that grants direct access draws outline text from the run's
            // untinted coverage, blended straight into the surface with the bytes the blit of
            // the pre-tinted mask would leave there. The colour leaves the cache identity, so a
            // foreground colour animation composes nothing per colour, and the blend skips the
            // empty spans that make up most of a run's area, which the blit copies pixel by pixel.
            var blitTarget = default(GlyphBlitTarget);
            var coverageKey = key with { Tint = RunMaskKey.CoverageTint };
            var blendsCoverage = alphaContext is null && mode != GlyphMaskMode.Subpixel &&
                run.GlyphTypeface.ColorTable is null && run.GlyphTypeface.BitmapSource is null &&
                context is ITransformedGlyphContext { RasterTarget: GlyphRasterTarget.Raster } coverageContext &&
                coverageContext.TryGetBlitTarget(out blitTarget);
            var hit = cache.TryGet(blendsCoverage ? coverageKey : key, out var runMask);

            // A subpixel mask in a GPU context's shared atlas loses its coverage when the atlas
            // drops its page to stay within budget; the run composes it again.
            if (hit && runMask.IsEvicted)
            {
                cache.Remove(key);
                hit = false;
            }

            // An upright zoom gesture changes the scale every frame, so its masks would never be
            // drawn twice. A software GPU, where rasterizing each frame costs many times a
            // bilinear draw, draws the mask of the last static frame stretched to the new scale
            // until the scale holds still; the first frame that repeats a scale rasterizes
            // again. A CPU surface rasterizes the frame from coverage that no cache keeps when
            // that costs no more than stretching (see PrefersRasterizingZoom), and stays sharp;
            // otherwise it stretches while the zoom stays within the band transformed text
            // stretches in, and rasterizes once, settling there, on leaving it: further out a
            // stretch would soften the text noticeably. Its subpixel masks have no single bitmap
            // to stretch, and a static frame drawn from coverage composes its pre-tinted mask
            // when the gesture starts stretching. A hardware GPU keeps rasterizing, which is as
            // fast there and stays sharp.
            var zoomContext = context as ITransformedGlyphContext;
            var zooming = zoomContext is { RasterTarget: not GlyphRasterTarget.HardwareGpu } &&
                (zoomContext.RasterTarget == GlyphRasterTarget.SoftwareGpu || mode != GlyphMaskMode.Subpixel) &&
                run.UprightChurn.Record(key.ScaleQ, default, hit);

            if (zooming && blendsCoverage && !hit && PrefersRasterizingZoom(run, transform) &&
                TryBuildCoverage(run, coverageKey, (float)scaleX, (float)scaleY, transient: true, out var frameCoverage))
            {
                if (frameCoverage is null)
                {
                    return true;   // whitespace-only run
                }

                run.SettledUpright = new SettledRunMask(key, transform, originX, originY);

                GlyphMaskBlitter.BlendRunCoverage(blitTarget, frameCoverage, originX + frameCoverage.OffsetX,
                    originY + frameCoverage.OffsetY, key.Tint, MaskGamma.GetTableForPremulBgra(key.Tint));

                return true;
            }

            if (zooming &&
                run.SettledUpright is { } settled && settled.Key.Mode == key.Mode && settled.Key.Tint == key.Tint &&
                settled.Transform.TryInvert(out var inverse) &&
                (zoomContext!.RasterTarget == GlyphRasterTarget.SoftwareGpu || IsWithinStretchBand(inverse * transform)) &&
                (cache.TryGet(settled.Key, out var settledMask) ||
                 blendsCoverage && TryComposeSettled(run, settled, maxSize, out settledMask)))
            {
                DrawStretchedRunMask(context, zoomContext, settledMask, settled, inverse * transform, mode,
                    alphaContext, alpha, solid.Color);

                return true;
            }

            if (!hit && blendsCoverage)
            {
                if (!TryBuildCoverage(run, coverageKey, (float)scaleX, (float)scaleY, transient: false, out var coverage))
                {
                    // More glyphs ink one pixel than the coverage records: the pre-tinted mask.
                    blendsCoverage = false;
                    hit = cache.TryGet(key, out runMask);
                }
                else if (coverage is null)
                {
                    return true;   // whitespace-only run
                }
                else
                {
                    runMask = new RunMask(new[]
                    {
                        new RunMaskPart(coverage, coverage.OffsetX, coverage.OffsetY, coverage.Width, coverage.Height),
                    });

                    cache.Add(coverageKey, runMask);
                    hit = true;
                }
            }

            if (!hit)
            {
                // The bound only sizes the chunks, it is not part of the key: chunks partition
                // the same pixels, so a mask composed for one context draws correctly on another.
                var composed = mode == GlyphMaskMode.Subpixel
                    ? alphaContext is null
                        ? ComposeLcdPayload(run, key, (float)scaleX, (float)scaleY, maxSize, lcdGeometry,
                            alpha, solid.Color.R, solid.Color.G, solid.Color.B)
                        : ComposeLcdMask(run, key, alphaContext, (float)scaleX, (float)scaleY, maxSize, lcdGeometry)
                    : alphaContext is null
                        ? Compose(run, key, (float)scaleX, (float)scaleY, maxSize)
                        : ComposeAlphaMask(run, key, alphaContext, (float)scaleX, (float)scaleY, maxSize);

                if (composed is null)
                {
                    return true;   // whitespace-only run
                }

                cache.Add(key, composed);
                runMask = composed;
            }

            run.SettledUpright = new SettledRunMask(key, transform, originX, originY);

            if (blendsCoverage)
            {
                var coverage = (RunCoverage)runMask.Parts[0].Handle;

                GlyphMaskBlitter.BlendRunCoverage(blitTarget, coverage, originX + coverage.OffsetX,
                    originY + coverage.OffsetY, key.Tint, MaskGamma.GetTableForPremulBgra(key.Tint));

                return true;
            }

            // The mask is already in device pixels; draw it under an identity transform so the
            // canvas transform is not applied twice.
            var oldTransform = context.Transform;
            context.Transform = Matrix.Identity;

            // Parts cover disjoint columns of the composed union, so every destination pixel is
            // blended exactly once, with the value a single mask would hold there.
            var parts = runMask.Parts;

            if (alphaContext is not null)
            {
                var straightTint = ((uint)alpha << 24) |
                    ((uint)solid.Color.R << 16) | ((uint)solid.Color.G << 8) | solid.Color.B;

                foreach (var part in parts)
                {
                    GetPartRects(part, originX, originY, out var sourceRect, out var destRect);

                    if (mode == GlyphMaskMode.Subpixel)
                    {
                        alphaContext.DrawLcdMask(part.Handle, sourceRect, destRect, straightTint);
                    }
                    else
                    {
                        alphaContext.DrawAlphaMask(part.Handle, sourceRect, destRect, straightTint);
                    }
                }
            }
            else if (mode == GlyphMaskMode.Subpixel)
            {
                if (context is ITransformedGlyphContext rasterContext && rasterContext.TryGetBlitTarget(out var target))
                {
                    // One pass straight into the surface, with the bytes of the two blits below.
                    foreach (var part in parts)
                    {
                        var payload = (LcdRunPayload)part.Handle;

                        LcdMaskBlitter.Blend(target, payload.Multiply, payload.Plus, payload.Width, payload.Height,
                            originX + part.OffsetX, originY + part.OffsetY);
                    }
                }
                else
                {
                    DrawLcdTwoPass(context, parts, originX, originY);
                }
            }
            else
            {
                foreach (var part in parts)
                {
                    GetPartRects(part, originX, originY, out var sourceRect, out var destRect);
                    context.DrawBitmap((IBitmapImpl)part.Handle, 1, sourceRect, destRect);
                }
            }

            context.Transform = oldTransform;

            return true;
        }

        /// <summary>
        /// Composes and caches the pre-tinted mask of a static frame that was drawn from the
        /// run's coverage, for a zoom gesture to stretch. Returns <c>false</c> for a run without
        /// ink.
        /// </summary>
        private static bool TryComposeSettled(ManagedGlyphRunImpl run, in SettledRunMask settled, int maxSize,
            out RunMask settledMask)
        {
            settledMask = null!;

            var composed = Compose(run, settled.Key, (float)settled.Transform.M11, (float)settled.Transform.M22,
                maxSize);

            if (composed is null)
            {
                return false;
            }

            run.RunMasks.Add(settled.Key, composed);
            settledMask = composed;

            return true;
        }

        /// <summary>
        /// The cost a glyph mask pixel that misses the cache adds to rasterizing a zoom frame,
        /// in pixels of a bilinear stretch of the settled run mask through the raster backend:
        /// on x64 at 14-30 px about 11 ns per rasterized mask pixel (outline, grid fit, cache
        /// insert) against 2.2 ns per stretched pixel. Tests raise it to make rasterizing costly.
        /// </summary>
        internal static double ZoomRasterPixelCost { get; set; } = 5;

        /// <summary>
        /// The cost of building and blending one pixel of a zoom frame's run coverage, in pixels
        /// of a bilinear stretch (about 0.5 ns).
        /// </summary>
        private const double ZoomCoveragePixelCost = 0.22;

        /// <summary>
        /// The cost of fetching one glyph's mask from the cache for a zoom frame, in pixels of a
        /// bilinear stretch (about 30 ns).
        /// </summary>
        private const double ZoomGlyphCost = 14;

        /// <summary>
        /// Rasterizing also fills the glyph mask cache for the scales a zoom passes through, so
        /// that a zoom back over them only builds and blends coverage, and keeps the frame sharp:
        /// it is preferred up to this factor of the stretch's cost.
        /// </summary>
        private const double ZoomRasterPreference = 1.2;

        /// <summary>
        /// Whether an upright zoom frame on a raster surface should rasterize rather than stretch
        /// the settled run mask. The run's last coverage build tells how many glyph mask pixels
        /// it used and how large its coverage was, and the typeface's mask cache what share of
        /// recently used mask pixels had to be rasterized; the pixel counts scale with the zoom
        /// since then, as does the area a stretch draws. Rasterizing costs the share of mask
        /// pixels that miss the cache, the coverage and a cache lookup per glyph; stretching
        /// costs the backend's bilinear draw, which on a raster surface runs per destination
        /// pixel through its raster pipeline. The miss share belongs to the typeface rather
        /// than the run, since the runs of a frame share glyph masks and the first run to need
        /// one rasterizes it for all. Text whose glyph masks are cached, or cheap to rasterize,
        /// rasterizes; dense, complex glyphs that miss the cache (CJK at many scales) stretch.
        /// </summary>
        private static bool PrefersRasterizingZoom(ManagedGlyphRunImpl run, in Matrix transform)
        {
            var last = run.LastUprightRaster;

            if (last.CoveragePixels == 0 || run.SettledUpright is not { } settled ||
                !settled.Transform.TryInvert(out var inverse))
            {
                return true;
            }

            var delta = inverse * transform;
            var growth = Math.Abs(delta.M11 * delta.M22 - delta.M12 * delta.M21);
            var stretching = last.CoveragePixels * growth;
            var missing = run.GlyphTypeface.MaskCache.RecentMissShare * last.MaskPixels;
            var rasterizing = (ZoomRasterPixelCost * missing + ZoomCoveragePixelCost * last.CoveragePixels) * growth +
                              ZoomGlyphCost * run.GlyphCount;

            return rasterizing <= ZoomRasterPreference * stretching;
        }

        [ThreadStatic]
        private static long t_rasterizedPixels;

        private static readonly Func<GlyphMaskKey, (GlyphTypeface, GlyphPathBuilder), GlyphMask> s_buildCountedMask =
            static (key, state) =>
            {
                var mask = GlyphMasks.Build(state.Item1, state.Item2, key);

                t_rasterizedPixels += mask.Width * mask.Height;

                return mask;
            };

        /// <summary>
        /// The coverage of a run of outline glyphs with the glyph masks, pens and phases
        /// <see cref="Compose"/> uses for them. Returns <c>false</c> when the coverage cannot
        /// represent the run. A <paramref name="transient"/> coverage lives in buffers the
        /// thread reuses: it is drawn once and never cached. Records in
        /// <see cref="ManagedGlyphRunImpl.LastUprightRaster"/> how many mask pixels the build
        /// used and how large the coverage is, and in the typeface's mask cache how many of
        /// those pixels it had to rasterize.
        /// </summary>
        private static bool TryBuildCoverage(ManagedGlyphRunImpl run, in RunMaskKey key, float scaleX, float scaleY,
            bool transient, out RunCoverage? coverage)
        {
            var typeface = run.GlyphTypeface;
            var embolden = GlyphSimulation.QuantizeEmboldenOutset(typeface.FontSimulations, run.FontRenderingEmSize, key.ScaleQ);
            var oblique = (typeface.FontSimulations & FontSimulations.Oblique) != 0;
            var simulated = embolden != 0 || oblique;
            var maskCache = typeface.MaskCache;
            var scratch = t_scratch ??= new GlyphPathBuilder();
            var count = run.GlyphCount;
            var indices = run.GlyphIndices;
            var positions = run.GlyphPositions;
            var originFraction = key.OriginPhase * (1f / GlyphMaskKey.PhaseCount);
            var state = (typeface, scratch);
            var masks = ArrayPool<GlyphMask>.Shared.Rent(Math.Max(1, count));
            var pens = ArrayPool<int>.Shared.Rent(Math.Max(2, count * 2));
            long used = 0;

            t_rasterizedPixels = 0;

            try
            {
                for (var i = 0; i < count; i++)
                {
                    var relativeX = originFraction + positions[i * 2] * scaleX;
                    SnapGlyphPen(in key, relativeX, out var penX, out var glyphPhase);

                    pens[i] = penX;
                    pens[count + i] = (int)MathF.Round(positions[i * 2 + 1] * scaleY);

                    var simulate = simulated && !typeface.IsColorGlyph(indices[i]);
                    var glyphKey = simulate
                        ? new GlyphMaskKey(indices[i], key.ScaleQ, glyphPhase, key.Mode, key.GridFit, key.PenSnap, embolden, oblique)
                        : new GlyphMaskKey(indices[i], key.ScaleQ, glyphPhase, key.Mode, key.GridFit, key.PenSnap);

                    masks[i] = maskCache.GetOrBuild(glyphKey, state, s_buildCountedMask);
                    used += masks[i].Width * masks[i].Height;
                }

                var built = transient
                    ? RunCoverage.TryBuildTransient(key, masks.AsSpan(0, count), pens.AsSpan(0, count),
                        pens.AsSpan(count, count), out coverage)
                    : RunCoverage.TryBuild(key, masks.AsSpan(0, count), pens.AsSpan(0, count),
                        pens.AsSpan(count, count), out coverage);

                maskCache.RecordUse(used, t_rasterizedPixels);
                run.LastUprightRaster = new UprightRasterCost(used,
                    coverage is null ? 0 : (long)coverage.Width * coverage.Height);

                return built;
            }
            finally
            {
                masks.AsSpan(0, count).Clear();
                ArrayPool<GlyphMask>.Shared.Return(masks);
                ArrayPool<int>.Shared.Return(pens);
            }
        }

        /// <summary>
        /// Draws an upright run from the typeface's atlas through its cached sprite set, laying
        /// the set out on first use.
        /// </summary>
        private static void DrawUprightFromAtlas(ITransformedGlyphContext context, ManagedGlyphRunImpl run,
            in RunMaskKey key, in Matrix transform, float scaleX, float scaleY, int originX, int originY,
            uint foregroundArgb)
        {
            var state = run.TransformedSprites;

            if (!state.TryGet(key, out var sprites))
            {
                var timer = GlyphPhaseTimers.Start();

                sprites = BuildUprightSprites(run, key, scaleX, scaleY);
                state.Add(sprites);
                GlyphPhaseTimers.Stop(GlyphTimerPhase.SpriteSetBuild, timer);
            }

            state.Settle(sprites, transform, originX, originY);
            DrawFromAtlas(context, run.GlyphTypeface, sprites, originX, originY, foregroundArgb);
        }

        /// <summary>
        /// Lays an upright run's grid-fitted glyph masks out relative to its snapped origin
        /// pixel, with the pens and phases the run mask compose uses.
        /// </summary>
        private static TransformedGlyphSprites BuildUprightSprites(ManagedGlyphRunImpl run, in RunMaskKey key,
            float scaleX, float scaleY)
        {
            var typeface = run.GlyphTypeface;
            var embolden = GlyphSimulation.QuantizeEmboldenOutset(typeface.FontSimulations, run.FontRenderingEmSize, key.ScaleQ);
            var oblique = (typeface.FontSimulations & FontSimulations.Oblique) != 0;
            var maskCache = typeface.MaskCache;
            var scratch = t_scratch ??= new GlyphPathBuilder();
            var count = run.GlyphCount;
            var indices = run.GlyphIndices;
            var positions = run.GlyphPositions;
            var originFraction = key.OriginPhase * (1f / GlyphMaskKey.PhaseCount);
            var state = (typeface, scratch);
            var laidOut = new TransformedSprite[count];
            var placed = 0;

            for (var i = 0; i < count; i++)
            {
                var relativeX = originFraction + positions[i * 2] * scaleX;
                SnapGlyphPen(in key, relativeX, out var penX, out var glyphPhase);
                var penY = (int)MathF.Round(positions[i * 2 + 1] * scaleY);

                var mask = maskCache.GetOrBuild(new GlyphMaskKey(indices[i], key.ScaleQ, glyphPhase, key.Mode,
                    key.GridFit, key.PenSnap, embolden, oblique), state, s_buildMask);

                if (mask.IsEmpty)
                {
                    continue;
                }

                laidOut[placed++] = new TransformedSprite
                {
                    Glyph = indices[i],
                    PhaseX = glyphPhase,
                    Kind = TransformedSpriteKind.Foreground,
                    X = penX + mask.Left,
                    Y = penY + mask.Top,
                    Width = mask.Width,
                    Height = mask.Height,
                };
            }

            if (placed != count)
            {
                Array.Resize(ref laidOut, placed);
            }

            return new TransformedGlyphSprites(key, embolden, oblique, laidOut) { IsUpright = true };
        }

        /// <summary>
        /// Per-channel blending through the portable interface: multiply the destination by
        /// the inverse corrected coverage, then add the pre-tinted corrected coverage, together
        /// the per-channel lerp.
        /// </summary>
        private static void DrawLcdTwoPass(IDrawingContextImpl context, ReadOnlySpan<RunMaskPart> parts, int originX,
            int originY)
        {
            // Resolved per draw, not captured statically, like the outline build path.
            var renderInterface = AvaloniaLocator.Current.GetRequiredService<IPlatformRenderInterface>();

            context.PushRenderOptions(new RenderOptions { BitmapBlendingMode = BitmapBlendingMode.Multiply });

            foreach (var part in parts)
            {
                GetPartRects(part, originX, originY, out var sourceRect, out var destRect);
                context.DrawBitmap((IBitmapImpl)((LcdRunPayload)part.Handle).GetBitmaps(renderInterface).Multiply, 1,
                    sourceRect, destRect);
            }

            context.PopRenderOptions();

            context.PushRenderOptions(new RenderOptions { BitmapBlendingMode = BitmapBlendingMode.Plus });

            foreach (var part in parts)
            {
                GetPartRects(part, originX, originY, out var sourceRect, out var destRect);
                context.DrawBitmap((IBitmapImpl)((LcdRunPayload)part.Handle).GetBitmaps(renderInterface).Plus, 1,
                    sourceRect, destRect);
            }

            context.PopRenderOptions();
        }

        /// <summary>
        /// Whether the run holds a glyph with only a COLR v1 paint graph
        /// (<see cref="ColorGlyphRunSplitter.IsV1OnlyGlyph"/>). Masks have no v1 paint: such a
        /// glyph draws through its drawing, and the caller cuts the run around it (see
        /// <see cref="ManagedGlyphRunImpl.ColorGlyphSegments"/>) rather than draw it as a
        /// monochrome outline.
        /// </summary>
        private static bool HasColrV1OnlyGlyph(ManagedGlyphRunImpl run)
        {
            var typeface = run.GlyphTypeface;

            if (typeface.ColorTable is not { HasV1Data: true } v1Colr)
            {
                return false;
            }

            var glyphs = run.GlyphIndices;

            for (var i = 0; i < glyphs.Length; i++)
            {
                if (ColorGlyphRunSplitter.IsV1OnlyGlyph(typeface, v1Colr, glyphs[i]))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Draws a settled upright run mask mapped through <paramref name="delta"/>, the change
        /// from its frame's transform to the current one, with bilinear sampling.
        /// </summary>
        private static void DrawStretchedRunMask(IDrawingContextImpl context, ITransformedGlyphContext stretchContext,
            RunMask runMask, in SettledRunMask settled, in Matrix delta, GlyphMaskMode mode,
            IAlphaGlyphMaskContext? alphaContext, byte alpha, Color color)
        {
            var oldTransform = context.Transform;
            var straightTint = ((uint)alpha << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

            context.Transform = Matrix.Identity;

            foreach (var part in runMask.Parts)
            {
                GetPartRects(part, settled.OriginX, settled.OriginY, out var sourceRect, out var destRect);

                var stretched = destRect.TransformToAABB(delta);

                // A GPU context realizes masks as alpha masks unless the typeface has colour of
                // its own, which renders grayscale into a pre-tinted bitmap.
                if (alphaContext is not null)
                {
                    stretchContext.DrawMaskStretched(part.Handle, sourceRect, stretched, straightTint,
                        mode == GlyphMaskMode.Subpixel);
                }
                else
                {
                    context.DrawBitmap((IBitmapImpl)part.Handle, 1, sourceRect, stretched);
                }
            }

            context.Transform = oldTransform;
        }

        private static void GetPartRects(in RunMaskPart part, int originX, int originY,
            out Rect sourceRect, out Rect destRect)
        {
            sourceRect = new Rect(0, 0, part.Width, part.Height);
            destRect = sourceRect.Translate(new Vector(originX + part.OffsetX, originY + part.OffsetY));
        }

        /// <summary>
        /// Gates the run on the context's dimension bound and the memory bound before any
        /// compose work, and reports the bound that sizes the composed chunks. Width beyond
        /// the bound is chunked; height is not, so it must fit. The composed union cannot
        /// exceed the scaled ink bounds by more than <see cref="RunMaskMargin"/>, so gating on
        /// Bounds keeps Compose from ever producing an oversized mask.
        /// </summary>
        private static bool FitsRunMaskBounds(IDrawingContextImpl context, Rect bounds,
            double scaleX, double scaleY, int bytesPerPixel, out int maxSize)
        {
            maxSize = context is IAlphaGlyphMaskContext bounded
                ? bounded.MaxRunMaskSize
                : DefaultMaxRunMaskSize;

            var width = bounds.Width * scaleX + RunMaskMargin;
            var height = bounds.Height * scaleY + RunMaskMargin;

            return height <= maxSize && width * height * bytesPerPixel <= MaxRunMaskBytes;
        }

        /// <summary>
        /// Splits a composed union of <paramref name="width"/> columns (or rows) into equal
        /// chunks of at most <paramref name="maxSize"/>; the last chunk takes the remainder.
        /// </summary>
        private static int GetChunkCount(int width, int maxSize, out int chunkWidth)
        {
            chunkWidth = Math.Min(width, Math.Max(1, maxSize));

            return (width + chunkWidth - 1) / chunkWidth;
        }

        private static void DisposeParts(RunMaskPart[] parts, int count)
        {
            for (var i = 0; i < count; i++)
            {
                parts[i].Handle.Dispose();
            }
        }

        /// <summary>
        /// Bytes per composed pixel of the variant this draw realizes: an A8 mask, an RGBA
        /// stripe mask, a pre-tinted BGRA bitmap, or the portable subpixel Multiply/Plus pair.
        /// </summary>
        private static int BytesPerPixel(GlyphMaskMode mode, IAlphaGlyphMaskContext? alphaContext)
            => mode == GlyphMaskMode.Subpixel
                ? alphaContext is null ? 8 : 4
                : alphaContext is null ? 4 : 1;

        /// <summary>
        /// Splits a glyph pen into placement pixel and phase. Under Strong hinting every pen
        /// rounds to a whole pixel with phase zero, so identical glyphs rasterize identically
        /// across the run — the uniformity that reads as GDI-era crispness.
        /// </summary>
        private static void SnapGlyphPen(in RunMaskKey key, float relativeX, out int penX, out byte phase)
        {
            if (key.PenSnap)
            {
                penX = (int)MathF.Round(relativeX);
                phase = 0;
                return;
            }

            GlyphMaskKey.SnapPen(relativeX, out penX, out phase);
        }

        /// <summary>
        /// Resolves the requested rendering mode onto a mask mode. Alias and Antialias map
        /// directly; Unspecified and SubpixelAntialias both mean LCD when the whole chain
        /// allows it — the same default the native blob applies — and degrade to grayscale
        /// otherwise. Unspecified means grayscale on the platforms that draw text so
        /// (<see cref="TextRasterizationDefaults.UnspecifiedRendersSubpixel"/>). Color art never
        /// renders subpixel: stripes only make sense for a solid foreground modulating pure
        /// coverage.
        /// </summary>
        internal static GlyphMaskMode ResolveMaskMode(TextRenderingMode textRenderingMode,
            IDrawingContextImpl context, GlyphTypeface typeface, out LcdMaskGeometry geometry)
        {
            geometry = LcdMaskGeometry.RgbHorizontal;

            switch (textRenderingMode)
            {
                case TextRenderingMode.Alias:
                    return GlyphMaskMode.Aliased;
                case TextRenderingMode.Antialias:
                    return GlyphMaskMode.Antialiased;
                case TextRenderingMode.Unspecified when !TextRasterizationDefaults.UnspecifiedRendersSubpixel:
                    return GlyphMaskMode.Antialiased;
            }

            if (typeface.ColorTable is null && typeface.BitmapSource is null &&
                context is IAlphaGlyphMaskContext lcdProbe && lcdProbe.TryGetLcdGeometry(out geometry))
            {
                return GlyphMaskMode.Subpixel;
            }

            return GlyphMaskMode.Antialiased;
        }

        /// <summary>
        /// The portable subpixel compose: accumulates stripe coverage exactly like the backend
        /// variant, then bakes the gamma-corrected coverage into the two blit payloads — the
        /// Multiply pass holds the inverse corrected coverage per channel (alpha opaque) and
        /// the Plus pass the premultiplied tinted coverage. Keyed by tint like the pre-tinted
        /// grayscale floor, so a foreground change recomposes.
        /// </summary>
        private static RunMask? ComposeLcdPayload(ManagedGlyphRunImpl run, RunMaskKey key,
            float scaleX, float scaleY, int maxSize, LcdMaskGeometry geometry, byte alpha, byte r, byte g, byte b)
        {
            var typeface = run.GlyphTypeface;
            var embolden = GlyphSimulation.QuantizeEmboldenOutset(typeface.FontSimulations, run.FontRenderingEmSize, key.ScaleQ);
            var oblique = (typeface.FontSimulations & FontSimulations.Oblique) != 0;
            var maskCache = typeface.MaskCache;
            var scratch = t_scratch ??= new GlyphPathBuilder();
            var count = run.GlyphCount;
            var indices = run.GlyphIndices;
            var positions = run.GlyphPositions;
            var originFraction = key.OriginPhase * (1f / GlyphMaskKey.PhaseCount);
            var state = (typeface, scratch);

            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;

            for (var i = 0; i < count; i++)
            {
                var relativeX = originFraction + positions[i * 2] * scaleX;
                SnapGlyphPen(in key, relativeX, out var penX, out var glyphPhase);
                var penY = (int)MathF.Round(positions[i * 2 + 1] * scaleY);

                var mask = maskCache.GetOrBuild(new GlyphMaskKey(indices[i], key.ScaleQ, glyphPhase, key.Mode, key.GridFit, key.PenSnap, embolden, oblique),
                    state, s_buildMask);

                UnionMask(mask, penX, penY, ref minX, ref minY, ref maxX, ref maxY);
            }

            if (minX >= maxX || minY >= maxY)
            {
                return null;
            }

            var height = maxY - minY;
            var chunkCount = GetChunkCount(maxX - minX, maxSize, out var chunkWidth);
            var parts = new RunMaskPart[chunkCount];
            var created = 0;
            var staging = ArrayPool<byte>.Shared.Rent(chunkWidth * height * 4);

            try
            {
                var table = MaskGamma.GetLcdTable(r, g, b);

                // Straight tint premultiplied by the text alpha once; per pixel only the
                // corrected coverage multiplies in.
                var tintB = Div255(b * alpha);
                var tintG = Div255(g * alpha);
                var tintR = Div255(r * alpha);

                for (var chunk = 0; chunk < chunkCount; chunk++)
                {
                    var chunkX = minX + chunk * chunkWidth;
                    var width = Math.Min(chunkWidth, maxX - chunkX);
                    var span = staging.AsSpan(0, width * height * 4);
                    span.Clear();

                    for (var i = 0; i < count; i++)
                    {
                        var relativeX = originFraction + positions[i * 2] * scaleX;
                        SnapGlyphPen(in key, relativeX, out var penX, out var glyphPhase);
                        var penY = (int)MathF.Round(positions[i * 2 + 1] * scaleY);

                        var mask = maskCache.GetOrBuild(new GlyphMaskKey(indices[i], key.ScaleQ, glyphPhase, key.Mode, key.GridFit, key.PenSnap, embolden, oblique),
                            state, s_buildMask);

                        RunMaskComposer.ComposeLcd(mask, penX - chunkX, penY - minY,
                            geometry == LcdMaskGeometry.BgrHorizontal, span, width, height);
                    }

                    var multiply = new uint[width * height];
                    var plus = new uint[width * height];

                    for (var y = 0; y < height; y++)
                    {
                        var src = span.Slice(y * width * 4, width * 4);
                        var mRow = multiply.AsSpan(y * width, width);
                        var pRow = plus.AsSpan(y * width, width);

                        for (var x = 0; x < width; x++)
                        {
                            var d = x * 4;

                            // Staging is RGBA semantic order; the payloads are BGRA bytes.
                            var covR = table[src[d]];
                            var covG = table[src[d + 1]];
                            var covB = table[src[d + 2]];
                            var covMax = covR > covG ? covR : covG;

                            if (covB > covMax)
                            {
                                covMax = covB;
                            }

                            mRow[x] = (uint)(255 - covB) | ((uint)(255 - covG) << 8) | ((uint)(255 - covR) << 16) |
                                      0xFF000000u;
                            pRow[x] = (uint)Div255(tintB * covB) | ((uint)Div255(tintG * covG) << 8) |
                                      ((uint)Div255(tintR * covR) << 16) | ((uint)Div255(alpha * covMax) << 24);
                        }
                    }

                    parts[created++] = new RunMaskPart(new LcdRunPayload(multiply, plus, width, height), chunkX, minY,
                        width, height);
                }

                return new RunMask(parts);
            }
            catch
            {
                DisposeParts(parts, created);
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(staging);
            }
        }

        private static int Div255(int value) => (value + 127) / 255;

        /// <summary>
        /// The subpixel compose: three filtered stripe coverages per pixel plus their maximum
        /// in alpha, realized as a backend mask and blended per channel at draw time. Only
        /// reachable for COLR-free typefaces on LCD-eligible contexts.
        /// </summary>
        private static RunMask? ComposeLcdMask(ManagedGlyphRunImpl run, RunMaskKey key,
            IAlphaGlyphMaskContext alphaContext, float scaleX, float scaleY, int maxSize, LcdMaskGeometry geometry)
        {
            var typeface = run.GlyphTypeface;
            var embolden = GlyphSimulation.QuantizeEmboldenOutset(typeface.FontSimulations, run.FontRenderingEmSize, key.ScaleQ);
            var oblique = (typeface.FontSimulations & FontSimulations.Oblique) != 0;
            var maskCache = typeface.MaskCache;
            var scratch = t_scratch ??= new GlyphPathBuilder();
            var count = run.GlyphCount;
            var indices = run.GlyphIndices;
            var positions = run.GlyphPositions;
            var originFraction = key.OriginPhase * (1f / GlyphMaskKey.PhaseCount);
            var state = (typeface, scratch);

            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;

            for (var i = 0; i < count; i++)
            {
                var relativeX = originFraction + positions[i * 2] * scaleX;
                SnapGlyphPen(in key, relativeX, out var penX, out var glyphPhase);
                var penY = (int)MathF.Round(positions[i * 2 + 1] * scaleY);

                var mask = maskCache.GetOrBuild(new GlyphMaskKey(indices[i], key.ScaleQ, glyphPhase, key.Mode, key.GridFit, key.PenSnap, embolden, oblique),
                    state, s_buildMask);

                UnionMask(mask, penX, penY, ref minX, ref minY, ref maxX, ref maxY);
            }

            if (minX >= maxX || minY >= maxY)
            {
                return null;
            }

            var height = maxY - minY;
            var chunkCount = GetChunkCount(maxX - minX, maxSize, out var chunkWidth);
            var parts = new RunMaskPart[chunkCount];
            var created = 0;
            var staging = ArrayPool<byte>.Shared.Rent(chunkWidth * height * 4);

            try
            {
                for (var chunk = 0; chunk < chunkCount; chunk++)
                {
                    var chunkX = minX + chunk * chunkWidth;
                    var width = Math.Min(chunkWidth, maxX - chunkX);
                    var span = staging.AsSpan(0, width * height * 4);
                    span.Clear();

                    for (var i = 0; i < count; i++)
                    {
                        var relativeX = originFraction + positions[i * 2] * scaleX;
                        SnapGlyphPen(in key, relativeX, out var penX, out var glyphPhase);
                        var penY = (int)MathF.Round(positions[i * 2 + 1] * scaleY);

                        var mask = maskCache.GetOrBuild(new GlyphMaskKey(indices[i], key.ScaleQ, glyphPhase, key.Mode, key.GridFit, key.PenSnap, embolden, oblique),
                            state, s_buildMask);

                        RunMaskComposer.ComposeLcd(mask, penX - chunkX, penY - minY,
                            geometry == LcdMaskGeometry.BgrHorizontal, span, width, height);
                    }

                    parts[created++] = new RunMaskPart(alphaContext.CreateLcdMask(span, width, height),
                        chunkX, minY, width, height);
                }

                return new RunMask(parts);
            }
            catch
            {
                DisposeParts(parts, created);
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(staging);
            }
        }

        /// <summary>
        /// The alpha-context compose: coverage only, into a pooled staging buffer realized as a
        /// backend mask. Only reachable for COLR-free typefaces, so no layer expansion here.
        /// </summary>
        private static RunMask? ComposeAlphaMask(ManagedGlyphRunImpl run, RunMaskKey key,
            IAlphaGlyphMaskContext alphaContext, float scaleX, float scaleY, int maxSize)
        {
            var typeface = run.GlyphTypeface;
            var embolden = GlyphSimulation.QuantizeEmboldenOutset(typeface.FontSimulations, run.FontRenderingEmSize, key.ScaleQ);
            var oblique = (typeface.FontSimulations & FontSimulations.Oblique) != 0;
            var maskCache = typeface.MaskCache;
            var scratch = t_scratch ??= new GlyphPathBuilder();
            var count = run.GlyphCount;
            var indices = run.GlyphIndices;
            var positions = run.GlyphPositions;
            var originFraction = key.OriginPhase * (1f / GlyphMaskKey.PhaseCount);
            var state = (typeface, scratch);

            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;

            for (var i = 0; i < count; i++)
            {
                var relativeX = originFraction + positions[i * 2] * scaleX;
                SnapGlyphPen(in key, relativeX, out var penX, out var glyphPhase);
                var penY = (int)MathF.Round(positions[i * 2 + 1] * scaleY);

                var mask = maskCache.GetOrBuild(new GlyphMaskKey(indices[i], key.ScaleQ, glyphPhase, key.Mode, key.GridFit, key.PenSnap, embolden, oblique),
                    state, s_buildMask);

                UnionMask(mask, penX, penY, ref minX, ref minY, ref maxX, ref maxY);
            }

            if (minX >= maxX || minY >= maxY)
            {
                return null;
            }

            var height = maxY - minY;
            var chunkCount = GetChunkCount(maxX - minX, maxSize, out var chunkWidth);
            var parts = new RunMaskPart[chunkCount];
            var created = 0;
            var staging = ArrayPool<byte>.Shared.Rent(chunkWidth * height);

            try
            {
                for (var chunk = 0; chunk < chunkCount; chunk++)
                {
                    var chunkX = minX + chunk * chunkWidth;
                    var width = Math.Min(chunkWidth, maxX - chunkX);
                    var span = staging.AsSpan(0, width * height);
                    span.Clear();

                    for (var i = 0; i < count; i++)
                    {
                        var relativeX = originFraction + positions[i * 2] * scaleX;
                        SnapGlyphPen(in key, relativeX, out var penX, out var glyphPhase);
                        var penY = (int)MathF.Round(positions[i * 2 + 1] * scaleY);

                        var mask = maskCache.GetOrBuild(new GlyphMaskKey(indices[i], key.ScaleQ, glyphPhase, key.Mode, key.GridFit, key.PenSnap, embolden, oblique),
                            state, s_buildMask);

                        RunMaskComposer.ComposeAlpha(mask, penX - chunkX, penY - minY, span, width, height);
                    }

                    parts[created++] = new RunMaskPart(alphaContext.CreateAlphaMask(span, width, height),
                        chunkX, minY, width, height);
                }

                return new RunMask(parts);
            }
            catch
            {
                DisposeParts(parts, created);
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(staging);
            }
        }

        private static unsafe RunMask? Compose(ManagedGlyphRunImpl run, RunMaskKey key, float scaleX, float scaleY,
            int maxSize)
        {
            var typeface = run.GlyphTypeface;
            var embolden = GlyphSimulation.QuantizeEmboldenOutset(typeface.FontSimulations, run.FontRenderingEmSize, key.ScaleQ);
            var oblique = (typeface.FontSimulations & FontSimulations.Oblique) != 0;
            var maskCache = typeface.MaskCache;
            var scratch = t_scratch ??= new GlyphPathBuilder();
            var count = run.GlyphCount;
            var indices = run.GlyphIndices;
            var positions = run.GlyphPositions;
            var originFraction = key.OriginPhase * (1f / GlyphMaskKey.PhaseCount);
            var colr = typeface.ColorTable;
            var cpal = typeface.ColorPaletteTable;
            var state = (typeface, scratch);

            // Bitmap strikes (CBDT or sbix): pick once per compose; glyphs the strike covers
            // draw as scaled decoded images, everything else falls through to outlines/COLR.
            // Without a decoder registered the strike data is ignored entirely (outline
            // fallback keeps rendering). Placement lookups decode once per (glyph, strike) —
            // the sources memoise — so calling from both passes is a warm hit the second time.
            var bitmapSource = typeface.BitmapSource;
            var decoder = bitmapSource is not null
                ? AvaloniaLocator.Current.GetService<IBitmapGlyphDecoder>()
                : null;
            var strike = default(Fonts.Tables.Bitmaps.BitmapStrike);
            var strikeScale = 0f;

            if (bitmapSource is not null && decoder is not null)
            {
                var pixelsPerEm = key.ScaleQ / GlyphMaskKey.ScaleQuantum;
                strike = bitmapSource.SelectStrike(pixelsPerEm);
                strikeScale = pixelsPerEm / strike.PpemY;
            }
            else
            {
                bitmapSource = null;
            }

            bool TryGetBitmapRect(ushort glyph, int penX, int penY,
                out Fonts.Tables.Bitmaps.BitmapGlyphPlacement placement,
                out int x, out int y, out int w, out int h)
            {
                placement = default;
                x = y = w = h = 0;

                if (bitmapSource is null || !bitmapSource.TryGetPlacement(strike, glyph, decoder!, out placement))
                {
                    return false;
                }

                w = Math.Max(1, (int)MathF.Round(placement.Bitmap.Width * strikeScale));
                h = Math.Max(1, (int)MathF.Round(placement.Bitmap.Height * strikeScale));
                x = penX + (int)MathF.Round(placement.Left * strikeScale);
                y = penY + (int)MathF.Round(placement.Top * strikeScale);
                return true;
            }

            // Simulations apply to outline glyphs only (see GlyphTypeface.IsColorGlyph): COLR
            // layers, strike images and a colour glyph's outline fallback are built unsimulated.
            var simulated = embolden != 0 || oblique;

            GlyphMask GetMask(ushort glyph, byte phase, bool simulate)
                => maskCache.GetOrBuild(simulate
                    ? new GlyphMaskKey(glyph, key.ScaleQ, phase, key.Mode, key.GridFit, key.PenSnap, embolden, oblique)
                    : new GlyphMaskKey(glyph, key.ScaleQ, phase, key.Mode, key.GridFit, key.PenSnap), state, s_buildMask);

            // Two passes over the same (glyph → v0 layers) expansion: the first unions the
            // placements, the second composes. The second pass refetches every mask through the
            // cache — a warm hit costs nanoseconds and avoids buffering (mask, pen, tint)
            // triples whose count is unknown until layers are expanded.
            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;

            for (var i = 0; i < count; i++)
            {
                // Each glyph's pen snaps individually: the run origin's fractional phase shifts
                // every pen, and each pen's own fraction picks that glyph's mask phase bucket.
                // v0 layer glyphs share their base glyph's pen and phase.
                var relativeX = originFraction + positions[i * 2] * scaleX;
                SnapGlyphPen(in key, relativeX, out var penX, out var glyphPhase);
                var penY = (int)MathF.Round(positions[i * 2 + 1] * scaleY);

                if (TryGetBitmapRect(indices[i], penX, penY, out _, out var bx, out var by, out var bw, out var bh))
                {
                    minX = Math.Min(minX, bx);
                    minY = Math.Min(minY, by);
                    maxX = Math.Max(maxX, bx + bw);
                    maxY = Math.Max(maxY, by + bh);
                }
                else if (colr is not null && cpal is not null && colr.TryGetBaseGlyphRecord(indices[i], out var baseRecord))
                {
                    for (var layer = 0; layer < baseRecord.NumLayers; layer++)
                    {
                        if (colr.TryGetLayerRecord(baseRecord.FirstLayerIndex + layer, out var layerRecord))
                        {
                            UnionMask(GetMask(layerRecord.GlyphIndex, glyphPhase, simulate: false), penX, penY,
                                ref minX, ref minY, ref maxX, ref maxY);
                        }
                    }
                }
                else
                {
                    UnionMask(GetMask(indices[i], glyphPhase, simulated && !typeface.IsColorGlyph(indices[i])),
                        penX, penY, ref minX, ref minY, ref maxX, ref maxY);
                }
            }

            if (minX >= maxX || minY >= maxY)
            {
                return null;
            }

            var height = maxY - minY;
            var chunkCount = GetChunkCount(maxX - minX, maxSize, out var chunkWidth);
            var parts = new RunMaskPart[chunkCount];
            var created = 0;

            // Resolved per compose (a cache miss), not captured statically — the same
            // locator-scope reasoning as the outline build path.
            var renderInterface = AvaloniaLocator.Current.GetRequiredService<IPlatformRenderInterface>();

            try
            {
                for (var chunk = 0; chunk < chunkCount; chunk++)
                {
                    var chunkX = minX + chunk * chunkWidth;
                    var width = Math.Min(chunkWidth, maxX - chunkX);
                    var bitmap = renderInterface.CreateWriteableBitmap(
                        new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

                    parts[created++] = new RunMaskPart(bitmap, chunkX, minY, width, height);

                    using (var framebuffer = bitmap.Lock())
                    {
                        // Compose straight into the locked framebuffer — no staging buffer (D7). The
                        // bitmap is never locked again, so its backend image identity stays stable.
                        var span = new Span<byte>((void*)framebuffer.Address, framebuffer.RowBytes * height);
                        span.Clear();

                        for (var i = 0; i < count; i++)
                        {
                            var relativeX = originFraction + positions[i * 2] * scaleX;
                            SnapGlyphPen(in key, relativeX, out var penX, out var glyphPhase);
                            var penY = (int)MathF.Round(positions[i * 2 + 1] * scaleY);

                            if (TryGetBitmapRect(indices[i], penX, penY, out var placement, out var bx, out var by, out var bw, out var bh))
                            {
                                // Strike bitmap: decoded once per (glyph, strike) via the source's memo,
                                // then blitted scaled, source-over.
                                RunMaskComposer.ComposeBitmap(placement.Bitmap, bx - chunkX, by - minY, bw, bh,
                                    span, width, height, framebuffer.RowBytes);
                            }
                            else if (colr is not null && cpal is not null && colr.TryGetBaseGlyphRecord(indices[i], out var baseRecord))
                            {
                                // COLR v0: flat-color layers composed bottom-to-top in record order. The
                                // 0xFFFF palette sentinel means "use the text foreground" — the run's
                                // tint, available right here on the mask path.
                                for (var layer = 0; layer < baseRecord.NumLayers; layer++)
                                {
                                    if (!colr.TryGetLayerRecord(baseRecord.FirstLayerIndex + layer, out var layerRecord))
                                    {
                                        continue;
                                    }

                                    uint layerTint;

                                    if (layerRecord.PaletteIndex == 0xFFFF)
                                    {
                                        layerTint = key.Tint;
                                    }
                                    else if (cpal.TryGetColor(layerRecord.PaletteIndex, out var color))
                                    {
                                        layerTint = RunMaskComposer.MakeTint(color.A, color.R, color.G, color.B);
                                    }
                                    else
                                    {
                                        continue;
                                    }

                                    RunMaskComposer.ComposeTinted(GetMask(layerRecord.GlyphIndex, glyphPhase, simulate: false),
                                        penX - chunkX, penY - minY, layerTint, span, width, height, framebuffer.RowBytes);
                                }
                            }
                            else
                            {
                                // Monochrome text takes the gamma/contrast coverage correction; the
                                // color layers above must not — the transform is non-linear, so
                                // abutting layers whose coverages sum to full would show seams.
                                RunMaskComposer.ComposeTinted(
                                    GetMask(indices[i], glyphPhase, simulated && !typeface.IsColorGlyph(indices[i])),
                                    penX - chunkX, penY - minY, key.Tint, span, width, height, framebuffer.RowBytes,
                                    MaskGamma.GetTableForPremulBgra(key.Tint));
                            }
                        }
                    }
                }

                return new RunMask(parts);
            }
            catch
            {
                DisposeParts(parts, created);
                throw;
            }
        }

        private static void UnionMask(GlyphMask mask, int penX, int penY,
            ref int minX, ref int minY, ref int maxX, ref int maxY)
        {
            if (mask.IsEmpty)
            {
                return;
            }

            minX = Math.Min(minX, penX + mask.Left);
            minY = Math.Min(minY, penY + mask.Top);
            maxX = Math.Max(maxX, penX + mask.Left + mask.Width);
            maxY = Math.Max(maxY, penY + mask.Top + mask.Height);
        }
    }
}
