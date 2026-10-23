using System;
using System.Buffers;
using System.Buffers.Binary;
using Avalonia.Platform;
using SkiaSharp;

namespace Avalonia.Skia.Imaging
{
    /// <summary>
    /// An ICC-to-ICC transform through Skia's color space conversion, which reads pixels
    /// tagged with one color space into another.
    /// </summary>
    internal sealed class SkiaColorTransform : IColorTransformImpl
    {
        private const uint RgbColorSpace = 0x52474220; // 'RGB '

        private readonly SKColorSpace _source;
        private readonly SKColorSpace _destination;

        private SkiaColorTransform(SKColorSpace source, SKColorSpace destination)
        {
            _source = source;
            _destination = destination;
        }

        public static SkiaColorTransform Create(ReadOnlySpan<byte> sourceProfile, ReadOnlySpan<byte> destinationProfile)
        {
            var source = CreateColorSpace(sourceProfile, "source");

            try
            {
                return new SkiaColorTransform(source, CreateColorSpace(destinationProfile, "destination"));
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }

        public unsafe void Transform(ReadOnlySpan<float> source, Span<float> destination, int pixelCount)
        {
            if (pixelCount == 0)
                return;

            var rowBytes = pixelCount * 4 * sizeof(float);
            var input = ArrayPool<float>.Shared.Rent(pixelCount * 4);
            var output = ArrayPool<float>.Shared.Rent(pixelCount * 4);

            try
            {
                for (var x = 0; x < pixelCount; x++)
                {
                    input[x * 4] = source[x * 3];
                    input[x * 4 + 1] = source[x * 3 + 1];
                    input[x * 4 + 2] = source[x * 3 + 2];
                    input[x * 4 + 3] = 1;
                }

                var sourceInfo = new SKImageInfo(pixelCount, 1, SKColorType.RgbaF32, SKAlphaType.Unpremul, _source);
                var destinationInfo = new SKImageInfo(pixelCount, 1, SKColorType.RgbaF32, SKAlphaType.Unpremul,
                    _destination);

                fixed (float* inputPointer = input)
                fixed (float* outputPointer = output)
                {
                    using var pixmap = new SKPixmap(sourceInfo, (IntPtr)inputPointer, rowBytes);

                    if (!pixmap.ReadPixels(destinationInfo, (IntPtr)outputPointer, rowBytes))
                        throw new InvalidOperationException("Skia could not convert between the color spaces.");
                }

                for (var x = 0; x < pixelCount; x++)
                {
                    destination[x * 3] = output[x * 4];
                    destination[x * 3 + 1] = output[x * 4 + 1];
                    destination[x * 3 + 2] = output[x * 4 + 2];
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(input);
                ArrayPool<float>.Shared.Return(output);
            }
        }

        public void Dispose()
        {
            _source.Dispose();
            _destination.Dispose();
        }

        // Transform works on three-channel rows, so a profile of any other color space is
        // refused here whatever CreateIcc would make of it.
        private static SKColorSpace CreateColorSpace(ReadOnlySpan<byte> profile, string role)
        {
            if (profile.Length < 20 || BinaryPrimitives.ReadUInt32BigEndian(profile.Slice(16)) != RgbColorSpace)
                throw new NotSupportedException(
                    $"The SkiaSharp imaging backend transforms RGB profiles only; the {role} profile is not RGB.");

            return SKColorSpace.CreateIcc(profile) ?? throw new NotSupportedException(
                $"The SkiaSharp imaging backend cannot use the {role} profile; Skia accepts RGB profiles " +
                "with a matrix and transfer curves.");
        }
    }
}
