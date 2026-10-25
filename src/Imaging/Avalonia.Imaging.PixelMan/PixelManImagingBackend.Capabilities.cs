using System;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.PixelMan;
using Avalonia.Platform;

namespace Avalonia.Imaging.PixelMan
{
    // PixelMan's conversions, palette generation and colour management, so pixel operations
    // reproduce WIC instead of the core approximations.
    public sealed partial class PixelManImagingBackend : ISupportsPixelConversion, ISupportsPaletteGeneration,
        IColorTransformProvider
    {
        /// <inheritdoc />
        bool ISupportsPixelConversion.TryCopyPixels(ILockedFramebuffer source, BitmapPalette? sourcePalette,
            PixelRect sourceRect, IntPtr destination, long destinationSize, int destinationStride, PixelFormat format,
            AlphaFormat alphaFormat, PixelConversionOptions? options)
        {
            if (PixelManFormats.ToPixelMan(source.Format, source.AlphaFormat) is not { } sourceFormat ||
                PixelManFormats.ToPixelMan(format, alphaFormat) is not { } targetFormat ||
                (sourceFormat.IsIndexed && sourcePalette is null) ||
                (targetFormat.IsIndexed && options?.Palette is null && targetFormat != sourceFormat))
            {
                return false;
            }

            // WIC keeps the source indices and palette when the target is the same indexed format,
            // and so does PixelMan; the contract remaps to the new palette, which the core path does.
            if (targetFormat.IsIndexed && targetFormat == sourceFormat && options?.Palette is not null)
                return false;

            using var sourcePm = sourcePalette is not null ? PixelManFormats.ToPixelMan(sourcePalette) : null;
            using var frame = Wrap(source, sourceFormat, sourcePm);

            unsafe
            {
                var span = new Span<byte>((void*)destination, checked((int)destinationSize));

                // PixelMan copies into an indexed format only from that same format, so a
                // palette target is matched by a conversion first.
                if (targetFormat.IsIndexed && targetFormat != sourceFormat)
                {
                    using var palette = PixelManFormats.ToPixelMan(options!.Palette!);
                    using var converted = frame.ConvertTo(targetFormat, palette,
                        new PmConversionOptions { AlphaThresholdPercent = options.AlphaThresholdPercent });

                    converted.CopyPixels(targetFormat, (uint)sourceRect.X, (uint)sourceRect.Y, (uint)sourceRect.Width,
                        (uint)sourceRect.Height, span, (nuint)destinationStride);
                }
                else
                {
                    frame.CopyPixels(targetFormat, (uint)sourceRect.X, (uint)sourceRect.Y, (uint)sourceRect.Width,
                        (uint)sourceRect.Height, span, (nuint)destinationStride);
                }
            }

            return true;
        }

        /// <inheritdoc />
        BitmapPalette ISupportsPaletteGeneration.CreateOptimalPalette(ILockedFramebuffer source, BitmapPalette? sourcePalette,
            int maxColors, bool addTransparentColor)
        {
            if (PixelManFormats.ToPixelMan(source.Format, source.AlphaFormat) is not { } format)
                throw new NotSupportedException($"The '{Name}' imaging backend cannot read {source.Format} pixels.");

            using var sourcePm = sourcePalette is not null ? PixelManFormats.ToPixelMan(sourcePalette) : null;
            using var frame = Wrap(source, format, sourcePm);
            using var palette = PmPalette.FromBitmap(frame, (uint)maxColors, addTransparentColor, default);

            return PixelManFormats.ToPalette(palette);
        }

        /// <inheritdoc />
        IColorTransformImpl IColorTransformProvider.CreateColorTransform(ReadOnlySpan<byte> sourceProfile,
            ReadOnlySpan<byte> destinationProfile, ColorRenderingIntent? intent)
        {
            var pmIntent = intent switch
            {
                // PixelMan's default picks from the profiles' tags as WIC does.
                null => PmRenderingIntent.Default,
                ColorRenderingIntent.Perceptual => PmRenderingIntent.Perceptual,
                ColorRenderingIntent.RelativeColorimetric => PmRenderingIntent.RelativeColorimetric,
                ColorRenderingIntent.Saturation => PmRenderingIntent.Saturation,
                _ => throw new NotSupportedException(
                    $"The '{Name}' imaging backend does not support the {intent} rendering intent."),
            };

            try
            {
                return new PixelManColorTransform(sourceProfile, destinationProfile, pmIntent);
            }
            catch (PmException exception)
            {
                throw new NotSupportedException($"The '{Name}' imaging backend cannot use the profiles: {exception.Message}", exception);
            }
        }

        // A frame over the caller's memory, without a copy; the caller keeps the memory
        // alive until the frame is disposed.
        private static PmBitmapFrame Wrap(ILockedFramebuffer source, PmPixelFormat format, PmPalette? palette)
        {
            var stride = (nuint)source.RowBytes;

            return PmBitmapFrame.CreateFromOwned(source.Address, format, (uint)source.Size.Width, (uint)source.Size.Height,
                stride, stride * (nuint)source.Size.Height, palette, static () => { });
        }

        /// <summary>
        /// Runs the exchange rows through 16-bit frames, the deepest encoded formats
        /// PixelMan's colour transform accepts.
        /// </summary>
        private sealed class PixelManColorTransform : IColorTransformImpl
        {
            private readonly PmColorTransform _transform;
            private readonly PmPixelFormat _sourceFormat;
            private readonly int _sourceChannels;
            private readonly int _destinationChannels;

            public PixelManColorTransform(ReadOnlySpan<byte> sourceProfile, ReadOnlySpan<byte> destinationProfile,
                PmRenderingIntent intent)
            {
                using var source = PmColorContext.FromMemory(sourceProfile);
                using var destination = PmColorContext.FromMemory(destinationProfile);

                (_sourceFormat, _sourceChannels) = GetCarrier(source.ColorSpace);

                var (destinationFormat, destinationChannels) = GetCarrier(destination.ColorSpace);

                _destinationChannels = destinationChannels;
                _transform = PmColorTransform.Create(source, destination, destinationFormat, intent);
            }

            public void Transform(ReadOnlySpan<float> source, Span<float> destination, int pixelCount)
            {
                if (pixelCount == 0)
                    return;

                var input = new ushort[pixelCount * _sourceChannels];

                for (var i = 0; i < input.Length; i++)
                    input[i] = (ushort)MathF.Round(Math.Clamp(source[i], 0f, 1f) * 65535f);

                using var frame = PmBitmapFrame.CreateFromCopy(_sourceFormat, (uint)pixelCount, 1,
                    (nuint)(input.Length * 2), MemoryMarshal.AsBytes(input.AsSpan()), null);
                using var result = _transform.Apply(frame);

                var output = new ushort[pixelCount * _destinationChannels];

                result.CopyPixels(_transform.DestinationFormat, 0, 0, 0, 0, MemoryMarshal.AsBytes(output.AsSpan()),
                    (nuint)(output.Length * 2));

                for (var i = 0; i < output.Length; i++)
                    destination[i] = output[i] / 65535f;
            }

            public void Dispose() => _transform.Dispose();

            private static (PmPixelFormat Format, int Channels) GetCarrier(PmColorSpace space) => space switch
            {
                PmColorSpace.Gray => (PmPixelFormat.Gray16, 1),
                PmColorSpace.Cmyk => (PmPixelFormat.Cmyk64, 4),
                _ => (PmPixelFormat.Rgb48, 3),
            };
        }
    }
}
