using System;
using System.Buffers.Binary;
using System.IO;

namespace Avalonia.Base.UnitTests.Media.Fonts
{
    /// <summary>
    /// Writes font files into a private temporary directory, so tests can load fonts by path and
    /// tell their own entries apart from those of tests running in parallel.
    /// </summary>
    internal sealed class FontFileFixture : IDisposable
    {
        public const string InterRegularUri =
            "resm:Avalonia.Base.UnitTests.Assets.Inter-Regular.ttf?assembly=Avalonia.Base.UnitTests";

        public const string InterBoldUri =
            "resm:Avalonia.Base.UnitTests.Assets.Inter-Bold.ttf?assembly=Avalonia.Base.UnitTests";

        public const string ManropeLightUri =
            "resm:Avalonia.Base.UnitTests.Assets.Manrope-Light.ttf?assembly=Avalonia.Base.UnitTests";

        public const string NotoMonoUri =
            "resm:Avalonia.Base.UnitTests.Assets.NotoMono-Regular.ttf?assembly=Avalonia.Base.UnitTests";

        public FontFileFixture()
        {
            DirectoryName = "fontfiles-" + Guid.NewGuid().ToString("N");
            Directory = Path.Combine(Path.GetTempPath(), DirectoryName);

            System.IO.Directory.CreateDirectory(Directory);
        }

        /// <summary>
        /// Gets the unique name of the fixture directory, which appears in every path below it.
        /// </summary>
        public string DirectoryName { get; }

        public string Directory { get; }

        public string Write(string fileName, byte[] data)
        {
            var path = Path.Combine(Directory, fileName);

            File.WriteAllBytes(path, data);

            return path;
        }

        public static byte[] ReadAsset(string uri)
        {
            using var stream = SfntFaceTestHelper.OpenAsset(uri);
            using var ms = new MemoryStream();

            stream.CopyTo(ms);

            return ms.ToArray();
        }

        /// <summary>
        /// Builds a TrueType collection with one face per supplied font. Every face gets its own copy
        /// of its font's tables and its own table directory, as in collections that share nothing.
        /// </summary>
        public static byte[] BuildCollection(params byte[][] fonts)
        {
            var headerSize = 12 + 4 * fonts.Length;
            var offsets = new int[fonts.Length];
            var size = Align(headerSize);

            for (var i = 0; i < fonts.Length; i++)
            {
                offsets[i] = size;
                size = Align(size + fonts[i].Length);
            }

            var result = new byte[size];

            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0), 0x74746366); // 'ttcf'
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), 0x00010000);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), (uint)fonts.Length);

            for (var i = 0; i < fonts.Length; i++)
            {
                var start = offsets[i];

                BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12 + 4 * i), (uint)start);

                fonts[i].CopyTo(result.AsSpan(start));

                // Table record offsets are absolute file offsets: rebase them by the face's start.
                var numTables = BinaryPrimitives.ReadUInt16BigEndian(result.AsSpan(start + 4));

                for (var t = 0; t < numTables; t++)
                {
                    var offsetPosition = start + 12 + t * 16 + 8;
                    var offset = BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(offsetPosition));

                    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(offsetPosition), offset + (uint)start);
                }
            }

            return result;
        }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Directory, true);
            }
            catch (IOException)
            {
                // A face a failed assertion left alive still maps a file; the OS temp cleanup gets it.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static int Align(int value) => (value + 3) & ~3;
    }
}
