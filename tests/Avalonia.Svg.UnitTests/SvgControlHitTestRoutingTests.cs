using Avalonia.Controls;
using Avalonia.Media.Svg;
using Avalonia.Rendering.Composition;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Svg.UnitTests;

/// <summary>
/// A hosting <see cref="SvgControl"/> renders its document through a child
/// composition visual and paints nothing of its own except a transparent
/// surface. That surface is what keeps the control hit-testable: the child
/// visual is a recording visual the compositor hit test never yields, so
/// without the surface no pointer would ever route to the control and the
/// <c>ElementPointer*</c> events would go silent.
/// </summary>
public class SvgControlHitTestRoutingTests
{
    private const string AnimatedDocument =
        """
        <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
          <rect width="100" height="100" fill="#0b1022"/>
          <g>
            <animateTransform attributeName="transform" type="rotate"
                              from="0 50 50" to="360 50 50" dur="4s" repeatCount="indefinite"/>
            <circle cx="80" cy="50" r="10" fill="#f59e0b"/>
          </g>
        </svg>
        """;

    [Fact]
    public void Hosting_Control_Stays_Hit_Testable_For_Pointer_Routing()
    {
        using var services = new CompositorTestServices(new Size(100, 100));
        using var document = SvgDocument.Parse(AnimatedDocument);
        var control = new SvgControl { Source = document };

        services.TopLevel.Content = control;
        services.RunJobs();

        // The document is animated, so the control hosts it as a child
        // composition visual rather than drawing it — confirm it is hosting.
        Assert.NotNull(ElementComposition.GetElementChildVisual(control));

        // The compositor hit test must still land on the control (its
        // transparent surface), so pointer events route to it.
        services.AssertHitTestFirst(new Point(50, 50), null, control);
    }
}
