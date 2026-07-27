using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Svg.Compilation;
using Avalonia.Media.Svg.Parsing;

namespace Avalonia.Media.Svg.Animation;

/// <summary>What a lifted gradient timeline drives on the composition brush.</summary>
internal enum SvgGradientLiftTarget
{
    /// <summary>A stop's color.</summary>
    StopColor,
    /// <summary>A stop's offset.</summary>
    StopOffset,
    /// <summary>x1/y1 of a linear gradient.</summary>
    StartPoint,
    /// <summary>x2/y2 of a linear gradient.</summary>
    EndPoint,
    /// <summary>cx/cy of a radial gradient.</summary>
    Center,
    /// <summary>fx/fy of a radial gradient.</summary>
    GradientOrigin,
    /// <summary>r of a radial gradient (drives both radii).</summary>
    Radius,
    /// <summary>fr of a radial gradient.</summary>
    FocalRadius,
}

/// <summary>One server-side key-frame animation of a lifted gradient.</summary>
internal sealed class SvgGradientLiftAnimation
{
    public SvgGradientLiftAnimation(SvgAnimationEntry entry, SvgGradientLiftTarget target)
    {
        Entry = entry;
        Target = target;
    }

    /// <summary>The timing source; claimed when the lift starts.</summary>
    public SvgAnimationEntry Entry { get; }

    public SvgGradientLiftTarget Target { get; }

    /// <summary>The stop element index for stop targets.</summary>
    public int StopIndex { get; init; } = -1;

    /// <summary>Frames parallel to the entry's values, for color targets.</summary>
    public Color[]? Colors { get; init; }

    /// <summary>Frames parallel to the entry's values, for scalar targets.</summary>
    public double[]? Scalars { get; init; }

    /// <summary>Frames parallel to the entry's values, for point targets.</summary>
    public Point[]? Points { get; init; }
}

/// <summary>
/// A gradient definition whose timelines all lift to server-side key-frame
/// animations on one shared composition gradient brush. A definition lifts
/// all-or-nothing: a mix of lifted and inert timelines on one brush would
/// render an inconsistent subset of the authored motion.
/// </summary>
internal sealed class SvgGradientLift
{
    private SvgGradientLift(SvgElement gradient, bool objectBoundingBox)
    {
        Gradient = gradient;
        ObjectBoundingBox = objectBoundingBox;
    }

    public SvgElement Gradient { get; }

    /// <summary>Coordinate units: bounding-box fractions or user space.</summary>
    public bool ObjectBoundingBox { get; }

    public List<SvgGradientLiftAnimation> Animations { get; } = new();

    /// <summary>
    /// Plans the lift of one gradient definition, or returns null when any of
    /// its timelines cannot lift. Requires a self-contained definition (no
    /// href template chain, no transform-origin - both re-introduce per-use
    /// state), a statically resolvable brush of the matching kind
    /// (<paramref name="resolved"/> supplies the static components of
    /// half-animated point pairs), liftable timing on every entry, and
    /// supported target attributes: stop-color and offset on stops, the
    /// geometry attributes on the definition itself. Both members of an
    /// animated coordinate pair must share one timing so they zip into a
    /// single point animation.
    /// </summary>
    public static SvgGradientLift? TryPlan(
        SvgElement gradient,
        List<SvgAnimationEntry> entries,
        ImmutableGradientBrush resolved,
        Size viewport)
    {
        var radial = gradient.Name == "radialGradient";
        if (!radial && gradient.Name != "linearGradient")
            return null;

        if (gradient.Href != null || gradient.GetStyleOrAttribute("transform-origin") != null)
            return null;

        var objectBoundingBox = gradient.GetAttribute("gradientUnits") != "userSpaceOnUse";

        var stopElements = new List<SvgElement>();
        foreach (var child in gradient.Children)
        {
            if (child.Name == "stop")
                stopElements.Add(child);
        }

        // Stop indices must line up with the resolved stop list; a mismatch
        // means the resolver skipped or synthesized stops.
        if (stopElements.Count != resolved.GradientStops.Count)
            return null;

        var lift = new SvgGradientLift(gradient, objectBoundingBox);
        var geometry = new Dictionary<string, SvgAnimationEntry>();

        foreach (var entry in entries)
        {
            if (!SvgCompositionAnimation.HasLiftableTiming(entry))
                return null;

            if (entry.Target == gradient)
            {
                var supported = radial
                    ? entry.AttributeName is "cx" or "cy" or "r" or "fx" or "fy" or "fr"
                    : entry.AttributeName is "x1" or "y1" or "x2" or "y2";
                if (!supported)
                    return null;

                // Later entries on one attribute win, matching the sampled
                // channel's per-tick order; earlier ones are claimed unstarted.
                geometry[entry.AttributeName] = entry;
                continue;
            }

            var stopIndex = stopElements.IndexOf(entry.Target);
            if (stopIndex < 0)
                return null;

            switch (entry.AttributeName)
            {
                case "stop-color":
                {
                    var colors = new Color[entry.Values.Length];
                    for (var i = 0; i < entry.Values.Length; i++)
                    {
                        if (!SvgColor.TryParse(entry.Values[i], out colors[i]))
                            return null;
                    }

                    lift.Animations.Add(new SvgGradientLiftAnimation(entry, SvgGradientLiftTarget.StopColor)
                    {
                        StopIndex = stopIndex,
                        Colors = colors,
                    });
                    continue;
                }

                case "offset":
                {
                    var offsets = new double[entry.Values.Length];
                    for (var i = 0; i < entry.Values.Length; i++)
                    {
                        if (!TryParseOffset(entry.Values[i], out offsets[i]))
                            return null;
                    }

                    lift.Animations.Add(new SvgGradientLiftAnimation(entry, SvgGradientLiftTarget.StopOffset)
                    {
                        StopIndex = stopIndex,
                        Scalars = offsets,
                    });
                    continue;
                }

                default:
                    return null;
            }
        }

        if (radial)
        {
            var radialBrush = (ImmutableRadialGradientBrush)resolved;

            if (!TryPlanPoint(lift, geometry, "cx", "cy", SvgGradientLiftTarget.Center,
                    radialBrush.Center.Point, objectBoundingBox, viewport))
            {
                return null;
            }

            // An unspecified focal point follows the center, so its animation
            // rides the same frames.
            if (gradient.GetAttribute("fx") == null && gradient.GetAttribute("fy") == null)
            {
                foreach (var animation in lift.Animations.ToArray())
                {
                    if (animation.Target == SvgGradientLiftTarget.Center)
                    {
                        lift.Animations.Add(new SvgGradientLiftAnimation(animation.Entry, SvgGradientLiftTarget.GradientOrigin)
                        {
                            Points = animation.Points,
                        });
                    }
                }
            }
            else if (!TryPlanPoint(lift, geometry, "fx", "fy", SvgGradientLiftTarget.GradientOrigin,
                         radialBrush.GradientOrigin.Point, objectBoundingBox, viewport))
            {
                return null;
            }

            if (!TryPlanScalar(lift, geometry, "r", SvgGradientLiftTarget.Radius, objectBoundingBox, viewport))
                return null;
            if (!TryPlanScalar(lift, geometry, "fr", SvgGradientLiftTarget.FocalRadius, objectBoundingBox, viewport))
                return null;
        }
        else
        {
            var linearBrush = (ImmutableLinearGradientBrush)resolved;

            if (!TryPlanPoint(lift, geometry, "x1", "y1", SvgGradientLiftTarget.StartPoint,
                    linearBrush.StartPoint.Point, objectBoundingBox, viewport))
            {
                return null;
            }

            if (!TryPlanPoint(lift, geometry, "x2", "y2", SvgGradientLiftTarget.EndPoint,
                    linearBrush.EndPoint.Point, objectBoundingBox, viewport))
            {
                return null;
            }
        }

        return lift.Animations.Count > 0 ? lift : null;
    }

