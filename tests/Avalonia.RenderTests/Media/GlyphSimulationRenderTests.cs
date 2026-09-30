using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.UnitTests;
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

        [Fact]
        public async Task Should_Render_Font_Simulations_Across_Sizes()
        {
            // The embolden strength follows the size, heavier relative to the em at small sizes;
            // the oblique slant and the advances do not change with the size.
            var regular = GlyphRunRowsControl.LoadAsset("Inter-Regular.ttf");
            var rows = new List<GlyphRunRow>();
            var y = 4.0;

            foreach (var size in new[] { 9.0, 12.0, 16.0, 24.0, 36.0 })
            {
                foreach (var simulations in GlyphRunRowsControl.AllSimulations)
                {
                    y += size * 1.25;
                    rows.Add(new GlyphRunRow(regular.WithSimulations(simulations), size, Text, new Point(10, y)));
                }

                y += 6;
            }

            await RenderToFile(GlyphRunRowsControl.CreateTarget(rows, 420, Math.Ceiling(y + 10)));

            CompareImages();
        }

        [Fact]
        public async Task Should_Render_Hinted_Font_Simulations()
        {
            // Simulations apply after hinting: bold stems keep the grid fit of the font program
            // and oblique stems are slanted, not fitted upright again.
            var regular = GlyphRunRowsControl.LoadAsset("NotoMono-Regular.ttf");
            var rows = new List<GlyphRunRow>();
            var y = 4.0;

            foreach (var simulations in GlyphRunRowsControl.AllSimulations)
            {
                y += 20;
                rows.Add(new GlyphRunRow(regular.WithSimulations(simulations), 14, Text, new Point(10, y)));
            }

            await RenderToFile(GlyphRunRowsControl.CreateTarget(rows, 260, Math.Ceiling(y + 10),
                TextHintingMode.Light));

            CompareImages();
        }

        [Fact]
        public async Task Should_Not_Simulate_Colour_Glyphs()
        {
            // 'H' is a COLR v0 glyph painting the 'O' outline in red. 'A' and 'g' follow the row's
            // simulations, the colour glyph never does. The right column is rotated and draws
            // through the transformed mask tier, which must leave the colour glyph unsimulated
            // too, for the static and the varied face alike.
            var rows = new List<GlyphRunRow>();
            var y = 4.0;

            foreach (var file in new[] { "Inter-Regular.ttf", "InterVariable.ttf" })
            {
                var source = new GlyphTypeface(CreateColrFace(GlyphRunRowsControl.AssetPath(file)));

                if (file == "InterVariable.ttf")
                {
                    source = source.WithVariations(FontVariationSettings.Parse("wght=900"));
                }

                foreach (var simulations in GlyphRunRowsControl.AllSimulations)
                {
                    var typeface = source.WithSimulations(simulations);

                    y += 40;
                    rows.Add(new GlyphRunRow(typeface, 32, "AHg", new Point(10, y)));
                    rows.Add(new GlyphRunRow(typeface, 32, "AHg", new Point(170, y), Matrix.CreateRotation(0.05)));
                }

                y += 8;
            }

            await RenderToFile(GlyphRunRowsControl.CreateTarget(rows, 340, Math.Ceiling(y + 24)));

            CompareImages();
        }

        private static SfntFace CreateColrFace(string path)
        {
            var baseFont = SyntheticFont.FromBytes(File.ReadAllBytes(path));
            var probe = baseFont.CreateGlyphTypeface();

            var colorGlyph = probe.CharacterToGlyphMap['H'];
            var layerGlyph = probe.CharacterToGlyphMap['O'];

            // COLR v0: one base glyph record for 'H' with one layer, the 'O' outline on palette
            // entry 0 (red).
            var colr = new BigEndianBuffer();
            colr.UInt16(0).UInt16(1).UInt32(14).UInt32(20).UInt16(1)
                .UInt16(colorGlyph).UInt16(0).UInt16(1)
                .UInt16(layerGlyph).UInt16(0);

            var bytes = ColrTestFont.Graft(baseFont, colr.ToArray(),
                ColrTestFont.Cpal(new[] { new[] { Colors.Red } })).ToBytes();

            Assert.True(SfntFace.TryLoad(new MemoryStream(bytes), out var face));

            return face!;
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
