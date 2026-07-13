using System;
using Avalonia.Platform;

namespace Avalonia.Media.Fonts.Rasterization
{
    /// <summary>
    /// Cache identity of a composed run mask. Everything positional is relative to the run's
    /// snapped origin pixel, so scrolling by whole pixels reuses the same mask; fractional
    /// horizontal motion cycles the four origin phases. <see cref="Tint"/> is the premultiplied
    /// BGRA of a solid foreground, or zero for the untinted alpha variant (zero is not a
    /// drawable premultiplied tint, so the sentinel cannot collide). Opacity is deliberately
    /// absent — it rides the draw call, so fades reuse the cached mask (D7).
    /// </summary>
    internal readonly record struct RunMaskKey(ushort ScaleQ, byte OriginPhase, GlyphMaskMode Mode, uint Tint);

    /// <summary>
    /// An immutable composed run mask: a backend bitmap plus its placement relative to the run's
    /// snapped origin pixel. The bitmap is written exactly once (inside the composing lock,
    /// before first draw) and never mutated afterwards, which is what makes the backend's
    /// image-identity caching turn it into a GPU-resident texture after the first draw (D8).
    /// </summary>
    internal sealed class RunMask : IDisposable
    {
        public RunMask(IDisposable handle, int offsetX, int offsetY, int width, int height)
        {
            Handle = handle;
            OffsetX = offsetX;
            OffsetY = offsetY;
            Width = width;
            Height = height;
        }

        /// <summary>
        /// The realized drawable: a pre-tinted <see cref="IBitmapImpl"/> on the portable floor,
        /// or a backend alpha-mask handle from <see cref="IAlphaGlyphMaskContext"/>.
        /// </summary>
        public IDisposable Handle { get; }

        /// <summary>Mask top-left relative to the run's snapped origin pixel, device px.</summary>
        public int OffsetX { get; }

        public int OffsetY { get; }

        public int Width { get; }

        public int Height { get; }

        public void Dispose() => Handle.Dispose();
    }

    /// <summary>
    /// A small per-run cache of composed masks, mirroring the shape of the Skia blob cache: one
    /// primary slot for the dominant repeat case plus a short overflow ring for phase/tint
    /// variants. Owned by the run impl, which is deterministically ref-counted by the scene
    /// graph, so disposing evicted (and finally all) masks here cannot outlive a consumer —
    /// the same lifetime contract the SKTextBlob cache relies on today.
    /// </summary>
    internal sealed class RunMaskCache : IDisposable
    {
        private const int SecondarySize = 3;

        private RunMaskKey _primaryKey;
        private RunMask? _primary;
        private (RunMaskKey Key, RunMask Mask)[]? _secondary;
        private int _nextEvict;

        public bool TryGet(in RunMaskKey key, out RunMask mask)
        {
            if (_primary is { } primary && _primaryKey == key)
            {
                mask = primary;
                return true;
            }

            if (_secondary is { } secondary)
            {
                for (var i = 0; i < secondary.Length; i++)
                {
                    if (secondary[i].Mask is { } hit && secondary[i].Key == key)
                    {
                        mask = hit;
                        return true;
                    }
                }
            }

            mask = null!;
            return false;
        }

        public void Add(in RunMaskKey key, RunMask mask)
        {
            if (_primary is null)
            {
                _primaryKey = key;
                _primary = mask;
                return;
            }

            _secondary ??= new (RunMaskKey, RunMask)[SecondarySize];

            ref var slot = ref _secondary[_nextEvict];
            slot.Mask?.Dispose();
            slot = (key, mask);
            _nextEvict = (_nextEvict + 1) % SecondarySize;
        }

        public void Dispose()
        {
            _primary?.Dispose();
            _primary = null;

            if (_secondary is { } secondary)
            {
                for (var i = 0; i < secondary.Length; i++)
                {
                    secondary[i].Mask?.Dispose();
                    secondary[i] = default;
                }
            }
        }
    }
}
