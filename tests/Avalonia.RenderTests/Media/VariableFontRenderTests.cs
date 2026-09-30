using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Media;
using Xunit;

namespace Avalonia.Skia.RenderTests
{
    public class VariableFontRenderTests : TestBase
    {
        private const string Text = "Hamburgefonstiv 0123";

        public VariableFontRenderTests()
            : base(@"Media\GlyphRun")
        {
        }

        [Fact]
        public async Task Should_Render_Varied_Runs_With_Simulations()
        {
            // Each block draws its own instance, never the default wght=400, with the simulations
            // stacked on that instance.
            var variable = GlyphRunRowsControl.LoadAsset("InterVariable.ttf");
            var rows = new List<GlyphRunRow>();
            var y = 4.0;

            foreach (var weight in new[] { "wght=300", "wght=900" })
            {
                var varied = variable.WithVariations(FontVariationSettings.Parse(weight));

                foreach (var simulations in GlyphRunRowsControl.AllSimulations)
                {
                    y += 26;
                    rows.Add(new GlyphRunRow(varied.WithSimulations(simulations), 20, Text, new Point(10, y)));
                }

                y += 6;
            }

            // The wght=900 rows again, rotated: they draw through the transformed mask tier, which
            // must match the weight and slant of the upright rows.
            var black = variable.WithVariations(FontVariationSettings.Parse("wght=900"));

            foreach (var simulations in GlyphRunRowsControl.AllSimulations)
            {
                y += 26;
                rows.Add(new GlyphRunRow(black.WithSimulations(simulations), 20, Text, new Point(10, y),
                    Matrix.CreateRotation(0.05)));
            }

            await RenderToFile(GlyphRunRowsControl.CreateTarget(rows, 360, Math.Ceiling(y + 30)));

            CompareImages();
        }
    }
}
