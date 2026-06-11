using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
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
            },
            // Missing glyphs (CJK in the corpus) fall back inside the embedded
            // collection instead of machine-dependent system fonts.
            FontFallbacks = new[]
            {
                new FontFallback { FontFamily = new FontFamily("fonts:svg-corpus#Mplus 1p") },
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

        public SvgHost(string svg)
        {
            _image = new SvgImage(SvgDocument.Parse(svg));
            Width = _image.Size.Width;
            Height = _image.Size.Height;
        }

        public override void Render(DrawingContext context)
        {
            var rect = new Rect(_image.Size);
            _image.Draw(context, rect, rect);
        }
    }

    public void Dispose() => TestRenderHelper.EndTest();
}
