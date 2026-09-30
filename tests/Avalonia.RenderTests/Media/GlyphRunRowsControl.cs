using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Xunit;

namespace Avalonia.Skia.RenderTests
{
    /// <summary>
    /// One unshaped glyph run: each character maps through the cmap to one glyph, drawn at
    /// <paramref name="Origin"/>, optionally transformed about that origin.
    /// </summary>
    internal sealed record GlyphRunRow(GlyphTypeface Typeface, double EmSize, string Text, Point Origin,
        Matrix? Transform = null);

    /// <summary>
    /// Draws rows of glyph runs in black under one hinting mode.
    /// </summary>
    internal sealed class GlyphRunRowsControl : Control
    {
        private readonly List<(GlyphRun Run, Matrix? Transform)> _runs = new();
        private readonly TextHintingMode _hintingMode;

        public GlyphRunRowsControl(IEnumerable<GlyphRunRow> rows, TextHintingMode hintingMode)
        {
            _hintingMode = hintingMode;

            foreach (var row in rows)
            {
                var glyphs = new ushort[row.Text.Length];

                for (var c = 0; c < row.Text.Length; c++)
                {
                    glyphs[c] = row.Typeface.CharacterToGlyphMap[row.Text[c]];
                }

                _runs.Add((new GlyphRun(row.Typeface, row.EmSize, row.Text.AsMemory(), glyphs, row.Origin),
                    row.Transform));
            }
        }

        public static readonly FontSimulations[] AllSimulations =
        {
            FontSimulations.None, FontSimulations.Bold, FontSimulations.Oblique,
            FontSimulations.Bold | FontSimulations.Oblique,
        };

        /// <summary>
        /// Loads a font from the render test assets as a regular face, bypassing the font manager
        /// so that faces sharing a family name stay distinct.
        /// </summary>
        public static GlyphTypeface LoadAsset(string fileName)
        {
            Assert.True(SfntFace.TryLoad(AssetPath(fileName), 0, out var face));

            return new GlyphTypeface(face);
        }

        public static string AssetPath(string fileName)
            => Path.Combine(TestRenderHelper.GetTestsDirectory(), "Avalonia.RenderTests", "Assets", fileName);

        public static Border CreateTarget(IEnumerable<GlyphRunRow> rows, double width, double height,
            TextHintingMode hintingMode = TextHintingMode.None)
            => new Border
            {
                Width = width,
                Height = height,
                Background = Brushes.White,
                Child = new GlyphRunRowsControl(rows, hintingMode),
            };

        public override void Render(DrawingContext context)
        {
            using var _ = context.PushTextOptions(new TextOptions { TextHintingMode = _hintingMode });

            foreach (var (run, transform) in _runs)
            {
                if (transform is { } matrix)
                {
                    var origin = run.BaselineOrigin;

                    using (context.PushTransform(Matrix.CreateTranslation(-origin.X, -origin.Y) * matrix *
                                                 Matrix.CreateTranslation(origin.X, origin.Y)))
                    {
                        context.DrawGlyphRun(Brushes.Black, run);
                    }
                }
                else
                {
                    context.DrawGlyphRun(Brushes.Black, run);
                }
            }
        }
    }
}
