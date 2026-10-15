using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Avalonia.Skia
{
    internal partial class DrawingContextImpl
    {
        // Skia treats a device rectangle edge this close to a whole pixel as lying on it.
        private const double PixelAlignmentTolerance = 1e-3;

        // Contexts are made per frame, so the clip stack comes from a per-thread pool and keeps
        // the depth the deepest frame needed, instead of each context allocating and regrowing
        // its own; a context made while another is drawn on the thread rents another.
        [ThreadStatic]
        private static Stack<ClipLevel[]>? t_clipStacks;

        [ThreadStatic]
        private static SKPoint[]? t_clipRadii;

        private ClipLevel[] _clipLevels = t_clipStacks is { Count: > 0 } pool ? pool.Pop() : new ClipLevel[8];
        private int _clipDepth;

        // The innermost clips not yet applied to the canvas. They are always the top of the
        // stack: applying one applies every clip below it first.
        private int _deferredClips;

        // The shape clips on the stack, applied or deferred.
        private int _shapeClips;

        // The transformed run being drawn, and the transform it is drawn under, whose ink a turned
        // rectangle clip is fitted against in its own frame.
        private Avalonia.Media.Fonts.Rasterization.ManagedGlyphRunImpl? _drawingRun;
        private Matrix _drawingRunTransform;

        /// <summary>
        /// Whether pixel-aligned rectangle clips wait to be applied to the canvas until something
        /// other than a batched glyph run draws under them. Glyph runs are trimmed to them
        /// instead, which is exact for sprites drawn 1:1, so pushing and popping such a clip
        /// leaves the pending glyph batches alone and rows of text that each clip to their
        /// bounds draw in one atlas call.
        /// </summary>
        private bool DefersClips => _grContext is not null && BatchesGlyphAtlasDraws;

        /// <summary>
        /// Records a pixel-aligned rectangle clip without applying it to the canvas. Returns
        /// <c>false</c> when the clip is of another kind or this context applies every clip.
        /// </summary>
        private bool TryDeferRectClip(Rect clip)
        {
            // The clip must restore the canvas transform it was pushed under when it is popped,
            // as the canvas restores it, so a transform only known to the canvas applies the clip.
            if (!DefersClips || _currentTransform is not { } transform)
            {
                return false;
            }

            var level = CreateRectClipLevel(clip, ToDevice(transform));

            if (level.Kind != GlyphRunClipKind.PixelAlignedRect)
            {
                return false;
            }

            CheckLease();

            level.Deferred = true;
            level.Local = clip.ToSKRect();
            level.Transform = transform;

            TrackClipLevel(level);
            _deferredClips++;

            return true;
        }

        /// <summary>
        /// Records an antialiased rounded rectangle clip, or one with edges between pixels,
        /// without applying it to the canvas. Returns <c>false</c> when this context applies
        /// every clip or the transform rotates or skews it.
        /// </summary>
        /// <remarks>
        /// Runs drawn under the clip stay pending only while they keep a pixel clear of its edges
        /// and of the squares its corners round off (<see cref="ClipLevel.Fits"/>). There the
        /// clip covers every pixel fully: Skia skips a clip that contains the draw, and its
        /// antialiased coverage of a pixel centre at least one and a half pixels inside an edge
        /// saturates to one, however its shader rounds. Such runs draw the same pixels with the
        /// clip as without, so the clip need not be on the canvas when they are drawn.
        /// </remarks>
        private bool TryDeferShapeClip(RoundedRect clip)
        {
            if (!DefersClips || CanvasTransform() is not { } transform)
            {
                return false;
            }

            var toDevice = ToDevice(transform);

            if (toDevice.M12 != 0 || toDevice.M21 != 0)
            {
                return !clip.IsRounded && TryDeferTurnedRectClip(clip.Rect, transform, toDevice, aliased: false);
            }

            if (toDevice.ContainsPerspective())
            {
                return false;
            }

            var device = clip.Rect.TransformToAABB(toDevice);
            var radiusX = Math.Max(Math.Max(clip.RadiiTopLeft.X, clip.RadiiTopRight.X),
                Math.Max(clip.RadiiBottomRight.X, clip.RadiiBottomLeft.X)) * Math.Abs(toDevice.M11);
            var radiusY = Math.Max(Math.Max(clip.RadiiTopLeft.Y, clip.RadiiTopRight.Y),
                Math.Max(clip.RadiiBottomRight.Y, clip.RadiiBottomLeft.Y)) * Math.Abs(toDevice.M22);

            // Also rejects NaN.
            if (!(Math.Abs(device.Left) <= 1 << 24 && Math.Abs(device.Top) <= 1 << 24 &&
                  Math.Abs(device.Right) <= 1 << 24 && Math.Abs(device.Bottom) <= 1 << 24 &&
                  radiusX >= 0 && radiusY >= 0))
            {
                return false;
            }

            CheckLease();

            var level = new ClipLevel(GlyphRunClipKind.RoundedRectOrGeometry, device.ToSKRect(), null)
            {
                Deferred = true,
                Transform = transform,
                IsShape = true,
                Shape = clip,
                Across = new SKRect((float)(device.Left + 1), (float)(device.Top + radiusY + 1),
                    (float)(device.Right - 1), (float)(device.Bottom - radiusY - 1)),
                Down = new SKRect((float)(device.Left + radiusX + 1), (float)(device.Top + 1),
                    (float)(device.Right - radiusX - 1), (float)(device.Bottom - 1)),
            };

            TrackClipLevel(level);
            _deferredClips++;

            return true;
        }

        /// <summary>
        /// Records a rectangle clip that the transform rotates or skews without applying it to the
        /// canvas. Returns <c>false</c> when this context applies every clip.
        /// </summary>
        private bool TryDeferTurnedRectClip(Rect clip)
        {
            if (!DefersClips || CanvasTransform() is not { } transform)
            {
                return false;
            }

            var toDevice = ToDevice(transform);

            return (toDevice.M12 != 0 || toDevice.M21 != 0) &&
                   TryDeferTurnedRectClip(clip, transform, toDevice, aliased: true);
        }

        /// <summary>
        /// Records <paramref name="clip"/>, turned by <paramref name="toDevice"/>, as a deferred
        /// shape clip, applied antialiased or, with <paramref name="aliased"/>, as a plain
        /// rectangle clip when something needs it on the canvas.
        /// </summary>
        /// <remarks>
        /// Runs drawn under the clip stay pending only while their device bounds keep a pixel and a
        /// half inside each of its four edges (<see cref="ClipEdges"/>). Every pixel centre of the
        /// bounds then lies that far inside, since a distance to a line is least at a corner of a
        /// rectangle, and there the clip covers the pixel fully whether it is antialiased or not.
        /// Such runs draw the same pixels with the clip as without.
        /// </remarks>
        private bool TryDeferTurnedRectClip(Rect clip, Matrix transform, Matrix toDevice, bool aliased)
        {
            if (toDevice.ContainsPerspective() ||
                !ClipEdges.TryCreate(clip, toDevice, out var edges, out var device))
            {
                return false;
            }

            CheckLease();

            var level = new ClipLevel(GlyphRunClipKind.Transformed, device, null)
            {
                Deferred = true,
                Transform = transform,
                IsShape = true,
                Shape = new RoundedRect(clip),
                Local = clip.ToSKRect(),
                Aliased = aliased,
                HasEdges = true,
                Edges = edges,
            };

            TrackClipLevel(level);
            _deferredClips++;

            return true;
        }

        /// <summary>
        /// The transform the canvas draws under, read back from the canvas when only the canvas
        /// knows it, such as after a restore. A clip deferred under it restores it when popped,
        /// which is exact for a 2D canvas matrix; a matrix with depth terms gives <c>null</c>.
        /// </summary>
        private Matrix? CanvasTransform()
        {
            if (_currentTransform is { } current)
            {
                return current;
            }

            var m = Canvas.TotalMatrix44;

            if (m.M02 != 0 || m.M12 != 0 || m.M20 != 0 || m.M21 != 0 || m.M22 != 1 || m.M23 != 0 || m.M32 != 0)
            {
                return null;
            }

            return Transform;
        }

        /// <summary>
        /// Before a run covering <paramref name="bounds"/> in device pixels is batched: marks the
        /// shape clips it does not fit, so they draw the pending runs when popped, and when one of
        /// them is still deferred, draws the pending runs and applies the deferred clips, so the
        /// run is batched under clips on the canvas. A turned rectangle clip that the run's device
        /// bounds reach past may still fit the run's ink in its own frame
        /// (<see cref="InkFitsTurnedClip"/>).
        /// </summary>
        private void AdmitToShapeClips(in SKRect bounds)
        {
            if (_shapeClips == 0)
            {
                return;
            }

            var applyDeferred = false;

            for (var i = 0; i < _clipDepth; i++)
            {
                ref var level = ref _clipLevels[i];

                if (level.IsShape && !level.Fits(bounds) && !InkFitsTurnedClip(level))
                {
                    level.Unfit = true;
                    applyDeferred |= level.Deferred;
                }
            }

            if (applyDeferred)
            {
                FlushGlyphBatch(GlyphBatchFlushReason.Clip);
            }
        }

        /// <summary>
        /// Whether nothing drawn inside <paramref name="deviceBounds"/> shows: it misses the canvas
        /// clip's device bounds, a deferred clip's device rectangle, or every rectangle of a region
        /// clip on the stack.
        /// </summary>
        bool Avalonia.Media.Fonts.Rasterization.ITransformedGlyphContext.ClipsAway(Rect deviceBounds)
        {
            var rect = new SKRect((float)deviceBounds.Left, (float)deviceBounds.Top, (float)deviceBounds.Right,
                (float)deviceBounds.Bottom);

            // Also rejects NaN.
            if (!(Math.Abs(rect.Left) <= 1 << 24 && Math.Abs(rect.Top) <= 1 << 24 &&
                  Math.Abs(rect.Right) <= 1 << 24 && Math.Abs(rect.Bottom) <= 1 << 24))
            {
                return false;
            }

            if (!SKRect.Create(Canvas.DeviceClipBounds.Left, Canvas.DeviceClipBounds.Top,
                    Canvas.DeviceClipBounds.Width, Canvas.DeviceClipBounds.Height).IntersectsWith(rect))
            {
                return true;
            }

            var pixels = new SKRectI((int)Math.Floor(rect.Left), (int)Math.Floor(rect.Top), (int)Math.Ceiling(rect.Right),
                (int)Math.Ceiling(rect.Bottom));

            for (var i = 0; i < _clipDepth; i++)
            {
                ref readonly var level = ref _clipLevels[i];

                if (level.Region is { } region ? !region.Intersects(pixels) :
                        level.Deferred && !level.Device.IntersectsWith(rect))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether the ink of the transformed run being drawn keeps clear of the turned rectangle
        /// clip <paramref name="level"/>, measured in the clip's own frame: the run is drawn under
        /// the transform the clip was pushed under, and its ink, grown by
        /// <see cref="InkClearance"/> device pixels at the transform's smallest scale, lies inside the
        /// clip's rectangle.
        /// </summary>
        /// <remarks>
        /// A pixel the run inks overlaps its ink box mapped to the device, so its centre lies within
        /// half a diagonal of it. Grown by <see cref="InkClearance"/>, the box keeps every such centre
        /// at least a pixel and a half inside the clip's edges, where the clip covers the pixel
        /// fully, antialiased or not, so the run draws the same pixels without the clip.
        /// </remarks>
        private bool InkFitsTurnedClip(in ClipLevel level)
        {
            if (!level.HasEdges || _drawingRun is not { } run || level.Transform != _drawingRunTransform ||
                run.InkBoundsWithStrikes is not { } ink)
            {
                return false;
            }

            var device = ToDevice(level.Transform);
            var sum = device.M11 * device.M11 + device.M12 * device.M12 + device.M21 * device.M21 +
                      device.M22 * device.M22;
            var determinant = device.M11 * device.M22 - device.M12 * device.M21;
            var smallest = Math.Sqrt(Math.Max(0, (sum - Math.Sqrt(Math.Max(0, sum * sum - 4 * determinant * determinant))) / 2));

            if (!(smallest > 0))
            {
                return false;
            }

            var grown = ink.Inflate(InkClearance / smallest);
            var clip = level.Local;

            return clip.Left <= grown.Left && clip.Top <= grown.Top && grown.Right <= clip.Right &&
                   grown.Bottom <= clip.Bottom;
        }

        /// <summary>
        /// How far, in device pixels, a run's ink box keeps from a turned clip's edges to fit it:
        /// a pixel and a half for full coverage, plus half a pixel diagonal from an inked pixel's
        /// centre to the box, with room for the rounding of pen phases.
        /// </summary>
        private const double InkClearance = 2.5;

        /// <summary>
        /// Whether applying the deferred clips to the canvas leaves every pending run's pixels as
        /// they are: each run lies inside the deferred rectangles and fits the deferred shapes.
        /// </summary>
        private bool PendingRunsUnaffectedByDeferredClips()
        {
            for (var i = _clipDepth - _deferredClips; i < _clipDepth; i++)
            {
                ref readonly var level = ref _clipLevels[i];

                if (level.IsShape ? !PendingRunsFit(level) : !PendingRunsLieInside(level.Device))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Whether every pending glyph run, grayscale or subpixel, fits the shape clip <paramref name="level"/>.</summary>
        private bool PendingRunsFit(in ClipLevel level)
        {
            for (var i = 0; i < _pendingBatchCount; i++)
            {
                if (!level.Fits(_pendingBatches![i].Bounds))
                {
                    return false;
                }
            }

            for (var i = 0; i < _lcdBatchCount; i++)
            {
                if (!level.Fits(_lcdBatch!.Bounds[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Applies the deferred clips to the canvas, in the order they were pushed, each under
        /// the transform it was pushed under; the current transform stays.
        /// </summary>
        private void ApplyDeferredClips()
        {
            if (_deferredClips == 0)
            {
                return;
            }

            var current = _currentTransform;
            var currentMatrix = current is null ? Canvas.TotalMatrix44 : default;

            for (var i = _clipDepth - _deferredClips; i < _clipDepth; i++)
            {
                ref var level = ref _clipLevels[i];

                SetCanvasTransform(level.Transform, default);
                Canvas.Save();
                ClipCanvas(level);
                level.Deferred = false;
            }

            SetCanvasTransform(current, currentMatrix);
            _deferredClips = 0;
        }

        /// <summary>
        /// Applies the deferred clips to the canvas for one draw that cannot reach the pending
        /// runs, leaving them deferred; the caller restores the canvas once after the draw.
        /// </summary>
        private void ApplyDeferredClipsUntilRestore()
        {
            var current = _currentTransform;
            var currentMatrix = current is null ? Canvas.TotalMatrix44 : default;

            Canvas.Save();

            for (var i = _clipDepth - _deferredClips; i < _clipDepth; i++)
            {
                ref readonly var level = ref _clipLevels[i];

                SetCanvasTransform(level.Transform, default);
                ClipCanvas(level);
            }

            SetCanvasTransform(current, currentMatrix);
        }

        /// <summary>Clips the canvas to a deferred clip under the current canvas transform, as pushing it would have.</summary>
        private void ClipCanvas(in ClipLevel level)
        {
            if (!level.IsShape || level.Aliased)
            {
                Canvas.ClipRect(level.Local);
                return;
            }

            var clip = level.Shape;
            var roundRect = SKRoundRectCache.Shared.Get();
            var radii = t_clipRadii ??= new SKPoint[4];

            radii[0] = clip.RadiiTopLeft.ToSKPoint();
            radii[1] = clip.RadiiTopRight.ToSKPoint();
            radii[2] = clip.RadiiBottomRight.ToSKPoint();
            radii[3] = clip.RadiiBottomLeft.ToSKPoint();
            roundRect.SetRectRadii(clip.Rect.ToSKRect(), radii);

            Canvas.ClipRoundRect(roundRect, antialias: true);
            SKRoundRectCache.Shared.Return(roundRect);
        }

        /// <summary>
        /// Sets the transform, or with <c>null</c> the canvas matrix <paramref name="matrix"/> read back
        /// earlier, which already includes the DPI scale.
        /// </summary>
        private void SetCanvasTransform(Matrix? transform, in SKMatrix44 matrix)
        {
            _currentTransform = transform;
            Canvas.SetMatrix(transform is { } value ? ToCanvasMatrix(value) : matrix);
        }

        /// <summary>Pops the innermost clip, rectangle, rounded rectangle, region or geometry.</summary>
        private void PopClipLevel()
        {
            if (_clipDepth > 0)
            {
                ref var level = ref _clipLevels[_clipDepth - 1];

                if (level.Deferred)
                {
                    CheckLease();

                    // What restoring the canvas would do: the clip was never applied, and the
                    // transform returns to the one the clip was pushed under.
                    SetCanvasTransform(level.Transform, default);
                    _deferredClips--;
                    UntrackClipLevel();
                    return;
                }

                // Pending runs trimmed to an applied pixel-aligned clip look the same under the
                // clips around it, as do runs batched under an applied shape clip that all fit it:
                // the clip was applied with no run pending that it would cut.
                if (DefersClips && (level.Kind == GlyphRunClipKind.PixelAlignedRect &&
                                    PendingRunsLieInside(level.Device) || level.IsShape && !level.Unfit))
                {
                    CheckLease();
                    RestoreCanvas();
                    UntrackClipLevel();
                    return;
                }
            }

            PrepareCanvas(GlyphBatchFlushReason.Clip);
            RestoreCanvas();
            UntrackClipLevel();
        }

        /// <summary>Whether every pending glyph run, grayscale or subpixel, lies inside <paramref name="rect"/>.</summary>
        private bool PendingRunsLieInside(SKRect rect)
        {
            for (var i = 0; i < _pendingBatchCount; i++)
            {
                if (!rect.Contains(_pendingBatches![i].Bounds))
                {
                    return false;
                }
            }

            for (var i = 0; i < _lcdBatchCount; i++)
            {
                if (!rect.Contains(_lcdBatch!.Bounds[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Trims a run's sprites, drawn 1:1 at (<paramref name="x"/>, <paramref name="y"/>) and
        /// covering <paramref name="bounds"/> in device pixels, to the pixel-aligned rectangle
        /// clips around it. Returns <c>false</c> when no sprite is left to draw.
        /// </summary>
        /// <remarks>
        /// A sprite cut to the clip samples the same texel for every pixel left, with nearest
        /// sampling and no scale, so the trimmed run draws exactly the pixels the clip lets
        /// through, under any clip that contains it. The trimmed sprites are kept with the run,
        /// so a run cut the same way frame after frame reuses them and the vertices built for
        /// its batch.
        /// </remarks>
        private bool TryTrimToClips(ref SkiaGlyphAtlasBatch backend, int x, int y, ref SKRect bounds)
        {
            if (_clipDepth == 0)
            {
                return true;
            }

            ref readonly var level = ref _clipLevels[_clipDepth - 1];

            if (!level.HasTrim || level.Trim.Contains(bounds))
            {
                return true;
            }

            var visible = SKRect.Intersect(level.Trim, bounds);

            if (visible.IsEmpty)
            {
                return false;
            }

            visible.Offset(-x, -y);

            if (backend.Trim(visible) is not { } trimmed)
            {
                return false;
            }

            backend = trimmed;
            bounds = trimmed.Bounds;
            bounds.Offset(x, y);

            return true;
        }

        /// <summary>
        /// Trims one sprite drawn 1:1 from <paramref name="source"/> to <paramref name="dest"/>
        /// to the pixel-aligned rectangle clips around it, as <see cref="TryTrimToClips(ref SkiaGlyphAtlasBatch, int, int, ref SKRect)"/>
        /// trims a run. Returns <c>false</c> when nothing of it is left to draw.
        /// </summary>
        private bool TryTrimToClips(ref SKRect source, ref SKRect dest)
        {
            if (_clipDepth == 0)
            {
                return true;
            }

            ref readonly var level = ref _clipLevels[_clipDepth - 1];

            if (!level.HasTrim || level.Trim.Contains(dest))
            {
                return true;
            }

            var visible = SKRect.Intersect(level.Trim, dest);

            if (visible.IsEmpty)
            {
                return false;
            }

            source = SKRect.Create(source.Left + visible.Left - dest.Left, source.Top + visible.Top - dest.Top,
                visible.Width, visible.Height);
            dest = visible;

            return true;
        }

        /// <summary>Records a rectangle clip pushed under the current transform and applied to the canvas.</summary>
        private void TrackRectClip(Rect clip) => TrackClipLevel(CreateRectClipLevel(clip, DeviceTransform));

        private static ClipLevel CreateRectClipLevel(Rect clip, Matrix transform)
        {
            var device = clip.TransformToAABB(transform);

            if (transform.M12 != 0 || transform.M21 != 0)
            {
                return new ClipLevel(GlyphRunClipKind.Transformed, device.ToSKRect(), null);
            }

            return TrySnapToPixels(device, out var snapped)
                ? new ClipLevel(GlyphRunClipKind.PixelAlignedRect, snapped, null)
                : new ClipLevel(GlyphRunClipKind.FractionalRect, device.ToSKRect(), null);
        }

        /// <summary>Records a rounded rectangle or geometry clip with <paramref name="bounds"/> in local coordinates.</summary>
        private void TrackShapeClip(Rect bounds)
        {
            var transform = DeviceTransform;
            var kind = transform.M12 != 0 || transform.M21 != 0
                ? GlyphRunClipKind.Transformed
                : GlyphRunClipKind.RoundedRectOrGeometry;

            TrackClipLevel(new ClipLevel(kind, bounds.TransformToAABB(transform).ToSKRect(), null));
        }

        /// <summary>Clears the clip stack, which holds regions, and returns it to this thread's pool.</summary>
        private void ReturnClipStack()
        {
            Array.Clear(_clipLevels);
            (t_clipStacks ??= new Stack<ClipLevel[]>()).Push(_clipLevels);
            _clipLevels = Array.Empty<ClipLevel>();
            _clipDepth = 0;
        }

        private void TrackRegionClip(SKRegion region) =>
            TrackClipLevel(new ClipLevel(GlyphRunClipKind.Region, region.Bounds, region));

        private void TrackClipLevel(ClipLevel level)
        {
            if (_clipDepth == _clipLevels.Length)
            {
                Array.Resize(ref _clipLevels, Math.Max(8, _clipDepth * 2));
            }

            // Runs are trimmed to the intersection of every pixel-aligned rectangle clip, applied
            // or not.
            if (_clipDepth > 0 && _clipLevels[_clipDepth - 1] is { HasTrim: true } parent)
            {
                level.HasTrim = true;
                level.Trim = level.Kind == GlyphRunClipKind.PixelAlignedRect
                    ? SKRect.Intersect(parent.Trim, level.Device)
                    : parent.Trim;
            }
            else if (level.Kind == GlyphRunClipKind.PixelAlignedRect)
            {
                level.HasTrim = true;
                level.Trim = level.Device;
            }

            if (level.IsShape)
            {
                _shapeClips++;
            }

            _clipLevels[_clipDepth++] = level;
        }

        private void UntrackClipLevel()
        {
            // A pop without a push is the caller's error, and Skia ignores a restore past the
            // first save as well.
            if (_clipDepth > 0)
            {
                if (_clipLevels[_clipDepth - 1].IsShape)
                {
                    _shapeClips--;
                }

                _clipLevels[--_clipDepth] = default;
            }
        }

        /// <summary>
        /// The transform from local to device coordinates, including the hidden DPI scale and the
        /// scale recorded content is shown at.
        /// </summary>
        private Matrix DeviceTransform => ToDevice(Transform);

        /// <summary>The map to device pixels of local coordinates under <paramref name="transform"/>.</summary>
        private Matrix ToDevice(in Matrix transform)
        {
            var device = _postTransform is { } post ? transform * post : transform;

            return _playbackScale is { } playback ? device * playback : device;
        }

        /// <summary>
        /// Whether the current transform places draws 1:1 on the device grid, as it does under
        /// <see cref="Avalonia.Media.Fonts.Rasterization.GlyphTargetSurface.DevicePixels"/>. Compared in
        /// the canvas's single precision, where that transform followed by the DPI scale is exactly
        /// the identity.
        /// </summary>
        private bool DrawsInDevicePixels => DeviceTransform.ToSKMatrix().IsIdentity;

        private static bool TrySnapToPixels(Rect device, out SKRect snapped)
        {
            var left = Math.Round(device.Left);
            var top = Math.Round(device.Top);
            var right = Math.Round(device.Right);
            var bottom = Math.Round(device.Bottom);

            snapped = new SKRect((float)left, (float)top, (float)right, (float)bottom);

            return Math.Abs(device.Left - left) <= PixelAlignmentTolerance &&
                   Math.Abs(device.Top - top) <= PixelAlignmentTolerance &&
                   Math.Abs(device.Right - right) <= PixelAlignmentTolerance &&
                   Math.Abs(device.Bottom - bottom) <= PixelAlignmentTolerance;
        }

        [ThreadStatic]
        private static long[]? t_clippedRuns;

        [ThreadStatic]
        private static long t_runsInsideEveryClip;

        private static readonly int s_clipKindCount = Enum.GetValues<GlyphRunClipKind>().Length;

        /// <summary>
        /// The batched glyph runs drawn on this thread whose innermost clip is of
        /// <paramref name="kind"/>, and whose device bounds lie inside that clip
        /// (<paramref name="inside"/>) or cross its edge. For a rounded rectangle, a geometry or a
        /// transformed clip, inside means inside its device bounds. For profiling tools.
        /// </summary>
        internal static long GetClippedRunsOnThread(GlyphRunClipKind kind, bool inside) =>
            t_clippedRuns?[(int)kind * 2 + (inside ? 1 : 0)] ?? 0;

        /// <summary>
        /// The batched glyph runs drawn on this thread under no clip, or inside every clip
        /// pushed, in the sense of <see cref="GetClippedRunsOnThread"/>; for profiling tools.
        /// </summary>
        internal static long RunsInsideEveryClipOnThread => t_runsInsideEveryClip;

        /// <summary>Counts a batched run covering <paramref name="bounds"/> in device pixels against the clips.</summary>
        private void CountClippedRun(SKRect bounds)
        {
            var counts = t_clippedRuns ??= new long[s_clipKindCount * 2];

            if (_clipDepth == 0)
            {
                counts[(int)GlyphRunClipKind.None * 2 + 1]++;
                t_runsInsideEveryClip++;
                return;
            }

            var innermost = _clipLevels[_clipDepth - 1];

            counts[(int)innermost.Kind * 2 + (innermost.Contains(bounds) ? 1 : 0)]++;

            for (var i = 0; i < _clipDepth; i++)
            {
                if (!_clipLevels[i].Contains(bounds))
                {
                    return;
                }
            }

            t_runsInsideEveryClip++;
        }

        /// <summary>
        /// A clip pushed on this context: its kind, its device rectangle (the device bounds of a
        /// shape or a transformed clip) and, for a region, the region.
        /// </summary>
        private struct ClipLevel
        {
            public ClipLevel(GlyphRunClipKind kind, SKRect device, SKRegion? region)
            {
                Kind = kind;
                Device = device;
                Region = region;
            }

            public readonly GlyphRunClipKind Kind;
            public readonly SKRect Device;
            public readonly SKRegion? Region;

            /// <summary>Whether a pixel-aligned rectangle clip lies on this level or below it.</summary>
            public bool HasTrim;

            /// <summary>The intersection of the pixel-aligned rectangle clips on this level and below it.</summary>
            public SKRect Trim;

            /// <summary>Whether the clip is recorded but not applied to the canvas.</summary>
            public bool Deferred;

            /// <summary>A deferred clip's rectangle in the coordinates of <see cref="Transform"/>.</summary>
            public SKRect Local;

            /// <summary>The transform a deferred clip was pushed under, which popping it restores.</summary>
            public Matrix Transform;

            /// <summary>
            /// Whether the clip is an antialiased rounded rectangle that batched runs may stay
            /// pending under while they keep clear of its edges and corners (<see cref="Fits"/>).
            /// </summary>
            public bool IsShape;

            /// <summary>A shape clip as it was pushed, in the coordinates of <see cref="Transform"/>.</summary>
            public RoundedRect Shape;

            /// <summary>
            /// The device bands of a shape clip where it leaves every pixel whole: the rectangle
            /// between the corners' heights, and the one between their widths, each a pixel inside
            /// the edges.
            /// </summary>
            public SKRect Across, Down;

            /// <summary>Whether a deferred shape clip is a rectangle applied without antialiasing.</summary>
            public bool Aliased;

            /// <summary>Whether the shape clip is a rotated or skewed rectangle that <see cref="Edges"/> bound.</summary>
            public bool HasEdges;

            /// <summary>The device edges of a rotated or skewed rectangle clip.</summary>
            public ClipEdges Edges;

            /// <summary>
            /// Whether a run that does not <see cref="Fits">fit</see> the shape clip was batched
            /// while it was pushed, so its pending runs must be drawn before it is popped.
            /// </summary>
            public bool Unfit;

            /// <summary>
            /// Whether <paramref name="bounds"/>, whole device pixels, lie where the shape clip
            /// covers every pixel fully, so drawing them without the clip changes no pixel.
            /// </summary>
            public readonly bool Fits(in SKRect bounds) =>
                HasEdges ? Edges.Encloses(bounds) : Inside(Across, bounds) || Inside(Down, bounds);

            private static bool Inside(in SKRect outer, in SKRect bounds) =>
                outer.Left <= bounds.Left && outer.Top <= bounds.Top && bounds.Right <= outer.Right &&
                bounds.Bottom <= outer.Bottom;

            /// <summary>Whether <paramref name="bounds"/>, whole device pixels, lie inside the clip.</summary>
            public readonly bool Contains(SKRect bounds)
            {
                if (Region is { } region)
                {
                    return region.Contains(SKRectI.Round(bounds));
                }

                return Device.Left <= bounds.Left && Device.Top <= bounds.Top && bounds.Right <= Device.Right &&
                       bounds.Bottom <= Device.Bottom;
            }
        }
        /// <summary>
        /// The four edges of a rectangle clip under a rotation or skew, in device pixels: each an
        /// inward unit normal and the least projection on it of a point a pixel and a half inside
        /// that edge.
        /// </summary>
        internal readonly struct ClipEdges
        {
            // A pixel centre this far inside every edge is fully covered by the clip, antialiased
            // or not, as for the rounded rectangle bands.
            private const double Margin = 1.5;

            private readonly double _x0, _y0, _d0, _x1, _y1, _d1, _x2, _y2, _d2, _x3, _y3, _d3;

            private ClipEdges(double[] normals)
            {
                _x0 = normals[0]; _y0 = normals[1]; _d0 = normals[2];
                _x1 = normals[3]; _y1 = normals[4]; _d1 = normals[5];
                _x2 = normals[6]; _y2 = normals[7]; _d2 = normals[8];
                _x3 = normals[9]; _y3 = normals[10]; _d3 = normals[11];
            }

            /// <summary>
            /// The edges of <paramref name="rect"/> mapped by <paramref name="toDevice"/>, and its
            /// device bounds. Returns <c>false</c> for a degenerate or unbounded quadrilateral.
            /// </summary>
            public static bool TryCreate(Rect rect, Matrix toDevice, out ClipEdges edges, out SKRect device)
            {
                edges = default;
                device = default;

                Span<Point> corners = stackalloc Point[]
                {
                    rect.TopLeft.Transform(toDevice), rect.TopRight.Transform(toDevice),
                    rect.BottomRight.Transform(toDevice), rect.BottomLeft.Transform(toDevice),
                };

                var minX = double.MaxValue;
                var minY = double.MaxValue;
                var maxX = double.MinValue;
                var maxY = double.MinValue;

                foreach (var corner in corners)
                {
                    minX = Math.Min(minX, corner.X);
                    minY = Math.Min(minY, corner.Y);
                    maxX = Math.Max(maxX, corner.X);
                    maxY = Math.Max(maxY, corner.Y);
                }

                // Also rejects NaN.
                if (!(Math.Abs(minX) <= 1 << 24 && Math.Abs(minY) <= 1 << 24 && Math.Abs(maxX) <= 1 << 24 &&
                      Math.Abs(maxY) <= 1 << 24))
                {
                    return false;
                }

                // The side of each edge the inside lies on follows the corners' turning direction,
                // which a reflection reverses.
                var turn = Math.Sign((corners[1].X - corners[0].X) * (corners[2].Y - corners[1].Y) -
                                     (corners[1].Y - corners[0].Y) * (corners[2].X - corners[1].X));

                if (turn == 0)
                {
                    return false;
                }

                var normals = new double[12];

                for (var i = 0; i < 4; i++)
                {
                    var from = corners[i];
                    var to = corners[(i + 1) % 4];
                    var dx = to.X - from.X;
                    var dy = to.Y - from.Y;
                    var length = Math.Sqrt(dx * dx + dy * dy);

                    if (!(length > 0))
                    {
                        return false;
                    }

                    var nx = -dy / length * turn;
                    var ny = dx / length * turn;

                    normals[i * 3] = nx;
                    normals[i * 3 + 1] = ny;
                    normals[i * 3 + 2] = nx * from.X + ny * from.Y + Margin;
                }

                edges = new ClipEdges(normals);
                device = new SKRect((float)minX, (float)minY, (float)maxX, (float)maxY);

                return true;
            }

            /// <summary>
            /// Whether <paramref name="bounds"/>, whole device pixels, lie at least a pixel and a
            /// half inside every edge.
            /// </summary>
            public bool Encloses(in SKRect bounds) =>
                Encloses(bounds, _x0, _y0, _d0) && Encloses(bounds, _x1, _y1, _d1) &&
                Encloses(bounds, _x2, _y2, _d2) && Encloses(bounds, _x3, _y3, _d3);

            // A projection on a normal is least at one of the rectangle's corners.
            private static bool Encloses(in SKRect bounds, double nx, double ny, double least) =>
                nx * (nx >= 0 ? bounds.Left : bounds.Right) + ny * (ny >= 0 ? bounds.Top : bounds.Bottom) >= least;
        }

        internal sealed partial class SkiaGlyphAtlasBatch
        {
            // The sprites cut to the visible rectangle they were last trimmed to, kept while runs
            // are cut the same way.
            private SkiaGlyphAtlasBatch? _trimmed;
            private SKRect _trimmedTo;

            /// <summary>
            /// The sprites cut to <paramref name="visible"/>, a rectangle of whole pixels relative
            /// to the batch's origin, each sampling the texels it covers; <c>null</c> when no sprite
            /// reaches into it. The result is kept until the batch is trimmed to another rectangle.
            /// </summary>
            public SkiaGlyphAtlasBatch? Trim(SKRect visible)
            {
                if (_trimmed is { } kept && _trimmedTo == visible)
                {
                    return kept;
                }

                var count = 0;

                for (var i = 0; i < Sources.Length; i++)
                {
                    if (!SKRect.Intersect(SpriteRect(i), visible).IsEmpty)
                    {
                        count++;
                    }
                }

                if (count == 0)
                {
                    return null;
                }

                var sources = new SKRect[count];
                var placements = new SKRotationScaleMatrix[count];
                var index = 0;

                for (var i = 0; i < Sources.Length; i++)
                {
                    var sprite = SpriteRect(i);
                    var cut = SKRect.Intersect(sprite, visible);

                    if (cut.IsEmpty)
                    {
                        continue;
                    }

                    var source = Sources[i];

                    sources[index] = SKRect.Create(source.Left + cut.Left - sprite.Left, source.Top + cut.Top - sprite.Top,
                        cut.Width, cut.Height);
                    placements[index] = SKRotationScaleMatrix.CreateTranslation(cut.Left, cut.Top);
                    index++;
                }

                // The replaced sprites may still be pending in a batch of this frame. They draw
                // from their arrays either way; disposing them only releases the vertices kept
                // for a batch that repeats, and no later frame draws them to build new ones.
                _trimmed?.Dispose();
                _trimmed = new SkiaGlyphAtlasBatch(sources, placements, null);
                _trimmedTo = visible;

                return _trimmed;
            }

            private SKRect SpriteRect(int index)
            {
                var placement = Placements[index];

                return SKRect.Create(placement.TX, placement.TY, Sources[index].Width, Sources[index].Height);
            }

            private void DisposeTrimmed()
            {
                _trimmed?.Dispose();
                _trimmed = null;
            }
        }
    }
}
