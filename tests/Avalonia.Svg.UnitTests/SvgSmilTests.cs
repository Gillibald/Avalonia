using System;
using Avalonia.Media;
using Avalonia.Media.Svg;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Media.Svg.Animation;
using Avalonia.Media.Svg.Compilation;
using Avalonia.Media.Svg.Parsing;
using Xunit;

namespace Avalonia.Svg.UnitTests;

public class SvgSmilTests
{
    private static Rect BoundsAt(SvgDocument document, SvgAnimator animator, double seconds)
    {
        var state = new SvgAnimationState();
        animator.Apply(TimeSpan.FromSeconds(seconds), state);
        var size = document.GetIntrinsicSize();
        using var scope = state.Materialize();
        using var recording = DrawingRecording.Create(ctx => SvgCompiler.CompileDocument(document, ctx, size));
        return recording.Bounds;
    }

    [Theory]
    [InlineData("2s", 2.0)]
    [InlineData("500ms", 0.5)]
    [InlineData("1.5", 1.5)]
    [InlineData("0.5min", 30.0)]
    [InlineData("1h", 3600.0)]
    public void Clock_Values_Parse(string value, double expectedSeconds)
    {
        Assert.True(SvgAnimator.TryParseClockValue(value, out var result));
        Assert.Equal(expectedSeconds, result.TotalSeconds, 6);
    }

    [Theory]
    [InlineData("indefinite")]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-1s")]
    public void Invalid_Clock_Values_Are_Rejected(string value)
    {
        Assert.False(SvgAnimator.TryParseClockValue(value, out _));
    }

