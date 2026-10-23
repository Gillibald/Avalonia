using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Avalonia.Imaging.TestKit.Fixtures
{
    /// <summary>
    /// Writes minimal ICC v2 display profiles: an RGB matrix/TRC profile with the sRGB
    /// primaries and a pure gamma curve, so a transform between two of them changes only
    /// the transfer curve and has an exact expected result.
    /// </summary>
    public static class IccProfileFixture
    {
        // The sRGB colorants adapted to the D50 profile connection space, as in the ICC sRGB profiles.
        private static readonly double[][] s_srgbColorants =
        {
            new[] { 0.4361, 0.2225, 0.0139 },
            new[] { 0.3851, 0.7169, 0.0971 },
            new[] { 0.1431, 0.0606, 0.7141 },
        };

        private static readonly double[] s_d50 = { 0.9642, 1.0, 0.8249 };

        /// <summary>Creates an RGB profile with the sRGB primaries and the given gamma.</summary>
        public static byte[] CreateRgb(double gamma)
        {
            var xyz = new Func<double[], byte[]>(XyzTag);
            var curve = CurveTag(gamma);

            return Write("RGB ", new List<(string, byte[])>
            {
                ("wtpt", xyz(s_d50)),
                ("rXYZ", xyz(s_srgbColorants[0])),
                ("gXYZ", xyz(s_srgbColorants[1])),
                ("bXYZ", xyz(s_srgbColorants[2])),
                ("rTRC", curve),
                ("gTRC", curve),
                ("bTRC", curve),
            });
        }

        /// <summary>Creates a gray profile with the given gamma.</summary>
        public static byte[] CreateGray(double gamma) => Write("GRAY", new List<(string, byte[])>
        {
            ("wtpt", XyzTag(s_d50)),
            ("kTRC", CurveTag(gamma)),
        });

        private static byte[] Write(string colorSpace, List<(string Signature, byte[] Data)> tags)
        {
            var tableSize = 4 + tags.Count * 12;
            var offsets = new int[tags.Count];
            var size = 128 + tableSize;

            for (var i = 0; i < tags.Count; i++)
            {
                offsets[i] = size;
                size += (tags[i].Data.Length + 3) & ~3;
            }

            var profile = new byte[size];
            var span = profile.AsSpan();

            BinaryPrimitives.WriteUInt32BigEndian(span, (uint)size);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(8), 0x02100000);
            WriteSignature(span.Slice(12), "mntr");
            WriteSignature(span.Slice(16), colorSpace);
            WriteSignature(span.Slice(20), "XYZ ");
            WriteSignature(span.Slice(36), "acsp");
            WriteXyz(span.Slice(68), s_d50);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(128), (uint)tags.Count);

            for (var i = 0; i < tags.Count; i++)
            {
                var entry = span.Slice(132 + i * 12);

                WriteSignature(entry, tags[i].Signature);
                BinaryPrimitives.WriteUInt32BigEndian(entry.Slice(4), (uint)offsets[i]);
                BinaryPrimitives.WriteUInt32BigEndian(entry.Slice(8), (uint)tags[i].Data.Length);
                tags[i].Data.CopyTo(span.Slice(offsets[i]));
            }

            return profile;
        }

        private static byte[] XyzTag(double[] xyz)
        {
            var tag = new byte[20];

            WriteSignature(tag, "XYZ ");
            WriteXyz(tag.AsSpan(8), xyz);

            return tag;
        }

        private static byte[] CurveTag(double gamma)
        {
            var tag = new byte[14];

            WriteSignature(tag, "curv");
            BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt16BigEndian(tag.AsSpan(12), (ushort)Math.Round(gamma * 256));

            return tag;
        }

        private static void WriteXyz(Span<byte> destination, double[] xyz)
        {
            for (var i = 0; i < 3; i++)
                BinaryPrimitives.WriteInt32BigEndian(destination.Slice(i * 4), (int)Math.Round(xyz[i] * 65536));
        }

        private static void WriteSignature(Span<byte> destination, string signature)
        {
            for (var i = 0; i < 4; i++)
                destination[i] = (byte)signature[i];
        }
    }
}
