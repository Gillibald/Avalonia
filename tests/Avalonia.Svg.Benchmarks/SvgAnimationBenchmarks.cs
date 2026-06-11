using System;
using System.Text;
using Avalonia.Harfbuzz;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.Svg.Animation;
using Avalonia.Svg.Compilation;
using BenchmarkDotNet.Attributes;

namespace Avalonia.Svg.Benchmarks;

/// <summary>
/// Measures the SMIL animation driver: the per-tick attribute interpolation
/// alone, and a full structural frame (tick plus document recompile) — the
/// cost the <c>Svg</c> control pays per frame while a structural animation
/// runs without a compositor.
/// </summary>
[MemoryDiagnoser]
public class SvgAnimationBenchmarks
{
    static SvgAnimationBenchmarks()
    {
        SkiaPlatform.Initialize();
        AvaloniaLocator.CurrentMutable
            .Bind<ITextShaperImpl>().ToConstant(new HarfBuzzTextShaper())
            .Bind<IAssetLoader>().ToConstant(new StandardAssetLoader());
    }

    private SvgDocument _document = null!;
    private SvgAnimator _animator = null!;
    private Size _size;
    private double _seconds;

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(5);
        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"400\" height=\"400\" viewBox=\"0 0 400 400\">");
        for (var i = 0; i < 20; i++)
        {
            sb.Append($"<rect x=\"{random.Next(360)}\" y=\"{i * 20}\" width=\"24\" height=\"12\" fill=\"#1f77b4\">");
            sb.Append($"<animate attributeName=\"x\" from=\"0\" to=\"{random.Next(200, 376)}\" dur=\"2s\" repeatCount=\"indefinite\"/>");
            sb.Append("</rect>");
        }

        for (var i = 0; i < 10; i++)
        {
            sb.Append($"<circle cx=\"{random.Next(400)}\" cy=\"{random.Next(400)}\" r=\"10\" fill=\"#d62728\">");
            sb.Append("<animate attributeName=\"fill\" from=\"#d62728\" to=\"#2ca02c\" dur=\"3s\" repeatCount=\"indefinite\"/>");
            sb.Append("</circle>");
        }

        sb.Append("</svg>");

        _document = SvgDocument.Parse(sb.ToString());
        _animator = SvgAnimator.TryCreate(_document)!;
        _size = _document.GetIntrinsicSize();
    }

    [GlobalCleanup]
    public void Cleanup() => _document?.Dispose();

    /// <summary>Interpolation and attribute writes only.</summary>
    [Benchmark]
    public void ApplyTick()
    {
        _seconds = (_seconds + 0.016) % 4;
        _animator.Apply(TimeSpan.FromSeconds(_seconds));
    }

    /// <summary>A structural animation frame: tick plus full recompile.</summary>
    [Benchmark]
    public Rect StructuralFrame()
    {
        _seconds = (_seconds + 0.016) % 4;
        _animator.Apply(TimeSpan.FromSeconds(_seconds));
        using var recording = DrawingRecording.Create(
            ctx => SvgCompiler.CompileDocument(_document, ctx, _size));
        return recording.Bounds;
    }
}
