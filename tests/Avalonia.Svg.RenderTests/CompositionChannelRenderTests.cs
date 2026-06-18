using System.IO;
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
        // through the legacy image there; only the composited pipeline
        // exercises the channel.
        SvgControl.EnableExperimentalCompositionAnimations = true;
        try
        {
            await RenderToFile(control);
        }
        finally
        {
            SvgControl.EnableExperimentalCompositionAnimations = false;
        }

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

        SvgControl.EnableExperimentalCompositionAnimations = true;
        try
        {
            await RenderToFile(image);
        }
        finally
        {
            SvgControl.EnableExperimentalCompositionAnimations = false;
        }

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
}
