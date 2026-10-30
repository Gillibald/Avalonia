using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace Avalonia.Skia;

partial class DrawingContextImpl
{
    /// <summary>
    /// What <see cref="PopLayer"/> needs to close a pushed layer: whether a real
    /// save-layer was opened and the folded opacity the layer replaced.
    /// </summary>
    private struct LayerState
    {
        public bool IsSaveLayer;
        public double OuterOpacity;
    }

    private readonly Stack<LayerState> _layerStack = new();

    public void PushLayer(LayerOptions options)
    {
        CheckLease();

        // A layer is composited as a whole, so it takes the folded opacity as
        // part of its composite alpha and everything inside it - draws, effect
        // filters, the backdrop, the mask - works at full opacity; applying it
        // again inside would fade twice.
        var opacity = options.EffectiveOpacity * _currentOpacity;
        var state = new LayerState { OuterOpacity = _currentOpacity };
        _currentOpacity = 1;

        var backdropBounds = options.BackdropBounds ?? options.Bounds;

        // A retained backdrop replaces the save-layer sample: draw the cached
        // result (refreshing it when the compositor granted a capture) and let
        // the subtree paint straight over it. Only sound while nothing else
        // about the layer would change compositing, and a mask has to cover the
        // content drawn after the backdrop, which only a real layer can do.
        if (options.BackdropEffect is { } backdrop
            && options.BackdropCache is { } cache
            && options.Effect is null
            && options.OpacityMask is null
            && options.EffectiveBlendMode == BitmapBlendingMode.SourceOver
            && TryPushCachedBackdropLayer(backdrop, cache, backdropBounds, options.BackdropClipImpl, opacity))
        {
            // A plain save keeps PopLayer symmetric without opening a layer;
            // with no layer to composite, draws inside fold the opacity again.
            Canvas.Save();
            _currentOpacity = state.OuterOpacity;
            _layerStack.Push(state);
            return;
        }

        var paint = SKPaintCache.Shared.Get();
        SKImageFilter? imageFilter = null;

        if (opacity < 1.0)
            paint.Color = new SKColor(255, 255, 255, (byte)Math.Round(opacity * 255));

        var blendMode = options.EffectiveBlendMode;
        if (blendMode != BitmapBlendingMode.SourceOver && blendMode != BitmapBlendingMode.Unspecified)
            paint.BlendMode = blendMode.ToSKBlendMode();

        if (options.Effect is { } effect)
            imageFilter = CreateEffect(effect, options.Bounds);

        // The mask applies to what the effect produced, so it joins the
        // composite filter after the effect: the layer's coverage is multiplied
        // by the mask's (DstIn), then opacity and blend mode composite it.
        if (options.OpacityMask is { } mask && options.Bounds is { } maskBounds)
        {
            var maskPaint = CreateOpacityMaskPaint(mask, maskBounds, options.OpacityMaskType);
            using var recorder = new SKPictureRecorder();
            var maskRect = maskBounds.ToSKRect();
            using (maskPaint)
                recorder.BeginRecording(maskRect).DrawRect(maskRect, maskPaint.Paint);
            // Return the paint wrapper's paint less the reset since the paint is already reset in the Dispose above.
            SKPaintCache.Shared.Return(maskPaint.Paint);

            using var maskPicture = recorder.EndRecording();
            using var maskFilter = SKImageFilter.CreatePicture(maskPicture, maskRect);
            var masked = SKImageFilter.CreateBlendMode(SKBlendMode.DstIn, imageFilter, maskFilter);
            imageFilter?.Dispose();
            imageFilter = masked;
        }

        if (imageFilter != null)
            paint.ImageFilter = imageFilter;

        SKImageFilter? backdropFilter = null;
        if (options.BackdropEffect is { } backdropEffect)
            backdropFilter = CreateEffect(backdropEffect, backdropBounds);

        if (backdropFilter != null)
        {
            // A backdrop filters the surface content the layer is opened over,
            // which the composite-time paint cannot express - it only exists on
            // the SaveLayerRec overload.
            var rec = new SKCanvasSaveLayerRec
            {
                Bounds = options.Bounds?.ToSKRect(),
                Paint = paint,
                Backdrop = backdropFilter
            };

            Canvas.SaveLayer(ref rec);
        }
        else if (options.Bounds.HasValue)
        {
            Canvas.SaveLayer(options.Bounds.Value.ToSKRect(), paint);
        }
        else
        {
            Canvas.SaveLayer(paint);
        }

        _saveLayerDepth++;
        state.IsSaveLayer = true;

        // The backdrop filled the whole layer, whose extent is the device-space
        // box around its bounds; cut it back to its own bounds and shape in
        // local space now, before any content is drawn, so the content is not
        // clipped. The edges match the retained draw's clip: hard for the
        // rectangle, antialiased for the geometry.
        if (backdropFilter != null)
        {
            if (backdropBounds is { } ownBounds)
                ClearOutside(ownBounds);
            if (options.BackdropClipImpl is GeometryImpl { FillPath: { } shape })
            {
                Canvas.Save();
                Canvas.ClipPath(shape, SKClipOperation.Difference, true);
                Canvas.Clear(SKColors.Transparent);
                Canvas.Restore();
            }
        }

        _layerStack.Push(state);

        imageFilter?.Dispose();
        backdropFilter?.Dispose();
        SKPaintCache.Shared.ReturnReset(paint);
    }

