using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Skia;
using Avalonia.Skia.Helpers;
using BenchmarkDotNet.Attributes;
using SkiaSharp;

namespace Avalonia.Benchmarks.Text;

/// <summary>
/// The Phase 3 scene gate: steady-state frame cost of drawing a text-heavy scene (40 runs,
/// ~18 glyphs each — a paragraph-sized page) through <c>DrawGlyphRun</c>, managed masks against
/// the backend blob path, plus the compose-churn worst case (a foreground animation cycling
/// more colors than the run-mask cache holds, so every draw recomposes). The memory column is
/// the D7/F0 contract at scene scale: warm frames must allocate nothing on either path.
/// </summary>
[MemoryDiagnoser]
public class GlyphRunDrawBenchmark
{
    private const int RunCount = 40;

    private DrawingContextImpl _context = null!;
    private SKBitmap _bitmap = null!;
    private SKCanvas _canvas = null!;
    private IGlyphRunImpl[] _backendRuns = null!;
    private IGlyphRunImpl[] _managedRuns = null!;
    private ImmutableSolidColorBrush[] _churnBrushes = null!;
    private int _churnIndex;

    [GlobalSetup]
    public void Setup()
    {
        AvaloniaLocator.CurrentMutable
            .Bind<IPlatformRenderInterface>().ToConstant(new PlatformRenderInterface());

        var typeface = CffFonts.Load(CffFonts.GlyfAsset);

        _bitmap = new SKBitmap(new SKImageInfo(800, 640, SKColorType.Bgra8888, SKAlphaType.Premul));
        _canvas = new SKCanvas(_bitmap);
        _context = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(_canvas, new Vector(96, 96));

        _backendRuns = new IGlyphRunImpl[RunCount];
        _managedRuns = new IGlyphRunImpl[RunCount];

        for (var i = 0; i < RunCount; i++)
        {
            var origin = new Point(8, 14 * (i + 1));
            _backendRuns[i] = new GlyphRunImpl(typeface, 12, BuildInfos(typeface, 12), origin);
            _managedRuns[i] = new ManagedGlyphRunImpl(typeface, 12, BuildInfos(typeface, 12), origin);
        }

        // Five colors overflow the run-mask cache (one primary + three secondary slots), so the
        // churn benchmark recomposes on every single draw — the F2 worst case.
        _churnBrushes = new[]
        {
            new ImmutableSolidColorBrush(Colors.Black),
            new ImmutableSolidColorBrush(Colors.DarkRed),
            new ImmutableSolidColorBrush(Colors.DarkGreen),
            new ImmutableSolidColorBrush(Colors.DarkBlue),
            new ImmutableSolidColorBrush(Colors.DarkOrange),
        };

        // Warm both paths so the benchmarks measure steady state.
        BackendWarmFrame();
        ManagedWarmFrame();
    }

    private static List<GlyphInfo> BuildInfos(GlyphTypeface typeface, double emSize)
    {
        var scale = emSize / typeface.Metrics.DesignEmHeight;
        var infos = new List<GlyphInfo>();
        var cluster = 0;

        foreach (var c in "The quick brown fox 42")
        {
            var glyph = typeface.CharacterToGlyphMap[c];
            typeface.TryGetGlyphMetrics(glyph, out var metrics);
            infos.Add(new GlyphInfo(glyph, cluster++, metrics.AdvanceWidth * scale));
        }

        return infos;
    }

    [Benchmark(Baseline = true)]
    public void BackendWarmFrame()
    {
        for (var i = 0; i < RunCount; i++)
        {
            _context.DrawGlyphRun(Brushes.Black, _backendRuns[i]);
        }
    }

    [Benchmark]
    public void ManagedWarmFrame()
    {
        for (var i = 0; i < RunCount; i++)
        {
            _context.DrawGlyphRun(Brushes.Black, _managedRuns[i]);
        }
    }

    [Benchmark]
    public void ManagedComposeChurnFrame()
    {
        var brush = _churnBrushes[_churnIndex];
        _churnIndex = (_churnIndex + 1) % _churnBrushes.Length;

        for (var i = 0; i < RunCount; i++)
        {
            _context.DrawGlyphRun(brush, _managedRuns[i]);
        }
    }
}
