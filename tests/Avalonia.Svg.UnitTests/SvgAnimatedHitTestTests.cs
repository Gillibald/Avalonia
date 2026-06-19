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
        using var instance = ((ICompositionImage)image).CreateInstance(services.Compositor)!;
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

    [Fact]
    public void Transform_Override_Folds_A_Composition_Transform_Into_The_Hit_Tree()
    {
        // The mechanism the composition channel uses for hit testing: a group's
        // current server transform is read back and overridden onto its hit node.
        // Tested deterministically with a supplied matrix (the server clock does
        // not advance in tests).
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <g id="spinner">
                <rect x="70" y="45" width="20" height="10" fill="#ff0000"/>
              </g>
            </svg>
            """);
        using var image = new SvgImage(document);
        var spinner = document.GetElementById("spinner")!;

        // Base: the rect sits at x in [70, 90], y in [45, 55], hit at its centre.
        Assert.NotEmpty(image.HitTestElements(new Point(80, 50)));

        // A non-identity override on the group moves the rect, so the base centre
        // no longer hits.
        image.ApplyHitTransformOverrides(e => e == spinner ? Matrix.CreateTranslation(40, 40) : (Matrix?)null);
        Assert.Empty(image.HitTestElements(new Point(80, 50)));

        // Resetting the override to identity restores the base hit — the supplied
        // transform is what drives the result.
        image.ApplyHitTransformOverrides(e => e == spinner ? Matrix.Identity : (Matrix?)null);
        Assert.NotEmpty(image.HitTestElements(new Point(80, 50)));
    }
}
