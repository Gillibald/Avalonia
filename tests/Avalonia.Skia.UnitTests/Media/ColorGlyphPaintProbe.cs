using System;
using System.Collections.Generic;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.Fonts.Tables.Colr;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Diagnostic probe (not a gate): prints the resolved COLR v1 paint-tree shape for a few
    /// Segoe UI Emoji glyphs so composite modes and nesting are known facts, not guesses.
    /// Enabled by setting COLOR_GLYPH_PROBE=1; reports via a deliberate failure message.
    /// </summary>
    public class ColorGlyphPaintProbe
    {
        [Fact]
        public void Dump_Paint_Tree_Shapes()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("COLOR_GLYPH_PROBE") != "1", "Probe disabled.");
            Assert.SkipWhen(!OperatingSystem.IsWindows(), "Windows fonts only.");

            using var skTypeface = SKFontManager.Default.MatchFamily("Segoe UI Emoji", SKFontStyle.Normal);

            Assert.SkipWhen(skTypeface is null, "No Segoe UI Emoji.");

            var typeface = GlyphTypeface.TryCreate(new SkiaTypeface(skTypeface!, FontSimulations.None))!;
            var report = new StringBuilder();

            foreach (var (label, codepoint) in new[] { ("fire", 0x1F525), ("heart", 0x2764), ("smile", 0x1F600), ("rainbow", 0x1F308) })
            {
                var glyph = typeface.CharacterToGlyphMap[codepoint];

                report.AppendLine($"== {label} U+{codepoint:X} glyph {glyph} ==");

                if (typeface.ColorTable is not { } colr || !colr.TryGetBaseGlyphV1Record(glyph, out var record))
                {
                    report.AppendLine("   no v1 record");
                    continue;
                }

                if (typeface.GetGlyphDrawing(glyph) is not ColorGlyphV1Drawing)
                {
                    report.AppendLine("   drawing is not v1");
                    continue;
                }

                // Re-resolve the paint tree the way the drawing does and dump its shape.
                var context = new ColrContext(typeface, colr, typeface.ColorPaletteTable!, 0, null);

                if (!typeface.TryGetBaseGlyphV1Paint(context, record, out var paint))
                {
                    report.AppendLine("   paint resolution failed");
                    continue;
                }

                Dump(paint!, report, 1, 0);
            }

            Assert.Fail(report.ToString());
        }

        private static void Dump(Paint paint, StringBuilder report, int depth, int siblingIndex)
        {
            if (depth > 6)
            {
                report.AppendLine($"{new string(' ', depth * 2)}...");
                return;
            }

            var indent = new string(' ', depth * 2);

            switch (paint)
            {
                case ResolvedClipBox clip:
                    report.AppendLine($"{indent}ClipBox {clip.Box}");
                    Dump(clip.Inner, report, depth + 1, 0);
                    break;
                case ColrLayers layers:
                    report.AppendLine($"{indent}Layers x{layers.Layers.Count}");
                    for (var i = 0; i < layers.Layers.Count && i < 8; i++)
                    {
                        Dump(layers.Layers[i], report, depth + 1, i);
                    }
                    break;
                case Composite comp:
                    report.AppendLine($"{indent}Composite {comp.Mode}");
                    report.AppendLine($"{indent} backdrop:");
                    Dump(comp.Backdrop, report, depth + 1, 0);
                    report.AppendLine($"{indent} source:");
                    Dump(comp.Source, report, depth + 1, 1);
                    break;
                case ResolvedTransform t:
                    report.AppendLine($"{indent}Transform {t.Matrix}");
                    Dump(t.Inner, report, depth + 1, 0);
                    break;
                case Glyph g:
                    report.AppendLine($"{indent}Glyph {g.GlyphIndex}");
                    Dump(g.Paint, report, depth + 1, 0);
                    break;
                case ResolvedSolid s:
                    report.AppendLine($"{indent}Solid {s.Color}");
                    break;
                case ResolvedLinearGradient:
                    report.AppendLine($"{indent}LinearGradient");
                    break;
                case ResolvedRadialGradient:
                    report.AppendLine($"{indent}RadialGradient");
                    break;
                case ResolvedConicGradient:
                    report.AppendLine($"{indent}ConicGradient");
                    break;
                default:
                    report.AppendLine($"{indent}{paint.GetType().Name}");
                    break;
            }
        }
    }
}
