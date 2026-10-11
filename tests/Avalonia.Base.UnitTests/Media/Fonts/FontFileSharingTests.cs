using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Fonts;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts
{
    public class FontFileSharingTests
    {
        private static readonly OpenTypeTag s_nameTag = new('n', 'a', 'm', 'e');

        [Fact]
        public void Faces_Of_One_Collection_File_Share_One_File_Data()
        {
            using var fixture = new FontFileFixture();

            var path = fixture.Write("collection.ttc", BuildEightFaceCollection());
            var faces = new List<SfntFace>();

            try
            {
                for (var i = 0; i < 8; i++)
                {
                    Assert.True(SfntFace.TryLoad(path, i, out var face));

                    faces.Add(face);
                }

                AssertSameFileData(faces);
            }
            finally
            {
                DisposeAll(faces);
            }
        }

        [Fact]
        public void Releasing_The_Last_Face_Releases_The_File()
        {
            using var fixture = new FontFileFixture();

            var path = fixture.Write("collection.ttc", BuildEightFaceCollection());

            Assert.True(SfntFace.TryLoad(path, 0, out var first));
            Assert.True(SfntFace.TryLoad(path, 1, out var second));

            first.Dispose();

            // The remaining face still reads the shared data.
            Assert.True(second.TryGetTable(s_nameTag, out _));

            second.Dispose();

            // Nothing maps the file any more: it can be replaced, and a new load sees the new file.
            File.Delete(path);

            var replacement = fixture.Write("collection.ttc",
                FontFileFixture.ReadAsset(FontFileFixture.InterRegularUri));

            Assert.True(SfntFace.TryLoad(replacement, 0, out var reloaded));

            using (reloaded)
            {
                Assert.True(reloaded.TryGetFontFileData(out var data, out _));
                Assert.Equal(new FileInfo(replacement).Length, data.Length);
                Assert.False(SfntFace.TryLoad(replacement, 1, out _));
            }
        }

        [Fact]
        public void Paths_That_Differ_Only_By_Case_Share_One_File_Data()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(),
                "Case-insensitive file systems only.");

            using var fixture = new FontFileFixture();

            var path = fixture.Write("Collection.ttc", BuildEightFaceCollection());
            var faces = new List<SfntFace>();

            try
            {
                Assert.True(SfntFace.TryLoad(path, 0, out var face));
                faces.Add(face);

                Assert.True(SfntFace.TryLoad(path.ToUpperInvariant(), 1, out face));
                faces.Add(face);

                Assert.True(SfntFace.TryLoad(path.ToLowerInvariant(), 2, out face));
                faces.Add(face);

                AssertSameFileData(faces);
            }
            finally
            {
                DisposeAll(faces);
            }
        }

        [Fact]
        public void Symlinked_Paths_Share_One_File_Data()
        {
            using var fixture = new FontFileFixture();

            var path = fixture.Write("collection.ttc", BuildEightFaceCollection());
            var link = Path.Combine(fixture.Directory, "link.ttc");

            try
            {
                File.CreateSymbolicLink(link, path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Windows needs developer mode or elevation for symbolic links.
                Assert.Skip("Symbolic links cannot be created here: " + e.Message);
            }

            var faces = new List<SfntFace>();

            try
            {
                Assert.True(SfntFace.TryLoad(path, 0, out var face));
                faces.Add(face);

                Assert.True(SfntFace.TryLoad(link, 1, out face));
                faces.Add(face);

                AssertSameFileData(faces);
            }
            finally
            {
                DisposeAll(faces);
            }
        }

        [Fact]
        public void Concurrent_Loads_Of_One_File_Share_One_File_Data()
        {
            using var fixture = new FontFileFixture();

            var path = fixture.Write("collection.ttc", BuildEightFaceCollection());
            var faces = new ConcurrentBag<SfntFace>();

            try
            {
                Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
                {
                    Assert.True(SfntFace.TryLoad(path, i % 8, out var face));

                    faces.Add(face);
                });

                Assert.Equal(64, faces.Count);

                AssertSameFileData(faces.ToList());
            }
            finally
            {
                DisposeAll(faces);
            }
        }

        [Fact]
        public void Every_Face_Of_A_Font_Set_Shares_One_File_Data_Per_File()
        {
            using var fixture = new FontFileFixture();

            var files = new[]
            {
                fixture.Write("collection.ttc", BuildEightFaceCollection()),
                fixture.Write("inter.ttf", FontFileFixture.ReadAsset(FontFileFixture.InterRegularUri)),
                fixture.Write("manrope.ttf", FontFileFixture.ReadAsset(FontFileFixture.ManropeLightUri)),
                fixture.Write("notomono.ttf", FontFileFixture.ReadAsset(FontFileFixture.NotoMonoUri))
            };

            var faces = new List<SfntFace>();

            try
            {
                // Each face is loaded twice, as a font picker and a text layout would do independently.
                for (var pass = 0; pass < 2; pass++)
                {
                    for (var i = 0; i < 8; i++)
                    {
                        Assert.True(SfntFace.TryLoad(files[0], i, out var face));
                        faces.Add(face);
                    }

                    for (var f = 1; f < files.Length; f++)
                    {
                        Assert.True(SfntFace.TryLoad(files[f], 0, out var face));
                        faces.Add(face);
                    }
                }

                var distinct = DistinctFileData(faces);

                Assert.Equal(files.Length, distinct.Count);
                Assert.Equal(files.Sum(f => new FileInfo(f).Length), distinct.Sum(d => (long)d.Length));

                // The cache holds one mapped entry per file, sized like the file.
                var entries = GetCacheEntries(fixture);

                Assert.Equal(files.Length, entries.Count);
                Assert.All(entries, e => Assert.Equal(typeof(FontFileMemory), e.OwnerType));
                Assert.Equal(files.Sum(f => new FileInfo(f).Length), entries.Sum(e => (long)e.Length));
            }
            finally
            {
                DisposeAll(faces);
            }

            // The cache keeps nothing alive on its own.
            Assert.Empty(GetCacheEntries(fixture));
        }

        private static List<FontFileCacheEntry> GetCacheEntries(FontFileFixture fixture) =>
            FontFileCache.GetSnapshot()
                .Where(e => e.Key is string path && path.Contains(fixture.DirectoryName, StringComparison.Ordinal))
                .ToList();

        [Fact]
        public void Content_Keys_Of_Equal_Files_Are_Equal()
        {
            var first = BuildEightFaceCollection();
            var second = (byte[])first.Clone();

            Assert.True(FontContentKey.TryCreate(first, out var firstKey));
            Assert.True(FontContentKey.TryCreate(second, out var secondKey));

            Assert.Equal(firstKey, secondKey);
            Assert.Equal(firstKey.GetHashCode(), secondKey.GetHashCode());
            Assert.Equal(first.Length, firstKey.Length);

            // Random-access reads produce the same key as the whole file in memory.
            Assert.True(FontContentKey.TryCreate(second.Length, (offset, destination) =>
            {
                second.AsSpan((int)offset, destination.Length).CopyTo(destination);

                return true;
            }, out var readKey));

            Assert.Equal(firstKey, readKey);
        }

        [Fact]
        public void Content_Keys_Differ_When_A_Name_Table_Differs()
        {
            var font = FontFileFixture.ReadAsset(FontFileFixture.InterRegularUri);
            var changed = (byte[])font.Clone();

            // The table directory stays equal; only the name table's content changes.
            changed[GetTableOffset(changed, 0, "name") + 20] ^= 0xFF;

            Assert.True(FontContentKey.TryCreate(font, out var key));
            Assert.True(FontContentKey.TryCreate(changed, out var changedKey));

            Assert.NotEqual(key, changedKey);

            // A different face set over the same tables is a different file.
            Assert.True(FontContentKey.TryCreate(FontFileFixture.BuildCollection(font), out var collectionKey));

            Assert.NotEqual(key, collectionKey);
        }

        [Fact]
        public void Content_Keys_Reject_Truncated_Files()
        {
            var font = BuildEightFaceCollection();

            Assert.False(FontContentKey.TryCreate(font.AsSpan(0, 8), out _));
            Assert.False(FontContentKey.TryCreate(font.AsSpan(0, 40), out _));
        }

        [Fact]
        public void Content_Keyed_Data_Is_Shared_Until_The_Last_Release()
        {
            var font = BuildEightFaceCollection();

            Assert.True(FontContentKey.TryCreate(font, out var key));

            var created = 0;

            (IDisposable Owner, ReadOnlyMemory<byte> Memory)? Create()
            {
                created++;

                var memory = UnmanagedFontMemory.LoadFromStream(new MemoryStream(font));

                return (memory, memory.Memory);
            }

            Assert.True(FontFileCache.TryGetOrCreate(key, Create, out var first));
            Assert.True(FontFileCache.TryGetOrCreate(key, Create, out var second));

            Assert.Same(first, second);
            Assert.Equal(1, created);
            Assert.Contains(FontFileCache.GetSnapshot(), e => e.Key is FontContentKey k && k.Equals(key));

            first.Release();
            second.Release();

            Assert.DoesNotContain(FontFileCache.GetSnapshot(), e => e.Key is FontContentKey k && k.Equals(key));

            Assert.True(FontFileCache.TryGetOrCreate(key, Create, out var third));

            Assert.NotSame(first, third);
            Assert.Equal(2, created);

            third.Release();
        }

        private static int GetTableOffset(byte[] font, int directoryOffset, string tag)
        {
            var numTables = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(directoryOffset + 4));
            var tagValue = (uint)new OpenTypeTag(tag[0], tag[1], tag[2], tag[3]);

            for (var i = 0; i < numTables; i++)
            {
                var record = directoryOffset + 12 + i * 16;

                if (BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record)) == tagValue)
                {
                    return (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record + 8));
                }
            }

            throw new InvalidOperationException("No " + tag + " table.");
        }

        internal static byte[] BuildEightFaceCollection()
        {
            var regular = FontFileFixture.ReadAsset(FontFileFixture.InterRegularUri);
            var bold = FontFileFixture.ReadAsset(FontFileFixture.InterBoldUri);

            return FontFileFixture.BuildCollection(regular, bold, regular, bold, regular, bold, regular, bold);
        }

        internal static void AssertSameFileData(IReadOnlyList<SfntFace> faces)
        {
            Assert.NotEmpty(faces);

            Assert.True(faces[0].TryGetFontFileData(out var first, out _));

            foreach (var face in faces)
            {
                Assert.True(face.TryGetFontFileData(out var data, out _));
                Assert.True(first.Span == data.Span, "Faces of one file must share one file data.");
            }
        }

        private static List<ReadOnlyMemory<byte>> DistinctFileData(IEnumerable<SfntFace> faces)
        {
            var distinct = new List<ReadOnlyMemory<byte>>();

            foreach (var face in faces)
            {
                Assert.True(face.TryGetFontFileData(out var data, out _));

                if (!distinct.Any(d => d.Span == data.Span))
                {
                    distinct.Add(data);
                }
            }

            return distinct;
        }

        private static void DisposeAll(IEnumerable<SfntFace> faces)
        {
            foreach (var face in faces)
            {
                face.Dispose();
            }
        }
    }
}