    private static bool TryPlanPoint(
        SvgGradientLift lift,
        Dictionary<string, SvgAnimationEntry> geometry,
        string xAttribute,
        string yAttribute,
        SvgGradientLiftTarget target,
        Point staticPoint,
        bool objectBoundingBox,
        Size viewport)
    {
        geometry.TryGetValue(xAttribute, out var xEntry);
        geometry.TryGetValue(yAttribute, out var yEntry);
        if (xEntry == null && yEntry == null)
            return true;

        if (xEntry != null && yEntry != null && !HaveIdenticalTiming(xEntry, yEntry))
            return false;

        var driver = xEntry ?? yEntry!;
        var points = new Point[driver.Values.Length];
        for (var i = 0; i < points.Length; i++)
        {
            var x = staticPoint.X;
            var y = staticPoint.Y;

            if (xEntry != null && !SvgPaintServers.TryParseCoordinate(
                    xEntry.Values[i], objectBoundingBox, SvgLengthAxis.Horizontal, viewport, out x))
            {
                return false;
            }

            if (yEntry != null && !SvgPaintServers.TryParseCoordinate(
                    yEntry.Values[i], objectBoundingBox, SvgLengthAxis.Vertical, viewport, out y))
            {
                return false;
            }

            points[i] = new Point(x, y);
        }

        lift.Animations.Add(new SvgGradientLiftAnimation(driver, target) { Points = points });

        // The zipped partner is claimed without its own animation.
        if (xEntry != null && yEntry != null)
            lift.Animations.Add(new SvgGradientLiftAnimation(yEntry, target) { Points = null });

        return true;
    }

    private static bool TryPlanScalar(
        SvgGradientLift lift,
        Dictionary<string, SvgAnimationEntry> geometry,
        string attribute,
        SvgGradientLiftTarget target,
        bool objectBoundingBox,
        Size viewport)
    {
        if (!geometry.TryGetValue(attribute, out var entry))
            return true;

        var scalars = new double[entry.Values.Length];
        for (var i = 0; i < scalars.Length; i++)
        {
            if (!SvgPaintServers.TryParseCoordinate(
                    entry.Values[i], objectBoundingBox, SvgLengthAxis.Other, viewport, out scalars[i])
                || scalars[i] < 0)
            {
                return false;
            }
        }

        lift.Animations.Add(new SvgGradientLiftAnimation(entry, target) { Scalars = scalars });
        return true;
    }

    private static bool HaveIdenticalTiming(SvgAnimationEntry a, SvgAnimationEntry b) =>
        a.Begin == b.Begin
        && a.Duration == b.Duration
        && a.RepeatCount.Equals(b.RepeatCount)
        && a.Freeze == b.Freeze
        && a.Discrete == b.Discrete
        && a.Values.Length == b.Values.Length;

    private static bool TryParseOffset(string value, out double result)
    {
        var trimmed = value.Trim();
        var percent = trimmed.EndsWith("%", StringComparison.Ordinal);
        if (percent)
            trimmed = trimmed.Substring(0, trimmed.Length - 1);

        if (!double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out result))
        {
            return false;
        }

        if (percent)
            result /= 100;

        result = Math.Clamp(result, 0, 1);
        return true;
    }
}
