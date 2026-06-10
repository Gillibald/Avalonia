using System;
using Avalonia.Media;
using Avalonia.Svg.Parsing;

namespace Avalonia.Svg.Compilation;

/// <summary>
/// Emits SVG <c>&lt;mask&gt;</c> references: the mask content compiles once into
/// a shared recording, is wrapped in an immutable content brush, and pushed via
/// <c>PushOpacityMask</c> — luminance by default, alpha when
/// <c>mask-type="alpha"</c>.
/// </summary>
internal static class SvgMasks
{
    /// <summary>
    /// Pushes the opacity mask for a <c>mask: url(#id)</c> reference. Returns
    /// null when the reference is invalid (the element renders unmasked).
    /// </summary>
    public static DrawingContext.PushedState? TryPush(
        DrawingContext context, SvgCompileContext compileContext, string id, Rect bounds)
    {
        if (compileContext.Document.GetElementById(id) is not { Name: "mask" } mask)
            return null;

        // Mask region; maskUnits default to objectBoundingBox with the spec's
        // -10% / 120% defaults.
        var boxUnits = mask.GetAttribute("maskUnits") != "userSpaceOnUse";
        if (boxUnits && (bounds.Width <= 0 || bounds.Height <= 0))
            return context.PushOpacity(0);

        var x = GetCoordinate(mask, "x", -0.1, boxUnits, SvgLengthAxis.Horizontal, compileContext.Viewport);
        var y = GetCoordinate(mask, "y", -0.1, boxUnits, SvgLengthAxis.Vertical, compileContext.Viewport);
        var width = GetCoordinate(mask, "width", 1.2, boxUnits, SvgLengthAxis.Horizontal, compileContext.Viewport);
        var height = GetCoordinate(mask, "height", 1.2, boxUnits, SvgLengthAxis.Vertical, compileContext.Viewport);

        var region = boxUnits
            ? new Rect(
                bounds.X + x * bounds.Width,
                bounds.Y + y * bounds.Height,
                width * bounds.Width,
                height * bounds.Height)
            : new Rect(x, y, width, height);

        if (region.Width <= 0 || region.Height <= 0)
            return context.PushOpacity(0);

        if (mask.Children.Count == 0)
            return context.PushOpacity(0);

        var recording = compileContext.GetSharedRecording(mask, out _);

        // The mask content paints 1:1 over the region: the source region in
        // content coordinates maps onto the region rect.
        Rect sourceRect;
        if (mask.GetAttribute("maskContentUnits") == "objectBoundingBox")
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return context.PushOpacity(0);

            sourceRect = new Rect(
                (region.X - bounds.X) / bounds.Width,
                (region.Y - bounds.Y) / bounds.Height,
                region.Width / bounds.Width,
                region.Height / bounds.Height);
        }
        else
        {
            sourceRect = region;
        }

        var brush = new DrawingRecordingBrush(recording)
        {
            TileMode = TileMode.None,
            Stretch = Stretch.Fill,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            SourceRect = new RelativeRect(sourceRect, RelativeUnit.Absolute),
            DestinationRect = RelativeRect.Fill,
        }.ToImmutable();

        // SVG masks are luminance by default; mask-type opts into alpha.
        var maskType = mask.GetStyleOrAttribute("mask-type") == "alpha" ? MaskType.Alpha : MaskType.Luminance;

        return context.PushOpacityMask(brush, region, maskType);
    }

    private static double GetCoordinate(
        SvgElement element, string attribute, double fallback,
        bool boxUnits, SvgLengthAxis axis, Size viewport)
    {
        var value = element.GetAttribute(attribute);
        if (value == null || !SvgLength.TryParse(value.AsSpan(), out var length))
            return fallback;

        if (boxUnits)
            return length.Unit == SvgLengthUnit.Percent ? length.Value / 100.0 : length.Value;

        return length.Resolve(axis, viewport);
    }
}
