using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Svg;
using Avalonia.Media.Imaging;
using Avalonia.Skia.RenderTests;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;
using Image = SixLabors.ImageSharp.Image;

namespace Avalonia.Svg.RenderTests;

/// <summary>
/// Base class for SVG golden-image tests. Renders a host control through both the
/// immediate and the composited pipeline (via the shared
/// <see cref="TestRenderHelper"/>) and diffs each against
/// <c>tests/TestFiles/Svg/&lt;area&gt;/&lt;test&gt;.expected.png</c>.
/// </summary>
public class SvgRenderTestBase : IDisposable
{
    private const double AllowedError = 0.022;

    static SvgRenderTestBase()
    {
        // Plain family names resolve against system fonts only, so map the
        // corpus families into the embedded collection before the FontManager
        // is constructed: rendering stays hermetic on machines that have any
        // (possibly partial) "Noto Sans" installed. The options must be bound
        // before FontManager.Current is first touched.
        AvaloniaLocator.CurrentMutable.Bind<FontManagerOptions>().ToConstant(new FontManagerOptions
        {
            FontFamilyMappings = new System.Collections.Generic.Dictionary<string, FontFamily>
            {
                ["Noto Sans"] = new FontFamily("fonts:svg-corpus#Noto Sans"),
                ["Amiri"] = new FontFamily("fonts:svg-corpus#Amiri"),
                ["Mplus 1p"] = new FontFamily("fonts:svg-corpus#Mplus 1p"),
                ["Noto Sans Devanagari"] = new FontFamily("fonts:svg-corpus#Noto Sans Devanagari"),
                ["Source Sans Pro"] = new FontFamily("fonts:svg-corpus#Source Sans Pro"),
                ["Noto Serif"] = new FontFamily("fonts:svg-corpus#Noto Serif"),
                ["Noto Color Emoji"] = new FontFamily("fonts:svg-corpus#Noto Color Emoji"),
            },
            // Emoji explicitly fall back to the embedded color font. CJK has
            // no explicit fallback entry on purpose: the FontFallbacks list
            // matches by coverage alone, while the embedded collection's
            // TryMatchCharacter is culture-aware — it routes xml:lang="ja"
            // runs to the Japanese M PLUS 1p and other Han to the Simplified
            // Chinese font, mirroring how browsers pick per-language fonts.
            FontFallbacks = new[]
            {
                new FontFallback { FontFamily = new FontFamily("fonts:svg-corpus#Noto Color Emoji") },
            },
        });

        FontManager.Current.AddFontCollection(new Avalonia.Media.Fonts.EmbeddedFontCollection(
            new Uri("fonts:svg-corpus"),
            new Uri("resm:Avalonia.Svg.RenderTests.Assets?assembly=Avalonia.Svg.RenderTests")));
    }

    public SvgRenderTestBase(string outputPath)
    {
        outputPath = outputPath.Replace('\\', Path.DirectorySeparatorChar);
        OutputPath = Path.Combine(TestRenderHelper.GetTestsDirectory(), "TestFiles", "Svg", outputPath);
        TestRenderHelper.BeginTest();
    }

    public string OutputPath { get; }

    protected async Task RenderToFile(Control target, [CallerMemberName] string testName = "", double dpi = 96)
    {
        if (!Directory.Exists(OutputPath))
            Directory.CreateDirectory(OutputPath);

        var immediatePath = Path.Combine(OutputPath, testName + ".immediate.out.png");
        var compositedPath = Path.Combine(OutputPath, testName + ".composited.out.png");
        await TestRenderHelper.RenderToFile(target, immediatePath, true, dpi);
        await TestRenderHelper.RenderToFile(target, compositedPath, false, dpi);
    }

    protected void CompareImages([CallerMemberName] string testName = "", bool skipImmediate = false)
    {
        var expectedPath = Path.Combine(OutputPath, testName + ".expected.png");
        var immediatePath = Path.Combine(OutputPath, testName + ".immediate.out.png");
        var compositedPath = Path.Combine(OutputPath, testName + ".composited.out.png");

        using (var expected = Image.Load<Rgba32>(expectedPath))
        using (var composited = Image.Load<Rgba32>(compositedPath))
        {
            if (!skipImmediate)
            {
                using var immediate = Image.Load<Rgba32>(immediatePath);
                var immediateError = TestRenderHelper.CompareImages(immediate, expected);
                if (immediateError > AllowedError)
                    Assert.Fail(immediatePath + ": Error = " + immediateError);
            }

            var compositedError = TestRenderHelper.CompareImages(composited, expected);
            if (compositedError > AllowedError)
                Assert.Fail(compositedPath + ": Error = " + compositedError);
        }
    }

    /// <summary>
    /// A control that renders a parsed SVG document through <see cref="SvgImage"/>
    /// at the image's intrinsic size.
    /// </summary>
    protected sealed class SvgHost : Control
    {
        private readonly SvgImage _image;

        public SvgHost(string svg, Uri? baseUri = null)
            : this(new SvgImage(SvgDocument.Parse(svg, baseUri)))
        {
        }

        /// <summary>
        /// Hosts a pre-built image — used by animation tests that apply a
        /// SMIL timestamp to the document before compiling.
        /// </summary>
        public SvgHost(SvgImage image)
        {
            _image = image;

            // A zero/negative-size document renders nothing; the render
            // surface still needs a pixel to produce a comparable file.
            Width = Math.Max(1, _image.Size.Width);
            Height = Math.Max(1, _image.Size.Height);
        }

        public override void Render(DrawingContext context)
        {
            var rect = new Rect(_image.Size);
            _image.Draw(context, rect, rect);
        }
    }

    public void Dispose() => TestRenderHelper.EndTest();
}
