using System;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using BenchmarkDotNet.Attributes;

namespace Avalonia.Benchmarks.Text;

/// <summary>
/// Phase 2 hot-path costs: the warm mask-cache hit (the per-glyph cost of composing a cached
/// run) and the compose loop itself (the F2 cold path minus rasterization). The memory column
/// is the D7 gate — a warm hit and a compose must not allocate.
/// </summary>
[MemoryDiagnoser]
public class GlyphMaskCacheBenchmark
{
    private const int GlyphCount = 16;
    private const int RunWidth = 480;
    private const int RunHeight = 40;

    private GlyphMaskCache _cache = null!;
    private GlyphMaskKey[] _keys = null!;
    private GlyphMask[] _masks = null!;
    private int[] _pens = null!;
    private byte[] _alphaRun = null!;
    private byte[] _tintedRun = null!;
    private uint _tint;

    [GlobalSetup]
    public void Setup()
    {
        var typeface = CffFonts.Load(CffFonts.GlyfAsset);
        var pool = CffFonts.BuildPool(typeface);
        var scratch = new GlyphPathBuilder();

        _cache = new GlyphMaskCache();
        _keys = new GlyphMaskKey[GlyphCount];
        _masks = new GlyphMask[GlyphCount];
        _pens = new int[GlyphCount];

        for (var i = 0; i < GlyphCount; i++)
        {
            _keys[i] = new GlyphMaskKey(
                pool[i % pool.Length], GlyphMaskKey.QuantizeScale(16f), (byte)(i % 4), GlyphMaskMode.Antialiased);
            _masks[i] = _cache.GetOrBuild(_keys[i], key => GlyphMasks.Build(typeface, scratch, key));
            _pens[i] = 8 + i * 24;
        }

        _alphaRun = new byte[RunWidth * RunHeight];
        _tintedRun = new byte[RunWidth * RunHeight * 4];
        _tint = RunMaskComposer.MakeTint(255, 32, 32, 32);
    }

    [Benchmark(Baseline = true)]
    public int WarmCacheHit()
    {
        // GlyphMask is internal, so return a consumed scalar to keep the call from being
        // dead-code-eliminated instead of the payload itself.
        return _cache.GetOrBuild(_keys[0], static _ => throw new InvalidOperationException("must be a hit")).Width;
    }

    [Benchmark]
    public void ComposeAlphaRun()
    {
        _alphaRun.AsSpan().Clear();

        for (var i = 0; i < GlyphCount; i++)
        {
            RunMaskComposer.ComposeAlpha(_masks[i], _pens[i], 30, _alphaRun, RunWidth, RunHeight);
        }
    }

    [Benchmark]
    public void ComposeTintedRun()
    {
        _tintedRun.AsSpan().Clear();

        for (var i = 0; i < GlyphCount; i++)
        {
            RunMaskComposer.ComposeTinted(_masks[i], _pens[i], 30, _tint, _tintedRun, RunWidth, RunHeight);
        }
    }
}
