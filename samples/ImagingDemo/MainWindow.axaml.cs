using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;

namespace ImagingDemo
{
    /// <summary>
    /// Drives the Universal Bitmap Infrastructure through operations that are supported
    /// identically by the SkiaSharp and ImageSharp backends: header-only identify, a
    /// decode plan (target size, crop region, target pixel/alpha format, interpolation,
    /// EXIF orientation, decompression-bomb guard), transform-on-encode to PNG/JPEG/WebP,
    /// managed pixel authoring through <see cref="PixelBuffer"/>, and a lossless PNG
    /// round-trip check.
    /// </summary>
    public partial class MainWindow : Window
    {
        private static readonly (string Label, PixelFormat Format, AlphaFormat Alpha)[] s_targetFormats =
        {
            ("Bgra8888 - Premul", PixelFormats.Bgra8888, AlphaFormat.Premul),
            ("Bgra8888 - Unpremul", PixelFormats.Bgra8888, AlphaFormat.Unpremul),
            ("Rgba8888 - Unpremul", PixelFormats.Rgba8888, AlphaFormat.Unpremul),
            ("Rgb565 - Opaque", PixelFormats.Rgb565, AlphaFormat.Opaque),
        };

        private static readonly BitmapInterpolationMode[] s_interpolation =
        {
            BitmapInterpolationMode.None,
            BitmapInterpolationMode.LowQuality,
            BitmapInterpolationMode.MediumQuality,
            BitmapInterpolationMode.HighQuality,
        };

        private static readonly BitmapRotation[] s_rotations =
        {
            BitmapRotation.None,
            BitmapRotation.Rotate90,
            BitmapRotation.Rotate180,
            BitmapRotation.Rotate270,
        };

        private byte[]? _sourceBytes;
        private Bitmap? _sourcePreview;
        private Bitmap? _decodeResult;
        private Bitmap? _encodeResult;

        public MainWindow()
        {
            InitializeComponent();

            TargetFormatCombo.ItemsSource = s_targetFormats.Select(f => f.Label).ToArray();
            TargetFormatCombo.SelectedIndex = 0;

            InterpolationCombo.ItemsSource = s_interpolation.Select(i => i.ToString()).ToArray();
            InterpolationCombo.SelectedIndex = s_interpolation.Length - 1;

            RotationCombo.ItemsSource = new[] { "None", "90 deg CW", "180 deg", "270 deg CW" };
            RotationCombo.SelectedIndex = 0;

            EncodeFormatCombo.ItemsSource = new[] { "PNG", "JPEG", "WebP" };
            EncodeFormatCombo.SelectedIndex = 0;

            UpdateBackendHeader();

            // Start with a generated gradient so there is content immediately.
            SetGeneratedSource(SampleImages.CreateGradient(), "gradient");
        }

        // --- Source loading --------------------------------------------------

