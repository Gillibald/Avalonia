using System;
using Avalonia.Platform;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// The last step of a decode plan that maps pixels to a palette (an indexed target with a
    /// <see cref="BitmapDecodeOptions.Palette"/>) or dithers them to a few gray levels. The
    /// backend decodes the rest of the plan to straight BGRA, then the frame converts that once
    /// with the plan's palette, dither and alpha threshold, through the same conversion
    /// <see cref="PixelBuffer.Convert"/> runs. Error diffusion carries error from row to row,
    /// so it cannot run inside the row-streaming decode pipeline.
    /// </summary>
    internal sealed class DecodePaletteStep
    {
        private DecodePaletteStep(PixelFormat format, AlphaFormat alphaFormat, PixelConversionOptions options)
        {
            Format = format;
            AlphaFormat = alphaFormat;
            Options = options;
        }

        /// <summary>The pixel format the frame reports and produces.</summary>
        public PixelFormat Format { get; }

        /// <summary>The alpha format the frame reports and produces.</summary>
        public AlphaFormat AlphaFormat { get; }

        /// <summary>The palette, dither and alpha threshold of the conversion.</summary>
        public PixelConversionOptions Options { get; }

        /// <summary>The palette of an indexed target, or null for a gray target.</summary>
        public BitmapPalette? Palette => Format.IsIndexed ? Options.Palette : null;

        /// <summary>
        /// Splits <paramref name="options"/> into the plan the backend decodes and the palette
        /// step that follows it. Returns <paramref name="options"/> itself, and no step, when
        /// the plan maps to no palette and dithers nothing.
        /// </summary>
        public static BitmapDecodeOptions? Split(BitmapDecodeOptions? options, out DecodePaletteStep? step)
        {
            step = null;

            if (options is not { TargetFormat: { } target } ||
                (target.IsIndexed ? options.Palette is null
                    : options.Dither == BitmapDitherMode.None || !ErrorDiffusion.IsTarget(target)))
            {
                return options;
            }

            if (options.AlphaThresholdPercent is < 0 or > 100 || double.IsNaN(options.AlphaThresholdPercent))
            {
                throw new ArgumentOutOfRangeException(nameof(options),
                    $"{nameof(BitmapDecodeOptions.AlphaThresholdPercent)} must be between 0 and 100; " +
                    $"got {options.AlphaThresholdPercent}.");
            }

            step = new DecodePaletteStep(target, options.TargetAlphaFormat ?? AlphaFormat.Unpremul,
                new PixelConversionOptions
                {
                    Palette = target.IsIndexed ? options.Palette : null,
                    Dither = options.Dither,
                    AlphaThresholdPercent = options.AlphaThresholdPercent,
                });

            // Palette matching and dithering work on straight colors.
            var decode = options.Clone();

            decode.TargetFormat = PixelFormats.Bgra8888;
            decode.TargetAlphaFormat = AlphaFormat.Unpremul;
            decode.OpaqueSatisfiesTargetAlpha = false;
            decode.Palette = null;
            decode.Dither = BitmapDitherMode.None;
            decode.AlphaThresholdPercent = 0;

            return decode;
        }

        /// <summary>Converts a frame the backend decoded to straight BGRA into the step's format.</summary>
        public unsafe PixelBuffer Apply(ILockedFramebuffer source, Vector dpi)
        {
            var stride = PixelFormatArithmetic.GetMinRowBytes(Format, source.Size.Width);
            var pixels = new byte[checked(stride * source.Size.Height)];

            fixed (byte* destination = pixels)
            {
                PixelOperations.CopyPixels(source, null, new PixelRect(source.Size), (IntPtr)destination,
                    pixels.Length, stride, Format, AlphaFormat, Options);
            }

            return PixelBuffer.TakeOwnership(pixels, source.Size, stride, Format, AlphaFormat, dpi, Palette);
        }
    }
}
