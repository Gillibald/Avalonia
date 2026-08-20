using System;
using System.Buffers.Binary;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts
{
    public class SfntFaceTests
    {
        private const string InterFontUri = "resm:Avalonia.Base.UnitTests.Assets.Inter-Regular.ttf?assembly=Avalonia.Base.UnitTests";

        private static readonly OpenTypeTag s_nameTag = new OpenTypeTag('n', 'a', 'm', 'e');
        private static readonly OpenTypeTag s_cmapTag = new OpenTypeTag('c', 'm', 'a', 'p');

        [Fact]
        public void Should_Load_Single_Face_From_Stream()
        {
            using var stream = OpenInterStream();

            Assert.True(SfntFace.TryLoad(stream, out var face));

            using (face)
            {
                Assert.Equal(0, face.FaceIndex);
                Assert.True(face.TryGetTable(s_nameTag, out var nameTable));
                Assert.True(nameTable.Length > 0);
                Assert.True(face.TryGetTable(s_cmapTag, out _));
                Assert.True(face.TryGetFontFileData(out var data, out var faceIndex));
                Assert.Equal(0, faceIndex);
                Assert.Equal(stream.Length, data.Length);
            }
        }

        [Fact]
        public void Should_Load_Faces_From_TrueType_Collection()
        {
            var font = ReadInterBytes();
            var ttc = BuildTtc(font, faceCount: 2);

            for (var i = 0; i < 2; i++)
            {
                Assert.True(SfntFace.TryLoad(new MemoryStream(ttc), i, out var face));

                using (face)
                {
                    Assert.Equal(i, face.FaceIndex);
                    Assert.True(face.TryGetTable(s_nameTag, out _));
                    Assert.True(face.TryGetFontFileData(out var data, out var faceIndex));
                    Assert.Equal(i, faceIndex);
                    Assert.Equal(ttc.Length, data.Length);

                    var glyphTypeface = new GlyphTypeface(face);

                    Assert.Equal("Inter", glyphTypeface.FamilyName);
                }
            }
        }

        [Fact]
        public void Should_Reject_Invalid_Face_Index()
        {
            var font = ReadInterBytes();
            var ttc = BuildTtc(font, faceCount: 2);

            Assert.False(SfntFace.TryLoad(new MemoryStream(ttc), 2, out _));
            Assert.False(SfntFace.TryLoad(new MemoryStream(ttc), -1, out _));

            // Single-face files only have face index zero.
            Assert.False(SfntFace.TryLoad(new MemoryStream(font), 1, out _));
        }

        [Fact]
        public void Should_Reject_Invalid_Font_Data()
        {
            // Too short for a table directory header.
            Assert.False(SfntFace.TryLoad(new MemoryStream(new byte[4]), out _));

            // Directory header claims more table records than the data can hold.
            var truncated = new byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(truncated.AsSpan(0), 0x00010000);
            BinaryPrimitives.WriteUInt16BigEndian(truncated.AsSpan(4), ushort.MaxValue);

            Assert.False(SfntFace.TryLoad(new MemoryStream(truncated), out _));

            // A directory without any tables.
            var empty = new byte[12];
            BinaryPrimitives.WriteUInt32BigEndian(empty.AsSpan(0), 0x00010000);

            Assert.False(SfntFace.TryLoad(new MemoryStream(empty), out _));
        }

        [Fact]
        public void Should_Reject_Table_With_Out_Of_Bounds_Offset()
        {
            // One table record whose offset and length point far beyond the end of the data.
            var data = new byte[12 + 16];
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0), 0x00010000);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), s_nameTag);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), 0xFFFF0000);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(24), 0xFFFF0000);

            Assert.True(SfntFace.TryLoad(new MemoryStream(data), out var face));

            using (face)
            {
                Assert.False(face.TryGetTable(s_nameTag, out _));
            }
        }

        [Fact]
        public void Should_Accept_Otto_Sfnt_Version()
        {
            // Patch the sfnt version tag to 'OTTO'; the table directory itself is format-agnostic.
            var font = ReadInterBytes();
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(0), 0x4F54544F);

            Assert.True(SfntFace.TryLoad(new MemoryStream(font), out var face));

            using (face)
            {
                var glyphTypeface = new GlyphTypeface(face);

                Assert.Equal("Inter", glyphTypeface.FamilyName);
            }
        }

        [Fact]
        public void Should_Load_Face_From_File_Path()
        {
            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ttf");

            try
            {
                File.WriteAllBytes(path, ReadInterBytes());

                Assert.True(SfntFace.TryLoad(path, 0, out var face));

                using (face)
                {
                    var glyphTypeface = new GlyphTypeface(face);

                    Assert.Equal("Inter", glyphTypeface.FamilyName);
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Should_Not_Load_Missing_File()
        {
            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ttf");

            Assert.False(SfntFace.TryLoad(path, 0, out _));
        }

        [Fact]
        public void Clone_Should_Share_Underlying_Data()
        {
            using var stream = OpenInterStream();

            Assert.True(SfntFace.TryLoad(stream, out var face));

            var clone = face.Clone();

            // The original's reference is released; the clone must keep the shared data alive.
            face.Dispose();

            Assert.False(face.TryGetTable(s_nameTag, out _));
            Assert.True(clone.TryGetTable(s_nameTag, out var nameTable));
            Assert.True(nameTable.Length > 0);

            clone.Dispose();

            Assert.False(clone.TryGetTable(s_nameTag, out _));
        }

        private static Stream OpenInterStream() => SfntFaceTestHelper.OpenAsset(InterFontUri);

        private static byte[] ReadInterBytes()
        {
            using var stream = OpenInterStream();
            using var ms = new MemoryStream();

            stream.CopyTo(ms);

            return ms.ToArray();
        }

        /// <summary>
        /// Builds a TrueType collection whose face directories all reference one stored copy of
        /// the supplied font.
        /// </summary>
        private static byte[] BuildTtc(byte[] font, int faceCount)
        {
            var header = 12 + 4 * faceCount;
            var result = new byte[header + font.Length];

            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0), 0x74746366); // 'ttcf'
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), 0x00010000);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), (uint)faceCount);

            for (var i = 0; i < faceCount; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12 + 4 * i), (uint)header);
            }

            font.CopyTo(result.AsSpan(header));

            // Table record offsets are absolute file offsets - rebase them by the header size.
            var numTables = BinaryPrimitives.ReadUInt16BigEndian(result.AsSpan(header + 4));

            for (var i = 0; i < numTables; i++)
            {
                var offsetPosition = header + 12 + i * 16 + 8;
                var offset = BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(offsetPosition));

                BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(offsetPosition), offset + (uint)header);
            }

            return result;
        }
    }

    public class GlyphTypefaceFontMemoryTests
    {
        private const string InterFontUri = "resm:Avalonia.Base.UnitTests.Assets.Inter-Regular.ttf?assembly=Avalonia.Base.UnitTests";

        [Fact]
        public void Should_Create_GlyphTypeface_From_Font_Memory()
        {
            using var stream = SfntFaceTestHelper.OpenAsset(InterFontUri);

            Assert.True(SfntFace.TryLoad(stream, out var face));

            var glyphTypeface = new GlyphTypeface(face);

            Assert.Equal("Inter", glyphTypeface.FamilyName);
            Assert.Same(face, glyphTypeface.FontMemory);
            Assert.Equal(FontWeight.Normal, glyphTypeface.Weight);
            Assert.Equal(FontStyle.Normal, glyphTypeface.Style);
            Assert.True(glyphTypeface.GlyphCount > 0);
        }

        [Fact]
        public void Simulations_Should_Be_Applied_To_Memory_Backed_GlyphTypeface()
        {
            using var stream = SfntFaceTestHelper.OpenAsset(InterFontUri);

            Assert.True(SfntFace.TryLoad(stream, out var face));

            var glyphTypeface = new GlyphTypeface(face, FontSimulations.Bold | FontSimulations.Oblique);

            Assert.Equal(FontWeight.Bold, glyphTypeface.Weight);
            Assert.Equal(FontStyle.Italic, glyphTypeface.Style);
            Assert.Equal(FontSimulations.Bold | FontSimulations.Oblique, glyphTypeface.FontSimulations);
        }

        [Fact]
        public void Dispose_Should_Cascade_To_Font_Memory()
        {
            using var stream = SfntFaceTestHelper.OpenAsset(InterFontUri);

            Assert.True(SfntFace.TryLoad(stream, out var face));

            var glyphTypeface = new GlyphTypeface(face);

            glyphTypeface.Dispose();

            Assert.False(face.TryGetTable(new OpenTypeTag('n', 'a', 'm', 'e'), out _));
        }

        [Fact]
        public void PlatformTypeface_Should_Be_Created_By_Render_Interface()
        {
            using (UnitTestApplication.Start(TestServices.MockPlatformRenderInterface))
            {
                using var stream = SfntFaceTestHelper.OpenAsset(InterFontUri);

                Assert.True(SfntFace.TryLoad(stream, out var face));

                var glyphTypeface = new GlyphTypeface(face);

                var platformTypeface = glyphTypeface.PlatformTypeface;

                Assert.NotNull(platformTypeface);
                Assert.Same(platformTypeface, glyphTypeface.PlatformTypeface);

                glyphTypeface.Dispose();

                // The cascade disposes the render typeface and the font memory.
                Assert.False(face.TryGetTable(new OpenTypeTag('n', 'a', 'm', 'e'), out _));
            }
        }

    }

    internal static class SfntFaceTestHelper
    {
        public static Stream OpenAsset(string uri)
        {
            var assetLoader = new StandardAssetLoader();

            return assetLoader.Open(new Uri(uri));
        }
    }
}
