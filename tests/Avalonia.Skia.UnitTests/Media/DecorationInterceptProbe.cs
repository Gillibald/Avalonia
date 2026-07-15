using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.UnitTests;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>Scratch probe: managed analytic GetIntersections vs SKTextBlob.GetIntercepts
    /// (default hinted font and hinting-free font) over the TextDecorations fixture's underline
    /// band, to attribute the missing descender gaps. Env-gated, not part of the suite.</summary>
    public class DecorationInterceptProbe
    {
        [Fact]
        public void Compare_Underline_Intercepts()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("DECORATION_INTERCEPT_PROBE") is not { Length: > 0 }, "probe");

            using var app = UnitTestApplication.Start(TestServices.MockPlatformRenderInterface
                .With(renderInterface: new PlatformRenderInterface(null)));

            using var skTypeface = SKFontManager.Default.MatchFamily("Courier New", SKFontStyle.Normal);
            Assert.NotNull(skTypeface);
            var typeface = new GlyphTypeface(new SkiaTypeface(skTypeface!, FontSimulations.None));

            const string text = "Neque porro quisquam est qui dolorem ipsum";
            const double emSize = 12;
            var scale = emSize / typeface.Metrics.DesignEmHeight;
            var infos = new List<GlyphInfo>();
            var cluster = 0;

            foreach (var c in text)
            {
                var glyph = typeface.CharacterToGlyphMap[c];
                typeface.TryGetGlyphMetrics(glyph, out var metrics);
                infos.Add(new GlyphInfo(glyph, cluster++, metrics.AdvanceWidth * scale));
            }

            using var run = new ManagedGlyphRunImpl(typeface, emSize, infos, new Point(0, 0));

            // Underline band from the font's own metrics, like the decoration drawing does.
            var underlinePos = (float)(typeface.Metrics.UnderlinePosition * -scale);
            var thickness = (float)(typeface.Metrics.UnderlineThickness * scale);
            Console.WriteLine(FormattableString.Invariant(
                $"underlinePos={underlinePos:F3} thickness={thickness:F3} (baseline-relative, +down)"));

            using var fontHinted = GlyphRunImpl.CreateFont(
                (SkiaTypeface)typeface.PlatformTypeface!, (float)emSize, default);
            using var fontUnhinted = GlyphRunImpl.CreateFont(
                (SkiaTypeface)typeface.PlatformTypeface!, (float)emSize,
                new TextOptions { TextHintingMode = TextHintingMode.None });

            SKTextBlob Build(SKFont font)
            {
                var builder = new SKTextBlobBuilder();
                var buffer = builder.AllocatePositionedRun(font, infos.Count);
                var points = new SKPoint[infos.Count];
                float x = 0;
                for (var i = 0; i < infos.Count; i++)
                {
                    points[i] = new SKPoint(x, 0);
                    x += (float)infos[i].GlyphAdvance;
                }
                buffer.SetPositions(points);
                buffer.SetGlyphs(infos.Select(g => g.GlyphIndex).ToArray());
                return builder.Build()!;
            }

            using var blobHinted = Build(fontHinted);
            using var blobUnhinted = Build(fontUnhinted);

            foreach (var (lo, hi) in new[]
                     {
                         (underlinePos, underlinePos + thickness),
                         (underlinePos - 0.5f, underlinePos + thickness + 0.5f),
                         (1.0f, 1.5f), (1.5f, 2.0f), (2.0f, 2.5f),
                     })
            {
                var managed = run.GetIntersections(lo, hi);
                var hinted = blobHinted.GetIntercepts(lo, hi);
                var unhinted = blobUnhinted.GetIntercepts(lo, hi);
                Console.WriteLine(FormattableString.Invariant($"band [{lo:F2},{hi:F2}]"));
                Console.WriteLine("  managed : " + string.Join(" ", managed.Select(v => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))));
                Console.WriteLine("  hinted  : " + string.Join(" ", hinted.Select(v => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))));
                Console.WriteLine("  unhinted: " + string.Join(" ", unhinted.Select(v => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))));
            }

            // Contract check: nonzero origin. The decoration code mixes results with absolute
            // baselineOrigin.X, so both impls must agree on the coordinate space.
            var origin = new Point(8, 20);
            using var managedAtOrigin = new ManagedGlyphRunImpl(typeface, emSize, infos, origin);
            using var backendAtOrigin = new GlyphRunImpl(typeface, emSize, infos, origin);

            foreach (var (lo, hi) in new[] { (1.5f, 2.0f), (2.0f, 2.5f), (21.5f, 22.0f), (22.0f, 22.5f) })
            {
                var m = managedAtOrigin.GetIntersections(lo, hi);
                var b = backendAtOrigin.GetIntersections(lo, hi);
                Console.WriteLine(FormattableString.Invariant($"origin(8,20) band [{lo:F2},{hi:F2}]"));
                Console.WriteLine("  managed : " + string.Join(" ", m.Select(v => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))));
                Console.WriteLine("  backend : " + string.Join(" ", b.Select(v => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))));
            }

            Assert.Fail("probe output above");
        }
    }
}
