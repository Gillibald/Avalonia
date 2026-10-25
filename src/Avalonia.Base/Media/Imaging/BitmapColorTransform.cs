using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// A prepared transform from one ICC color profile to another, applied to pixel
    /// buffers or to individual colors. Requires an imaging backend with color management;
    /// see <see cref="IsSupported"/>.
    /// </summary>
    /// <remarks>
    /// Profiles are RGB, gray or CMYK ICC profiles. Pixels are transformed on their encoded
    /// channel values: integer formats map directly to 0 to 1, float formats are encoded
    /// with the sRGB curve first, the same rule format conversions use. Alpha is carried
    /// through unchanged. A transform may be used from several threads at once.
    /// </remarks>
    public sealed class BitmapColorTransform : IDisposable
    {
        private const int IccHeaderSize = 128;

        private readonly int _sourceChannels;
        private readonly int _destinationChannels;
        private IColorTransformImpl? _impl;

        private BitmapColorTransform(IColorTransformImpl impl, ColorRenderingIntent? intent,
            int sourceChannels, int destinationChannels)
        {
            _impl = impl;
            Intent = intent;
            _sourceChannels = sourceChannels;
            _destinationChannels = destinationChannels;
        }

        /// <summary>
        /// Gets whether the active imaging backend can transform colors between profiles.
        /// </summary>
        public static bool IsSupported => ImagingBackend.CurrentOrNull is IColorTransformProvider;

        /// <summary>
        /// Gets the rendering intent the transform was created with, or null when the color
        /// management chose it from the profiles.
        /// </summary>
        public ColorRenderingIntent? Intent { get; }

        /// <summary>
        /// Gets the number of channels of a source color: 1 for gray, 3 for RGB, 4 for CMYK.
        /// </summary>
        public int SourceChannelCount => _sourceChannels;

        /// <summary>
        /// Gets the number of channels of a destination color: 1 for gray, 3 for RGB, 4 for CMYK.
        /// </summary>
        public int DestinationChannelCount => _destinationChannels;

        /// <summary>
        /// Prepares a transform whose rendering intent the color management chooses from what the
        /// profiles hold, as WIC does; the intent a profile header names is only a hint.
        /// </summary>
        /// <param name="sourceProfile">The ICC profile the input colors are in.</param>
        /// <param name="destinationProfile">The ICC profile the output colors are in.</param>
        /// <exception cref="ArgumentException">A profile is not an ICC profile.</exception>
        /// <exception cref="NotSupportedException">
        /// The active backend has no color management, or cannot use one of the profiles.
        /// </exception>
        public static BitmapColorTransform Create(ReadOnlyMemory<byte> sourceProfile, ReadOnlyMemory<byte> destinationProfile)
            => CreateCore(sourceProfile, destinationProfile, null);

        /// <summary>
        /// Prepares a transform with an explicit rendering intent.
        /// </summary>
        /// <param name="sourceProfile">The ICC profile the input colors are in.</param>
        /// <param name="destinationProfile">The ICC profile the output colors are in.</param>
        /// <param name="intent">The rendering intent to apply.</param>
        /// <exception cref="ArgumentException">A profile is not an ICC profile.</exception>
        /// <exception cref="NotSupportedException">
        /// The active backend has no color management, or cannot use one of the profiles.
        /// </exception>
        public static BitmapColorTransform Create(ReadOnlyMemory<byte> sourceProfile, ReadOnlyMemory<byte> destinationProfile,
            ColorRenderingIntent intent)
        {
            if (intent is < ColorRenderingIntent.Perceptual or > ColorRenderingIntent.AbsoluteColorimetric)
                throw new ArgumentOutOfRangeException(nameof(intent), intent, "Unknown rendering intent.");

            return CreateCore(sourceProfile, destinationProfile, intent);
        }

        private static BitmapColorTransform CreateCore(ReadOnlyMemory<byte> sourceProfile,
            ReadOnlyMemory<byte> destinationProfile, ColorRenderingIntent? intent)
        {
            var sourceChannels = ReadChannelCount(sourceProfile.Span, nameof(sourceProfile));
            var destinationChannels = ReadChannelCount(destinationProfile.Span, nameof(destinationProfile));
            var backend = ImagingBackend.Current;

            if (backend is not IColorTransformProvider provider)
                throw new NotSupportedException(
                    $"The '{backend.Name}' imaging backend does not support color transforms.");

            var impl = provider.CreateColorTransform(sourceProfile.Span, destinationProfile.Span, intent);

            return new BitmapColorTransform(impl, intent, sourceChannels, destinationChannels);
        }

        /// <summary>
        /// Transforms a run of colors, for example a single color given by its channel values.
        /// </summary>
        /// <param name="source">
        /// <see cref="SourceChannelCount"/> values per color in the source profile's channel
        /// order, each in 0 to 1; values outside are clamped.
        /// </param>
        /// <param name="destination">
        /// Receives <see cref="DestinationChannelCount"/> values per color, each in 0 to 1.
        /// </param>
        public void TransformColors(ReadOnlySpan<float> source, Span<float> destination)
        {
            var impl = GetImpl();

            if (source.Length % _sourceChannels != 0)
                throw new ArgumentException(
                    $"The source length is not a multiple of {_sourceChannels} channels.", nameof(source));

            var count = source.Length / _sourceChannels;
            var destinationLength = count * _destinationChannels;

            if (destination.Length < destinationLength)
                throw new ArgumentException("The destination is too small.", nameof(destination));

            var clamped = ArrayPool<float>.Shared.Rent(source.Length);

            try
            {
                for (var i = 0; i < source.Length; i++)
                    clamped[i] = Clamp01(source[i]);

                destination = destination.Slice(0, destinationLength);
                impl.Transform(clamped.AsSpan(0, source.Length), destination, count);

                for (var i = 0; i < destination.Length; i++)
                    destination[i] = Clamp01(destination[i]);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(clamped);
            }
        }

        /// <summary>
        /// Transforms every pixel of a buffer into a new buffer.
        /// </summary>
        /// <param name="source">
        /// The pixels, in a format of the source profile's color model: an RGB or indexed
        /// format for an RGB profile, a gray format for a gray profile, a CMYK format for a
        /// CMYK profile.
        /// </param>
        /// <param name="destinationFormat">
        /// The format of the result, of the destination profile's color model; not indexed.
        /// </param>
        /// <returns>
        /// The transformed pixels with the source's size and DPI. When both formats have alpha
        /// the result keeps the source's alpha values and alpha format.
        /// </returns>
        public PixelBuffer Apply(PixelBuffer source, PixelFormat destinationFormat)
        {
            _ = source ?? throw new ArgumentNullException(nameof(source));

            var impl = GetImpl();

            if (GetChannelCount(source.Format) != _sourceChannels)
                throw new ArgumentException(
                    $"The {source.Format} source does not match the source profile's color model.", nameof(source));

            if (destinationFormat.IsIndexed || GetChannelCount(destinationFormat) != _destinationChannels)
                throw new ArgumentException(
                    $"{destinationFormat} does not match the destination profile's color model.", nameof(destinationFormat));

            var width = source.Size.Width;
            var height = source.Size.Height;
            var input = ToSourceCarrier(source);
            var outputFormat = GetCarrierFormat(destinationFormat);
            var outputStride = width * outputFormat.BitsPerPixel / 8;
            var output = new byte[checked(outputStride * height)];
            var sourceRow = new float[width * _sourceChannels];
            var destinationRow = new float[width * _destinationChannels];
            var alpha = new ushort[width];

            for (var y = 0; y < height; y++)
            {
                ReadRow(input.Pixels.Span.Slice(y * input.Stride), input.Format, sourceRow, alpha);
                impl.Transform(sourceRow, destinationRow, width);
                WriteRow(destinationRow, alpha, output.AsSpan(y * outputStride, outputStride), outputFormat);
            }

            var alphaFormat = !destinationFormat.HasAlpha
                ? AlphaFormat.Opaque
                : source.Format.HasAlpha ? source.AlphaFormat : AlphaFormat.Unpremul;
            var carrierAlpha = outputFormat.HasAlpha ? AlphaFormat.Unpremul : AlphaFormat.Opaque;
            var result = PixelBuffer.Create(output, source.Size, outputStride, outputFormat, carrierAlpha, source.Dpi);

            return outputFormat == destinationFormat && carrierAlpha == alphaFormat
                ? result
                : result.Convert(destinationFormat, alphaFormat);
        }

        /// <summary>
        /// Releases the backend transform.
        /// </summary>
        public void Dispose()
        {
            _impl?.Dispose();
            _impl = null;
        }

        private IColorTransformImpl GetImpl() =>
            _impl ?? throw new ObjectDisposedException(nameof(BitmapColorTransform));

        private static int ReadChannelCount(ReadOnlySpan<byte> profile, string parameterName)
        {
            if (profile.Length < IccHeaderSize ||
                BinaryPrimitives.ReadUInt32BigEndian(profile.Slice(36)) != 0x61637370) // 'acsp'
            {
                throw new ArgumentException("The data is not an ICC profile.", parameterName);
            }

            return BinaryPrimitives.ReadUInt32BigEndian(profile.Slice(16)) switch
            {
                0x52474220 => 3, // 'RGB '
                0x47524159 => 1, // 'GRAY'
                0x434D594B => 4, // 'CMYK'
                _ => throw new NotSupportedException(
                    "Only RGB, gray and CMYK color profiles can be transformed."),
            };
        }

        private static int GetChannelCount(PixelFormat format) =>
            format.IsCmyk ? 4 : format.IsGrayscale ? 1 : 3;

        // The core converts losslessly between integer formats of one color model, except
        // CMYK, whose conversions pass through RGB; CMYK is therefore read and written as is.
        private static PixelFormat GetCarrierFormat(PixelFormat format) =>
            format.IsCmyk ? format : format.IsGrayscale ? PixelFormats.Gray16 : PixelFormats.Rgba64;

        private static PixelBuffer ToSourceCarrier(PixelBuffer source)
        {
            var carrier = GetCarrierFormat(source.Format);
            var alphaFormat = carrier.HasAlpha ? AlphaFormat.Unpremul : AlphaFormat.Opaque;

            if (source.Format == carrier && (source.AlphaFormat == alphaFormat || !carrier.HasAlpha))
                return source;

            return source.Convert(carrier, alphaFormat);
        }

        private static void ReadRow(ReadOnlySpan<byte> row, PixelFormat format, Span<float> colors, Span<ushort> alpha)
        {
            var width = alpha.Length;

            if (format == PixelFormats.Cmyk32)
            {
                for (var i = 0; i < width * 4; i++)
                    colors[i] = row[i] / 255f;

                alpha.Fill(ushort.MaxValue);
                return;
            }

            var samples = MemoryMarshal.Cast<byte, ushort>(row);

            if (format == PixelFormats.Rgba64)
            {
                for (var x = 0; x < width; x++)
                {
                    colors[x * 3] = samples[x * 4] / 65535f;
                    colors[x * 3 + 1] = samples[x * 4 + 1] / 65535f;
                    colors[x * 3 + 2] = samples[x * 4 + 2] / 65535f;
                    alpha[x] = samples[x * 4 + 3];
                }

                return;
            }

            // Gray16 or Cmyk64: every sample is a color channel.
            for (var i = 0; i < colors.Length; i++)
                colors[i] = samples[i] / 65535f;

            alpha.Fill(ushort.MaxValue);
        }

        private static void WriteRow(ReadOnlySpan<float> colors, ReadOnlySpan<ushort> alpha, Span<byte> row,
            PixelFormat format)
        {
            var width = alpha.Length;

            if (format == PixelFormats.Cmyk32)
            {
                for (var i = 0; i < width * 4; i++)
                    row[i] = (byte)MathF.Round(Clamp01(colors[i]) * 255f);

                return;
            }

            var samples = MemoryMarshal.Cast<byte, ushort>(row);

            if (format == PixelFormats.Rgba64)
            {
                for (var x = 0; x < width; x++)
                {
                    samples[x * 4] = ToUInt16(colors[x * 3]);
                    samples[x * 4 + 1] = ToUInt16(colors[x * 3 + 1]);
                    samples[x * 4 + 2] = ToUInt16(colors[x * 3 + 2]);
                    samples[x * 4 + 3] = alpha[x];
                }

                return;
            }

            for (var i = 0; i < colors.Length; i++)
                samples[i] = ToUInt16(colors[i]);
        }

        private static ushort ToUInt16(float value) => (ushort)MathF.Round(Clamp01(value) * 65535f);

        // NaN maps to 0 so a misbehaving transform cannot produce undefined samples.
        private static float Clamp01(float value) => value > 0 ? (value < 1 ? value : 1) : 0;
    }
}