    [Fact]
    public void Documents_Without_Animations_Have_No_Animator()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect width="10" height="10" fill="red"/>
            </svg>
            """);

        Assert.Null(SvgAnimator.TryCreate(document));
    }

    [Fact]
    public void Animate_Targets_Parent_By_Default_And_Href_When_Present()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect id="a" width="10" height="10" fill="red">
                <animate attributeName="x" from="0" to="50" dur="1s"/>
              </rect>
              <rect id="b" width="10" height="10" fill="red"/>
              <animate href="#b" attributeName="y" from="0" to="50" dur="1s"/>
            </svg>
            """);

        var animator = SvgAnimator.TryCreate(document)!;

        Assert.Equal(2, animator.Entries.Count);
        Assert.Same(document.GetElementById("a"), animator.Entries[0].Target);
        Assert.Equal("x", animator.Entries[0].AttributeName);
        Assert.Same(document.GetElementById("b"), animator.Entries[1].Target);
        Assert.Equal("y", animator.Entries[1].AttributeName);
    }

    [Fact]
    public void Linear_Animation_Interpolates_And_Begin_Delays()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <rect width="10" height="10" fill="red">
                <animate attributeName="x" from="0" to="100" begin="2s" dur="10s"/>
              </rect>
            </svg>
            """);
        var animator = SvgAnimator.TryCreate(document)!;

        // Before begin: the base value applies.
        Assert.Equal(new Rect(0, 0, 10, 10), BoundsAt(document, animator, 1));
        // Half-way through the simple duration.
        Assert.Equal(new Rect(50, 0, 10, 10), BoundsAt(document, animator, 7));
        // After the active duration without freeze: back to the base value.
        Assert.Equal(new Rect(0, 0, 10, 10), BoundsAt(document, animator, 13));
    }

    [Fact]
    public void Values_List_Interpolates_Per_Segment()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <rect width="10" height="10" fill="red">
                <animate attributeName="x" values="0;100;0" dur="10s"/>
              </rect>
            </svg>
            """);
        var animator = SvgAnimator.TryCreate(document)!;

        Assert.Equal(new Rect(50, 0, 10, 10), BoundsAt(document, animator, 2.5));
        Assert.Equal(new Rect(100, 0, 10, 10), BoundsAt(document, animator, 5));
        Assert.Equal(new Rect(50, 0, 10, 10), BoundsAt(document, animator, 7.5));
    }

    [Theory]
    [InlineData(0.0, 0x22, 0xd3, 0xee)]   // exactly the first value
    [InlineData(2.0, 0x8b, 0xa2, 0xd2)]   // halfway to the second value
    public void Color_Interpolation_Round_Trips_Through_The_Css_Parser(
        double seconds, byte r, byte g, byte b)
    {
        // Interpolated colors are written back as strings and re-parsed by the
        // compiler. CSS hex carries alpha last while Color.ToString carries it
        // first (and may emit known-color names), so use colors that are NOT
        // known names to pin the round trip.
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <rect id="a" width="10" height="10" fill="#22d3ee">
                <animate attributeName="fill" from="#22d3ee" to="#f472b6" dur="4s"/>
              </rect>
            </svg>
            """);
        var animator = SvgAnimator.TryCreate(document)!;
        var state = new SvgAnimationState();
        animator.Apply(TimeSpan.FromSeconds(seconds), state);

        var target = document.GetElementById("a")!;
        var sampled = state.Get(target, "fill");
        Assert.NotNull(sampled);
        Assert.True(SvgColor.TryParse(sampled!, out var color));
        Assert.Equal(255, color.A);
        Assert.Equal(r, color.R);
        Assert.Equal(g, color.G);
        Assert.Equal(b, color.B);
    }

    [Fact]
    public void Discrete_Animation_Steps()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <rect width="10" height="10" fill="red">
                <animate attributeName="x" values="0;100" dur="10s" calcMode="discrete"/>
              </rect>
            </svg>
            """);
        var animator = SvgAnimator.TryCreate(document)!;

        Assert.Equal(new Rect(0, 0, 10, 10), BoundsAt(document, animator, 4.9));
        Assert.Equal(new Rect(100, 0, 10, 10), BoundsAt(document, animator, 5.1));
    }

    [Fact]
    public void Repeat_Cycles_And_Freeze_Holds_The_End_Value()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <rect width="10" height="10" fill="red">
                <animate attributeName="x" from="0" to="100" dur="2s" repeatCount="2" fill="freeze"/>
              </rect>
            </svg>
            """);
        var animator = SvgAnimator.TryCreate(document)!;

        // Second iteration, half-way.
        Assert.Equal(new Rect(50, 0, 10, 10), BoundsAt(document, animator, 3));
        // Past the active duration: frozen at the end value.
        Assert.Equal(new Rect(100, 0, 10, 10), BoundsAt(document, animator, 10));
    }

    [Fact]
    public void Set_Applies_Only_During_Its_Active_Window()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect width="50" height="50" fill="red">
                <set attributeName="visibility" to="hidden" begin="1s" dur="2s"/>
              </rect>
            </svg>
            """);
        var animator = SvgAnimator.TryCreate(document)!;

        Assert.Equal(new Rect(0, 0, 50, 50), BoundsAt(document, animator, 0.5));
        // Hidden during the window — the dynamic visibility toggle.
        Assert.Equal(default, BoundsAt(document, animator, 2));
        Assert.Equal(new Rect(0, 0, 50, 50), BoundsAt(document, animator, 4));
    }

    [Fact]
    public void AnimateTransform_Translate_Moves_The_Subtree()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="200">
              <g>
                <animateTransform attributeName="transform" type="translate" from="0 0" to="100 50" dur="10s"/>
                <rect width="10" height="10" fill="red"/>
              </g>
            </svg>
            """);
        var animator = SvgAnimator.TryCreate(document)!;

        Assert.Equal(new Rect(0, 0, 10, 10), BoundsAt(document, animator, 0));
        Assert.Equal(new Rect(50, 25, 10, 10), BoundsAt(document, animator, 5));
    }

    [Fact]
    public void AnimateTransform_Scale_Freezes_At_The_End()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="200">
              <rect width="10" height="10" fill="red">
                <animateTransform attributeName="transform" type="scale" from="1" to="3" dur="2s" fill="freeze"/>
              </rect>
            </svg>
            """);
        var animator = SvgAnimator.TryCreate(document)!;

        Assert.Equal(new Rect(0, 0, 30, 30), BoundsAt(document, animator, 5));
    }

    [Fact]
    public void Color_Values_Interpolate_Componentwise()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect width="10" height="10" fill="red">
                <animate attributeName="fill" from="red" to="blue" dur="4s"/>
              </rect>
            </svg>
            """);
        var animator = SvgAnimator.TryCreate(document)!;
        var entry = animator.Entries[0];

        // Sampled values feed the compiler, so they must round-trip through
        // the CSS parser the compiler uses (not Avalonia's Color.TryParse,
        // whose 8-digit hex carries alpha first).
        Assert.True(SvgColor.TryParse(entry.Sample(TimeSpan.FromSeconds(2))!, out var mid));
        Assert.Equal(255, mid.A);
        Assert.Equal(128, mid.R);
        Assert.Equal(0, mid.G);
        Assert.Equal(128, mid.B);
    }

    [Fact]
    public void To_Animation_Interpolates_From_The_Declared_Base_Value()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <rect x="20" width="10" height="10" fill="red">
                <animate attributeName="x" to="100" dur="10s"/>
              </rect>
            </svg>
            """);
        var animator = SvgAnimator.TryCreate(document)!;

        Assert.Equal(new Rect(60, 0, 10, 10), BoundsAt(document, animator, 5));
    }

    [Fact]
    public void Shape_Color_Animations_Run_On_The_Paint_Channel()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect id="r" width="10" height="10" fill="red">
                <animate attributeName="fill" from="red" to="blue" dur="4s"/>
              </rect>
            </svg>
            """);
        var animator = SvgAnimator.TryCreate(document)!;

        Assert.False(animator.HasStructural);
        Assert.Contains((document.GetElementById("r")!, "fill"), animator.GetPaintTargets());
    }

    [Fact]
    public void Geometry_Animations_Are_Structural()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect width="10" height="10" fill="red">
                <animate attributeName="x" from="0" to="50" dur="1s"/>
              </rect>
            </svg>
            """);

        Assert.True(SvgAnimator.TryCreate(document)!.HasStructural);
    }

    [Fact]
    public void Bound_Paint_Brushes_Mutate_Without_Structural_Recompiles()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect id="r" width="10" height="10" fill="red">
                <animate attributeName="fill" from="red" to="blue" dur="4s"/>
              </rect>
            </svg>
            """);
        var animator = SvgAnimator.TryCreate(document)!;
        var rect = document.GetElementById("r")!;

        var brush = new SolidColorBrush(Colors.Red);
        animator.BindPaintBrushes(
            new System.Collections.Generic.Dictionary<(SvgElement, string), SolidColorBrush>
            {
                [(rect, "fill")] = brush,
            });

        // Paint-channel mutations never request a recompile.
        var state = new SvgAnimationState();
        Assert.False(animator.Apply(TimeSpan.FromSeconds(2), state));
        Assert.Equal(Color.FromRgb(128, 0, 128), brush.Color);

        // Deactivation (no freeze) restores the bind-time base color.
        Assert.False(animator.Apply(TimeSpan.FromSeconds(10), state));
        Assert.Equal(Colors.Red, brush.Color);
    }

    [Fact]
    public void Compile_Registers_Mutable_Brushes_For_Paint_Targets()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect id="r" width="10" height="10" fill="red">
                <animate attributeName="fill" from="red" to="blue" dur="4s"/>
              </rect>
            </svg>
            """);
        var animator = SvgAnimator.TryCreate(document)!;
        var rect = document.GetElementById("r")!;

        using var image = new SvgImage(document, compositor: null, animator.GetPaintTargets());

        Assert.NotNull(image.AnimatedBrushes);
        var brush = Assert.Contains((rect, "fill"), image.AnimatedBrushes!);
        Assert.Equal(Colors.Red, brush.Color);
    }

    [Fact]
    public void Animations_Inside_Shared_Containers_Are_Skipped()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <symbol id="s">
                <rect width="10" height="10" fill="red">
                  <animate attributeName="x" from="0" to="50" dur="1s"/>
                </rect>
              </symbol>
              <use href="#s" width="50" height="50"/>
            </svg>
            """);

        Assert.Null(SvgAnimator.TryCreate(document));
    }

    [Fact]
    public void Repeated_Apply_Reports_Change_Only_When_Values_Move()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect width="10" height="10" fill="red">
                <animate attributeName="x" values="0;100" dur="10s" calcMode="discrete"/>
              </rect>
            </svg>
            """);
        var animator = SvgAnimator.TryCreate(document)!;
        var state = new SvgAnimationState();

        Assert.True(animator.Apply(TimeSpan.FromSeconds(1), state));
        // Same discrete step: no structural change, no recompile needed.
        Assert.False(animator.Apply(TimeSpan.FromSeconds(2), state));
        Assert.True(animator.Apply(TimeSpan.FromSeconds(6), state));
    }
}
