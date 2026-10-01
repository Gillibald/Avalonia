using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Xunit;

namespace Avalonia.Skia.RenderTests
{
    /// <summary>
    /// Text under rotation, skew, non-uniform scale and display sizes. These draws take the
    /// transformed mask tier: glyph masks rasterized under the device transform, unhinted.
    /// </summary>
    public class TransformedTextRenderTests : TestBase
    {
        public TransformedTextRenderTests()
            : base(@"Media\TransformedText")
        {
        }

        [Fact]
        public async Task Transformed_Text_Rotated_Paragraph()
        {
            // 14 and 24 px lines rotated by 15 degrees, with kerning pairs and repeated stems.
            var regular = Load("Inter-Regular.ttf");
            var rotation = Rotate(15) * At(44, 28);

            await Render(440, 300, new (GlyphRun, Matrix, IBrush)[]
            {
                (Run(regular, "A static paragraph rotated by fifteen degrees", 14), At(0, 12) * rotation, Brushes.Black),
                (Run(regular, "composes once into cached run masks, then every", 14), At(0, 29) * rotation, Brushes.Black),
                (Run(regular, "frame is one blit per run. 0123456789 fjord", 14), At(0, 46) * rotation, Brushes.Black),
                (Run(regular, "AVATAR Wavy kerning, iiii WWWW (14 px).", 14), At(0, 63) * rotation, Brushes.Black),
                (Run(regular, "Larger lines at 24 px", 24), At(0, 97) * rotation, Brushes.Black),
                (Run(regular, "Hamburgefonstiv 0123", 24), At(0, 127) * rotation, Brushes.Black)
            });

            CompareImages();
        }

        [Fact]
        public async Task Transformed_Text_Quarter_Turns()
        {
            // The same label at 0, 90, 180 and 270 degrees around one centre. The upright label
            // takes the hinted upright tier, the turned ones the unhinted transformed tier.
            var regular = Load("Inter-Regular.ttf");
            var runs = new (GlyphRun, Matrix, IBrush)[4];

            for (var turn = 0; turn < 4; turn++)
            {
                runs[turn] = (Run(regular, "Quarter turn", 16), At(25, 6) * Rotate(turn * 90) * At(160, 140), Brushes.Black);
            }

            await Render(320, 320, runs);

            CompareImages();
        }

        [Fact]
        public async Task Transformed_Text_Rotated_Display_Sizes()
        {
            // Bold display text at 72 and 200 px, slightly rotated.
            var bold = Load("Inter-Bold.ttf");

            await Render(900, 440, new (GlyphRun, Matrix, IBrush)[]
            {
                (Run(bold, "Display 72", 72), Rotate(-6) * At(22, 102), Brushes.Black),
                (Run(bold, "Big 200", 200), Rotate(6) * At(52, 318), Brushes.Black)
            });

            CompareImages();
        }

        [Fact]
        public async Task Transformed_Text_Rotated_Simulated_Faces()
        {
            // Oblique, bold and bold oblique simulations of the regular face, rotated by 12 degrees;
            // the embolden strength matches upright text of the same size.
            var oblique = Load("Inter-Regular.ttf", FontSimulations.Oblique);
            var bold = Load("Inter-Regular.ttf", FontSimulations.Bold);
            var both = Load("Inter-Regular.ttf", FontSimulations.Bold | FontSimulations.Oblique);
            var rotation = Rotate(12);

            await Render(400, 240, new (GlyphRun, Matrix, IBrush)[]
            {
                (Run(oblique, "Simulated oblique, 24 px", 24), rotation * At(30, 48), Brushes.Black),
                (Run(bold, "Simulated bold, 24 px", 24), rotation * At(20, 83), Brushes.Black),
                (Run(both, "Simulated bold oblique", 24), rotation * At(12, 118), Brushes.Black)
            });

            CompareImages();
        }

        [Fact]
        public async Task Transformed_Text_Skewed_And_Anisotropic_Labels()
        {
            // A skewed label and two labels scaled differently per axis, one in a mid-luminance
            // colour so its gamma correction differs from the black lines.
            var regular = Load("Inter-Regular.ttf");

            await Render(360, 180, new (GlyphRun, Matrix, IBrush)[]
            {
                (Run(regular, "Skewed label, 16 px", 16), new Matrix(1, 0, -0.35, 1, 0, 0) * At(32, 40), Brushes.Black),
                (Run(regular, "Wide label 1.6 x 0.8", 16), Matrix.CreateScale(1.6, 0.8) * At(20, 90),
                    new SolidColorBrush(Color.FromRgb(0x22, 0x44, 0x99))),
                (Run(regular, "Tall label 0.8 x 1.6", 16), Matrix.CreateScale(0.8, 1.6) * At(20, 148), Brushes.Black)
            });

            CompareImages();
        }

        [Fact]
        public async Task Transformed_Text_400_Ppem_Glyph()
        {
            // Single glyphs far above the upright mask ceiling, upright and rotated by 20 degrees.
            var regular = Load("Inter-Regular.ttf");

            await Render(760, 560, new (GlyphRun, Matrix, IBrush)[]
            {
                (Run(regular, "g", 400), At(36, 330), Brushes.Black),
                (Run(regular, "R", 400), Rotate(20) * At(470, 340), Brushes.Black)
            });

            CompareImages();
        }

        private static GlyphTypeface Load(string file, FontSimulations simulations = FontSimulations.None)
        {
            var path = Path.Combine(TestRenderHelper.GetTestsDirectory(), "Avalonia.RenderTests", "Assets", file);

            Assert.True(SfntFace.TryLoad(path, 0, out var face));

            return new GlyphTypeface(face, simulations);
        }

        private static GlyphRun Run(GlyphTypeface typeface, string text, double size)
        {
            var glyphs = new ushort[text.Length];

            for (var i = 0; i < text.Length; i++)
            {
                glyphs[i] = typeface.CharacterToGlyphMap[text[i]];
            }

            return new GlyphRun(typeface, size, text.AsMemory(), glyphs, new Point(0, 0));
        }

        private static Matrix Rotate(double degrees) => Matrix.CreateRotation(Math.PI * degrees / 180);

        private static Matrix At(double x, double y) => Matrix.CreateTranslation(x, y);

        private Task Render(int width, int height, (GlyphRun Run, Matrix Transform, IBrush Brush)[] runs,
            [System.Runtime.CompilerServices.CallerMemberName] string testName = "")
            => RenderToFile(new Border
            {
                Width = width,
                Height = height,
                Background = Brushes.White,
                Child = new RunsControl(runs),
            }, testName);

        private sealed class RunsControl : Control
        {
            private readonly (GlyphRun Run, Matrix Transform, IBrush Brush)[] _runs;

            public RunsControl((GlyphRun, Matrix, IBrush)[] runs) => _runs = runs;

            public override void Render(DrawingContext context)
            {
                foreach (var (run, transform, brush) in _runs)
                {
                    using (context.PushTransform(transform))
                    {
                        context.DrawGlyphRun(brush, run);
                    }
                }
            }
        }
    }
}
