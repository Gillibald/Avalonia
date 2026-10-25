using System;
using System.Linq;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Imaging
{
    public class BitmapMetadataEngineTests
    {
        [Theory]
        [InlineData("/app1/ifd/{ushort=274}", "/ifd/{ushort=274}")]
        [InlineData("/APP1/IFD/{USHORT=274}", "/ifd/{ushort=274}")]
        [InlineData("/app1/{ushort=0}/{ushort=271}", "/ifd/{ushort=271}")]
        [InlineData("/app1/{ushort=1}/{ushort=259}", "/app1/thumb/{ushort=259}")]
        [InlineData("/app1/ifd/exif/{ushort=36867}", "/ifd/exif/{ushort=36867}")]
        [InlineData("/app1/ifd/{ushort=34665}/{ushort=36867}", "/ifd/exif/{ushort=36867}")]
        [InlineData("/app1/ifd/gps/{ushort=2}", "/ifd/gps/{ushort=2}")]
        [InlineData("/app1/ifd/{ushort=34853}/{ushort=2}", "/ifd/gps/{ushort=2}")]
        [InlineData("/app1/ifd/exif/{ushort=40965}/{ushort=1}", "/ifd/exif/interop/{ushort=1}")]
        [InlineData("/app1/xmp", "/xmp")]
        [InlineData("/app13/irb/8bimiptc/iptc", "/iptc")]
        [InlineData("/app13/{ushort=0}/{ulonglong=61857348781060}/iptc", "/iptc")]
        [InlineData("/app13/{ushort=0}/{ulonglong=61857348781060}/iptc/{str=Keywords}", "/iptc/{str=Keywords}")]
        public void Jpeg_Spellings_Should_Resolve_To_Canonical_Paths(string query, string canonical)
        {
            Assert.Equal(canonical, MetadataQuery.Canonicalize(query, BitmapContainerFormats.Jpeg));
        }

        [Theory]
        [InlineData("/ifd/{ushort=34665}/{ushort=33434}", "/ifd/exif/{ushort=33434}")]
        [InlineData("/ifd/xmp", "/xmp")]
        [InlineData("/ifd/{ushort=700}", "/xmp")]
        [InlineData("/ifd/{ushort=33723}", "/iptc")]
        [InlineData("/ifd/{ushort=34377}/{ulonglong=61857348781060}/iptc", "/iptc")]
        public void Tiff_Spellings_Should_Resolve_To_Canonical_Paths(string query, string canonical)
        {
            Assert.Equal(canonical, MetadataQuery.Canonicalize(query, BitmapContainerFormats.Tiff));
        }

        [Theory]
        [InlineData("/tEXt/{str=Title}", "/text/{str=Title}")]
        [InlineData("/text/{wstr=Title}", "/text/{str=Title}")]
        [InlineData("/iCCP/ProfileData", "/icc")]
        [InlineData("/tEXt/Title", "/text/{str=Title}")]
        [InlineData("/png/tEXt/{str=Author}", "/text/{str=Author}")]
        [InlineData("/[0]tEXt/{str=Author}", "/text/{str=Author}")]
        [InlineData("/[1]tEXt/{str=Author}", "/text/{str=Author}")]
        [InlineData("/[2]zTXt/{str=Comment}", "/ztxt/{str=Comment}")]
        [InlineData("/[1]iTXt/{str=Title}", "/itxt/{str=Title}")]
        [InlineData("/zTXt/{str=Comment}", "/ztxt/{str=Comment}")]
        [InlineData("/iTXt/{str=Title}/{str=de}/{str=Titel}", "/itxt/{str=Title}/{str=de}/{str=Titel}")]
        [InlineData("/iTXt/keyword", "/[0]itxt/Keyword")]
        [InlineData("/[2]iTXt/TEXTENTRY", "/[2]itxt/TextEntry")]
        public void Png_Spellings_Should_Resolve_To_Canonical_Paths(string query, string canonical)
        {
            Assert.Equal(canonical, MetadataQuery.Canonicalize(query, BitmapContainerFormats.Png));
        }

        [Theory]
        [InlineData("/app13/irb/8bimiptc/iptc/Keywords", "/iptc/{str=Keywords}")]
        [InlineData("/app13/irb/8bimiptc/iptc/{str=by-line}", "/iptc/{str=By-line}")]
        [InlineData("/app13/irb/8bimiptc/iptc/{str=Province/State}", "/iptc/{str=Province/State}")]
        [InlineData("/iptc/{ushort=537}", "/iptc/{str=Keywords}")]
        [InlineData("/iptc/{ushort=600}", "/iptc/{ushort=600}")]
        public void Iptc_Datasets_Should_Resolve_To_Their_Wic_Names(string query, string canonical)
        {
            Assert.Equal(canonical, MetadataQuery.Canonicalize(query, BitmapContainerFormats.Jpeg));
            Assert.Equal(canonical, MetadataQuery.Canonicalize(query.Replace("/app13/irb/8bimiptc/iptc", "/ifd/iptc"),
                BitmapContainerFormats.Tiff));
        }

        [Theory]
        [InlineData("/block/{ULONGLONG=18446744073709551615}", "/block/{ulonglong=18446744073709551615}")]
        [InlineData("/block/{longlong=7}", "/block/{longlong=7}")]
        [InlineData("/block/{guid={6AAE2E3D-7F8B-4A3B-9F4E-0D3C2B1A0F9E}}", "/block/{guid={6aae2e3d-7f8b-4a3b-9f4e-0d3c2b1a0f9e}}")]
        public void Wide_Integer_And_Guid_Selectors_Should_Normalize(string query, string canonical)
        {
            Assert.Equal(canonical, MetadataQuery.Canonicalize(query, BitmapContainerFormats.Jpeg));
        }

        [Theory]
        [InlineData("/grctlext/delay", "/grctlext/Delay")]
        [InlineData("/logscrdesc/{str=globalcolortableflag}", "/logscrdesc/GlobalColorTableFlag")]
        [InlineData("/commentext/textentry", "/[0]commentext/TextEntry")]
        [InlineData("/[1]appext/data", "/[1]appext/Data")]
        [InlineData("/grctlext/{ushort=5}", "/grctlext/{ushort=5}")]
        public void Gif_Spellings_Should_Resolve_To_Wic_Field_Names(string query, string canonical)
        {
            Assert.Equal(canonical, MetadataQuery.Canonicalize(query, BitmapContainerFormats.Gif));
        }

        [Fact]
        public void Unknown_Names_Should_Throw()
        {
            Assert.Throws<ArgumentException>(() => MetadataQuery.Canonicalize("/logscrdesc/Bogus", BitmapContainerFormats.Gif));
            Assert.Throws<ArgumentException>(() => MetadataQuery.Canonicalize("/iptc/NoSuchDataset", BitmapContainerFormats.Jpeg));
        }

        [Fact]
        public void Itxt_Fields_Should_View_The_Entries_In_Order()
        {
            var metadata = new BitmapMetadata(BitmapContainerFormats.Png);

            metadata.SetQuery("/iTXt/{str=A}", "first");
            metadata.SetQuery("/iTXt/{str=B}/{str=de}/{str=Bee}", "second");

            Assert.Equal("A", metadata.GetQuery("/iTXt/Keyword"));
            Assert.Equal("first", metadata.GetQuery("/[0]iTXt/TextEntry"));
            Assert.Equal(string.Empty, metadata.GetQuery("/[0]iTXt/LanguageTag"));
            Assert.Equal("B", metadata.GetQuery("/[1]iTXt/Keyword"));
            Assert.Equal("de", metadata.GetQuery("/[1]iTXt/LanguageTag"));
            Assert.Equal("Bee", metadata.GetQuery("/[1]iTXt/TranslatedKeyword"));
            Assert.Equal("second", metadata.GetQuery("/[1]iTXt/TextEntry"));
            Assert.True(metadata.ContainsQuery("/[1]iTXt/Keyword"));
            Assert.False(metadata.ContainsQuery("/[2]iTXt/Keyword"));
            Assert.Throws<NotSupportedException>(() => metadata.SetQuery("/iTXt/TextEntry", "x"));
        }

        [Fact]
        public void Gif_Flags_Should_Read_As_Booleans()
        {
            var metadata = new BitmapMetadata(BitmapContainerFormats.Gif);

            metadata.SetQuery("/grctlext/TransparencyFlag", true);
            metadata.SetQuery("/grctlext/Disposal", (byte)2);

            Assert.Equal(true, metadata.GetQuery("/grctlext/transparencyflag"));
            Assert.Equal((byte)2, metadata.GetQuery("/grctlext/Disposal"));
            Assert.True(metadata.Store.TryRead("/grctlext/TransparencyFlag", out var stored));
            Assert.Equal(MetadataValueType.Byte, stored.Type);
        }

        [Fact]
        public void A_Repeated_Iptc_Dataset_Should_Read_As_An_Array()
        {
            var metadata = new BitmapMetadata(BitmapContainerFormats.Jpeg);

            metadata.SetQuery("/app13/irb/8bimiptc/iptc/Keywords", new[] { "sea", "boat" });

            Assert.Equal(new[] { "sea", "boat" }, metadata.GetQuery("/iptc/{ushort=537}"));
        }

        [Theory]
        [InlineData("ifd")]
        [InlineData("/ifd//{ushort=1}")]
        [InlineData("/ifd/{ushort=abc}")]
        [InlineData("/ifd/{bogus=1}")]
        [InlineData("/ifd/{ushort}")]
        public void Malformed_Queries_Should_Throw(string query)
        {
            var metadata = new BitmapMetadata(BitmapContainerFormats.Jpeg);

            Assert.Throws<ArgumentException>(() => metadata.GetQuery(query));
        }

        [Fact]
        public void Values_Should_Round_Trip_In_Wic_Shapes()
        {
            var metadata = new BitmapMetadata(BitmapContainerFormats.Jpeg);

            metadata.SetQuery("/app1/ifd/{ushort=271}", "Make");
            metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
            metadata.SetQuery("/app1/ifd/{ushort=282}", 72UL | (1UL << 32));
            metadata.SetQuery("/app1/ifd/exif/{ushort=37500}", new byte[] { 1, 2, 3 });
            metadata.SetQuery("/app1/ifd/{ushort=40000}", new ushort[] { 1, 2 });

            Assert.Equal("Make", metadata.GetQuery("/app1/ifd/{ushort=271}"));
            Assert.Equal((ushort)6, metadata.GetQuery("/app1/ifd/{ushort=274}"));
            Assert.Equal(72UL | (1UL << 32), metadata.GetQuery("/app1/ifd/{ushort=282}"));
            Assert.Equal(new byte[] { 1, 2, 3 }, metadata.GetQuery("/app1/ifd/{ushort=34665}/{ushort=37500}"));
            Assert.Equal(new ushort[] { 1, 2 }, metadata.GetQuery("/ifd/{ushort=40000}"));
        }

        [Fact]
        public void Writing_A_Number_Should_Keep_The_Stored_Type()
        {
            var metadata = new BitmapMetadata(BitmapContainerFormats.Jpeg);

            metadata.SetQuery("/ifd/{ushort=274}", (ushort)1);
            metadata.SetQuery("/ifd/{ushort=274}", 8);

            Assert.Equal((ushort)8, metadata.GetQuery("/ifd/{ushort=274}"));
        }

        [Fact]
        public void Text_Outside_Latin1_Should_Be_Stored_As_Unicode()
        {
            var metadata = new BitmapMetadata(BitmapContainerFormats.Png);

            metadata.SetQuery("/text/{str=Title}", "Café");
            metadata.SetQuery("/text/{str=Comment}", "日本");

            Assert.True(metadata.Store.TryRead("/text/{str=Title}", out var latin1));
            Assert.True(metadata.Store.TryRead("/text/{str=Comment}", out var unicode));
            Assert.Equal(MetadataValueType.Ascii, latin1.Type);
            Assert.Equal(MetadataValueType.Unicode, unicode.Type);
            Assert.Equal("日本", metadata.GetQuery("/tEXt/{str=Comment}"));
        }

        [Fact]
        public void A_Block_Query_Should_Return_A_View_Of_The_Same_Values()
        {
            var metadata = new BitmapMetadata(BitmapContainerFormats.Jpeg);

            metadata.SetQuery("/app1/ifd/{ushort=271}", "Make");

            var block = Assert.IsType<BitmapMetadata>(metadata.GetQuery("/app1/ifd"));

            Assert.Equal("Make", block.GetQuery("/{ushort=271}"));

            block.SetQuery("/{ushort=272}", "Model");

            Assert.Equal("Model", metadata.GetQuery("/app1/ifd/{ushort=272}"));
        }

        [Fact]
        public void Setting_A_Block_Should_Copy_Its_Values()
        {
            var source = new BitmapMetadata(BitmapContainerFormats.Jpeg);

            source.SetQuery("/ifd/exif/{ushort=36867}", "2026:10:23 12:00:00");
            source.SetQuery("/ifd/exif/{ushort=37500}", new BitmapMetadataBlob(new byte[] { 9 }));

            var target = new BitmapMetadata(BitmapContainerFormats.Jpeg);

            target.SetQuery("/app1/ifd/exif", source.GetQuery("/app1/ifd/exif"));

            Assert.Equal("2026:10:23 12:00:00", target.GetQuery("/ifd/exif/{ushort=36867}"));
            Assert.Equal(new byte[] { 9 },
                Assert.IsType<BitmapMetadataBlob>(target.GetQuery("/ifd/exif/{ushort=37500}")).GetBlobValue());
        }

        [Fact]
        public void Children_Should_List_Immediate_Segments()
        {
            var metadata = new BitmapMetadata(BitmapContainerFormats.Jpeg);

            metadata.SetQuery("/ifd/{ushort=271}", "Make");
            metadata.SetQuery("/ifd/exif/{ushort=36867}", "2026:10:23 12:00:00");
            metadata.XmpPacket = "<x/>";

            Assert.Equal(new[] { "/ifd", "/xmp" }, metadata.GetChildren().OrderBy(c => c, StringComparer.Ordinal));
            Assert.Equal(new[] { "/exif", "/{ushort=271}" }, metadata.GetChildren("/app1/ifd").OrderBy(c => c, StringComparer.Ordinal));
        }

        [Fact]
        public void Container_Spelling_Should_List_Jpeg_Blocks_As_Wic_Enumerates_Them()
        {
            var metadata = new BitmapMetadata(BitmapContainerFormats.Jpeg);

            metadata.SetQuery("/app1/ifd/{ushort=271}", "Make");
            metadata.SetQuery("/app1/ifd/exif/{ushort=36867}", "2026:10:25 12:00:00");
            metadata.SetQuery("/app1/ifd/gps/{ushort=2}", new ulong[] { 1, 2, 3 });
            metadata.SetQuery("/app13/irb/8bimiptc/iptc/Keywords", "Keyword");
            metadata.XmpPacket = "<x/>";

            Assert.Equal(new[] { "/app1", "/app13", "/xmp" }, Sorted(metadata.GetChildren(null, MetadataPathSpelling.Container)));
            Assert.Equal(new[] { "/{ushort=0}" }, metadata.GetChildren("/app1", MetadataPathSpelling.Container));
            Assert.Equal(new[] { "/{ushort=271}", "/{ushort=34665}", "/{ushort=34853}" },
                Sorted(metadata.GetChildren("/app1/{ushort=0}", MetadataPathSpelling.Container)));
            Assert.Equal(new[] { "/{str=Keywords}" },
                metadata.GetChildren("/app13/{ushort=0}/{ulonglong=61857348781060}/iptc", MetadataPathSpelling.Container));
            AssertEveryEnumeratedPathResolves(metadata);
        }

        [Fact]
        public void Container_Spelling_Should_List_Tiff_Blocks_Under_Their_Pointer_Tags()
        {
            var metadata = new BitmapMetadata(BitmapContainerFormats.Tiff);

            metadata.SetQuery("/ifd/{ushort=271}", "Make");
            metadata.SetQuery("/ifd/exif/{ushort=36867}", "2026:10:25 12:00:00");
            metadata.SetQuery("/ifd/iptc/Keywords", "Keyword");
            metadata.XmpPacket = "<x/>";

            Assert.Equal(new[] { "/ifd" }, metadata.GetChildren(null, MetadataPathSpelling.Container));
            Assert.Equal(new[] { "/{ushort=271}", "/{ushort=33723}", "/{ushort=34665}", "/{ushort=700}" },
                Sorted(metadata.GetChildren("/ifd", MetadataPathSpelling.Container)));
            AssertEveryEnumeratedPathResolves(metadata);
        }

        [Fact]
        public void Container_Spelling_Should_Number_Png_Text_Chunks_And_Spell_Chunk_Fields()
        {
            var metadata = new BitmapMetadata(BitmapContainerFormats.Png);

            metadata.SetQuery("/tEXt/{str=Title}", "Title");
            metadata.SetQuery("/tEXt/{str=Author}", "Author");
            metadata.SetQuery("/iTXt/{str=Description}", "Description");
            metadata.SetQuery("/gAMA/ImageGamma", 45455u);
            metadata.SetQuery("/iCCP/ProfileData", new BitmapMetadataBlob(new byte[] { 1, 2, 3 }));

            Assert.Equal(new[] { "/[1]tEXt", "/gAMA", "/iCCP", "/iTXt", "/tEXt" },
                Sorted(metadata.GetChildren(null, MetadataPathSpelling.Container)));
            Assert.Equal(new[] { "/Keyword", "/LanguageTag", "/TextEntry", "/TranslatedKeyword" },
                Sorted(metadata.GetChildren("/iTXt", MetadataPathSpelling.Container)));
            Assert.Equal(new[] { "/ImageGamma" }, metadata.GetChildren("/gAMA", MetadataPathSpelling.Container));
            Assert.Equal(new[] { "/ProfileData" }, metadata.GetChildren("/iCCP", MetadataPathSpelling.Container));
            AssertEveryEnumeratedPathResolves(metadata);
        }

        [Fact]
        public void Container_Spelling_Should_Start_A_Nested_View_At_Its_Block()
        {
            var metadata = new BitmapMetadata(BitmapContainerFormats.Jpeg);

            metadata.SetQuery("/app1/ifd/exif/{ushort=36867}", "2026:10:25 12:00:00");

            var exif = Assert.IsType<BitmapMetadata>(metadata.GetQuery("/app1/ifd/exif"));

            Assert.Equal(new[] { "/{ushort=36867}" }, exif.GetChildren(null, MetadataPathSpelling.Container));
        }

        private static string[] Sorted(System.Collections.Generic.IEnumerable<string> values)
            => values.OrderBy(v => v, StringComparer.Ordinal).ToArray();

        private static void AssertEveryEnumeratedPathResolves(BitmapMetadata metadata, string block = "")
        {
            foreach (var child in metadata.GetChildren(block.Length == 0 ? null : block, MetadataPathSpelling.Container))
            {
                var path = block + child;

                if (metadata.GetChildren(path, MetadataPathSpelling.Container).Count == 0)
                    Assert.True(metadata.GetQuery(path) is not null, $"{path} does not resolve.");
                else
                    AssertEveryEnumeratedPathResolves(metadata, path);
            }
        }

        [Fact]
        public void Removing_A_Block_Should_Remove_Everything_Below_It()
        {
            var metadata = new BitmapMetadata(BitmapContainerFormats.Jpeg);

            metadata.SetQuery("/ifd/{ushort=271}", "Make");
            metadata.SetQuery("/ifd/exif/{ushort=36867}", "2026:10:23 12:00:00");

            metadata.RemoveQuery("/app1/ifd/exif");

            Assert.False(metadata.ContainsQuery("/ifd/exif"));
            Assert.True(metadata.ContainsQuery("/ifd/{ushort=271}"));

            metadata.SetQuery("/ifd/{ushort=271}", null);

            Assert.False(metadata.ContainsQuery("/ifd"));
        }

        [Fact]
        public void Read_Only_Metadata_Should_Reject_Changes_And_Clone_Writable()
        {
            var store = MemoryMetadataStore.CopyOf(new BitmapMetadata(BitmapContainerFormats.Png).Store, isReadOnly: true);
            var metadata = new BitmapMetadata(store, BitmapContainerFormats.Png);

            Assert.True(metadata.IsReadOnly);
            Assert.Throws<InvalidOperationException>(() => metadata.SetQuery("/text/{str=Title}", "x"));
            Assert.Throws<InvalidOperationException>(() => metadata.XmpPacket = "<x/>");

            var copy = metadata.Clone();

            copy.SetQuery("/text/{str=Title}", "x");

            Assert.Equal("x", copy.GetQuery("/text/{str=Title}"));
            Assert.Null(metadata.GetQuery("/text/{str=Title}"));
        }

        [Fact]
        public void Photo_Properties_Should_Map_To_Exif_Tags()
        {
            var metadata = new BitmapMetadata(BitmapContainerFormats.Jpeg);
            IPhotoMetadata photo = metadata;

            photo.Title = "Title";
            photo.Authors = new[] { "Alice", "Bob" };
            photo.Rating = 9;
            photo.DateTaken = new DateTime(2026, 10, 23, 12, 34, 56);
            photo.Comment = "Grand café";

            Assert.Equal("Title", metadata.GetQuery("/app1/ifd/{ushort=270}"));
            Assert.Equal("Alice;Bob", metadata.GetQuery("/app1/ifd/{ushort=315}"));
            Assert.Equal((ushort)5, metadata.GetQuery("/app1/ifd/{ushort=18246}"));
            Assert.Equal("2026:10:23 12:34:56", metadata.GetQuery("/app1/ifd/exif/{ushort=36867}"));
            Assert.Equal(new[] { "Alice", "Bob" }, photo.Authors);
            Assert.Equal("Grand café", photo.Comment);
            Assert.StartsWith("UNICODE", System.Text.Encoding.ASCII.GetString(
                Assert.IsType<BitmapMetadataBlob>(metadata.GetQuery("/ifd/exif/{ushort=37510}")).GetBlobValue()));
        }

        [Fact]
        public void Photo_Properties_Should_Map_To_Png_Text_And_Reject_Exif_Only_Ones()
        {
            var metadata = new BitmapMetadata(BitmapContainerFormats.Png);
            IPhotoMetadata photo = metadata;

            photo.Title = "Title";
            photo.Comment = "Comment";

            Assert.Equal("Title", metadata.GetQuery("/tEXt/{str=Title}"));
            Assert.Equal("Comment", metadata.GetQuery("/tEXt/{str=Comment}"));
            Assert.Null(photo.CameraModel);
            Assert.Throws<NotSupportedException>(() => photo.CameraModel = "Model");
        }

        [Fact]
        public void Opaque_Bytes_Should_Read_As_Blobs_And_Byte_Arrays_As_Byte_Values()
        {
            Assert.Equal(MetadataValueType.Byte, MetadataValueConverter.FromObject(new byte[] { 1, 2 }, null).Type);
            Assert.Equal(MetadataValueType.Undefined,
                MetadataValueConverter.FromObject(new BitmapMetadataBlob(new byte[] { 1, 2 }), null).Type);

            var metadata = new BitmapMetadata(BitmapContainerFormats.Jpeg);

            metadata.SetQuery("/app1/ifd/exif/{ushort=37500}", new BitmapMetadataBlob(new byte[] { 1, 2, 3 }));
            metadata.SetQuery("/app1/ifd/{ushort=50341}", new byte[] { 4, 5 });

            var blob = Assert.IsType<BitmapMetadataBlob>(metadata.GetQuery("/app1/ifd/exif/{ushort=37500}"));

            Assert.Equal(new byte[] { 1, 2, 3 }, blob.GetBlobValue());
            Assert.Equal(new byte[] { 4, 5 }, Assert.IsType<byte[]>(metadata.GetQuery("/app1/ifd/{ushort=50341}")));
        }

        [Fact]
        public void Signed_And_Floating_Point_Arrays_Should_Round_Trip()
        {
            var metadata = new BitmapMetadata(BitmapContainerFormats.Jpeg);
            var values = new object[]
            {
                new sbyte[] { -1, 2 }, new short[] { -3, 4 }, new int[] { -5, 6 }, new long[] { -7, 8 },
                new float[] { 1.5f, -2.5f }, new double[] { 3.25, -4.75 },
            };

            for (var i = 0; i < values.Length; i++)
            {
                var query = "/app1/ifd/{ushort=" + (60000 + i) + "}";

                metadata.SetQuery(query, values[i]);

                Assert.Equal(values[i], metadata.GetQuery(query));
            }
        }

        [Fact]
        public void Unsupported_Value_Types_Should_Throw()
        {
            var metadata = new BitmapMetadata(BitmapContainerFormats.Jpeg);

            Assert.Throws<NotSupportedException>(() => metadata.SetQuery("/ifd/{ushort=1}", new object()));
        }
    }
}