    private void ClearOutside(Rect bounds)
    {
        Canvas.Save();
        Canvas.ClipRect(bounds.ToSKRect(), SKClipOperation.Difference);
        Canvas.Clear(SKColors.Transparent);
        Canvas.Restore();
    }

    // PopLayer is shared with IDrawingContextImpl's existing PopLayer(); see
    // DrawingContextImpl.cs for the single implementation.

    /// <summary>
    /// The retained half of the <see cref="BackdropLayerCache"/> handshake: the
    /// retained payload as a surface (so granted sub-rects can be refreshed in
    /// place), its snapshot for drawing, which payload it holds, the device rect
    /// it belongs to, and the context it lives on so a device loss is
    /// detectable.
    /// </summary>
    private sealed class SkiaBackdropCacheState : IDisposable
    {
        public SkiaBackdropCacheState(GRContext? context, SKSurface surface, SKRectI deviceRect,
            BackdropCachePayload payload)
        {
            Context = context;
            Surface = surface;
            DeviceRect = deviceRect;
            Payload = payload;
        }

        public GRContext? Context { get; }
        public SKSurface Surface { get; }
        public SKImage? Snapshot { get; set; }
        public SKRectI DeviceRect { get; }
        public BackdropCachePayload Payload { get; }

        public void Dispose()
        {
            Snapshot?.Dispose();
            Surface.Dispose();
        }
    }

    private static void DropBackdropCache(BackdropLayerCache cache)
    {
        (cache.PlatformState as IDisposable)?.Dispose();
        cache.PlatformState = null;
        cache.IsValid = false;
    }