        private async void OnOpenImage(object? sender, RoutedEventArgs e)
        {
            try
            {
                var top = TopLevel.GetTopLevel(this);
                if (top is null)
                    return;

                var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Open an image (cross-backend formats)",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Images")
                        {
                            Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif", "*.bmp" },
                            MimeTypes = new[] { "image/png", "image/jpeg", "image/webp", "image/gif", "image/bmp" },
                        },
                    },
                });

                if (files.Count == 0)
                    return;

                await using var stream = await files[0].OpenReadAsync();
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);

                SetSource(buffer.ToArray(), files[0].Name);
            }
            catch (Exception ex)
            {
                Log("Open failed: " + ex.Message);
            }
        }

        private void OnGenerateGradient(object? sender, RoutedEventArgs e) =>
            SetGeneratedSource(SampleImages.CreateGradient(), "gradient");

        private void OnGenerateCheckerboard(object? sender, RoutedEventArgs e) =>
            SetGeneratedSource(SampleImages.CreateCheckerboard(), "checkerboard");

        private void SetGeneratedSource(PixelBuffer buffer, string name)
        {
            try
            {
                // Encode the managed buffer to PNG so it becomes an ordinary encoded
                // source; this exercises the PixelBuffer -> encoder path directly.
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(new BitmapEncoderFrame { Pixels = buffer });

                using var stream = new MemoryStream();
                encoder.Save(stream);

                SetSource(stream.ToArray(), $"generated {name} ({buffer.Size.Width}x{buffer.Size.Height}, PNG)");
            }
            catch (Exception ex)
            {
                Log("Generate failed: " + ex.Message);
            }
        }

        private void SetSource(byte[] bytes, string label)
        {
            try
            {
                BitmapImageInfo info;
                using (var idStream = new MemoryStream(bytes))
                    info = BitmapDecoder.Identify(idStream);

                _sourceBytes = bytes;

                SourceInfo.Text = FormatInfo(label, info);

                // Decode with no target format: the frame keeps the file's native pixel
                // format, and realizing the bitmap transcodes it to a render-installable
                // format when needed, so displaying never requires pre-selecting one.
                ReplacePreview(ref _sourcePreview, DecodeToBitmap(bytes, null), SourcePreview);

                // Default the crop box to the whole image.
                CropW.Value = info.PixelSize.Width;
                CropH.Value = info.PixelSize.Height;
                CropX.Value = 0;
                CropY.Value = 0;

                Log($"Loaded '{label}': {info.FormatName} {info.PixelSize.Width}x{info.PixelSize.Height}" +
                    (info.HasAlpha is { } hasAlpha ? $", alpha={hasAlpha}" : ""));
            }
            catch (Exception ex)
            {
                Log("Load failed: " + ex.Message);
            }
        }

        private static string FormatInfo(string label, BitmapImageInfo info)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"source     : {label}");
            sb.AppendLine($"format     : {info.FormatName}");
            sb.AppendLine($"pixel size : {info.PixelSize.Width} x {info.PixelSize.Height}");
            sb.AppendLine($"dpi        : {(info.Dpi is { } d ? $"{d.X:0} x {d.Y:0}" : "not stored (Skia reports none)")}");
            sb.AppendLine($"pixel fmt  : {(info.PixelFormat is { } pf ? pf.ToString() : "not declared in header")}");
            sb.AppendLine($"frames     : {(info.FrameCount is { } fc ? fc.ToString() : "unknown without a full parse")}");
            sb.Append($"has alpha  : {(info.HasAlpha is { } a ? a.ToString() : "not declared in header")}");
            return sb.ToString();
        }

        // --- Decode plan -----------------------------------------------------

        private void OnDecodeWithPlan(object? sender, RoutedEventArgs e)
        {
            if (_sourceBytes is null)
            {
                Log("No source loaded.");
                return;
            }

            try
            {
                var (format, alpha) = SelectedTargetFormat();

                var options = new BitmapDecodeOptions
                {
                    TargetFormat = format,
                    TargetAlphaFormat = alpha,
                    Interpolation = s_interpolation[Math.Max(0, InterpolationCombo.SelectedIndex)],
                    RespectExifOrientation = RespectExif.IsChecked == true,
                };

                var targetWidth = (int)(TargetWidth.Value ?? 0);
                var targetHeight = (int)(TargetHeight.Value ?? 0);
                if (targetWidth > 0 || targetHeight > 0)
                    options.TargetSize = new PixelSize(targetWidth, targetHeight);

                if (CropEnabled.IsChecked == true)
                {
                    options.SourceRegion = new PixelRect(
                        (int)(CropX.Value ?? 0), (int)(CropY.Value ?? 0),
                        (int)(CropW.Value ?? 1), (int)(CropH.Value ?? 1));
                }

                var maxPixels = (long)(MaxPixels.Value ?? 0);
                if (maxPixels > 0)
                    options.MaxPixels = maxPixels;

                var watch = Stopwatch.StartNew();
                var bitmap = DecodeToBitmap(_sourceBytes, options);
                watch.Stop();

                ReplacePreview(ref _decodeResult, bitmap, DecodeResult);

                DecodeInfo.Text =
                    $"Decoded {bitmap.PixelSize.Width}x{bitmap.PixelSize.Height}, " +
                    $"format {bitmap.Format?.ToString() ?? "?"}, alpha {bitmap.AlphaFormat?.ToString() ?? "?"}, " +
                    $"dpi {bitmap.Dpi.X:0}x{bitmap.Dpi.Y:0}, in {watch.Elapsed.TotalMilliseconds:0.0} ms.";
                Log("Decode plan applied: " + DecodeInfo.Text);
            }
            catch (Exception ex)
            {
                DecodeInfo.Text = "Decode failed: " + ex.Message;
                Log("Decode failed: " + ex.Message);
            }
        }

        private (PixelFormat, AlphaFormat) SelectedTargetFormat()
        {
            var entry = s_targetFormats[Math.Max(0, TargetFormatCombo.SelectedIndex)];
            return (entry.Format, entry.Alpha);
        }

        // --- Transform & encode ---------------------------------------------

        private void OnEncodeFormatChanged(object? sender, SelectionChangedEventArgs e)
        {
            // Fires while the combo is being populated in the constructor, before the
            // option panels are usable.
            if (PngPanel is null || JpegPanel is null || WebpPanel is null)
                return;

            var index = EncodeFormatCombo.SelectedIndex;
            PngPanel.IsVisible = index == 0;
            JpegPanel.IsVisible = index == 1;
            WebpPanel.IsVisible = index == 2;
        }

        private void OnEncodeRoundTrip(object? sender, RoutedEventArgs e)
        {
            if (_sourceBytes is null)
            {
                Log("No source loaded.");
                return;
            }

            try
            {
                using var source = DecodeToBitmap(_sourceBytes, null);
                var encoder = BuildEncoder(out var formatName);

                using var encoded = new MemoryStream();
                source.Save(encoded, encoder);
                var bytes = encoded.ToArray();

                // Re-decode with no target format; realizing the bitmap transcodes the
                // native format (a JPEG re-decodes to Rgb24 on ImageSharp) when it must.
                var roundTripped = DecodeToBitmap(bytes, null);
                ReplacePreview(ref _encodeResult, roundTripped, EncodeResult);

                EncodeInfo.Text =
                    $"Encoded {formatName}: {bytes.Length:N0} bytes, transform [{DescribeTransform(encoder.Transform)}]. " +
                    $"Re-decoded to {roundTripped.PixelSize.Width}x{roundTripped.PixelSize.Height}.";
                Log(EncodeInfo.Text);
            }
            catch (Exception ex)
            {
                EncodeInfo.Text = "Encode failed: " + ex.Message;
                Log("Encode failed: " + ex.Message);
            }
        }

        private async void OnSaveToFile(object? sender, RoutedEventArgs e)
        {
            if (_sourceBytes is null)
            {
                Log("No source loaded.");
                return;
            }

            try
            {
                var top = TopLevel.GetTopLevel(this);
                if (top is null)
                    return;

                var extension = EncodeFormatCombo.SelectedIndex switch
                {
                    1 => "jpg",
                    2 => "webp",
                    _ => "png",
                };

                var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save encoded image",
                    SuggestedFileName = "imaging-demo-output",
                    DefaultExtension = extension,
                });

                if (file is null)
                    return;

                using var source = DecodeToBitmap(_sourceBytes, null);
                var encoder = BuildEncoder(out var formatName);

                await using var stream = await file.OpenWriteAsync();
                source.Save(stream, encoder);

                Log($"Saved {formatName} to '{file.Name}'.");
            }
            catch (Exception ex)
            {
                Log("Save failed: " + ex.Message);
            }
        }

        private void OnLosslessCheck(object? sender, RoutedEventArgs e)
        {
            if (_sourceBytes is null)
            {
                Log("No source loaded.");
                return;
            }

            try
            {
                // Decode deterministically to a managed snapshot, PNG-encode it, decode
                // it back the same way, and compare pixel-for-pixel. PNG is the only
                // lossless encode both backends share, so this must match exactly.
                var format = PixelFormats.Bgra8888;
                var alpha = AlphaFormat.Unpremul;

                var before = DecodeToPixelBuffer(_sourceBytes, format, alpha);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(new BitmapEncoderFrame { Pixels = before });

                using var stream = new MemoryStream();
                encoder.Save(stream);
                var pngBytes = stream.ToArray();

                var after = DecodeToPixelBuffer(pngBytes, format, alpha);

                var identical = PixelsEqual(before, after);
                var message = identical
                    ? $"Lossless PNG round-trip verified: {before.Size.Width}x{before.Size.Height} " +
                      $"Bgra8888 pixels identical before and after ({pngBytes.Length:N0} bytes)."
                    : "Round-trip MISMATCH - pixels differ (unexpected for PNG).";

                EncodeInfo.Text = message;
                Log($"{message} [backend: {ImagingBackend.Current.Name}]");
            }
            catch (Exception ex)
            {
                EncodeInfo.Text = "Round-trip check failed: " + ex.Message;
                Log("Round-trip check failed: " + ex.Message);
            }
        }

        private BitmapEncoder BuildEncoder(out string formatName)
        {
            BitmapEncoder encoder;

            switch (EncodeFormatCombo.SelectedIndex)
            {
                case 1:
                    encoder = new JpegBitmapEncoder { Quality = (int)(JpegQuality.Value ?? 90) };
                    formatName = "JPEG";
                    break;
                case 2:
                    encoder = new WebpBitmapEncoder
                    {
                        Lossless = WebpLossless.IsChecked == true,
                        Quality = (int)(WebpQuality.Value ?? 75),
                    };
                    formatName = "WebP";
                    break;
                default:
                    encoder = new PngBitmapEncoder { CompressionLevel = (int)(PngCompression.Value ?? 6) };
                    formatName = "PNG";
                    break;
            }

            encoder.Transform = new BitmapTransform(
                s_rotations[Math.Max(0, RotationCombo.SelectedIndex)],
                FlipH.IsChecked == true,
                FlipV.IsChecked == true);

            return encoder;
        }

        private static string DescribeTransform(BitmapTransform transform)
        {
            if (transform.IsIdentity)
                return "identity";

            var parts = new List<string>();
            if (transform.Rotation != BitmapRotation.None)
                parts.Add(transform.Rotation.ToString());
            if (transform.FlipHorizontal)
                parts.Add("FlipH");
            if (transform.FlipVertical)
                parts.Add("FlipV");

            return string.Join("+", parts);
        }

        // --- Shared imaging helpers -----------------------------------------

        private static Bitmap DecodeToBitmap(byte[] bytes, BitmapDecodeOptions? options)
        {
            using var stream = new MemoryStream(bytes);
            return Bitmap.Decode(stream, options);
        }

        private static PixelBuffer DecodeToPixelBuffer(byte[] bytes, PixelFormat format, AlphaFormat alpha)
        {
            var options = new BitmapDecodeOptions { TargetFormat = format, TargetAlphaFormat = alpha };

            using var stream = new MemoryStream(bytes);
            using var decoder = BitmapDecoder.Create(stream, options);
            using var frame = decoder.ReadNextFrame()
                ?? throw new InvalidOperationException("The image produced no frame.");

            return frame.ToPixelBuffer();
        }

        private static bool PixelsEqual(PixelBuffer a, PixelBuffer b)
        {
            if (a.Size != b.Size || !a.Format.Equals(b.Format))
                return false;

            var spanA = a.Pixels.Span;
            var spanB = b.Pixels.Span;
            var rowBytes = a.Size.Width * (a.Format.BitsPerPixel / 8);

            for (var y = 0; y < a.Size.Height; y++)
            {
                var rowA = spanA.Slice(y * a.Stride, rowBytes);
                var rowB = spanB.Slice(y * b.Stride, rowBytes);

                if (!rowA.SequenceEqual(rowB))
                    return false;
            }

            return true;
        }

        private static void ReplacePreview(ref Bitmap? slot, Bitmap next, Image target)
        {
            var old = slot;
            slot = next;
            target.Source = next;
            old?.Dispose();
        }

        // --- Backend header + capability matrix -----------------------------

        private void UpdateBackendHeader()
        {
            var backend = ImagingBackend.Current;
            var requested = Program.RequestedImageSharp ? "ImageSharp" : "SkiaSharp (default)";

            var note = Program.RequestedImageSharp && !string.Equals(backend.Name, "ImageSharp", StringComparison.Ordinal)
                ? Program.ImageSharpCompiledIn
                    ? "  (requested but not bound)"
                    : "  (requested but not compiled in - no license key at build time)"
                : string.Empty;

            BackendText.Text =
                $"Active imaging backend: {backend.Name}    requested: {requested}    " +
                $"render backend: SkiaSharp (always){note}\n" +
                "Switch backend with:  dotnet run -- --imaging=imagesharp   (or set AVALONIA_IMAGING=imagesharp)";

            CapabilityText.Text = BuildCapabilityMatrix(backend);
        }

        private static string BuildCapabilityMatrix(IImagingBackend backend)
        {
            var sb = new StringBuilder();
            sb.AppendLine("FORMAT    DECODE ENCODE META ANIM PAL 16BPC ICC FUSED");

            foreach (var codec in backend.SupportedCodecs)
            {
                var caps = codec.Capabilities;
                sb.AppendLine(string.Format(
                    "{0,-9} {1,-6} {2,-6} {3,-4} {4,-4} {5,-3} {6,-5} {7,-3} {8}",
                    codec.FormatName,
                    Mark(caps, BitmapCodecCapabilities.Decode),
                    Mark(caps, BitmapCodecCapabilities.Encode),
                    Mark(caps, BitmapCodecCapabilities.Metadata),
                    Mark(caps, BitmapCodecCapabilities.Animation),
                    Mark(caps, BitmapCodecCapabilities.Palette),
                    Mark(caps, BitmapCodecCapabilities.HighDepth),
                    Mark(caps, BitmapCodecCapabilities.ColorProfile),
                    Mark(caps, BitmapCodecCapabilities.FusedDecode)));
            }

            sb.Append("This sample uses only the SkiaSharp-and-ImageSharp intersection: " +
                      "decode PNG/JPEG/WebP/GIF/BMP, encode PNG/JPEG/WebP.");
            return sb.ToString();
        }

        private static string Mark(BitmapCodecCapabilities caps, BitmapCodecCapabilities flag) =>
            (caps & flag) != 0 ? "yes" : "-";

        // --- Log -------------------------------------------------------------

        private void Log(string message)
        {
            LogBox.Text = LogBox.Text is { Length: > 0 } existing
                ? existing + Environment.NewLine + message
                : message;
            LogBox.CaretIndex = LogBox.Text.Length;
        }
    }
}
