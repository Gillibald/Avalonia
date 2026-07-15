using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Skia.Helpers;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// The LCD eligibility policy: subpixel text is offered only on display-bound surfaces
    /// that declare horizontal stripe geometry, outside every composited layer, on targets
    /// that permit subpixel text at all — everywhere else the mask path must degrade to
    /// grayscale, the way every platform stack does.
    /// </summary>
    public class LcdEligibilityTests
    {
        [Fact]
        public void An_Offscreen_Surface_Is_Ineligible_Despite_Declared_Stripes()
        {
            // SurfaceRenderTarget declares RGB stripes on every offscreen bitmap; without the
            // display bit the geometry must not make LCD eligible.
            using var surface = CreateSurface(SKPixelGeometry.RgbHorizontal);
            using var context = new Avalonia.Skia.DrawingContextImpl(new Avalonia.Skia.DrawingContextImpl.CreateInfo
            {
                Surface = surface,
                Canvas = surface.Canvas,
                Dpi = new Vector(96, 96),
            });

            Assert.False(((IAlphaGlyphMaskContext)context).TryGetLcdGeometry(out _));
        }

        [Fact]
        public void A_Surface_With_Rgb_Stripes_Is_Eligible()
        {
            using var surface = CreateSurface(SKPixelGeometry.RgbHorizontal);
            using var context = CreateContext(surface);

            Assert.True(((IAlphaGlyphMaskContext)context).TryGetLcdGeometry(out var geometry));
            Assert.Equal(LcdMaskGeometry.RgbHorizontal, geometry);
        }

        [Fact]
        public void Bgr_Stripes_Report_The_Swapped_Order()
        {
            using var surface = CreateSurface(SKPixelGeometry.BgrHorizontal);
            using var context = CreateContext(surface);

            Assert.True(((IAlphaGlyphMaskContext)context).TryGetLcdGeometry(out var geometry));
            Assert.Equal(LcdMaskGeometry.BgrHorizontal, geometry);
        }

        [Fact]
        public void Unknown_And_Vertical_Geometry_Are_Ineligible()
        {
            using (var surface = CreateSurface(SKPixelGeometry.Unknown))
            using (var context = CreateContext(surface))
            {
                Assert.False(((IAlphaGlyphMaskContext)context).TryGetLcdGeometry(out _));
            }

            using (var surface = CreateSurface(SKPixelGeometry.RgbVertical))
            using (var context = CreateContext(surface))
            {
                Assert.False(((IAlphaGlyphMaskContext)context).TryGetLcdGeometry(out _));
            }
        }

        [Fact]
        public void A_Canvas_Without_A_Surface_Is_Ineligible()
        {
            var info = new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var context = DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));

            Assert.False(((IAlphaGlyphMaskContext)context).TryGetLcdGeometry(out _));
        }

        [Fact]
        public void Targets_That_Disable_Subpixel_Text_Are_Ineligible()
        {
            using var surface = CreateSurface(SKPixelGeometry.RgbHorizontal);
            using var context = new Avalonia.Skia.DrawingContextImpl(new Avalonia.Skia.DrawingContextImpl.CreateInfo
            {
                Surface = surface,
                Canvas = surface.Canvas,
                Dpi = new Vector(96, 96),
                DisableSubpixelTextRendering = true,
            });

            Assert.False(((IAlphaGlyphMaskContext)context).TryGetLcdGeometry(out _));
        }

        [Fact]
        public void Every_Composited_Layer_Vetoes_And_Popping_Restores()
        {
            using var surface = CreateSurface(SKPixelGeometry.RgbHorizontal);
            using var context = CreateContext(surface);

            var probe = (IAlphaGlyphMaskContext)context;

            // Core layer.
            context.PushLayer(new Rect(0, 0, 16, 16));
            Assert.False(probe.TryGetLcdGeometry(out _));
            context.PopLayer();
            Assert.True(probe.TryGetLcdGeometry(out _));

            // Layer options (blend/isolation groups).
            ((Avalonia.Platform.IDrawingContextImplWithLayers)context).PushLayer(new LayerOptions
            {
                Opacity = 0.5,
            });
            Assert.False(probe.TryGetLcdGeometry(out _));
            context.PopLayer();
            Assert.True(probe.TryGetLcdGeometry(out _));

            // Opacity mask.
            context.PushOpacityMask(Brushes.White, new Rect(0, 0, 16, 16));
            Assert.False(probe.TryGetLcdGeometry(out _));
            context.PopOpacityMask();
            Assert.True(probe.TryGetLcdGeometry(out _));

            // Effect.
            ((Avalonia.Platform.IDrawingContextImplWithEffects)context).PushEffect(
                new Rect(0, 0, 16, 16), new ImmutableBlurEffect(2));
            Assert.False(probe.TryGetLcdGeometry(out _));
            ((Avalonia.Platform.IDrawingContextImplWithEffects)context).PopEffect();
            Assert.True(probe.TryGetLcdGeometry(out _));

            // Tracked (non-layered) opacity vetoes on CPU contexts: the two-pass payloads are
            // fixed and cannot fold an ambient opacity, so those draws degrade instead of
            // blending wrong. (GPU contexts fold opacity into the blender tint and stay
            // eligible; the GPU suites cover that side.)
            context.PushOpacity(0.5, null);
            Assert.False(probe.TryGetLcdGeometry(out _));
            context.PopOpacity();
            Assert.True(probe.TryGetLcdGeometry(out _));
        }

        [Fact]
        public void The_Resolver_Follows_The_Rendering_Mode_And_The_Context()
        {
            using var surface = CreateSurface(SKPixelGeometry.RgbHorizontal);
            using var context = CreateContext(surface);
            using var skTypeface = SKTypeface.FromFamilyName("Arial");
            var typeface = new GlyphTypeface(new Avalonia.Skia.SkiaTypeface(skTypeface, FontSimulations.None));

            Assert.Equal(GlyphMaskMode.Aliased, MaskGlyphRunRenderer.ResolveMaskMode(
                TextRenderingMode.Alias, context, typeface, out _));
            Assert.Equal(GlyphMaskMode.Antialiased, MaskGlyphRunRenderer.ResolveMaskMode(
                TextRenderingMode.Antialias, context, typeface, out _));
            Assert.Equal(GlyphMaskMode.Subpixel, MaskGlyphRunRenderer.ResolveMaskMode(
                TextRenderingMode.SubpixelAntialias, context, typeface, out _));

            // Unspecified means subpixel-when-eligible, the same default the blob applies.
            Assert.Equal(GlyphMaskMode.Subpixel, MaskGlyphRunRenderer.ResolveMaskMode(
                TextRenderingMode.Unspecified, context, typeface, out _));

            // Inside a layer the same requests degrade to grayscale.
            context.PushLayer(new Rect(0, 0, 16, 16));
            Assert.Equal(GlyphMaskMode.Antialiased, MaskGlyphRunRenderer.ResolveMaskMode(
                TextRenderingMode.SubpixelAntialias, context, typeface, out _));
            context.PopLayer();
        }

        private static SKSurface CreateSurface(SKPixelGeometry geometry)
        {
            var info = new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul);

            return SKSurface.Create(info, new SKSurfaceProperties(geometry));
        }

        private static Avalonia.Skia.DrawingContextImpl CreateContext(SKSurface surface)
            => new(new Avalonia.Skia.DrawingContextImpl.CreateInfo
            {
                Surface = surface,
                Canvas = surface.Canvas,
                Dpi = new Vector(96, 96),
                SurfaceIsDisplay = true,
            });
    }
}