    private bool TryPushCachedBackdropLayer(IEffect effect, BackdropLayerCache cache, Rect? bounds,
        IGeometryImpl? shape, double opacity)
    {
        // Consume the grant either way: it certifies this frame's dirty region
        // and must not fire later on a frame whose region no longer covers the
        // refresh's input.
        var refresh = cache.RefreshRequested;
        cache.RefreshRequested = false;
        Rect[]? partialRects = null;
        if (cache.RefreshRects.Count > 0)
        {
            if (refresh && cache.IsValid)
                partialRects = cache.RefreshRects.ToArray();
            cache.RefreshRects.Clear();
        }

        Rect[]? ingestRects = null;
        if (cache.IngestRects.Count > 0)
        {
            if (refresh && cache.IsValid)
                ingestRects = cache.IngestRects.ToArray();
            cache.IngestRects.Clear();
        }

        // Nothing is retained for this payload: release what an earlier one
        // left behind and sample live.
        if (cache.Payload == BackdropCachePayload.None)
        {
            DropBackdropCache(cache);
            return false;
        }

        var state = cache.PlatformState as SkiaBackdropCacheState;
        if (state != null && (state.Context != _grContext || state.Payload != cache.Payload))
        {
            // The context the surface lived on is gone, or the pixels belong to
            // another payload. The target recreation that comes with a device
            // loss forces a full redraw, and a payload switch invalidates, so
            // the live path below stays correct, and the still-invalid slot
            // makes the compositor grant a refresh on the next frame that
            // touches this backdrop.
            DropBackdropCache(cache);
            state = null;
        }

        // The capture snapshots the base surface, but inside an active
        // save-layer the live sample reads that layer instead; and a canvas
        // without a surface has nothing to snapshot at all.
        if (Surface == null || _saveLayerDepth > 0 || bounds is not { } localBounds)
        {
            cache.IsValid = false;
            return false;
        }

        if (cache.IsValid && !refresh)
        {
            if (state is { Snapshot: not null })
            {
                DrawBackdropCache(state, effect, localBounds, shape, opacity);
                return true;
            }

            cache.IsValid = false; // valid without pixels: uncached in practice
            return false;
        }

        if (!refresh)
        {
            cache.IsValid = false;
            return false;
        }

        var ctm = Canvas.TotalMatrix;
        using (var dest = Surface.Snapshot())
        {
            var mapped = ctm.MapRect(localBounds.ToSKRect());
            if (cache.Payload == BackdropCachePayload.Input)
            {
                // The retained input reaches past the box by the filter's
                // reach, so the filter sees real content at the edges exactly
                // like the live sample does.
                var margin = BackdropInputMargin(effect, ctm);
                mapped.Inflate(margin, margin);
            }

            var deviceRect = new SKRectI(
                Math.Max(0, (int)Math.Floor(mapped.Left)),
                Math.Max(0, (int)Math.Floor(mapped.Top)),
                Math.Min(dest.Width, (int)Math.Ceiling(mapped.Right)),
                Math.Min(dest.Height, (int)Math.Ceiling(mapped.Bottom)));

            if (deviceRect.Width <= 0 || deviceRect.Height <= 0)
            {
                cache.IsValid = false;
                return false;
            }

            if (cache.IsValid)
            {
                // Partial refresh: only the granted sub-rects are certified
                // fresh, so nothing broader may be re-ingested. If the retained
                // surface no longer matches, fall to the live path; the next
                // touch escalates to a full grant.
                if (state == null || state.DeviceRect != deviceRect)
                {
                    cache.IsValid = false;
                    return false;
                }

                if (cache.Payload == BackdropCachePayload.Input && ingestRects != null)
                {
                    IngestBackdropInput(state, dest, ctm, ingestRects);
                }
                else if (cache.Payload == BackdropCachePayload.Output && partialRects != null)
                {
                    RefreshBackdropCachePartially(state, dest, effect, localBounds, ctm, partialRects);
                }
                else
                {
                    cache.IsValid = false;
                    return false;
                }
            }
            else
            {
                // Full refresh: reallocate the retained surface when the rect
                // or context moved on.
                if (state == null || state.DeviceRect != deviceRect)
                {
                    DropBackdropCache(cache);
                    var info = new SKImageInfo(deviceRect.Width, deviceRect.Height,
                        dest.ColorType, SKAlphaType.Premul, dest.ColorSpace);
                    var surface = _grContext != null
                        ? SKSurface.Create(_grContext, false, info)
                        : SKSurface.Create(info);
                    if (surface == null)
                        return false;

                    state = new SkiaBackdropCacheState(_grContext, surface, deviceRect, cache.Payload);
                    cache.PlatformState = state;
                }

                if (cache.Payload == BackdropCachePayload.Input)
                    IngestBackdropInput(state, dest, deviceRect);
                else
                    CaptureBackdropOutput(state, dest, effect, localBounds, ctm);
            }
        }

        // The destination snapshot is disposed before the target surface is
        // written again, so the composite below does not force a copy-on-write
        // of the whole target.
        state = (SkiaBackdropCacheState)cache.PlatformState!;
        state.Snapshot ??= state.Surface.Snapshot();
        cache.IsValid = true;

        DrawBackdropCache(state, effect, localBounds, shape, opacity);
        return true;
    }

