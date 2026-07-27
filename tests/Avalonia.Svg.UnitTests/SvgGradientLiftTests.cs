using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Svg;
using Avalonia.Media.Svg.Animation;
using Avalonia.Media.Svg.Compilation;
using Xunit;

namespace Avalonia.Svg.UnitTests;

/// <summary>
/// Planning gradient timelines onto one shared composition gradient brush: a
/// definition lifts all-or-nothing, coordinate pairs zip under one timing, and
/// anything the plan cannot express stays inert.
/// </summary>
public class SvgGradientLiftTests
{
    private static readonly Size s_viewport = new(100, 100);

    private static (SvgDocument Document, SvgAnimator Animator, SvgElement Gradient, ImmutableGradientBrush Resolved)
        Load(string defs, string id = "g")
    {
        var document = SvgDocument.Parse(
            $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <defs>{defs}</defs>
              <rect width="100" height="100" fill="url(#{id})"/>
            </svg>
            """);
        var animator = SvgAnimator.TryCreate(document)!;
        var gradient = document.GetElementById(id)!;

        var context = new SvgCompileContext(document, s_viewport);
        var style = SvgStyle.CreateDefault(s_viewport);
        var resolved = (ImmutableGradientBrush)SvgPaintServers.Resolve(context, id, style, new Rect(0, 0, 1, 1))!;

        return (document, animator, gradient, resolved);
    }

    private static SvgGradientLift? Plan(
        (SvgDocument Document, SvgAnimator Animator, SvgElement Gradient, ImmutableGradientBrush Resolved) loaded)
    {
        var entries = new List<SvgAnimationEntry>();
        foreach (var entry in loaded.Animator.Entries)
        {
            if (loaded.Animator.GetGradientElement(entry) == loaded.Gradient)
                entries.Add(entry);
        }

        return SvgGradientLift.TryPlan(loaded.Gradient, entries, loaded.Resolved, s_viewport);
    }

    [Fact]
    public void Stop_Color_Timeline_Plans_A_Lift()
    {
        var loaded = Load(
            """
            <linearGradient id="g">
              <stop offset="0" stop-color="#ff0000">
                <animate attributeName="stop-color" values="#ff0000;#0000ff" dur="2s" repeatCount="indefinite"/>
              </stop>
              <stop offset="1" stop-color="#0000ff"/>
            </linearGradient>
            """);

        var plan = Plan(loaded);

        Assert.NotNull(plan);
        var animation = Assert.Single(plan!.Animations);
        Assert.Equal(SvgGradientLiftTarget.StopColor, animation.Target);
        Assert.Equal(0, animation.StopIndex);
        Assert.Equal(new[] { Color.FromRgb(0xff, 0, 0), Color.FromRgb(0, 0, 0xff) }, animation.Colors);
    }

    [Fact]
    public void Geometry_Pair_With_One_Timing_Zips_Into_A_Point_Animation()
    {
        var loaded = Load(
            """
            <linearGradient id="g">
              <animate attributeName="x1" values="0;0.4" dur="3s" repeatCount="indefinite"/>
              <animate attributeName="y1" values="0.1;0.6" dur="3s" repeatCount="indefinite"/>
              <stop offset="0" stop-color="#ff0000"/>
              <stop offset="1" stop-color="#0000ff"/>
            </linearGradient>
            """);

        var plan = Plan(loaded);

        Assert.NotNull(plan);
        var driver = plan!.Animations.Single(a => a.Points != null);
        Assert.Equal(SvgGradientLiftTarget.StartPoint, driver.Target);
        Assert.Equal(new[] { new Point(0, 0.1), new Point(0.4, 0.6) }, driver.Points);
        // The zipped partner is claimed without frames of its own.
        Assert.Equal(2, plan.Animations.Count);
    }

    [Fact]
    public void Geometry_Pair_With_Different_Timing_Stays_Inert()
    {
        var loaded = Load(
            """
            <linearGradient id="g">
              <animate attributeName="x1" values="0;0.4" dur="3s" repeatCount="indefinite"/>
              <animate attributeName="y1" values="0.1;0.6" dur="5s" repeatCount="indefinite"/>
              <stop offset="0" stop-color="#ff0000"/>
              <stop offset="1" stop-color="#0000ff"/>
            </linearGradient>
            """);

        Assert.Null(Plan(loaded));
    }

    [Fact]
    public void Href_Template_Chains_Stay_Inert()
    {
        var loaded = Load(
            """
            <linearGradient id="t">
              <stop offset="0" stop-color="#ff0000"/>
              <stop offset="1" stop-color="#0000ff"/>
            </linearGradient>
            <linearGradient id="g" href="#t">
              <stop offset="0" stop-color="#ff0000">
                <animate attributeName="stop-color" values="#ff0000;#0000ff" dur="2s" repeatCount="indefinite"/>
              </stop>
              <stop offset="1" stop-color="#0000ff"/>
            </linearGradient>
            """);

        Assert.Null(Plan(loaded));
    }

    [Fact]
    public void Unspecified_Focal_Point_Follows_An_Animated_Center()
    {
        var loaded = Load(
            """
            <radialGradient id="g">
              <animate attributeName="cx" values="0.3;0.7" dur="3s" repeatCount="indefinite"/>
              <stop offset="0" stop-color="#ff0000"/>
              <stop offset="1" stop-color="#0000ff"/>
            </radialGradient>
            """);

        var plan = Plan(loaded);

        Assert.NotNull(plan);
        var center = plan!.Animations.Single(a => a.Target == SvgGradientLiftTarget.Center);
        var origin = plan.Animations.Single(a => a.Target == SvgGradientLiftTarget.GradientOrigin);
        Assert.Equal(center.Points, origin.Points);
        // cy stays at its static half-box default while cx animates.
        Assert.Equal(new[] { new Point(0.3, 0.5), new Point(0.7, 0.5) }, center.Points);
    }

    [Fact]
    public void Unsupported_Stop_Attribute_Stays_Inert()
    {
        var loaded = Load(
            """
            <linearGradient id="g">
              <stop offset="0" stop-color="#ff0000">
                <animate attributeName="stop-opacity" values="1;0.2" dur="2s" repeatCount="indefinite"/>
              </stop>
              <stop offset="1" stop-color="#0000ff"/>
            </linearGradient>
            """);

        Assert.Null(Plan(loaded));
    }

    [Fact]
    public void UserSpace_Geometry_Frames_Resolve_Against_The_Viewport()
    {
        var loaded = Load(
            """
            <linearGradient id="g" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2="100" y2="0">
              <animate attributeName="x2" values="50%;100%" dur="3s" repeatCount="indefinite"/>
              <stop offset="0" stop-color="#ff0000"/>
              <stop offset="1" stop-color="#0000ff"/>
            </linearGradient>
            """);

        var plan = Plan(loaded);

        Assert.NotNull(plan);
        var end = plan!.Animations.Single(a => a.Target == SvgGradientLiftTarget.EndPoint);
        Assert.Equal(new[] { new Point(50, 0), new Point(100, 0) }, end.Points);
    }
}
