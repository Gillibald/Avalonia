using System;
using System.Collections.Generic;
using Avalonia.Media.Imaging;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Imaging
{
    // Expected values come from WIC reading and writing the same packet through WPF.
    public class XmpMetadataTests
    {
        private const string Packet = @"<?xpacket begin=""" + "﻿" + @""" id=""W5M0MpCehiHzreSzNTczkc9d""?>
<x:xmpmeta xmlns:x=""adobe:ns:meta/"">
 <rdf:RDF xmlns:rdf=""http://www.w3.org/1999/02/22-rdf-syntax-ns#"">
  <rdf:Description rdf:about=""""
    xmlns:xmp=""http://ns.adobe.com/xap/1.0/""
    xmlns:dc=""http://purl.org/dc/elements/1.1/""
    xmlns:exif=""http://ns.adobe.com/exif/1.0/""
    xmlns:xmpMM=""http://ns.adobe.com/xap/1.0/mm/""
    xmlns:stEvt=""http://ns.adobe.com/xap/1.0/sType/ResourceEvent#""
    xmlns:xmpRights=""http://ns.adobe.com/xap/1.0/rights/""
    xmlns:foo=""http://example.com/foo/""
    xmp:CreatorTool=""AttrTool"">
   <xmp:Rating>3</xmp:Rating>
   <dc:title><rdf:Alt><rdf:li xml:lang=""x-default"">Title</rdf:li><rdf:li xml:lang=""de-DE"">Titel</rdf:li></rdf:Alt></dc:title>
   <dc:subject><rdf:Bag><rdf:li>alpha</rdf:li><rdf:li>beta</rdf:li></rdf:Bag></dc:subject>
   <dc:creator><rdf:Seq><rdf:li>Jane</rdf:li></rdf:Seq></dc:creator>
   <exif:Flash rdf:parseType=""Resource""><exif:Fired>True</exif:Fired><exif:Mode>2</exif:Mode></exif:Flash>
   <xmpMM:History><rdf:Seq><rdf:li rdf:parseType=""Resource""><stEvt:action>saved</stEvt:action></rdf:li><rdf:li><rdf:Description stEvt:action=""created""/></rdf:li></rdf:Seq></xmpMM:History>
   <xmpRights:WebStatement rdf:resource=""http://example.com/rights""/>
   <foo:bar>baz</foo:bar>
   <foo:nested><rdf:Description><foo:inner>deep</foo:inner></rdf:Description></foo:nested>
  </rdf:Description>
  <rdf:Description rdf:about="""" xmlns:photoshop=""http://ns.adobe.com/photoshop/1.0/"">
   <photoshop:City>Berlin</photoshop:City>
  </rdf:Description>
 </rdf:RDF>
</x:xmpmeta>
<?xpacket end=""w""?>";

        private const string Foo = @"http\:\/\/example.com\/foo\/";

        private static BitmapMetadata Jpeg()
        {
            var metadata = new BitmapMetadata(BitmapContainerFormats.Jpeg) { XmpPacket = Packet };

            return metadata;
        }

        // Lists every block and value below a block as WPF's enumeration does.
        private static List<string> Dump(BitmapMetadata metadata, string block)
        {
            var lines = new List<string>();

            foreach (var child in metadata.GetChildren(block, MetadataPathSpelling.Container))
            {
                var path = block + child;
                var value = metadata.GetQuery(path);

                if (value is BitmapMetadata nested)
                {
                    lines.Add(path + " <" + nested.Format + ">");
                    lines.AddRange(Dump(metadata, path));
                }
                else
                {
                    lines.Add(path + " = " + value);
                }
            }

            return lines;
        }

        [Fact]
        public void Xmp_Should_Read_As_Wic_Lists_It()
        {
            Assert.Equal(new[]
            {
                "/xmp/xmp:CreatorTool = AttrTool",
                "/xmp/xmp:Rating = 3",
                "/xmp/dc:title <xmpalt>",
                "/xmp/dc:title/x-default = Title",
                "/xmp/dc:title/de-DE = Titel",
                "/xmp/dc:subject <xmpbag>",
                "/xmp/dc:subject/{ulong=0} = alpha",
                "/xmp/dc:subject/{ulong=1} = beta",
                "/xmp/dc:creator <xmpseq>",
                "/xmp/dc:creator/{ulong=0} = Jane",
                "/xmp/exif:Flash <xmpstruct>",
                "/xmp/exif:Flash/exif:Fired = True",
                "/xmp/exif:Flash/exif:Mode = 2",
                "/xmp/xmpMM:History <xmpseq>",
                "/xmp/xmpMM:History/{ulong=0} <xmpstruct>",
                "/xmp/xmpMM:History/{ulong=0}/stEvt:action = saved",
                "/xmp/xmpMM:History/{ulong=1} <xmpstruct>",
                "/xmp/xmpMM:History/{ulong=1}/stEvt:action = created",
                "/xmp/xmpRights:WebStatement = ",
                "/xmp/" + Foo + ":bar = baz",
                "/xmp/" + Foo + ":nested <xmpstruct>",
                "/xmp/" + Foo + ":nested/" + Foo + ":inner = deep",
                "/xmp/photoshop:City = Berlin",
            }, Dump(Jpeg(), "/xmp"));
        }

        [Theory]
        [InlineData("/xmp/xmp:Rating", "3")]
        [InlineData("/xmp/{wstr=http://ns.adobe.com/xap/1.0/}:Rating", "3")]
        [InlineData("/xmp/XMP:Rating", "3")]
        [InlineData("/xmp/xmp:rating", null)]
        [InlineData("/xmp/dc:title/x-default", "Title")]
        [InlineData("/xmp/dc:title/de-DE", "Titel")]
        [InlineData("/xmp/dc:title/{ulong=0}", "Title")]
        [InlineData("/xmp/dc:subject/{ulong=1}", "beta")]
        [InlineData("/xmp/dc:subject/{ulong=2}", null)]
        [InlineData("/xmp/exif:Flash/exif:Fired", "True")]
        [InlineData("/xmp/xmpMM:History/{ulong=1}/stEvt:action", "created")]
        [InlineData("/xmp/xmpRights:WebStatement", "")]
        [InlineData("/xmp/foo:bar", null)]
        [InlineData("/xmp/" + Foo + ":bar", "baz")]
        [InlineData("/xmp/photoshop:City", "Berlin")]
        [InlineData("/app1/xmp/dc:title/x-default", "Title")]
        public void Xmp_Queries_Should_Resolve_As_In_Wic(string query, string? expected)
        {
            Assert.Equal(expected, Jpeg().GetQuery(query));
        }

        [Fact]
        public void Xmp_Blocks_Should_Report_Wic_Formats()
        {
            var metadata = Jpeg();

            Assert.Equal("xmp", Assert.IsType<BitmapMetadata>(metadata.GetQuery("/xmp")).Format);
            Assert.Equal("xmpalt", Assert.IsType<BitmapMetadata>(metadata.GetQuery("/xmp/dc:title")).Format);
            Assert.Equal("xmpbag", Assert.IsType<BitmapMetadata>(metadata.GetQuery("/xmp/dc:subject")).Format);
            Assert.Equal("xmpstruct", Assert.IsType<BitmapMetadata>(metadata.GetQuery("/xmp/exif:Flash")).Format);
        }

        [Fact]
        public void Xmp_Writes_Should_Round_Trip_Through_The_Packet()
        {
            var metadata = Jpeg();

            metadata.SetQuery("/xmp/xmp:Rating", "5");
            metadata.SetQuery("/xmp/dc:title/x-default", "New");
            metadata.SetQuery("/xmp/dc:subject/{ulong=2}", "gamma");
            metadata.SetQuery("/xmp/foo:added", "x");
            metadata.SetQuery("/xmp/exif:Flash/exif:Mode", "3");
            metadata.SetQuery("/xmp/dc:rights", new BitmapMetadata("xmpalt"));
            metadata.SetQuery("/xmp/dc:rights/x-default", "Copy");
            metadata.SetQuery("/xmp/dc:contributor", new BitmapMetadata("xmpbag"));
            metadata.SetQuery("/xmp/dc:contributor/{ulong=0}", "Bob");
            metadata.RemoveQuery("/xmp/photoshop:City");

            // A fresh store parses the written packet.
            var reread = new BitmapMetadata(BitmapContainerFormats.Jpeg) { XmpPacket = metadata.XmpPacket };

            Assert.Equal("5", reread.GetQuery("/xmp/xmp:Rating"));
            Assert.Equal("New", reread.GetQuery("/xmp/dc:title/x-default"));
            Assert.Equal("Titel", reread.GetQuery("/xmp/dc:title/de-DE"));
            Assert.Equal("gamma", reread.GetQuery("/xmp/dc:subject/{ulong=2}"));
            Assert.Equal("x", reread.GetQuery("/xmp/foo:added"));
            Assert.Equal("3", reread.GetQuery("/xmp/exif:Flash/exif:Mode"));
            Assert.Equal("Copy", reread.GetQuery("/xmp/dc:rights/x-default"));
            Assert.Equal("Bob", reread.GetQuery("/xmp/dc:contributor/{ulong=0}"));
            Assert.Equal("baz", reread.GetQuery("/xmp/" + Foo + ":bar"));
            Assert.Null(reread.GetQuery("/xmp/photoshop:City"));
            Assert.Equal("xmpalt", Assert.IsType<BitmapMetadata>(reread.GetQuery("/xmp/dc:rights")).Format);
        }

        [Fact]
        public void Xmp_Writes_Should_Fail_Where_Wic_Fails()
        {
            var metadata = new BitmapMetadata(BitmapContainerFormats.Jpeg);

            // Containers must exist, arrays take no gaps, values are text.
            Assert.Throws<InvalidOperationException>(() => metadata.SetQuery("/xmp/dc:title/x-default", "NoContainer"));
            Assert.Throws<InvalidOperationException>(() => metadata.SetQuery("/xmp/dc:subject/{ulong=0}", "NoBag"));

            metadata.SetQuery("/xmp/dc:creator", new BitmapMetadata("xmpseq"));

            Assert.Throws<InvalidOperationException>(() => metadata.SetQuery("/xmp/dc:creator/{ulong=3}", "gap"));
            Assert.Throws<NotSupportedException>(() => metadata.SetQuery("/xmp/xmp:Count", 7u));
        }

        [Fact]
        public void Xmp_Written_From_Scratch_Should_Resolve_As_Wic_Writes_It()
        {
            var metadata = new BitmapMetadata(BitmapContainerFormats.Jpeg);

            metadata.SetQuery("/xmp/dc:format", "image/jpeg");
            metadata.SetQuery("/xmp/{wstr=http://example.com/u/}:prop", "uri");
            metadata.SetQuery("/xmp/http\\:\\/\\/example.com\\/v\\/:prop", "escaped");
            metadata.SetQuery("/xmp/zzz:prop", "unknownprefix");
            metadata.SetQuery("/xmp/dc:rights", new BitmapMetadata("xmpalt"));
            metadata.SetQuery("/xmp/dc:rights/{ulong=0}", "byindex");
            metadata.SetQuery("/xmp/dc:rights/en-US", "english");

            var reread = new BitmapMetadata(BitmapContainerFormats.Jpeg) { XmpPacket = metadata.XmpPacket };

            // WIC writes one description per namespace, so properties come back grouped by it.
            Assert.Equal(new[]
            {
                "/xmp/dc:format = image/jpeg",
                "/xmp/dc:rights <xmpalt>",
                "/xmp/dc:rights/{} = byindex",
                "/xmp/dc:rights/en-US = english",
                @"/xmp/http\:\/\/example.com\/u\/:prop = uri",
                @"/xmp/http\:\/\/example.com\/v\/:prop = escaped",
                "/xmp/zzz:prop = unknownprefix",
            }, Dump(reread, "/xmp"));
        }

        [Fact]
        public void Tiff_Xmp_Should_Resolve_Under_Tag_700()
        {
            var metadata = new BitmapMetadata(BitmapContainerFormats.Tiff) { XmpPacket = Packet };

            Assert.Equal("Title", metadata.GetQuery("/ifd/{ushort=700}/dc:title/x-default"));
            Assert.Equal("Title", metadata.GetQuery("/ifd/xmp/dc:title/x-default"));
            Assert.Contains("/{ushort=700}", metadata.GetChildren("/ifd", MetadataPathSpelling.Container));
            Assert.Contains("/dc:title", metadata.GetChildren("/ifd/{ushort=700}", MetadataPathSpelling.Container));
        }
    }
}
