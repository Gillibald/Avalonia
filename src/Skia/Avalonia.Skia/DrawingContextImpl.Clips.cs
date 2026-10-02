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

        /// <summary>Records a rectangle clip pushed under the current transform.</summary>
        private void TrackRectClip(Rect clip)
        {
            var transform = DeviceTransform;

            if (transform.M12 != 0 || transform.M21 != 0)
            {
                TrackClipLevel(new ClipLevel(GlyphRunClipKind.Transformed, clip.TransformToAABB(transform).ToSKRect(),
                    null));
                return;
            }

            var device = clip.TransformToAABB(transform);
            var aligned = TrySnapToPixels(device, out var snapped);

            TrackClipLevel(new ClipLevel(aligned ? GlyphRunClipKind.PixelAlignedRect : GlyphRunClipKind.FractionalRect,
                aligned ? snapped : device.ToSKRect(), null));
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

        private void TrackClipLevel(in ClipLevel level)
        {
            if (_clipDepth == _clipLevels.Length)
            {
                Array.Resize(ref _clipLevels, _clipDepth * 2);
            }

            _clipLevels[_clipDepth++] = level;
        }

        private void UntrackClipLevel()
        {
            // A pop without a push is the caller's error, and Skia ignores a restore past the
            // first save as well.
            if (_clipDepth > 0)
            {
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
        private readonly record struct ClipLevel(GlyphRunClipKind Kind, SKRect Device, SKRegion? Region)
        {
            /// <summary>Whether <paramref name="bounds"/>, whole device pixels, lie inside the clip.</summary>
            public bool Contains(SKRect bounds)
            {
                if (Region is { } region)
                {
                    return region.Contains(SKRectI.Round(bounds));
                }

                return Device.Left <= bounds.Left && Device.Top <= bounds.Top && bounds.Right <= Device.Right &&
                       bounds.Bottom <= Device.Bottom;
            }
        }
    }
}
