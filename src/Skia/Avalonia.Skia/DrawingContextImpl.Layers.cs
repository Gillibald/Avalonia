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
    /// filtered destination as device pixels, the rect it belongs to, and the
    /// context the image lives on so a device loss is detectable.
    /// </summary>
    private sealed class SkiaBackdropCacheState : IDisposable
    {
        public SkiaBackdropCacheState(GRContext? context, SKImage image, SKRectI deviceRect)
        {
            Context = context;
            Image = image;
            DeviceRect = deviceRect;
        }

        public GRContext? Context { get; }
        public SKImage Image { get; }
        public SKRectI DeviceRect { get; }

        public void Dispose() => Image.Dispose();
    }

    private bool TryPushCachedBackdropLayer(IEffect effect, BackdropLayerCache cache, Rect? bounds)
    {
        // Consume the grant either way: it certifies this frame's dirty region
        // and must not fire later on a frame whose region no longer covers the
        // filter's input.
        var refresh = cache.RefreshRequested;
        cache.RefreshRequested = false;

        var state = cache.PlatformState as SkiaBackdropCacheState;
        if (state != null && state.Context != _grContext)
        {
            // The context the image lived on is gone. The target recreation
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

        if (cache.IsValid && state != null)
        {
            DrawBackdropCache(state);
            return true;
        }

        if (!refresh)
        {
            cache.IsValid = false;
            return false;
        }

        // Capture. The compositor widened this frame's dirty region to cover
        // the whole input area, so the destination is fresh everywhere the
        // filter reads. The result is built offscreen instead of through the
        // save-layer backdrop so it can be retained; drawing pure filter(dest)
        // src-over afterwards is exactly what restoring a backdrop-initialized
        // layer with an empty subtree contribution does.
        var ctm = Canvas.TotalMatrix;
        SkiaBackdropCacheState newState;
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

            var info = new SKImageInfo(deviceRect.Width, deviceRect.Height,
                dest.ColorType, SKAlphaType.Premul, dest.ColorSpace);
            using var scratch = _grContext != null
                ? SKSurface.Create(_grContext, false, info)
                : SKSurface.Create(info);
            if (scratch == null)
            {
                cache.IsValid = false;
                return false;
            }

            var scratchCanvas = scratch.Canvas;
            scratchCanvas.Clear(SKColors.Transparent);

            // A save-layer paint filter is applied with the matrix captured at
            // the save, so set (device offset after ctm) for the filter, then
            // drop to plain offset space inside the layer to land the snapshot
            // pixels 1:1. The whole destination is the source, like the live
            // sample - the filter reads only what it needs.
            var offset = SKMatrix.CreateTranslation(-deviceRect.Left, -deviceRect.Top);
            scratchCanvas.SetMatrix(SKMatrix.Concat(offset, ctm));

            using (var filter = CreateEffect(effect, localBounds))
            {
                var paint = SKPaintCache.Shared.Get();
                paint.ImageFilter = filter;
                scratchCanvas.SaveLayer(paint);
                scratchCanvas.SetMatrix(offset);
                scratchCanvas.DrawImage(dest, 0, 0);
                scratchCanvas.Restore();
                SKPaintCache.Shared.ReturnReset(paint);
            }

            newState = new SkiaBackdropCacheState(_grContext, scratch.Snapshot(), deviceRect);
        }

        // The destination snapshot is disposed before the surface is written
        // again, so drawing the image below does not force a copy-on-write of
        // the whole target.
        (cache.PlatformState as IDisposable)?.Dispose();
        cache.PlatformState = newState;
        cache.IsValid = true;

        DrawBackdropCache(newState);
        return true;
    }

    private void DrawBackdropCache(SkiaBackdropCacheState state)
    {
        // The image holds device pixels of this surface, so bypass the
        // transform to land it 1:1; the active clip - including a geometry clip
        // shaping the backdrop - still applies.
        Canvas.Save();
        Canvas.ResetMatrix();
        Canvas.DrawImage(state.Image, state.DeviceRect.Left, state.DeviceRect.Top);
        Canvas.Restore();
    }
}
