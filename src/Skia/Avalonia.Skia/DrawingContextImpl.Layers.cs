using System;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace Avalonia.Skia;

partial class DrawingContextImpl
{
    public void PushLayer(LayerOptions options)
    {
        CheckLease();

        // A retained backdrop replaces the save-layer sample: draw the cached
        // filtered image (refreshing it when the compositor granted a capture)
        // and let the subtree paint straight over it. Only sound while nothing
        // else about the layer would change compositing.
        if (options.BackdropEffect is { } backdrop
            && options.BackdropCache is { } cache
            && options.Effect is null
            && options.EffectiveOpacity >= 1.0
            && options.EffectiveBlendMode == BitmapBlendingMode.SourceOver
            && TryPushCachedBackdropLayer(backdrop, cache, options.Bounds))
        {
            // A plain save keeps PopLayer symmetric without opening a layer.
            Canvas.Save();
            _pushedLayerIsSaveLayer.Push(false);
            return;
        }

        var paint = SKPaintCache.Shared.Get();
        SKImageFilter? imageFilter = null;

        var opacity = options.EffectiveOpacity;
        if (opacity < 1.0)
            paint.Color = new SKColor(255, 255, 255, (byte)Math.Round(opacity * 255));

        var blendMode = options.EffectiveBlendMode;
        if (blendMode != BitmapBlendingMode.SourceOver && blendMode != BitmapBlendingMode.Unspecified)
            paint.BlendMode = blendMode.ToSKBlendMode();

        if (options.Effect is { } effect)
        {
            imageFilter = CreateEffect(effect, options.Bounds);
            paint.ImageFilter = imageFilter;
        }

        SKImageFilter? backdropFilter = null;
        if (options.BackdropEffect is { } backdropEffect)
            backdropFilter = CreateEffect(backdropEffect, options.Bounds);

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
        _pushedLayerIsSaveLayer.Push(true);

        imageFilter?.Dispose();
        backdropFilter?.Dispose();
        SKPaintCache.Shared.ReturnReset(paint);
    }

    // PopLayer is shared with IDrawingContextImpl's existing PopLayer(); see
    // DrawingContextImpl.cs for the single implementation.

    /// <summary>
    /// The retained half of the <see cref="BackdropLayerCache"/> handshake: the
    /// filtered destination as a retained surface (so granted sub-rects can be
    /// re-filtered in place), its snapshot for drawing, the device rect it
    /// belongs to, and the context it lives on so a device loss is detectable.
    /// </summary>
    private sealed class SkiaBackdropCacheState : IDisposable
    {
        public SkiaBackdropCacheState(GRContext? context, SKSurface surface, SKRectI deviceRect)
        {
            Context = context;
            Surface = surface;
            DeviceRect = deviceRect;
        }

        public GRContext? Context { get; }
        public SKSurface Surface { get; }
        public SKImage? Snapshot { get; set; }
        public SKRectI DeviceRect { get; }

        public void Dispose()
        {
            Snapshot?.Dispose();
            Surface.Dispose();
        }
    }

    private bool TryPushCachedBackdropLayer(IEffect effect, BackdropLayerCache cache, Rect? bounds)
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

