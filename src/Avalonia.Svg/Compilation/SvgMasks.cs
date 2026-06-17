using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Media.Svg.Parsing;

namespace Avalonia.Media.Svg.Compilation;

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
    public static IDisposable? TryPush(
        DrawingContext context, SvgCompileContext compileContext, string id, Rect bounds)
    {
        if (compileContext.Document.GetElementById(id) is not { Name: "mask" } mask)
            return null;

        // Mask region; maskUnits default to objectBoundingBox with the spec's
        // -10% / 120% defaults, which are percentages in either units mode.
        var boxUnits = mask.GetAttribute("maskUnits") != "userSpaceOnUse";
        if (boxUnits && (bounds.Width <= 0 || bounds.Height <= 0))
            return context.PushOpacity(0);

        var x = GetCoordinate(mask, "x", -10, boxUnits, SvgLengthAxis.Horizontal, compileContext.Viewport);
        var y = GetCoordinate(mask, "y", -10, boxUnits, SvgLengthAxis.Vertical, compileContext.Viewport);
        var width = GetCoordinate(mask, "width", 120, boxUnits, SvgLengthAxis.Horizontal, compileContext.Viewport);
        var height = GetCoordinate(mask, "height", 120, boxUnits, SvgLengthAxis.Vertical, compileContext.Viewport);

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

        // Null means a circular mask reference; the reference is ignored and
        // the element renders unmasked.
        var recording = compileContext.GetSharedRecording(mask, out _);
        if (recording == null)
            return null;

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

        // A mask on the mask element itself chains: masking the mask's content
        // scales its luminance, which is the same as applying both masks to the
        // element. A chain that cycles back to this mask is dropped entirely —
        // the mask applies alone, matching browsers.
        IDisposable? chainedMask = null;
        if (mask.GetStyleOrAttribute("mask") is { } chainedValue
            && SvgClipPaths.TryParseUrlReference(chainedValue, out var chainedId)
            && !ChainIsCyclic(compileContext, mask))
        {
            chainedMask = TryPush(context, compileContext, chainedId, bounds);
        }

        // SVG masks are luminance by default; mask-type opts into alpha.
        var maskType = mask.GetStyleOrAttribute("mask-type") == "alpha" ? MaskType.Alpha : MaskType.Luminance;

        // color-interpolation=linearRGB computes the luminance on linearized
        // values: the content linearizes through the same transfer tables the
        // filter pipeline uses, then the backend's luma conversion applies.
        if (maskType == MaskType.Luminance && UsesLinearLuminance(mask))
        {
            var inner = recording;
            var contentBounds = inner.Bounds;
            recording = DrawingRecording.Create(ctx =>
            {
                using (ctx.PushLayer(new LayerOptions
                       {
                           Bounds = contentBounds,
                           Effect = SvgFilters.CreateLinearizeEffect(),
                       }))
                {
                    ctx.DrawRecording(inner);
                }
            });

            brush = new DrawingRecordingBrush(recording)
            {
                TileMode = TileMode.None,
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                SourceRect = new RelativeRect(sourceRect, RelativeUnit.Absolute),
                DestinationRect = RelativeRect.Fill,
            }.ToImmutable();
        }

        var maskState = context.PushOpacityMask(brush, region, maskType);
        return chainedMask == null ? maskState : new ChainedState(maskState, chainedMask);
    }

    /// <summary>
    /// Resolves the inherited <c>color-interpolation</c> property for the mask
    /// element; the initial value (and <c>auto</c>) selects sRGB.
    /// </summary>
    private static bool UsesLinearLuminance(SvgElement mask)
    {
        for (var element = mask; element != null; element = element.Parent)
        {
            switch (element.GetStyleOrAttribute("color-interpolation"))
            {
                case "sRGB":
                case "auto":
                    return false;
                case "linearRGB":
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Follows the <c>mask</c> attributes from <paramref name="mask"/> and
    /// reports whether the chain returns to an already visited mask.
    /// </summary>
    private static bool ChainIsCyclic(SvgCompileContext compileContext, SvgElement mask)
    {
        var visited = new HashSet<SvgElement> { mask };
        var current = mask;

        while (current.GetStyleOrAttribute("mask") is { } value
               && SvgClipPaths.TryParseUrlReference(value, out var id)
               && compileContext.Document.GetElementById(id) is { Name: "mask" } next)
        {
            if (!visited.Add(next))
                return true;

            current = next;
        }

        return false;
    }

    private sealed class ChainedState : IDisposable
    {
        private DrawingContext.PushedState _inner;
        private IDisposable? _outer;

        public ChainedState(DrawingContext.PushedState inner, IDisposable outer)
        {
            _inner = inner;
            _outer = outer;
        }

        public void Dispose()
        {
            // Pop in reverse push order: the mask itself, then its chained mask.
            _inner.Dispose();
            _outer?.Dispose();
            _outer = null;
        }
    }

    private static double GetCoordinate(
        SvgElement element, string attribute, double percentFallback,
        bool boxUnits, SvgLengthAxis axis, Size viewport)
    {
        var value = element.GetAttribute(attribute);
        if (value == null || !SvgLength.TryParse(value.AsSpan(), out var length))
        {
            // The spec defaults are percentages, sensitive to the units mode.
            length = new SvgLength(percentFallback, SvgLengthUnit.Percent);
        }

        if (boxUnits)
            return length.Unit == SvgLengthUnit.Percent ? length.Value / 100.0 : length.Value;

        return length.Resolve(axis, viewport);
    }
}
