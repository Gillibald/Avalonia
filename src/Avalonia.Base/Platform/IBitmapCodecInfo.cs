using System;
using System.Collections.Generic;
using Avalonia.Metadata;

namespace Avalonia.Platform
{
    /// <summary>
    /// Describes what the active imaging backend can do for a single container format,
    /// before any stream is opened.
    /// </summary>
    /// <remarks>
    /// These flags describe per-format support of the active backend and are used to
    /// fail fast on unsupported operations and to query support up front. What a live
    /// decoder instance actually exposes is discovered through the capability
    /// interfaces (<see cref="IMetadataSource"/>, <see cref="IAnimatedFrame"/>, ...)
    /// by pattern matching.
    /// </remarks>
    [Flags]
    public enum BitmapCodecCapabilities
    {
        /// <summary>No support for this format.</summary>
        None = 0,

        /// <summary>The backend can decode this format.</summary>
        Decode = 1 << 0,

        /// <summary>The backend can encode this format.</summary>
        Encode = 1 << 1,

        /// <summary>The backend reads and writes metadata for this format.</summary>
        Metadata = 1 << 2,

        /// <summary>The backend decodes more than the first frame of this format.</summary>
        MultiFrame = 1 << 3,

        /// <summary>The backend exposes animation data (frame delay, disposal, loop count).</summary>
        Animation = 1 << 4,

        /// <summary>The backend exposes indexed pixel access and palettes.</summary>
        Palette = 1 << 5,

        /// <summary>
        /// The backend can execute (part of) the decode plan natively, e.g. scale at
        /// decode time. This is an optimization flag: the decode plan semantics are
        /// guaranteed regardless, with the shared pixel pipeline covering formats the
        /// backend cannot fuse.
        /// </summary>
        FusedDecode = 1 << 6,

        /// <summary>The backend passes ICC color profiles through.</summary>
        ColorProfile = 1 << 7,

        /// <summary>The backend decodes true high bit depth (16bpc/float) without upscaling 8-bit data.</summary>
        HighDepth = 1 << 8,

        /// <summary>The backend extracts embedded thumbnails.</summary>
        Thumbnail = 1 << 9,

        /// <summary>The backend encodes multiple frames (animated GIF, multi-page TIFF).</summary>
        MultiFrameEncode = 1 << 10,
    }

    /// <summary>
    /// Describes a container format supported by the active imaging backend.
    /// </summary>
    [Unstable]
    public interface IBitmapCodecInfo
    {
        /// <summary>
        /// Gets the well-known format name, e.g. "JPEG", "PNG", "GIF".
        /// </summary>
        string FormatName { get; }

        /// <summary>
        /// Gets the MIME types associated with the format.
        /// </summary>
        IReadOnlyList<string> MimeTypes { get; }

        /// <summary>
        /// Gets the file extensions associated with the format, without a leading dot.
        /// </summary>
        IReadOnlyList<string> FileExtensions { get; }

        /// <summary>
        /// Gets the container format identifier. Well-known formats use the WIC
        /// container GUIDs, see <see cref="Avalonia.Media.Imaging.BitmapContainerFormats"/>.
        /// </summary>
        Guid ContainerFormat { get; }

        /// <summary>
        /// Gets what the active backend can do for this format.
        /// </summary>
        BitmapCodecCapabilities Capabilities { get; }
    }
}
