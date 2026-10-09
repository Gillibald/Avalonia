using System;
using SkiaSharp;

namespace Avalonia.Skia
{
    internal partial class DrawingContextImpl
    {
        // Skia treats a device rectangle edge this close to a whole pixel as lying on it.
        private const double PixelAlignmentTolerance = 1e-3;

        private ClipLevel[] _clipLevels = new ClipLevel[8];
        private int _clipDepth;

        // The innermost clips not yet applied to the canvas. They are always the top of the
        // stack: applying one applies every clip below it first.
        private int _deferredClips;

        // The shape clips on the stack, applied or deferred.
        private int _shapeClips;

        /// <summary>
        /// Whether pixel-aligned rectangle clips wait to be applied to the canvas until something
        /// other than a batched glyph run draws under them. Glyph runs are trimmed to them
        /// instead, which is exact for sprites drawn 1:1, so pushing and popping such a clip
        /// leaves the pending glyph batches alone and rows of text that each clip to their
        /// bounds draw in one atlas call.
        /// </summary>
        private bool DefersClips => _grContext is not null && BatchesGlyphAtlasDraws && !_postTransform.HasValue;

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

            var level = CreateRectClipLevel(clip, transform);

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
            if (!DefersClips || CanvasTransform() is not { } transform || transform.M12 != 0 || transform.M21 != 0 ||
                transform.ContainsPerspective())
            {
                return false;
            }

            var device = clip.Rect.TransformToAABB(transform);
            var radiusX = Math.Max(Math.Max(clip.RadiiTopLeft.X, clip.RadiiTopRight.X),
                Math.Max(clip.RadiiBottomRight.X, clip.RadiiBottomLeft.X)) * Math.Abs(transform.M11);
            var radiusY = Math.Max(Math.Max(clip.RadiiTopLeft.Y, clip.RadiiTopRight.Y),
                Math.Max(clip.RadiiBottomRight.Y, clip.RadiiBottomLeft.Y)) * Math.Abs(transform.M22);

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
        /// run is batched under clips on the canvas.
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

                if (level.IsShape && !level.Fits(bounds))
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

        /// <summary>Clips the canvas to a deferred clip under the current canvas transform, as pushing it would have.</summary>
        private void ClipCanvas(in ClipLevel level)
        {
            if (!level.IsShape)
            {
                Canvas.ClipRect(level.Local);
                return;
            }

            var clip = level.Shape;
            var roundRect = SKRoundRectCache.Shared.Get();

            roundRect.SetRectRadii(clip.Rect.ToSKRect(),
                new[]
                {
                    clip.RadiiTopLeft.ToSKPoint(), clip.RadiiTopRight.ToSKPoint(),
                    clip.RadiiBottomRight.ToSKPoint(), clip.RadiiBottomLeft.ToSKPoint(),
                });

            Canvas.ClipRoundRect(roundRect, antialias: true);
            SKRoundRectCache.Shared.Return(roundRect);
        }

        private void SetCanvasTransform(Matrix? transform, in SKMatrix44 matrix)
        {
            _currentTransform = transform;
            Canvas.SetMatrix(transform is { } value ? value.ToSKMatrix44() : matrix);
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

        private void TrackRegionClip(SKRegion region) =>
            TrackClipLevel(new ClipLevel(GlyphRunClipKind.Region, region.Bounds, region));

        private void TrackClipLevel(ClipLevel level)
        {
            if (_clipDepth == _clipLevels.Length)
            {
                Array.Resize(ref _clipLevels, _clipDepth * 2);
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

        /// <summary>The transform from local to device coordinates, including the hidden DPI scale.</summary>
        private Matrix DeviceTransform => _postTransform is { } post ? Transform * post : Transform;

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

            /// <summary>
            /// Whether a run that does not <see cref="Fits">fit</see> the shape clip was batched
            /// while it was pushed, so its pending runs must be drawn before it is popped.
            /// </summary>
            public bool Unfit;

            /// <summary>
            /// Whether <paramref name="bounds"/>, whole device pixels, lie where the shape clip
            /// covers every pixel fully, so drawing them without the clip changes no pixel.
            /// </summary>
            public readonly bool Fits(in SKRect bounds) => Inside(Across, bounds) || Inside(Down, bounds);

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
