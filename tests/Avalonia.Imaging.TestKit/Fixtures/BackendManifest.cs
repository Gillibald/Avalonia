using System;
using System.Collections.Generic;
using Avalonia.Platform;

namespace Avalonia.Imaging.TestKit.Fixtures
{
    /// <summary>
    /// Declares what the backend under test supports, per container format, so the
    /// contract tests can gate their assertions and parameterize their expectations.
    /// </summary>
    public sealed class BackendManifest
    {
        private Dictionary<string, FormatManifest>? _byName;

        /// <summary>
        /// Gets the per-format declarations. Format names match
        /// <see cref="IBitmapCodecInfo.FormatName"/>, e.g. "PNG", "BMP".
        /// </summary>
        public IReadOnlyList<FormatManifest> Formats { get; init; } = Array.Empty<FormatManifest>();

        /// <summary>
        /// Gets whether the backend observes
        /// <see cref="Avalonia.Media.Imaging.BitmapDecodeOptions.Cancellation"/>.
        /// </summary>
        public bool SupportsCancellation { get; init; }

        /// <summary>
        /// Returns the declaration for a format, or null when the manifest does not
        /// mention it. The lookup ignores case to be forgiving about name spelling;
        /// the capability contract still asserts the exact catalog names.
        /// </summary>
        public FormatManifest? TryGetFormat(string formatName)
        {
            _ = formatName ?? throw new ArgumentNullException(nameof(formatName));

            var byName = _byName ??= BuildIndex();

            return byName.TryGetValue(formatName, out var format) ? format : null;
        }

        private Dictionary<string, FormatManifest> BuildIndex()
        {
            var byName = new Dictionary<string, FormatManifest>(StringComparer.OrdinalIgnoreCase);

            foreach (var format in Formats)
            {
                if (!byName.TryAdd(format.FormatName, format))
                    throw new InvalidOperationException($"The manifest declares '{format.FormatName}' twice.");
            }

            return byName;
        }
    }

    /// <summary>
    /// The declared support for a single container format.
    /// </summary>
    public sealed class FormatManifest
    {
        public FormatManifest(string formatName)
        {
            FormatName = !string.IsNullOrWhiteSpace(formatName)
                ? formatName
                : throw new ArgumentException("A format name is required.", nameof(formatName));
        }

        /// <summary>Gets the format name, matching <see cref="IBitmapCodecInfo.FormatName"/>.</summary>
        public string FormatName { get; }

        /// <summary>Gets whether the backend decodes this format.</summary>
        public bool Decode { get; init; }

        /// <summary>Gets whether the backend encodes this format.</summary>
        public bool Encode { get; init; }

        /// <summary>Gets whether the backend exposes animation data for this format.</summary>
        public bool Animation { get; init; }

        /// <summary>Gets whether the backend exposes indexed pixels and palettes for this format.</summary>
        public bool Palette { get; init; }

        /// <summary>Gets whether the backend reads and writes metadata for this format.</summary>
        public bool Metadata { get; init; }

        /// <summary>Gets whether decoders of this format have random frame access.</summary>
        public bool SeekableFrames { get; init; }

        /// <summary>Gets whether the backend encodes more than one frame for this format.</summary>
        public bool MultiFrameEncode { get; init; }

        /// <summary>Gets the decode plan parts the backend executes natively for this format.</summary>
        public FusedDecodeParts FusedParts { get; init; }

        /// <summary>
        /// Gets how many extra frame-sized buffer rents, beyond the installed
        /// destination, a decode of this format may perform.
        /// </summary>
        public int CopyBudget { get; init; }

        /// <summary>
        /// Gets the per-channel tolerance applied when decoded pixels are compared
        /// against a fixture reference.
        /// </summary>
        public int PixelTolerance { get; init; }
    }
}
