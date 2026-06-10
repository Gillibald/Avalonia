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
                if (double.TryParse(orient, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var degrees))
                {
                    angle = Matrix.ToRadians(degrees);
                }

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

        var placement =
            contentMatrix
            * Matrix.CreateTranslation(-reference.X, -reference.Y)
            * Matrix.CreateScale(scale, scale)
            * Matrix.CreateRotation(angle)
            * Matrix.CreateTranslation(vertex.Position.X, vertex.Position.Y);

        var recording = compileContext.GetSharedRecording(marker);
        context.DrawRecording(recording, placement, Avalonia.Media.DrawingRecordingOwnership.Shared);
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
