using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Skia.Helpers;
using Avalonia.UnitTests;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>Scratch probe: managed-heap cost of instantiating and rendering every installed
    /// font family, the font-picker ComboBox scenario. Env-gated, not part of the suite.</summary>
    public class FontMemoryProbe
    {
        [Fact]
        public void Measure_All_System_Families()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("FONT_MEMORY_PROBE") != "1", "probe");

            using var app = UnitTestApplication.Start(TestServices.MockPlatformRenderInterface
                .With(renderInterface: new PlatformRenderInterface(null),
                    fontManagerImpl: new FontManagerImpl()));

            AvaloniaLocator.CurrentMutable.Bind<FontManagerOptions>().ToConstant(new FontManagerOptions
            {
                TextRasterizationMode = TextRasterizationMode.Managed,
            });

            var families = SKFontManager.Default.FontFamilies.Distinct().OrderBy(f => f).ToList();
            var report = new StringBuilder();

            var baseline = GC.GetTotalMemory(forceFullCollection: true);
            report.AppendLine(FormattableString.Invariant(
                $"families {families.Count}, baseline heap {baseline / 1024.0 / 1024.0:0.0} MB"));

            // Stage A: instantiate a GlyphTypeface per family — what binding the ComboBox items
            // does before anything renders.
            var typefaces = new List<GlyphTypeface>();

            foreach (var family in families)
            {
                if (new Typeface(family).GlyphTypeface is { } glyphTypeface)
                {
                    typefaces.Add(glyphTypeface);
                }
            }

            var afterCreate = GC.GetTotalMemory(true);
            report.AppendLine(FormattableString.Invariant(
                $"after create: {(afterCreate - baseline) / 1024.0 / 1024.0:0.0} MB ({(afterCreate - baseline) / Math.Max(1, typefaces.Count) / 1024.0:0.0} KB/typeface)"));

            // Stage B: lay out and draw each family name once at 16 px — the visible-item work.
            var info = new SKImageInfo(512, 64, SKColorType.Bgra8888, SKAlphaType.Premul);

            using (var bitmap = new SKBitmap(info))
            using (var canvas = new SKCanvas(bitmap))
            using (var context = DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96)))
            {
                foreach (var family in families)
                {
                    canvas.Clear(SKColors.White);

                    using var layout = new TextLayout(family, new Typeface(family), 16, Brushes.Black);
                    using var drawingContext = new PlatformDrawingContext(context, false);

                    layout.Draw(drawingContext, new Point(4, 4));
                }
            }

            var afterRender = GC.GetTotalMemory(true);
            report.AppendLine(FormattableString.Invariant(
                $"after render: +{(afterRender - afterCreate) / 1024.0 / 1024.0:0.0} MB (total {(afterRender - baseline) / 1024.0 / 1024.0:0.0} MB)"));

            var tally = Avalonia.Skia.SkiaTypeface.s_tableTally;

            if (!tally.IsEmpty)
            {
                report.AppendLine("table copies by tag:");

                foreach (var pair in tally.OrderByDescending(p => p.Value.Bytes).Take(14))
                {
                    report.AppendLine(FormattableString.Invariant(
                        $"  {pair.Key}: {pair.Value.Bytes / 1024.0 / 1024.0:0.0} MB in {pair.Value.Count} copies"));
                }

                report.AppendLine(FormattableString.Invariant(
                    $"  TOTAL: {tally.Sum(p => p.Value.Bytes) / 1024.0 / 1024.0:0.0} MB in {tally.Sum(p => p.Value.Count)} copies"));
            }

            GC.KeepAlive(typefaces);

            Assert.Fail(report.ToString());
        }
    }
}
