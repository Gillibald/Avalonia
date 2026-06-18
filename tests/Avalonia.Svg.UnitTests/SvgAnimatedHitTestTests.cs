using System;
using Avalonia.Media;
using Avalonia.Media.Svg;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Svg.UnitTests;

/// <summary>
/// A hosted SVG instance hit tests against its current animation frame, not the
/// document's base state. Driven directly through the instance — the
/// <see cref="CompositorTestServices"/> render timer always ticks at zero, so a
/// hosted control's clock would never advance.
/// </summary>
public class SvgAnimatedHitTestTests
{
    [Fact]
    public void Structural_Hit_Testing_Follows_The_Animation()
    {
        using var services = new CompositorTestServices();
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect x="10" y="40" width="20" height="20" fill="#ff0000">
                <animate attributeName="x" from="10" to="70" dur="2s" fill="freeze"/>
              </rect>
            </svg>
            """);
        using var image = new SvgImage(document);
        using var instance = image.CreateInstance(services.Compositor)!;
        var hitSource = Assert.IsAssignableFrom<ISvgHitTestSource>(instance);

        // Base frame: the rect covers x in [10, 30].
        Assert.NotEmpty(hitSource.HitTest(new Point(20, 50)));
        Assert.Empty(hitSource.HitTest(new Point(50, 50)));

        // Halfway through the 2s timeline x animates to 40, so the rect covers
        // x in [40, 60]: the hit follows it instead of staying at the base.
        instance.OnClock(TimeSpan.FromSeconds(1));

        Assert.Empty(hitSource.HitTest(new Point(20, 50)));
        Assert.NotEmpty(hitSource.HitTest(new Point(50, 50)));
    }
}
