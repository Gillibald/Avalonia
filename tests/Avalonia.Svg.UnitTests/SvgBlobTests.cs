using System.IO;
using System.Text;
using Avalonia.Media.Svg;
using Xunit;

namespace Avalonia.Svg.UnitTests;

public class SvgBlobTests
{
    [Theory]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24"><path id="p" d="M0 0 L10 10 Z" fill="#3b82f6"/></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><g id="g"><rect x="1" width="10" height="10"/><circle r="5"/></g></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink"><rect id="r" width="5" height="5"/><use id="u" xlink:href="#r"/></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><defs><linearGradient id="grad" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#fff"/><stop offset="1" stop-color="#000"/></linearGradient></defs><rect width="10" height="10" fill="url(#grad)"/></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><text x="0" y="10">Hello <tspan font-weight="bold">world</tspan>!</text></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><text xml:space="preserve">a    b</text></svg>""")]
    public void Blob_RoundTrips_To_An_Equal_Tree(string xml)
    {
        using var original = SvgDocument.Parse(xml);
        var blob = SvgBlobWriter.Write(original);
        using var roundTrip = SvgDocument.FromCompiledBlob(blob);

        AssertTreeEqual(original.Root, roundTrip.Root);
    }

    [Fact]
    public void Blob_Starts_With_Magic_And_Version()
    {
        using var document = SvgDocument.Parse("""<svg xmlns="http://www.w3.org/2000/svg"/>""");
        var blob = SvgBlobWriter.Write(document);

        Assert.True(blob.Length >= 4);
        Assert.Equal((byte)'A', blob[0]);
        Assert.Equal((byte)'S', blob[1]);
        Assert.Equal((byte)'B', blob[2]);
        Assert.Equal(1, blob[3]);
    }

    [Fact]
    public void Blob_Preserves_Id_Map_With_First_Id_Wins()
    {
        // Two elements share an id; SvgDocument.Load keeps the first, and the blob
        // reader must reproduce that.
        using var original = SvgDocument.Parse(
            """<svg xmlns="http://www.w3.org/2000/svg"><rect id="dup" width="1" height="1"/><circle id="dup" r="2"/></svg>""");
        using var roundTrip = SvgDocument.FromCompiledBlob(SvgBlobWriter.Write(original));

        Assert.Equal("rect", original.GetElementById("dup")!.Name);
        Assert.Equal("rect", roundTrip.GetElementById("dup")!.Name);
    }

    [Fact]
    public void Load_Stream_Reconstructs_A_Blob_And_Still_Parses_Xml()
    {
        using var original = SvgDocument.Parse(
            """<svg xmlns="http://www.w3.org/2000/svg"><rect id="r" width="10" height="10"/></svg>""");
        var blob = SvgBlobWriter.Write(original);

        // A blob stream is recognized by its magic header and reconstructed...
        using var blobStream = new MemoryStream(blob);
        using var fromBlob = SvgDocument.Load(blobStream);
        Assert.NotNull(fromBlob.GetElementById("r"));

        // ...while an XML stream still parses through the same entry point.
        using var xmlStream = new MemoryStream(
            Encoding.UTF8.GetBytes("""<svg xmlns="http://www.w3.org/2000/svg"><circle id="c" r="4"/></svg>"""));
        using var fromXml = SvgDocument.Load(xmlStream);
        Assert.NotNull(fromXml.GetElementById("c"));
    }

    private static void AssertTreeEqual(SvgElement expected, SvgElement actual)
    {
        Assert.Equal(expected.Name, actual.Name);

        Assert.Equal(expected.Attributes.Count, actual.Attributes.Count);
        foreach (var pair in expected.Attributes)
        {
            Assert.True(actual.Attributes.TryGetValue(pair.Key, out var value), $"Missing attribute '{pair.Key}'.");
            Assert.Equal(pair.Value, value);
        }

        var expectedContent = expected.Content;
        var actualContent = actual.Content;
        var expectedCount = expectedContent?.Count ?? 0;
        var actualCount = actualContent?.Count ?? 0;
        Assert.Equal(expectedCount, actualCount);

        for (var i = 0; i < expectedCount; i++)
        {
            switch (expectedContent![i])
            {
                case string text:
                    Assert.Equal(text, Assert.IsType<string>(actualContent![i]));
                    break;
                case SvgElement child:
                    AssertTreeEqual(child, Assert.IsType<SvgElement>(actualContent![i]));
                    break;
            }
        }
    }
}
