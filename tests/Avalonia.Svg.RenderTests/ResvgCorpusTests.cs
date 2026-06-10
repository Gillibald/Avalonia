using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
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

    private static TheoryData<string> Discover(string category)
    {
        var data = new TheoryData<string>();
        var root = Path.Combine(CorpusRoot, category);

        foreach (var file in Directory.EnumerateFiles(root, "*.svg", SearchOption.AllDirectories))
            data.Add(Path.GetRelativePath(CorpusRoot, file).Replace(Path.DirectorySeparatorChar, '/'));

        return data;
    }

    private async Task RunCorpusTest(string test)
    {
        if (s_quarantine.Value.TryGetValue(test, out var reason))
            Assert.Skip(reason);

        var svg = await File.ReadAllTextAsync(
            Path.Combine(CorpusRoot, test.Replace('/', Path.DirectorySeparatorChar)));

        // Goldens live next to their test file; RenderToFile/CompareImages
        // resolve testName relative to OutputPath (== CorpusRoot).
        var testName = test.Substring(0, test.Length - ".svg".Length)
            .Replace('/', Path.DirectorySeparatorChar);
        Directory.CreateDirectory(Path.Combine(OutputPath, Path.GetDirectoryName(testName)!));

        await RenderToFile(new SvgHost(svg), testName);
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
