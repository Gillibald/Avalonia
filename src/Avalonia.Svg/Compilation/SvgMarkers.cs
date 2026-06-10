using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Svg.Parsing;

namespace Avalonia.Svg.Compilation;

/// <summary>
/// Emits <c>marker-start</c>/<c>marker-mid</c>/<c>marker-end</c> placements for a
/// shape's vertices: each marker compiles once into a shared recording and every
/// placement is a single fused <c>DrawRecording(recording, matrix, Shared)</c>.
/// </summary>
internal static class SvgMarkers
{
    public static void Emit(
        DrawingContext context, SvgCompileContext compileContext, in SvgStyle style,
        IReadOnlyList<SvgPathVertex> vertices)
    {
        if (vertices.Count == 0)
            return;

        for (var i = 0; i < vertices.Count; i++)
        {
            var isStart = i == 0;
            var isEnd = i == vertices.Count - 1;
            var reference = isStart ? style.MarkerStart : isEnd ? style.MarkerEnd : style.MarkerMid;
            if (reference == null)
                continue;

            if (compileContext.Document.GetElementById(reference) is { Name: "marker" } marker)
                EmitMarker(context, compileContext, style, marker, vertices[i], isStart);
        }
    }

    private static void EmitMarker(
        DrawingContext context, SvgCompileContext compileContext, in SvgStyle style,
        SvgElement marker, in SvgPathVertex vertex, bool isStart)
    {
        // markerUnits default to strokeWidth: the marker scales with the pen.
        var scale = 1.0;
        if (marker.GetAttribute("markerUnits") != "userSpaceOnUse")
        {
            scale = style.StrokeWidth;
            if (scale <= 0)
                return;
        }

        var markerWidth = GetAttribute(marker, "markerWidth", 3);
        var markerHeight = GetAttribute(marker, "markerHeight", 3);
        if (markerWidth <= 0 || markerHeight <= 0)
            return;

        var angle = 0.0;
        var orient = marker.GetAttribute("orient");
        switch (orient)
        {
            case null:
                break;
            case "auto":
                angle = vertex.Angle;
                break;
            case "auto-start-reverse":
                angle = vertex.Angle + (isStart ? Math.PI : 0);
                break;
            default:
                if (TryParseAngle(orient, out var radians))
                    angle = radians;
                break;
        }

        var contentMatrix = Matrix.Identity;
        if (marker.GetAttribute("viewBox") is { } viewBoxValue
            && SvgViewBox.TryParse(viewBoxValue.AsSpan(), out var viewBox))
        {
            var preserveAspectRatio = SvgPreserveAspectRatio.Default;
            if (marker.GetAttribute("preserveAspectRatio") is { } par)
                SvgPreserveAspectRatio.TryParse(par.AsSpan(), out preserveAspectRatio);
            contentMatrix = preserveAspectRatio.ComputeTransform(viewBox, new Size(markerWidth, markerHeight));
        }

        // refX/refY are in viewBox coordinates; the marker is positioned so the
        // mapped reference point lands on the vertex.
        var reference = new Point(GetAttribute(marker, "refX", 0), GetAttribute(marker, "refY", 0))
            .Transform(contentMatrix);

        var position =
            Matrix.CreateTranslation(-reference.X, -reference.Y)
            * Matrix.CreateScale(scale, scale)
            * Matrix.CreateRotation(angle)
            * Matrix.CreateTranslation(vertex.Position.X, vertex.Position.Y);

        // Null means a circular marker reference; the reference is ignored.
        var recording = compileContext.GetSharedRecording(marker, out _);
        if (recording == null)
            return;

        // A marker establishes a viewport: content clips to it unless overflow
        // opts out ('visible'/'auto'; the initial value is hidden).
        var overflow = marker.GetStyleOrAttribute("overflow");
        if (overflow is "visible" or "auto")
        {
            context.DrawRecording(recording, contentMatrix * position, Avalonia.Media.DrawingRecordingOwnership.Shared);
        }
        else
        {
            using (context.PushTransform(position))
            using (context.PushClip(new Rect(0, 0, markerWidth, markerHeight)))
            {
                context.DrawRecording(recording, contentMatrix, Avalonia.Media.DrawingRecordingOwnership.Shared);
            }
        }
    }

    /// <summary>Parses a CSS angle: a number with an optional deg/grad/rad/turn metric (degrees by default).</summary>
    private static bool TryParseAngle(string value, out double radians)
    {
        radians = 0;
        var trimmed = value.Trim();
        var multiplier = Math.PI / 180.0;

        if (trimmed.EndsWith("deg", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.Substring(0, trimmed.Length - 3);
        }
        else if (trimmed.EndsWith("grad", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = Math.PI / 200.0;
            trimmed = trimmed.Substring(0, trimmed.Length - 4);
        }
        else if (trimmed.EndsWith("rad", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 1.0;
            trimmed = trimmed.Substring(0, trimmed.Length - 3);
        }
        else if (trimmed.EndsWith("turn", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = Math.PI * 2.0;
            trimmed = trimmed.Substring(0, trimmed.Length - 4);
        }

        if (!double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        radians = number * multiplier;
        return true;
    }

    private static double GetAttribute(SvgElement element, string name, double fallback)
    {
        if (element.GetAttribute(name) is { } value
            && SvgLength.TryParse(value.AsSpan(), out var length)
            && length.Unit != SvgLengthUnit.Percent)
        {
            return length.Resolve(SvgLengthAxis.Other, default);
        }

        return fallback;
    }
}
