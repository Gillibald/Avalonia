using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Svg;
using Avalonia.Media.Imaging;
using Avalonia.Skia.RenderTests;
using Xunit;

namespace Avalonia.Svg.RenderTests;

/// <summary>
/// Runs the vendored resvg test-suite corpus (see
/// <c>tests/TestFiles/Svg/Corpus/resvg/README.md</c>): every test SVG renders
/// through both pipelines and diffs against a golden produced by this
/// implementation and visually verified against the test's stated expectation
/// when first added. Tests listed in <c>quarantine.txt</c> are skipped with
/// their reason — that file is the measurable compliance gap.
/// </summary>
public class ResvgCorpusTests : SvgRenderTestBase
{
    private static readonly Lazy<Dictionary<string, string>> s_quarantine = new(LoadQuarantine);

    public ResvgCorpusTests() : base(Path.Combine("Corpus", "resvg"))
    {
    }

    private static string CorpusRoot =>
        Path.Combine(TestRenderHelper.GetTestsDirectory(), "TestFiles", "Svg", "Corpus", "resvg");

    public static TheoryData<string> ShapesTests { get; } = Discover("shapes");

    [Theory]
    [MemberData(nameof(ShapesTests))]
    public Task Shapes(string test) => RunCorpusTest(test);

    public static TheoryData<string> PaintingTests { get; } = Discover("painting");

    [Theory]
    [MemberData(nameof(PaintingTests))]
    public Task Painting(string test) => RunCorpusTest(test);

    public static TheoryData<string> PaintServersTests { get; } = Discover("paint-servers");

    [Theory]
    [MemberData(nameof(PaintServersTests))]
    public Task PaintServers(string test) => RunCorpusTest(test);

    public static TheoryData<string> MaskingTests { get; } = Discover("masking");

    [Theory]
    [MemberData(nameof(MaskingTests))]
    public Task Masking(string test) => RunCorpusTest(test);

    public static TheoryData<string> TextTests { get; } = Discover("text");

    [Theory]
    [MemberData(nameof(TextTests))]
    public Task Text(string test) => RunCorpusTest(test);

    public static TheoryData<string> StructureTests { get; } = Discover("structure");

    [Theory]
    [MemberData(nameof(StructureTests))]
    public Task Structure(string test) => RunCorpusTest(test);

    public static TheoryData<string> FiltersTests { get; } = Discover("filters");

    [Theory]
    [MemberData(nameof(FiltersTests))]
    public Task Filters(string test) => RunCorpusTest(test);

    private static TheoryData<string> Discover(string category)
    {
        var data = new TheoryData<string>();
        var root = Path.Combine(CorpusRoot, category);

        foreach (var file in Directory.EnumerateFiles(root, "*.svg", SearchOption.AllDirectories))
            data.Add(Path.GetRelativePath(CorpusRoot, file).Replace(Path.DirectorySeparatorChar, '/'));

        return data;
    }

    /// <summary>
    /// Pins that <c>ch</c> resolves through real glyph metrics: the rect's
    /// width must equal ten advances of the "0" glyph computed from the same
    /// glyph typeface the compiler uses — not the 0.5em fallback.
    /// </summary>
    [Fact]
    public void Ch_Lengths_Use_The_Fonts_Zero_Advance()
    {
        using var document = SvgDocument.Parse(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="400" height="100" font-family="Noto Sans" font-size="32">
              <rect width="10ch" height="50" fill="green"/>
            </svg>
            """);
        using var image = new SvgImage(document);

        Assert.True(FontManager.Current.TryGetGlyphTypeface(new Typeface("Noto Sans"), out var glyphTypeface));
        Assert.True(glyphTypeface!.CharacterToGlyphMap.TryGetGlyph('0', out var zero));
        Assert.True(glyphTypeface.TryGetHorizontalGlyphAdvance(zero, out var advance));
        var expected = 10.0 * advance * 32 / glyphTypeface.Metrics.DesignEmHeight;

        // Recording bounds apply render-bounds rounding (ceil to whole pixels).
        Assert.Equal(Math.Ceiling(expected), image.ContentBounds.Width, 3);
        Assert.NotEqual(10 * 16.0, Math.Ceiling(expected), 3); // distinguishable from the 0.5em fallback
    }

    private async Task RunCorpusTest(string test)
    {
        if (s_quarantine.Value.TryGetValue(test, out var reason))
            Assert.Skip(reason);

        var path = Path.Combine(CorpusRoot, test.Replace('/', Path.DirectorySeparatorChar));
        var svg = await File.ReadAllTextAsync(path);

        // Goldens live next to their test file; RenderToFile/CompareImages
        // resolve testName relative to OutputPath (== CorpusRoot). The file
        // location doubles as the base for relative image references.
        var testName = test.Substring(0, test.Length - ".svg".Length)
            .Replace('/', Path.DirectorySeparatorChar);
        Directory.CreateDirectory(Path.Combine(OutputPath, Path.GetDirectoryName(testName)!));

        await RenderToFile(new SvgHost(svg, new Uri(path)), testName);
        CompareImages(testName);
    }

    private static Dictionary<string, string> LoadQuarantine()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(CorpusRoot, "quarantine.txt");
        if (!File.Exists(path))
            return result;

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                continue;

            var separator = trimmed.IndexOf(" -- ", StringComparison.Ordinal);
            if (separator < 0)
                result[trimmed] = "quarantined (no reason recorded)";
            else
                result[trimmed.Substring(0, separator).Trim()] = trimmed.Substring(separator + 4).Trim();
        }

        return result;
    }
}
