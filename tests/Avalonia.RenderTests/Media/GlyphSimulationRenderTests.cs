using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Xunit;

namespace Avalonia.Skia.RenderTests
{
    public class GlyphSimulationRenderTests : TestBase
    {
        private const string Text = "Hamburgefonstiv 0123";

        public GlyphSimulationRenderTests()
            : base(@"Media\GlyphRun")
        {
        }

        [Fact]
        public async Task Should_Render_Font_Simulations()
        {
            // One row per simulation: None, Bold, Oblique, Bold | Oblique. The synthesized rows
            // must come out heavier and slanted, not as copies of the regular face.
            var target = new Border
            {
                Width = 300,
                Height = 140,
                Background = Brushes.White,
                Child = new SimulationRowsControl(
                    CreateGlyphTypeface(FontSimulations.None),
                    CreateGlyphTypeface(FontSimulations.Bold),
                    CreateGlyphTypeface(FontSimulations.Oblique),
                    CreateGlyphTypeface(FontSimulations.Bold | FontSimulations.Oblique))
            };

            await RenderToFile(target);

            CompareImages();
        }

        private static GlyphTypeface CreateGlyphTypeface(FontSimulations simulations)
        {
            var path = Path.Combine(TestRenderHelper.GetTestsDirectory(),
                "Avalonia.Skia.UnitTests", "Fonts", "DejaVuSans.ttf");

            Assert.True(SfntFace.TryLoad(path, 0, out var face));

            // Simulated rows are variants of the regular face, the way font collections
            // synthesize them.
            var glyphTypeface = new GlyphTypeface(face).WithSimulations(simulations);

            Assert.Equal(simulations, glyphTypeface.FontSimulations);

            return glyphTypeface;
        }

        private sealed class SimulationRowsControl : Control
        {
            private const double EmSize = 22;

            private readonly GlyphRun[] _rows;

            public SimulationRowsControl(params GlyphTypeface[] typefaces)
            {
                _rows = new GlyphRun[typefaces.Length];

                for (var i = 0; i < typefaces.Length; i++)
                {
                    var typeface = typefaces[i];
                    var glyphs = new ushort[Text.Length];

                    for (var c = 0; c < Text.Length; c++)
                    {
                        glyphs[c] = typeface.CharacterToGlyphMap[Text[c]];
                    }

                    _rows[i] = new GlyphRun(typeface, EmSize, Text.AsMemory(), glyphs,
                        new Point(10, 28 + i * 32));
                }
            }

            public override void Render(DrawingContext context)
            {
                foreach (var row in _rows)
                {
                    context.DrawGlyphRun(Brushes.Black, row);
                }
            }
        }
    }
}
