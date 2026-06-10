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
