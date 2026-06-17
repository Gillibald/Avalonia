using System;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Animation;
using Xunit;

namespace Avalonia.Svg.RenderTests;

/// <summary>
/// Deterministic SMIL animation frames: the animator applies a fixed
/// timestamp to the document, the result compiles like any static document
/// and renders against a per-timestamp golden. Complements
/// <c>SvgSmilTests</c>, which pins the sampled values — these verify the
/// frames actually paint.
/// </summary>
public class AnimationRenderTests : SvgRenderTestBase
{
    public AnimationRenderTests()
        : base(@"Animation")
    {
    }

    private async Task RenderFrame(string svg, double seconds, string testName)
    {
        using var document = SvgDocument.Parse(svg);
        var animator = SvgAnimator.TryCreate(document);
        Assert.NotNull(animator);
        animator.Apply(TimeSpan.FromSeconds(seconds));

        var host = new SvgHost(new SvgImage(document));
        await RenderToFile(host, testName);
        CompareImages(testName);
    }

    private const string SlidingRect =
        """
        <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
          <rect x="1" y="1" width="198" height="98" fill="none" stroke="black"/>
          <rect x="10" y="30" width="40" height="40" fill="seagreen">
            <animate attributeName="x" from="10" to="150" dur="2s"/>
          </rect>
        </svg>
        """;

    [Fact]
    public Task Structural_Frame_At_Start() => RenderFrame(SlidingRect, 0, nameof(Structural_Frame_At_Start));

    [Fact]
    public Task Structural_Frame_At_Midpoint() => RenderFrame(SlidingRect, 1, nameof(Structural_Frame_At_Midpoint));

    [Fact]
    public Task Structural_Frame_Near_End() => RenderFrame(SlidingRect, 1.75, nameof(Structural_Frame_Near_End));

    [Fact]
    public Task Paint_Frame_At_Midpoint() => RenderFrame(
        """
        <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
          <rect x="1" y="1" width="98" height="98" fill="none" stroke="black"/>
          <circle cx="50" cy="50" r="35" fill="#ff0000">
            <animate attributeName="fill" from="#ff0000" to="#0000ff" dur="2s"/>
          </circle>
        </svg>
        """,
        1,
        nameof(Paint_Frame_At_Midpoint));

    [Fact]
    public Task Transform_Frame_At_Midpoint() => RenderFrame(
        """
        <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
          <rect x="1" y="1" width="98" height="98" fill="none" stroke="black"/>
          <rect x="30" y="30" width="40" height="40" fill="goldenrod">
            <animateTransform attributeName="transform" type="rotate"
                              from="0 50 50" to="90 50 50" dur="2s"/>
          </rect>
        </svg>
        """,
        1,
        nameof(Transform_Frame_At_Midpoint));
}
