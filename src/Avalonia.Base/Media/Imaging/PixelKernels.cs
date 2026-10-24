using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Avalonia.Media.Imaging;

/// <summary>
/// Row kernels for the 8-bit pixel layouts: channel swizzles, three-to-four byte expansion
/// and back, and the alpha conversions. Every kernel processes whole vectors where the
/// hardware has a byte shuffle (SSSE3, Arm64 AdvSimd) and finishes with a scalar tail that
/// produces the same bytes, so results never depend on the machine.
/// </summary>
/// <remarks>
/// Four-byte pixels keep alpha in the last byte. The alpha formulas are the WIC ones used
/// by <see cref="PixelFormatWriter"/>: premultiply rounds to nearest, unpremultiply
/// truncates a 16.16 fixed-point reciprocal and clamps.
/// </remarks>
internal static class PixelKernels
{
    private static readonly uint[] s_reciprocals = CreateReciprocals();

    /// <summary>
    /// Gets or sets whether vector code runs; tests and benchmarks turn it off to compare
    /// against the scalar code.
    /// </summary>
    internal static bool UseVectors { get; set; } = Ssse3.IsSupported || AdvSimd.Arm64.IsSupported;

    private static bool CanShuffle => UseVectors && (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported);

    /// <summary>
    /// Copies four-byte pixels, optionally swapping the first and third byte (RGBA and BGRA)
    /// and optionally forcing the fourth byte to 255.
    /// </summary>
    public static void Copy4(ReadOnlySpan<byte> source, Span<byte> dest, bool swap, bool opaque)
    {
        var length = Math.Min(source.Length, dest.Length) & ~3;
        var i = 0;

        if (!swap && !opaque)
        {
            source.Slice(0, length).CopyTo(dest);
            return;
        }

        if (CanShuffle)
        {
            var mask = swap
                ? Vector128.Create((byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15)
                : Vector128.Create((byte)0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15);
            var alpha = opaque ? Vector128.Create(0xFF000000u).AsByte() : Vector128<byte>.Zero;

            ref var src = ref MemoryMarshal.GetReference(source);
            ref var dst = ref MemoryMarshal.GetReference(dest);

            for (; i + 16 <= length; i += 16)
            {
                var pixels = Shuffle(Vector128.LoadUnsafe(ref src, (nuint)i), mask) | alpha;

                pixels.StoreUnsafe(ref dst, (nuint)i);
            }
        }

        // The scalar code works on whole pixels as little-endian integers, byte 0 lowest.
        ref var from = ref MemoryMarshal.GetReference(source);
        ref var to = ref MemoryMarshal.GetReference(dest);
        var alphaBits = opaque ? 0xFF000000u : 0u;

        for (; i < length; i += 4)
        {
            var pixel = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref from, i));

            if (swap)
                pixel = (pixel & 0xFF00FF00u) | ((pixel >> 16) & 0xFFu) | ((pixel & 0xFFu) << 16);

            Unsafe.WriteUnaligned(ref Unsafe.Add(ref to, i), pixel | alphaBits);
        }
    }

    /// <summary>
    /// Expands three-byte pixels to four bytes with an opaque fourth byte, optionally
    /// swapping the first and third byte.
    /// </summary>
    public static void Expand3To4(ReadOnlySpan<byte> source, Span<byte> dest, bool swap)
    {
        var pixels = Math.Min(source.Length / 3, dest.Length / 4);
        var p = 0;

        if (CanShuffle)
        {
            var mask = swap
                ? Vector128.Create((byte)2, 1, 0, 255, 5, 4, 3, 255, 8, 7, 6, 255, 11, 10, 9, 255)
                : Vector128.Create((byte)0, 1, 2, 255, 3, 4, 5, 255, 6, 7, 8, 255, 9, 10, 11, 255);
            var alpha = Vector128.Create(0xFF000000u).AsByte();

            ref var src = ref MemoryMarshal.GetReference(source);
            ref var dst = ref MemoryMarshal.GetReference(dest);

            // Each step reads 16 bytes but consumes 12, so it stops while 16 remain readable.
            for (; p + 4 <= pixels && p * 3 + 16 <= source.Length; p += 4)
            {
                var expanded = Shuffle(Vector128.LoadUnsafe(ref src, (nuint)(p * 3)), mask) | alpha;

                expanded.StoreUnsafe(ref dst, (nuint)(p * 4));
            }
        }

        ref var from = ref MemoryMarshal.GetReference(source);
        ref var to = ref MemoryMarshal.GetReference(dest);
        var firstShift = swap ? 16 : 0;
        var thirdShift = swap ? 0 : 16;

        for (; p < pixels; p++)
        {
            ref var pixel = ref Unsafe.Add(ref from, p * 3);
            var value = 0xFF000000u | ((uint)pixel << firstShift) | ((uint)Unsafe.Add(ref pixel, 1) << 8) |
                ((uint)Unsafe.Add(ref pixel, 2) << thirdShift);

            Unsafe.WriteUnaligned(ref Unsafe.Add(ref to, p * 4), value);
        }
    }

    /// <summary>
    /// Contracts four-byte pixels to three bytes, dropping the fourth, optionally swapping
    /// the first and third byte.
    /// </summary>
    public static void Contract4To3(ReadOnlySpan<byte> source, Span<byte> dest, bool swap)
    {
        var pixels = Math.Min(source.Length / 4, dest.Length / 3);
        var p = 0;

        if (CanShuffle)
        {
            var mask = swap
                ? Vector128.Create((byte)2, 1, 0, 6, 5, 4, 10, 9, 8, 14, 13, 12, 255, 255, 255, 255)
                : Vector128.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 255, 255, 255, 255);

            ref var src = ref MemoryMarshal.GetReference(source);
            ref var dst = ref MemoryMarshal.GetReference(dest);

            // Each step writes 16 bytes of which 12 are pixels, so it stops while 16 remain writable.
            for (; p + 4 <= pixels && p * 3 + 16 <= dest.Length; p += 4)
            {
                var contracted = Shuffle(Vector128.LoadUnsafe(ref src, (nuint)(p * 4)), mask);

                contracted.StoreUnsafe(ref dst, (nuint)(p * 3));
            }
        }

        ref var from = ref MemoryMarshal.GetReference(source);
        ref var to = ref MemoryMarshal.GetReference(dest);
        var firstShift = swap ? 16 : 0;
        var thirdShift = swap ? 0 : 16;

        for (; p < pixels; p++)
        {
            var value = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref from, p * 4));
            ref var pixel = ref Unsafe.Add(ref to, p * 3);

            pixel = (byte)(value >> firstShift);
            Unsafe.Add(ref pixel, 1) = (byte)(value >> 8);
            Unsafe.Add(ref pixel, 2) = (byte)(value >> thirdShift);
        }
    }

    /// <summary>
    /// Premultiplies four-byte pixels in place: each colour byte becomes
    /// <c>(c * a + 127) / 255</c>.
    /// </summary>
    public static void Premultiply(Span<byte> pixels)
    {
        var length = pixels.Length & ~3;
        var i = 0;

        if (CanShuffle)
        {
            var alphaSpread = Vector128.Create((byte)3, 3, 3, 3, 7, 7, 7, 7, 11, 11, 11, 11, 15, 15, 15, 15);
            var alphaBytes = Vector128.Create(0xFF000000u).AsByte();
            var half = Vector128.Create((ushort)128);

            ref var row = ref MemoryMarshal.GetReference(pixels);

            for (; i + 16 <= length; i += 16)
            {
                var color = Vector128.LoadUnsafe(ref row, (nuint)i);
                var alpha = Shuffle(color, alphaSpread);
                var (colorLow, colorHigh) = Vector128.Widen(color);
                var (alphaLow, alphaHigh) = Vector128.Widen(alpha);

                // round(t / 255) for t = c * a, without a division: (t + 128 + ((t + 128) >> 8)) >> 8.
                var low = colorLow * alphaLow + half;
                var high = colorHigh * alphaHigh + half;

                low = Vector128.ShiftRightLogical(low + Vector128.ShiftRightLogical(low, 8), 8);
                high = Vector128.ShiftRightLogical(high + Vector128.ShiftRightLogical(high, 8), 8);

                var result = Vector128.ConditionalSelect(alphaBytes, color, Vector128.Narrow(low, high));

                result.StoreUnsafe(ref row, (nuint)i);
            }
        }

        ref var bytes = ref MemoryMarshal.GetReference(pixels);

        for (; i < length; i += 4)
        {
            ref var pixel = ref Unsafe.Add(ref bytes, i);
            int alpha = Unsafe.Add(ref pixel, 3);

            pixel = Premultiply(pixel, alpha);
            Unsafe.Add(ref pixel, 1) = Premultiply(Unsafe.Add(ref pixel, 1), alpha);
            Unsafe.Add(ref pixel, 2) = Premultiply(Unsafe.Add(ref pixel, 2), alpha);
        }
    }

    // (c * a + 127) / 255 without the division, the same identity the vector code uses.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Premultiply(int channel, int alpha)
    {
        var rounded = channel * alpha + 128;

        return (byte)((rounded + (rounded >> 8)) >> 8);
    }

    /// <summary>
    /// Unpremultiplies four-byte pixels in place: each colour byte becomes
    /// <c>min(255, (c * ((255 &lt;&lt; 16) / a)) &gt;&gt; 16)</c>, and a pixel with zero alpha becomes
    /// transparent black.
    /// </summary>
    public static void Unpremultiply(Span<byte> pixels)
    {
        var length = pixels.Length & ~3;
        var reciprocals = s_reciprocals;
        var i = 0;

        if (UseVectors && (Sse41.IsSupported || AdvSimd.Arm64.IsSupported))
        {
            var alphaBytes = Vector128.Create(0xFF000000u).AsByte();
            var max = Vector128.Create(255u);

            ref var row = ref MemoryMarshal.GetReference(pixels);

            for (; i + 16 <= length; i += 16)
            {
                var color = Vector128.LoadUnsafe(ref row, (nuint)i);
                var (low, high) = Vector128.Widen(color);
                var (p0, p1) = Vector128.Widen(low);
                var (p2, p3) = Vector128.Widen(high);

                // The reciprocal varies per pixel, so it is looked up per pixel and
                // broadcast across the pixel's four lanes.
                p0 = Vector128.Min(Vector128.ShiftRightLogical(p0 * Vector128.Create(reciprocals[pixels[i + 3]]), 16), max);
                p1 = Vector128.Min(Vector128.ShiftRightLogical(p1 * Vector128.Create(reciprocals[pixels[i + 7]]), 16), max);
                p2 = Vector128.Min(Vector128.ShiftRightLogical(p2 * Vector128.Create(reciprocals[pixels[i + 11]]), 16), max);
                p3 = Vector128.Min(Vector128.ShiftRightLogical(p3 * Vector128.Create(reciprocals[pixels[i + 15]]), 16), max);

                var result = Vector128.Narrow(Vector128.Narrow(p0, p1), Vector128.Narrow(p2, p3));

                Vector128.ConditionalSelect(alphaBytes, color, result).StoreUnsafe(ref row, (nuint)i);
            }
        }

        ref var bytes = ref MemoryMarshal.GetReference(pixels);

        for (; i < length; i += 4)
        {
            ref var pixel = ref Unsafe.Add(ref bytes, i);
            var reciprocal = reciprocals[Unsafe.Add(ref pixel, 3)];

            pixel = (byte)Math.Min((pixel * reciprocal) >> 16, 255u);
            Unsafe.Add(ref pixel, 1) = (byte)Math.Min((Unsafe.Add(ref pixel, 1) * reciprocal) >> 16, 255u);
            Unsafe.Add(ref pixel, 2) = (byte)Math.Min((Unsafe.Add(ref pixel, 2) * reciprocal) >> 16, 255u);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Shuffle(Vector128<byte> value, Vector128<byte> mask)
    {
        // Both instructions return zero for the index 255.
        if (Ssse3.IsSupported)
            return Ssse3.Shuffle(value, mask);

        if (AdvSimd.Arm64.IsSupported)
            return AdvSimd.Arm64.VectorTableLookup(value, mask);

        return Vector128.Shuffle(value, mask);
    }

    // The zero-alpha entry is zero, so the colour of a transparent pixel becomes zero.
    private static uint[] CreateReciprocals()
    {
        var table = new uint[256];

        for (var alpha = 1; alpha < 256; alpha++)
            table[alpha] = (255u << 16) / (uint)alpha;

        return table;
    }
}
