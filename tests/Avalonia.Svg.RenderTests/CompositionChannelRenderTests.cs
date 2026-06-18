using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Svg;
using Avalonia.Rendering.Composition;
using Avalonia.Skia.RenderTests;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Avalonia.Svg.RenderTests;

/// <summary>
/// End-to-end verification of the animation composition channel: a host
/// partitions the document into slice visuals, claims the transform and opacity
/// timelines as server-side key-frame animations, and keeps rendering the same
/// content. Durations are huge so the captured frame sits at the initial key
/// frame and stays comparable to a golden. Covers both hosts that can parent the
/// channel's visual subtree — the <see cref="SvgControl"/> and, via
/// <see cref="ICompositionImage"/>, the <see cref="Image"/> control.
/// </summary>
public class CompositionChannelRenderTests : SvgRenderTestBase
{
    private const string SlicedDocument =
        """
        <svg xmlns="http://www.w3.org/2000/svg" width="200" height="200">
          <rect width="200" height="200" fill="#0b1022"/>
          <circle cx="100" cy="100" r="18" fill="#f59e0b"/>
          <g>
            <animateTransform attributeName="transform" type="rotate"
                              from="0 100 100" to="360 100 100" dur="10000s" repeatCount="indefinite"/>
            <circle cx="160" cy="100" r="10" fill="#2563eb"/>
            <g>
              <animateTransform attributeName="transform" type="rotate"
                                from="0 160 100" to="360 160 100" dur="10000s" repeatCount="indefinite"/>
              <circle cx="178" cy="100" r="4" fill="#cbd5e1"/>
            </g>
          </g>
          <g transform="translate(0 40)">
            <g>
              <animateTransform attributeName="transform" type="rotate"
                                from="90 100 100" to="450 100 100" dur="10000s" repeatCount="indefinite"/>
              <rect x="140" y="95" width="20" height="10" fill="#10b981"/>
            </g>
          </g>
          <circle cx="30" cy="170" r="8" fill="#22d3ee">
            <animate attributeName="opacity" values="1;0.999" dur="10000s" repeatCount="indefinite"/>
          </circle>
          <rect x="160" y="160" width="20" height="20" fill="#f472b6">
            <animate attributeName="width" values="20;20.01" dur="10000s" repeatCount="indefinite"/>
          </rect>
        </svg>
        """;

    public CompositionChannelRenderTests()
        : base(@"Animation")
    {
    }

    [Fact]
    public async Task Composition_Channel_Renders_Sliced_Document()
    {
        using var document = SvgDocument.Parse(SlicedDocument);

        var control = new SvgControl
        {
            Source = document,
            Width = 200,
            Height = 200,
        };

        // The immediate pipeline has no compositor, so the control renders
        // through the static image there; only the composited pipeline exercises
        // the channel.
        await RenderToFile(control);

        CompareImages(skipImmediate: true);
    }

    [Fact]
    public async Task Image_Source_Hosts_The_Composition_Channel()
    {
        using var document = SvgDocument.Parse(SlicedDocument);

        var image = new Image
        {
            Source = new SvgImage(document),
            Width = 200,
            Height = 200,
        };

        await RenderToFile(image);

        // Proof the channel is actually engaged: the Image parents the channel's
        // visual subtree. A static fallback (whose first frame is pixel-identical
        // at t≈0) would leave no child visual, so this distinguishes the two.
        Assert.NotNull(ElementComposition.GetElementChildVisual(image));

        // The same document at the same size renders identically whether hosted
        // by the Svg control or the Image control, so it diffs against the
        // SvgControl golden.
        var expectedPath = Path.Combine(
            OutputPath, nameof(Composition_Channel_Renders_Sliced_Document) + ".expected.png");
        var compositedPath = Path.Combine(
            OutputPath, nameof(Image_Source_Hosts_The_Composition_Channel) + ".composited.out.png");

        using var expected = SixLabors.ImageSharp.Image.Load<Rgba32>(expectedPath);
        using var composited = SixLabors.ImageSharp.Image.Load<Rgba32>(compositedPath);
        var error = TestRenderHelper.CompareImages(composited, expected);
        Assert.True(error <= 0.022, $"{compositedPath}: Error = {error}");
    }

    [Fact]
    public async Task Image_Hosts_A_Paint_Only_Animation()
    {
        // No transform/opacity, so the partition has no composition group: the
        // relaxed gate still hosts it as a single static slice whose mutable fill
        // brush the paint tick drives.
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect width="100" height="100" fill="#0b1022"/>
              <circle cx="50" cy="50" r="30" fill="#22d3ee">
                <animate attributeName="fill" values="#22d3ee;#f472b6" dur="10000s" repeatCount="indefinite"/>
              </circle>
            </svg>
            """);

        await RenderHostedImage(document);
    }

    [Fact]
    public async Task Image_Hosts_A_Structural_Only_Animation()
    {
        // A geometry timeline stays structural (no composition group); the
        // relaxed gate hosts it as a self-recompiling structural slice.
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect width="100" height="100" fill="#0b1022"/>
              <rect x="40" y="40" width="20" height="20" fill="#f472b6">
                <animate attributeName="width" values="20;20.01" dur="10000s" repeatCount="indefinite"/>
              </rect>
            </svg>
            """);

        await RenderHostedImage(document);
    }

    [Fact]
    public async Task Image_Hosts_An_Animation_Under_Root_Layer_State()
    {
        // A filter on the root can't be sliced, so the whole document hosts as a
        // single structural slice; it must still render (with the filter applied)
        // and be hosted rather than drawn statically.
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" filter="url(#b)">
              <defs><filter id="b"><feGaussianBlur stdDeviation="1"/></filter></defs>
              <rect width="100" height="100" fill="#0b1022"/>
              <rect x="40" y="40" width="20" height="20" fill="#f472b6">
                <animate attributeName="width" values="20;20.01" dur="10000s" repeatCount="indefinite"/>
              </rect>
            </svg>
            """);

        await RenderHostedImage(document);
    }

    /// <summary>
    /// Renders an animated document through the <see cref="Image"/> control, then
    /// asserts it is actually hosted (a child visual is parented — the partitioner
    /// built a host) and that the composited render matches the static immediate
    /// render: at t≈0 the hosted output holds the document's base state, no golden
    /// required.
    /// </summary>
    private async Task RenderHostedImage(SvgDocument document, [CallerMemberName] string testName = "")
    {
        var svgImage = new SvgImage(document);
        var image = new Image
        {
            Source = svgImage,
            Width = svgImage.Size.Width,
            Height = svgImage.Size.Height,
        };

        await RenderToFile(image, testName);

        Assert.NotNull(ElementComposition.GetElementChildVisual(image));

        var immediatePath = Path.Combine(OutputPath, testName + ".immediate.out.png");
        var compositedPath = Path.Combine(OutputPath, testName + ".composited.out.png");
        using var immediate = SixLabors.ImageSharp.Image.Load<Rgba32>(immediatePath);
        using var composited = SixLabors.ImageSharp.Image.Load<Rgba32>(compositedPath);
        var error = TestRenderHelper.CompareImages(composited, immediate);
        Assert.True(error <= 0.022, $"{compositedPath} vs immediate: Error = {error}");
    }
}
