using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Svg.Parsing;

namespace Avalonia.Svg.Compilation;

/// <summary>
/// Resolves <c>&lt;pattern&gt;</c> paint servers: the pattern content compiles
/// once into a shared <c>DrawingRecording</c>, is wrapped in a
/// <see cref="DrawingRecordingBrush"/>, and snapshotted to an immutable content
/// brush so it can be captured by immutable recordings.
/// </summary>
internal static class SvgPatterns
{
    private const int MaxReferenceChain = 8;

    public static IImmutableBrush? Resolve(SvgCompileContext context, SvgElement element, Rect bounds, double opacity)
    {
        var chain = BuildReferenceChain(context, element);

        // Tile rectangle; patternUnits default to objectBoundingBox.
        var boxUnits = GetChained(chain, "patternUnits") != "userSpaceOnUse";
        if (boxUnits && (bounds.Width <= 0 || bounds.Height <= 0))
            return null;

        var x = GetCoordinate(chain, "x", 0, boxUnits, SvgLengthAxis.Horizontal, context.Viewport);
        var y = GetCoordinate(chain, "y", 0, boxUnits, SvgLengthAxis.Vertical, context.Viewport);
        var width = GetCoordinate(chain, "width", 0, boxUnits, SvgLengthAxis.Horizontal, context.Viewport);
        var height = GetCoordinate(chain, "height", 0, boxUnits, SvgLengthAxis.Vertical, context.Viewport);
        if (width <= 0 || height <= 0)
            return null;

        // Content comes from the first pattern in the chain that has children.
        SvgElement? contentSource = null;
        foreach (var candidate in chain)
        {
            if (candidate.Children.Count > 0)
            {
                contentSource = candidate;
                break;
            }
        }

        if (contentSource == null)
            return null;

        // Null means a circular pattern reference; the paint is invalid.
        var recording = context.GetSharedRecording(contentSource, out _);
        if (recording == null)
            return null;

        // The source region selects the tile's extent in content coordinates.
        var tileWidthUser = boxUnits ? width * bounds.Width : width;
        var tileHeightUser = boxUnits ? height * bounds.Height : height;

        // viewBox maps content per preserveAspectRatio (xMidYMid meet default);
        // without one the tile content fills the tile 1:1.
        var stretch = Stretch.Fill;
        var alignmentX = AlignmentX.Left;
        var alignmentY = AlignmentY.Top;

        Rect sourceRect;
        if (GetChained(chain, "viewBox") is { } viewBoxValue
            && SvgViewBox.TryParse(viewBoxValue.AsSpan(), out var viewBox))
        {
            sourceRect = new Rect(viewBox.X, viewBox.Y, viewBox.Width, viewBox.Height);

            var preserveAspectRatio = SvgPreserveAspectRatio.Default;
            if (GetChained(chain, "preserveAspectRatio") is { } par)
                SvgPreserveAspectRatio.TryParse(par.AsSpan(), out preserveAspectRatio);

            (stretch, alignmentX, alignmentY) = preserveAspectRatio.ToTileBrushMapping();
        }
        else if (GetChained(chain, "patternContentUnits") == "objectBoundingBox")
        {
            // Content coordinates are bounding-box fractions.
            sourceRect = new Rect(0, 0, tileWidthUser / bounds.Width, tileHeightUser / bounds.Height);
        }
        else
        {
            sourceRect = new Rect(0, 0, tileWidthUser, tileHeightUser);
        }

        if (sourceRect.Width <= 0 || sourceRect.Height <= 0)
            return null;

        var destinationRect = boxUnits
            ? new RelativeRect(x, y, width, height, RelativeUnit.Relative)
            : new RelativeRect(x, y, width, height, RelativeUnit.Absolute);

        var brush = new DrawingRecordingBrush(recording)
        {
            TileMode = TileMode.Tile,
            Stretch = stretch,
            AlignmentX = alignmentX,
            AlignmentY = alignmentY,
            SourceRect = new RelativeRect(sourceRect, RelativeUnit.Absolute),
            DestinationRect = destinationRect,
            Opacity = opacity,
        };

        if (GetChained(chain, "patternTransform") is { } transformValue
            && SvgTransformParser.TryParse(transformValue.AsSpan(), out var transform)
            && !transform.IsIdentity)
        {
            // patternTransform (and its transform-origin) acts in user space,
            // anchored at the user origin. The brush shader composes the
            // transform about the target bounds position and then translates
            // by the resolved destination position, so conjugating by the sum
            // of the two yields the user-space behavior for both units modes.
            transform = SvgCompiler.ApplyTransformOrigin(
                element, transform, new Rect(context.Viewport), bounds);

            var destinationPosition = boxUnits
                ? new Point(bounds.X + x * bounds.Width, bounds.Y + y * bounds.Height)
                : new Point(x, y);
            var anchorX = bounds.X + destinationPosition.X;
            var anchorY = bounds.Y + destinationPosition.Y;
            if (anchorX != 0 || anchorY != 0)
            {
                transform = Matrix.CreateTranslation(anchorX, anchorY)
                            * transform
                            * Matrix.CreateTranslation(-anchorX, -anchorY);
            }

            brush.Transform = new ImmutableTransform(transform);
        }

        // Snapshot to an immutable content brush: the live DrawingRecordingBrush
        // is an AvaloniaObject and cannot be captured by an immutable recording.
        return brush.ToImmutable();
    }

    private static List<SvgElement> BuildReferenceChain(SvgCompileContext context, SvgElement element)
    {
        var chain = new List<SvgElement> { element };
        var current = element;

        for (var depth = 0; depth < MaxReferenceChain; depth++)
        {
            var href = current.Href;
            if (href is not { Length: > 1 } || href[0] != '#')
                break;

            var target = context.Document.GetElementById(href.Substring(1));
            if (target is not { Name: "pattern" } || chain.Contains(target))
                break;

            chain.Add(target);
            current = target;
        }

        return chain;
    }

    private static string? GetChained(List<SvgElement> chain, string attribute)
    {
        foreach (var element in chain)
        {
            if (element.GetAttribute(attribute) is { } value)
                return value;
        }

        return null;
    }

    private static double GetCoordinate(
        List<SvgElement> chain, string attribute, double fallback,
        bool boxUnits, SvgLengthAxis axis, Size viewport)
    {
        var value = GetChained(chain, attribute);
        if (value == null || !SvgLength.TryParse(value.AsSpan(), out var length))
            return fallback;

        if (boxUnits)
            return length.Unit == SvgLengthUnit.Percent ? length.Value / 100.0 : length.Value;

        return length.Resolve(axis, viewport);
    }
}
