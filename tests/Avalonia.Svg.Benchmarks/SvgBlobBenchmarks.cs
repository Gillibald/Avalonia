using System;
using Avalonia.Harfbuzz;
using Avalonia.Media;
using Avalonia.Media.Svg;
using Avalonia.Platform;
using Avalonia.Skia;
using BenchmarkDotNet.Attributes;

namespace Avalonia.Svg.Benchmarks;

/// <summary>
/// Compares the current runtime cost of turning compiled inline SVG into a
/// <see cref="SvgDocument"/> — re-parsing minified XML
/// (<see cref="SvgDocument.Parse"/>, what <c>FromXamlContent</c> does today) —
/// against reading the proposed string-pooled binary blob
/// (<see cref="SvgDocument.FromCompiledBlob"/>). The blob bytes are produced
/// once in <see cref="Setup"/>; in the real pipeline they live in a field-RVA
/// payload, so the <see cref="ReadOnlySpan{T}"/> handed to the reader is
/// zero-copy. The decisive metric is <c>Allocated</c>: the parse step is only a
/// few percent of first-frame time but 38–55 % of its allocations (see
/// planning/bench-svg-baseline.log).
/// </summary>
[MemoryDiagnoser]
public class SvgBlobBenchmarks
{
    static SvgBlobBenchmarks()
    {
        SkiaPlatform.Initialize();
        AvaloniaLocator.CurrentMutable
            .Bind<ITextShaperImpl>().ToConstant(new HarfBuzzTextShaper())
            .Bind<IAssetLoader>().ToConstant(new StandardAssetLoader());
    }

    [Params(Workload.Icon, Workload.Logo, Workload.Map, Workload.Chart)]
    public Workload Document { get; set; }

    private string _xml = null!;
    private byte[] _blob = null!;

    [GlobalSetup]
    public void Setup()
    {
        _xml = SvgWorkloads.Build(Document);

        using var document = SvgDocument.Parse(_xml);
        _blob = SvgBlobWriter.Write(document);

        // Guard the comparison: a reader that built a wrong (e.g. empty) tree
        // would allocate less and make the numbers meaningless.
        using var roundTrip = SvgDocument.FromCompiledBlob(_blob);
        if (!TreeEqual(document.Root, roundTrip.Root))
            throw new InvalidOperationException($"Blob round-trip mismatch for {Document}.");
    }

    /// <summary>Current path: re-parse the minified XML string.</summary>
    [Benchmark(Baseline = true)]
    public Size ParseXml()
    {
        using var document = SvgDocument.Parse(_xml);
        return document.GetIntrinsicSize();
    }

    /// <summary>Proposed path: read the compiled binary blob.</summary>
    [Benchmark]
    public Size DecodeBlob()
    {
        using var document = SvgDocument.FromCompiledBlob(_blob);
        return document.GetIntrinsicSize();
    }

    /// <summary>Cold cost via the XML path: parse + compile to a recording.</summary>
    [Benchmark]
    public Size FirstFrameXml()
    {
        using var document = SvgDocument.Parse(_xml);
        using var image = new SvgImage(document);
        return image.Size;
    }

    /// <summary>Cold cost via the blob path: decode + compile to a recording.</summary>
    [Benchmark]
    public Size FirstFrameBlob()
    {
        using var document = SvgDocument.FromCompiledBlob(_blob);
        using var image = new SvgImage(document);
        return image.Size;
    }

    private static bool TreeEqual(SvgElement a, SvgElement b)
    {
        if (a.Name != b.Name || a.Attributes.Count != b.Attributes.Count)
            return false;

        foreach (var pair in a.Attributes)
        {
            if (!b.Attributes.TryGetValue(pair.Key, out var value) || value != pair.Value)
                return false;
        }

        var contentA = a.Content;
        var contentB = b.Content;
        var countA = contentA?.Count ?? 0;
        var countB = contentB?.Count ?? 0;
        if (countA != countB)
            return false;

        for (var i = 0; i < countA; i++)
        {
            switch (contentA![i])
            {
                case string text when contentB![i] is not string other || other != text:
                    return false;
                case SvgElement child when contentB![i] is not SvgElement otherChild || !TreeEqual(child, otherChild):
                    return false;
            }
        }

        return true;
    }
}
