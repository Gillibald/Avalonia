using System;
using Avalonia.Media.Svg;
using Avalonia.Media.Svg.Animation;
using Xunit;

namespace Avalonia.Svg.UnitTests;

public class SvgAnimationStateTests
{
    private const string AnimatedDoc =
        """
        <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
          <rect id="r" x="0" width="10" height="10">
            <animate attributeName="x" from="0" to="90" dur="2s" fill="freeze"/>
          </rect>
        </svg>
        """;

    [Fact]
    public void Apply_Writes_To_State_Not_The_Shared_Element()
    {
        using var document = SvgDocument.Parse(AnimatedDoc);
        var animator = SvgAnimator.TryCreate(document)!;
        var rect = document.GetElementById("r")!;

        var state = new SvgAnimationState();
        Assert.True(animator.Apply(TimeSpan.FromSeconds(1), state));

        Assert.NotNull(state.Get(rect, "x"));
        Assert.Null(rect.GetAnimatedValue("x")); // the shared element is untouched
    }

    [Fact]
    public void Two_Instances_Animate_The_Same_Document_Independently()
    {
        using var document = SvgDocument.Parse(AnimatedDoc);
        var animator = SvgAnimator.TryCreate(document)!;
        var rect = document.GetElementById("r")!;

        var early = new SvgAnimationState();
        var late = new SvgAnimationState();
        animator.Apply(TimeSpan.FromSeconds(0.5), early);
        animator.Apply(TimeSpan.FromSeconds(1.5), late);

        Assert.NotNull(early.Get(rect, "x"));
        Assert.NotEqual(early.Get(rect, "x"), late.Get(rect, "x"));
        Assert.Null(rect.GetAnimatedValue("x"));
    }

    [Fact]
    public void Materialize_Makes_The_Override_Live_Then_Clears_It()
    {
        using var document = SvgDocument.Parse(AnimatedDoc);
        var animator = SvgAnimator.TryCreate(document)!;
        var rect = document.GetElementById("r")!;

        var state = new SvgAnimationState();
        animator.Apply(TimeSpan.FromSeconds(1), state);
        var expected = state.Get(rect, "x");

        using (state.Materialize())
            Assert.Equal(expected, rect.GetAnimatedValue("x"));

        Assert.Null(rect.GetAnimatedValue("x"));
    }

    [Fact]
    public void Repeating_A_Timestamp_Reports_No_Change()
    {
        using var document = SvgDocument.Parse(AnimatedDoc);
        var animator = SvgAnimator.TryCreate(document)!;
        var state = new SvgAnimationState();

        Assert.True(animator.Apply(TimeSpan.FromSeconds(1), state));
        Assert.False(animator.Apply(TimeSpan.FromSeconds(1), state));
    }
}
