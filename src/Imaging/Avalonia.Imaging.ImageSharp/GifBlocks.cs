using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using Avalonia.Platform;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Metadata;

namespace Avalonia.Imaging.ImageSharp
{
    /// <summary>
    /// The GIF blocks ImageSharp keeps after decoding, at their WIC paths. ImageSharp
    /// composites the frames, so frame rectangles and the logical screen fields it drops are
    /// not reported.
    /// </summary>
    internal static class GifBlocks
    {
        private static readonly byte[] s_netscape = Encoding.ASCII.GetBytes("NETSCAPE2.0");

        public static IEnumerable<KeyValuePair<string, MetadataValue>> ForContainer(Image image)
        {
            var gif = image.Metadata.GetGifMetadata();
            var table = gif.GlobalColorTable;

            yield return Entry("/logscrdesc/Width", Short((ushort)image.Width));
            yield return Entry("/logscrdesc/Height", Short((ushort)image.Height));
            yield return Entry("/logscrdesc/GlobalColorTableFlag", Byte(table is { Length: > 0 } ? (byte)1 : (byte)0));

            // The table holds 2^(size + 1) entries.
            if (table is { Length: > 0 } colors)
                yield return Entry("/logscrdesc/GlobalColorTableSize", Byte((byte)(Math.Max(1, BitOperations.Log2((uint)colors.Length - 1) + 1) - 1)));

            // ImageSharp reports a loop count of one for files without a NETSCAPE block, so
            // the block is only reported for animations.
            if (image.Frames.Count > 1)
            {
                var loop = gif.RepeatCount;

                yield return Entry("/[0]appext/Application", new MetadataValue(MetadataValueType.Undefined, s_netscape));
                yield return Entry("/[0]appext/Data",
                    new MetadataValue(MetadataValueType.Undefined, new byte[] { 3, 1, (byte)loop, (byte)(loop >> 8), 0 }));
            }

            for (var i = 0; i < gif.Comments.Count; i++)
            {
                yield return Entry("/[" + i.ToString(CultureInfo.InvariantCulture) + "]commentext/TextEntry",
                    new MetadataValue(MetadataValueType.Ascii, Encoding.Latin1.GetBytes(gif.Comments[i])));
            }
        }

        public static IEnumerable<KeyValuePair<string, MetadataValue>> ForFrame(ImageFrameMetadata frameMetadata)
        {
            var gif = frameMetadata.GetGifMetadata();

            yield return Entry("/grctlext/Disposal", Byte((byte)gif.DisposalMode));
            yield return Entry("/grctlext/TransparencyFlag", Byte(gif.HasTransparency ? (byte)1 : (byte)0));
            yield return Entry("/grctlext/Delay", Short((ushort)gif.FrameDelay));
            yield return Entry("/grctlext/TransparentColorIndex", Byte(gif.TransparencyIndex));
            yield return Entry("/imgdesc/LocalColorTableFlag", Byte(gif.ColorTableMode == FrameColorTableMode.Local ? (byte)1 : (byte)0));
        }

        private static KeyValuePair<string, MetadataValue> Entry(string path, MetadataValue value) => new(path, value);

        private static MetadataValue Byte(byte value) => new(MetadataValueType.Byte, new[] { value });

        private static MetadataValue Short(ushort value) => new(MetadataValueType.Short, BitConverter.GetBytes(value));
    }
}