    /// <summary>
    /// How far, in device pixels, the filter reads past the box: the effect's
    /// reach scaled by the largest axis scale of the transform, plus a pixel for
    /// the rounding of the box itself. The reach covers Skia's blur kernel
    /// support (three sigma) for every radius from four up and falls short by
    /// under half a pixel below that.
    /// </summary>
    private static int BackdropInputMargin(IEffect effect, SKMatrix ctm)
    {
        var padding = effect.GetEffectOutputPadding();
        var reach = Math.Max(Math.Max(padding.Left, padding.Right), Math.Max(padding.Top, padding.Bottom));
        var scale = Math.Max(
            Math.Sqrt(ctm.ScaleX * ctm.ScaleX + ctm.SkewY * ctm.SkewY),
            Math.Sqrt(ctm.SkewX * ctm.SkewX + ctm.ScaleY * ctm.ScaleY));
        return (int)Math.Ceiling(reach * scale) + 1;
    }

    private void CaptureBackdropOutput(SkiaBackdropCacheState state, SKImage dest, IEffect effect,
        Rect localBounds, SKMatrix ctm)
    {
        var deviceRect = state.DeviceRect;

        // The old snapshot goes before the write, or the write forces a
        // copy-on-write duplicate of the whole retained surface.
        state.Snapshot?.Dispose();
        state.Snapshot = null;

        var canvas = state.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        // A save-layer paint filter is applied with the matrix captured at the
        // save, so set (device offset after ctm) for the filter, then drop to
        // plain offset space inside the layer to land the snapshot pixels 1:1.
        // The whole destination is the source, like the live sample - the
        // filter reads only what it needs.
        var offset = SKMatrix.CreateTranslation(-deviceRect.Left, -deviceRect.Top);
        canvas.SetMatrix(SKMatrix.Concat(offset, ctm));

        using var filter = CreateEffect(effect, localBounds);
        var paint = SKPaintCache.Shared.Get();
        paint.ImageFilter = filter;
        canvas.SaveLayer(paint);
        canvas.SetMatrix(offset);
        canvas.DrawImage(dest, 0, 0);
        canvas.Restore();
        SKPaintCache.Shared.ReturnReset(paint);
    }

    private void RefreshBackdropCachePartially(SkiaBackdropCacheState state, SKImage dest, IEffect effect,
        Rect localBounds, SKMatrix ctm, Rect[] rects)
    {
        var deviceRect = state.DeviceRect;

        // The old snapshot goes before the write, or the write forces a
        // copy-on-write duplicate of the whole retained surface.
        state.Snapshot?.Dispose();
        state.Snapshot = null;

        var canvas = state.Surface.Canvas;
        var offset = SKMatrix.CreateTranslation(-deviceRect.Left, -deviceRect.Top);
        using var filter = CreateEffect(effect, localBounds);
        var paint = SKPaintCache.Shared.Get();
        paint.ImageFilter = filter;
        try
        {
            foreach (var local in rects)
            {
                if (ToDeviceSubRect(local, ctm, deviceRect) is not { } sub)
                    continue;

                // Same staging recipe as the full capture, confined to the
                // sub-rect: the filter reads its input margin beyond the clip
                // from the drawn destination, which the partial grant's region
                // covers. Clearing first stops the fresh output from blending
                // over the stale pixels.
                canvas.Save();
                canvas.ResetMatrix();
                canvas.ClipRect(SKRect.Create(
                    sub.Left - deviceRect.Left, sub.Top - deviceRect.Top, sub.Width, sub.Height));
                canvas.Clear(SKColors.Transparent);
                canvas.SetMatrix(SKMatrix.Concat(offset, ctm));
                canvas.SaveLayer(paint);
                canvas.SetMatrix(offset);
                canvas.DrawImage(dest, 0, 0);
                canvas.Restore();
                canvas.Restore();
            }
        }
        finally
        {
            SKPaintCache.Shared.ReturnReset(paint);
        }
    }

    /// <summary>
    /// Copies the granted sub-rects of the destination into a retained input.
    /// The grant certifies exactly these rects as painted this frame beneath
    /// the sample point; everything else retained is still current.
    /// </summary>
    private static void IngestBackdropInput(SkiaBackdropCacheState state, SKImage dest, SKMatrix ctm,
        Rect[] rects)
    {
        foreach (var local in rects)
        {
            if (ToDeviceSubRect(local, ctm, state.DeviceRect) is { } sub)
                IngestBackdropInput(state, dest, sub);
        }
    }

