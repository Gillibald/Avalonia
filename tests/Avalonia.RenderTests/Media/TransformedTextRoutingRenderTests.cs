using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Fonts.Rasterization;
using Xunit;

namespace Avalonia.Skia.RenderTests
{
    /// <summary>
    /// Transformed text reaches the transformed mask tier on every output this suite renders:
    /// the CPU raster outputs and the Mesa software GL (llvmpipe) and Vulkan (lavapipe)
    /// outputs, which the Slug tier would otherwise take. No golden: the tier is observed on
    /// the run itself.
    /// </summary>
    public class TransformedTextRoutingRenderTests : TestBase
    {
        public TransformedTextRoutingRenderTests()
            : base(@"Media\GlyphRun")
        {
        }

        [Fact]
        public async Task Rotated_Text_Takes_The_Transformed_Mask_Tier_On_Every_Output()
        {
            var run = CreateRotatedRun(out var target);

            await RenderToFile(target);

            var impl = (ManagedGlyphRunImpl)run.PlatformImpl.Item;

            // Every output draws the same sprite set: the CPU outputs blit its glyph masks, the
            // software GPU outputs draw it from the typeface's atlas.
            var gpu = MesaSoftwareRenderer.GlEnabled || MesaSoftwareRenderer.VulkanEnabled;

            Assert.Equal(1, impl.TransformedSprites.Count);
            Assert.Equal(gpu, impl.GlyphTypeface.MaskAtlas.Count > 0);
            Assert.Null(impl.SlugRunArtifact);
            Assert.Null(impl.NativeTextArtifact);
        }

        [Fact]
        public async Task The_Switch_Sends_Rotated_Text_To_Slug_On_The_Gpu_Outputs()
        {
            Assert.SkipUnless(MesaSoftwareRenderer.GlEnabled || MesaSoftwareRenderer.VulkanEnabled,
                "No Mesa GPU output enabled.");

            var run = CreateRotatedRun(out var target);
            var previous = MaskGlyphRunRenderer.TransformedTextRouting;

            MaskGlyphRunRenderer.TransformedTextRouting = TransformedTextRouting.Slug;

            try
            {
                await RenderToFile(target);
            }
            finally
            {
                MaskGlyphRunRenderer.TransformedTextRouting = previous;
            }

            var impl = (ManagedGlyphRunImpl)run.PlatformImpl.Item;

            Assert.Equal(0, impl.TransformedSprites.Count);
            Assert.NotNull(impl.SlugRunArtifact);
        }

        private static GlyphRun CreateRotatedRun(out Control target)
        {
            const string text = "Rotated managed text";

            var path = Path.Combine(TestRenderHelper.GetTestsDirectory(), "Avalonia.RenderTests", "Assets",
                "Inter-Regular.ttf");

            Assert.True(SfntFace.TryLoad(path, 0, out var face));

            var typeface = new GlyphTypeface(face);
            var glyphs = new ushort[text.Length];

            for (var i = 0; i < text.Length; i++)
            {
                glyphs[i] = typeface.CharacterToGlyphMap[text[i]];
            }

            var origin = new Point(10, 40);
            var run = new GlyphRun(typeface, 20, text.AsMemory(), glyphs, origin);

            target = new Border
            {
                Width = 240,
                Height = 140,
                Background = Brushes.White,
                Child = new RotatedGlyphRunControl(run, origin),
            };

            return run;
        }

        private sealed class RotatedGlyphRunControl : Control
        {
            private readonly GlyphRun _run;
            private readonly Point _origin;

            public RotatedGlyphRunControl(GlyphRun run, Point origin)
            {
                _run = run;
                _origin = origin;
            }

            public override void Render(DrawingContext context)
            {
                using (context.PushTransform(Matrix.CreateTranslation(-_origin.X, -_origin.Y) *
                           Matrix.CreateRotation(0.3) * Matrix.CreateTranslation(_origin.X, _origin.Y)))
                {
                    context.DrawGlyphRun(Brushes.Black, _run);
                }
            }
        }
    }
}
