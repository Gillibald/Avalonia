using System;
using System.IO;
using System.Linq;
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
    /// outputs. No golden: the tier is observed on the run itself.
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
            // software GPU outputs draw it from an atlas, the shared one on GL and the
            // typeface's own on Vulkan.
            var gpu = MesaSoftwareRenderer.GlEnabled || MesaSoftwareRenderer.VulkanEnabled;
            var owner = impl.GlyphTypeface.MaskOwnerId;
            var atlased = impl.GlyphTypeface.MaskAtlas.Count > 0 ||
                          GlyphMaskAtlas.Shared.GetPages().Any(page => page.Keys.Any(key => key.Owner == owner));

            Assert.Equal(1, impl.TransformedSprites.Count);
            Assert.Equal(gpu, atlased);
            Assert.Null(impl.NativeTextArtifact);
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