    private static void IngestBackdropInput(SkiaBackdropCacheState state, SKImage dest, SKRectI sub)
    {
        var deviceRect = state.DeviceRect;

        // The old snapshot goes before the write, or the write forces a
        // copy-on-write duplicate of the whole retained surface.
        state.Snapshot?.Dispose();
        state.Snapshot = null;

        // Src replaces the stale pixels outright, translucent ones included.
        var canvas = state.Surface.Canvas;
        var paint = SKPaintCache.Shared.Get();
        paint.BlendMode = SKBlendMode.Src;
        canvas.Save();
        canvas.ResetMatrix();
        canvas.ClipRect(SKRect.Create(
            sub.Left - deviceRect.Left, sub.Top - deviceRect.Top, sub.Width, sub.Height));
        canvas.DrawImage(dest, -deviceRect.Left, -deviceRect.Top, paint);
        canvas.Restore();
        SKPaintCache.Shared.ReturnReset(paint);
    }

    private static SKRectI? ToDeviceSubRect(Rect local, SKMatrix ctm, SKRectI deviceRect)
    {
        var mapped = ctm.MapRect(local.ToSKRect());
        var sub = new SKRectI(
            Math.Max(deviceRect.Left, (int)Math.Floor(mapped.Left)),
            Math.Max(deviceRect.Top, (int)Math.Floor(mapped.Top)),
            Math.Min(deviceRect.Right, (int)Math.Ceiling(mapped.Right)),
            Math.Min(deviceRect.Bottom, (int)Math.Ceiling(mapped.Bottom)));
        return sub.Width > 0 && sub.Height > 0 ? sub : null;
    }

    /// <summary>
    /// Composites the retained payload at <paramref name="opacity"/> inside the
    /// backdrop's bounds and shape. The payload itself is always captured at full
    /// opacity, so it stays valid when only the opacity changes.
    /// </summary>
    private void DrawBackdropCache(SkiaBackdropCacheState state, IEffect effect, Rect localBounds,
        IGeometryImpl? shape, double opacity)
    {
        if (state.Snapshot is not { } image)
            return;

        // Confine the draw the way the live backdrop is confined, in local space
        // before the matrix is dropped below.
        Canvas.Save();
        Canvas.ClipRect(localBounds.ToSKRect());
        if (shape is GeometryImpl { FillPath: { } path })
            Canvas.ClipPath(path, SKClipOperation.Intersect, true);

        // The image holds device pixels of this surface, so bypass the
        // transform to land it 1:1.
        if (state.Payload == BackdropCachePayload.Output)
        {
            var imagePaint = SKPaintCache.Shared.Get();
            if (opacity < 1.0)
                imagePaint.Color = new SKColor(255, 255, 255, (byte)Math.Round(opacity * 255));
            Canvas.ResetMatrix();
            Canvas.DrawImage(image, state.DeviceRect.Left, state.DeviceRect.Top, imagePaint);
            Canvas.Restore();
            SKPaintCache.Shared.ReturnReset(imagePaint);
            return;
        }

        // A retained input runs the filter on every draw, with the matrix at
        // the save so the filter works in the visual's space, over the input
        // landed 1:1 in device space. The layer's bounds follow the filter's
        // input needs past the active clip, so the clip only confines the
        // result, never what the filter reads.
        using var filter = CreateEffect(effect, localBounds);
        var paint = SKPaintCache.Shared.Get();
        paint.ImageFilter = filter;
        if (opacity < 1.0)
            paint.Color = new SKColor(255, 255, 255, (byte)Math.Round(opacity * 255));
        Canvas.SaveLayer(paint);
        Canvas.Save();
        Canvas.ResetMatrix();
        Canvas.DrawImage(image, state.DeviceRect.Left, state.DeviceRect.Top);
        Canvas.Restore();
        Canvas.Restore();
        Canvas.Restore();
        SKPaintCache.Shared.ReturnReset(paint);
    }
}
