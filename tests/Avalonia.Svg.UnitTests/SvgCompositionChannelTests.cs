using System;
using System.Linq;
using Avalonia.Svg.Animation;
using Xunit;

namespace Avalonia.Svg.UnitTests;

public class SvgCompositionChannelTests
{
    [Fact]
    public void Indefinite_Rotate_Classifies_With_Center()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <g>
                <animateTransform attributeName="transform" type="rotate"
                                  from="40 50 60" to="400 50 60" dur="6s" repeatCount="indefinite"/>
                <circle cx="80" cy="50" r="5"/>
              </g>
            </svg>
            """);
        var animator = SvgAnimator.TryCreate(document)!;

        Assert.True(SvgCompositionAnimation.TryClassify(animator.Entries[0], out var animation));
        Assert.Equal(SvgCompositionAnimationKind.Rotate, animation!.Kind);
        Assert.Equal(50, animation.CenterX);
        Assert.Equal(60, animation.CenterY);
        Assert.Equal(new[] { 40f, 400f }, new[] { animation.Frames[0][0], animation.Frames[1][0] });
    }

    [Theory]
    [InlineData("""<animateTransform attributeName="transform" type="rotate" from="0 1 1" to="360 2 2" dur="2s" repeatCount="indefinite"/>""")] // per-frame centers
    [InlineData("""<animateTransform attributeName="transform" type="rotate" from="0" to="360" dur="2s" repeatCount="2.5"/>""")] // fractional repeat
    [InlineData("""<animateTransform attributeName="transform" type="rotate" from="0" to="360" dur="2s"/>""")] // finite without freeze
    [InlineData("""<animateTransform attributeName="transform" type="rotate" from="0" to="360" dur="2s" calcMode="discrete" repeatCount="indefinite"/>""")] // discrete
    [InlineData("""<animate attributeName="x" from="0" to="50" dur="2s" repeatCount="indefinite"/>""")] // geometry
    public void Unsupported_Shapes_Stay_Structural(string animation)
    {
        using var document = SvgDocument.Parse(
            $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <rect width="10" height="10">{animation}</rect>
            </svg>
            """);
        var animator = SvgAnimator.TryCreate(document)!;

        Assert.False(SvgCompositionAnimation.TryClassify(animator.Entries[0], out _));
    }

    [Fact]
    public void Finite_Frozen_Repeat_Classifies()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <g>
                <animateTransform attributeName="transform" type="translate"
                                  from="0" to="50 10" dur="2s" repeatCount="3" fill="freeze"/>
                <rect width="10" height="10"/>
              </g>
            </svg>
            """);
        var animator = SvgAnimator.TryCreate(document)!;

        Assert.True(SvgCompositionAnimation.TryClassify(animator.Entries[0], out var animation));
        Assert.Equal(SvgCompositionAnimationKind.Translate, animation!.Kind);
        Assert.Equal(new[] { 0f, 0f }, animation.Frames[0]);
        Assert.Equal(new[] { 50f, 10f }, animation.Frames[1]);
    }

    [Fact]
    public void Partitioner_Builds_Slices_In_Document_Order()
    {
        // static run | composition rotate group (with nested moon) | structural
        // geometry animation | trailing static run.
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="200">
              <rect id="bg" width="200" height="200" fill="#111"/>
              <circle id="static1" cx="20" cy="20" r="5"/>
              <g id="orbit">
                <animateTransform attributeName="transform" type="rotate"
                                  from="0 100 100" to="360 100 100" dur="8s" repeatCount="indefinite"/>
                <circle id="planet" cx="150" cy="100" r="10"/>
                <g id="moonOrbit">
                  <animateTransform attributeName="transform" type="rotate"
                                    from="0 150 100" to="360 150 100" dur="2s" repeatCount="indefinite"/>
                  <circle id="moon" cx="165" cy="100" r="3"/>
                </g>
              </g>
              <circle id="pulse" cx="100" cy="100" r="20">
                <animate attributeName="r" values="20;24;20" dur="3s" repeatCount="indefinite"/>
              </circle>
              <text id="label" x="10" y="190">orrery</text>
            </svg>
            """);
        var animator = SvgAnimator.TryCreate(document)!;

        var root = SvgCompositionPartitioner.TryBuild(document, animator);
        Assert.NotNull(root);
        Assert.Equal(4, root!.Children.Count);

        var lead = Assert.IsType<SvgStaticSlice>(root.Children[0]);
        Assert.Equal(new[] { "bg", "static1" }, lead.Roots.Select(r => r.Id));

        var orbit = Assert.IsType<SvgCompositionGroup>(root.Children[1]);
        Assert.Equal("orbit", orbit.Element.Id);
        Assert.True(orbit.SuppressTransform);
        Assert.Single(orbit.Animations);

        // The planet stays as a static slice inside the orbit group; the moon
        // nests as its own composition group.
        Assert.Equal(2, orbit.Children.Count);
        var planetRun = Assert.IsType<SvgStaticSlice>(orbit.Children[0]);
        Assert.Equal("planet", Assert.Single(planetRun.Roots).Id);
        var moon = Assert.IsType<SvgCompositionGroup>(orbit.Children[1]);
        Assert.Equal("moonOrbit", moon.Element.Id);

        var pulse = Assert.IsType<SvgStructuralSlice>(root.Children[2]);
        Assert.Equal("pulse", pulse.Root.Id);

        var tail = Assert.IsType<SvgStaticSlice>(root.Children[3]);
        Assert.Equal("label", Assert.Single(tail.Roots).Id);
    }

    [Fact]
    public void Transformed_Plain_Wrapper_Moves_Its_Transform_To_The_Visual()
    {
        // The comet pattern: a static squash around an animated rotate must
        // compose squash-outside-rotate, so the wrapper transform leaves the
        // recordings.
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="200">
              <g id="squash" transform="scale(1 0.5)">
                <g id="cometOrbit">
                  <animateTransform attributeName="transform" type="rotate"
                                    from="0 100 100" to="360 100 100" dur="5s" repeatCount="indefinite"/>
                  <circle id="comet" cx="180" cy="100" r="4"/>
                </g>
              </g>
            </svg>
            """);
        var animator = SvgAnimator.TryCreate(document)!;

        var root = SvgCompositionPartitioner.TryBuild(document, animator)!;
        var wrapper = Assert.IsType<SvgCompositionGroup>(Assert.Single(root.Children));
        Assert.Equal("squash", wrapper.Element.Id);
        Assert.Equal("scale(1 0.5)", wrapper.StaticTransform);
        Assert.True(wrapper.SuppressTransform);
        Assert.Empty(wrapper.Animations);

        var orbit = Assert.IsType<SvgCompositionGroup>(Assert.Single(wrapper.Children));
        Assert.Equal("cometOrbit", orbit.Element.Id);
        Assert.Single(orbit.Animations);
    }

    [Fact]
    public void Paint_Only_Animations_Stay_In_Static_Slices()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <circle id="lamp" cx="20" cy="20" r="5" fill="#22d3ee">
                <animate attributeName="fill" from="#22d3ee" to="#f472b6" dur="2s" repeatCount="indefinite"/>
              </circle>
              <g id="spinner">
                <animateTransform attributeName="transform" type="rotate"
                                  from="0 50 50" to="360 50 50" dur="4s" repeatCount="indefinite"/>
                <rect x="45" y="20" width="10" height="10"/>
              </g>
            </svg>
            """);
        var animator = SvgAnimator.TryCreate(document)!;

        var root = SvgCompositionPartitioner.TryBuild(document, animator)!;
        Assert.Equal(2, root.Children.Count);
        var lampRun = Assert.IsType<SvgStaticSlice>(root.Children[0]);
        Assert.Equal("lamp", Assert.Single(lampRun.Roots).Id);
        Assert.IsType<SvgCompositionGroup>(root.Children[1]);
    }

    [Fact]
    public void Membership_Includes_Subtrees_And_Ancestors()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <g id="outer">
                <g id="inner">
                  <rect id="leaf" width="10" height="10"/>
                </g>
                <circle id="sibling" r="5"/>
              </g>
            </svg>
            """);

        var inner = document.GetElementById("inner")!;
        var membership = SvgCompositionPartitioner.BuildMembership(new[] { inner });

        Assert.Contains(document.GetElementById("leaf")!, membership);
        Assert.Contains(inner, membership);
        Assert.Contains(document.GetElementById("outer")!, membership);
        Assert.Contains(document.Root!, membership);
        Assert.DoesNotContain(document.GetElementById("sibling")!, membership);
    }

    [Fact]
    public void Slice_Compiles_Produce_The_Sliced_Content_Only()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="200">
              <rect id="bg" x="0" y="0" width="200" height="200" fill="#111"/>
              <g id="orbit">
                <animateTransform attributeName="transform" type="rotate"
                                  from="0 100 100" to="360 100 100" dur="8s" repeatCount="indefinite"/>
                <circle id="planet" cx="160" cy="100" r="10" fill="red"/>
              </g>
              <rect id="badge" x="20" y="20" width="30" height="30" fill="blue"/>
            </svg>
            """);
        var animator = SvgAnimator.TryCreate(document)!;
        var root = SvgCompositionPartitioner.TryBuild(document, animator)!;

        // Suppress the orbit transform like the host does, then compile each
        // slice through the membership filter.
        document.GetElementById("orbit")!.SetAnimatedValue("transform", "");

        static Rect CompileBounds(SvgDocument document, System.Collections.Generic.HashSet<SvgElement> membership)
        {
            var options = new Compilation.SvgCompileOptions { ElementFilter = membership.Contains };
            using var recording = Avalonia.Rendering.Composition.DrawingRecording.Create(
                ctx => Compilation.SvgCompiler.CompileDocument(
                    document, ctx, document.GetIntrinsicSize(), options));
            return recording.Bounds;
        }

        var lead = (SvgStaticSlice)root.Children[0];
        var leadBounds = CompileBounds(document, SvgCompositionPartitioner.BuildMembership(lead.Roots));
        Assert.Equal(new Rect(0, 0, 200, 200), leadBounds);

        var orbit = (SvgCompositionGroup)root.Children[1];
        var planetRun = (SvgStaticSlice)Assert.Single(orbit.Children);
        var planetBounds = CompileBounds(document, SvgCompositionPartitioner.BuildMembership(planetRun.Roots));
        Assert.Equal(new Rect(150, 90, 20, 20), planetBounds);

        var tail = (SvgStaticSlice)root.Children[2];
        var tailBounds = CompileBounds(document, SvgCompositionPartitioner.BuildMembership(tail.Roots));
        Assert.Equal(new Rect(20, 20, 30, 30), tailBounds);
    }

    [Fact]
    public void Claimed_Entries_Are_Skipped_By_Apply()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <g id="orbit">
                <animateTransform attributeName="transform" type="rotate"
                                  from="0 50 50" to="360 50 50" dur="4s" repeatCount="indefinite"/>
                <rect width="10" height="10"/>
              </g>
            </svg>
            """);
        var animator = SvgAnimator.TryCreate(document)!;
        var entry = animator.Entries[0];

        entry.Claimed = true;
        Assert.False(animator.Apply(TimeSpan.FromSeconds(1)));
        Assert.Null(document.GetElementById("orbit")!.GetAnimatedValue("transform"));
        Assert.False(animator.HasUnclaimedWork);

        entry.Claimed = false;
        Assert.True(animator.Apply(TimeSpan.FromSeconds(1)));
        Assert.NotNull(document.GetElementById("orbit")!.GetAnimatedValue("transform"));
    }
}