        var state = cache.PlatformState as SkiaBackdropCacheState;
        if (state != null && state.Context != _grContext)
        {
            // The context the surface lived on is gone. The target recreation
            // that comes with a device loss forces a full redraw, so the live
            // path below stays correct, and the still-invalid slot makes the
            // compositor grant a refresh on the next frame that touches this
            // backdrop.
            state.Dispose();
            cache.PlatformState = null;
            state = null;
            cache.IsValid = false;
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
                DrawBackdropCache(state);
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

        // A refresh grant. The compositor widened this frame's dirty region to
        // cover the refresh's input area - the whole padded area for a full
        // refresh, the granted sub-rects' neighborhoods for a partial one - so
        // the destination is fresh everywhere the staging reads. The result is
        // built offscreen instead of through the save-layer backdrop so it can
        // be retained; drawing pure filter(dest) src-over afterwards is exactly
        // what restoring a backdrop-initialized layer with an empty subtree
        // contribution does.
        var ctm = Canvas.TotalMatrix;
        using (var dest = Surface.Snapshot())
        {
            var mapped = ctm.MapRect(localBounds.ToSKRect());
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
                // Partial refresh: only the granted sub-rects' input is
                // certified fresh, so nothing broader may be re-ingested. If
                // the retained surface no longer matches, fall to the live
                // path; the next touch escalates to a full grant.
                if (partialRects == null || state == null || state.DeviceRect != deviceRect)
                {
                    cache.IsValid = false;
                    return false;
                }

                RefreshBackdropCachePartially(state, dest, effect, localBounds, ctm, partialRects);
            }
            else
            {
                // Full refresh: reallocate the retained surface when the rect
                // or context moved on.
                if (state == null || state.DeviceRect != deviceRect)
                {
                    (cache.PlatformState as IDisposable)?.Dispose();
                    cache.PlatformState = null;
                    var info = new SKImageInfo(deviceRect.Width, deviceRect.Height,
                        dest.ColorType, SKAlphaType.Premul, dest.ColorSpace);
                    var surface = _grContext != null
                        ? SKSurface.Create(_grContext, false, info)
                        : SKSurface.Create(info);
                    if (surface == null)
                    {
                        cache.IsValid = false;
                        return false;
                    }

                    state = new SkiaBackdropCacheState(_grContext, surface, deviceRect);
                    cache.PlatformState = state;
                }

                // The old snapshot goes before the write, or the write forces a
                // copy-on-write duplicate of the whole retained surface.
                state.Snapshot?.Dispose();
                state.Snapshot = null;

                var canvas = state.Surface.Canvas;
                canvas.Clear(SKColors.Transparent);

                // A save-layer paint filter is applied with the matrix captured
                // at the save, so set (device offset after ctm) for the filter,
                // then drop to plain offset space inside the layer to land the
                // snapshot pixels 1:1. The whole destination is the source, like
                // the live sample - the filter reads only what it needs.
                var offset = SKMatrix.CreateTranslation(-deviceRect.Left, -deviceRect.Top);
                canvas.SetMatrix(SKMatrix.Concat(offset, ctm));

                using (var filter = CreateEffect(effect, localBounds))
                {
                    var paint = SKPaintCache.Shared.Get();
                    paint.ImageFilter = filter;
                    canvas.SaveLayer(paint);
                    canvas.SetMatrix(offset);
                    canvas.DrawImage(dest, 0, 0);
                    canvas.Restore();
                    SKPaintCache.Shared.ReturnReset(paint);
                }
            }
        }

        // The destination snapshot is disposed before the target surface is
        // written again, so the composite below does not force a copy-on-write
        // of the whole target.
        state = (SkiaBackdropCacheState)cache.PlatformState!;
        state.Snapshot ??= state.Surface.Snapshot();
        cache.IsValid = true;

        DrawBackdropCache(state);
        return true;
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
                var mapped = ctm.MapRect(local.ToSKRect());
                var sub = new SKRectI(
                    Math.Max(deviceRect.Left, (int)Math.Floor(mapped.Left)),
                    Math.Max(deviceRect.Top, (int)Math.Floor(mapped.Top)),
                    Math.Min(deviceRect.Right, (int)Math.Ceiling(mapped.Right)),
                    Math.Min(deviceRect.Bottom, (int)Math.Ceiling(mapped.Bottom)));
                if (sub.Width <= 0 || sub.Height <= 0)
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

    private void DrawBackdropCache(SkiaBackdropCacheState state)
    {
        if (state.Snapshot is not { } image)
            return;

        // The image holds device pixels of this surface, so bypass the
        // transform to land it 1:1; the active clip - including a geometry clip
        // shaping the backdrop - still applies.
        Canvas.Save();
        Canvas.ResetMatrix();
        Canvas.DrawImage(image, state.DeviceRect.Left, state.DeviceRect.Top);
        Canvas.Restore();
    }
}
