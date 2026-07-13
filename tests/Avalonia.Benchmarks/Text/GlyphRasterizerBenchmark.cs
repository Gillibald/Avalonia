using System;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Platform;
using Avalonia.Skia;
using BenchmarkDotNet.Attributes;
using SkiaSharp;

namespace Avalonia.Benchmarks.Text;

/// <summary>
/// Cold-path cost of the managed glyph rasterizer (Phase 1 gate): one table walk plus one
/// coverage fill per invocation, against Skia drawing the same glyph from the same font bytes
/// into an equally sized alpha surface.
/// </summary>
/// <remarks>
/// The Skia number is a warm-strike draw — Skia caches the rasterized glyph internally after the
/// first invocation and there is no public way to defeat that cache per iteration — so the
/// managed column is a cold raster compared against Skia's cached blit. The Phase 1 plan gate
/// ("within ~2x of Skia mask generation") therefore reads conservatively here: matching a warm
/// Skia draw with a cold managed raster is a stronger result than the gate asks for. The memory
/// column is the D7/F3 signal: steady-state managed rasterization should allocate only what the
/// table walkers themselves still allocate per parse (a known pr2-era item), not anything from
/// the rasterization layer.
/// </remarks>
[MemoryDiagnoser]
public class GlyphRasterizerBenchmark
{
    private GlyphTypeface _typeface = null!;
    private SKTypeface _skTypeface = null!;
    private SKFont _skFont = null!;
    private SKTextBlob _skBlob = null!;
    private SKSurface _skSurface = null!;
    private GlyphPathBuilder _builder = null!;
    private byte[] _destination = null!;
    private Matrix _transform;
    private ushort _glyph;
    private int _width;
    private int _height;
    private float _offsetX;
    private float _offsetY;

    [Params(16f, 48f)]
    public float PixelSize;

    [GlobalSetup]
    public void Setup()
    {
        var assetLoader = new StandardAssetLoader();
        using var stream = assetLoader.Open(new Uri(CffFonts.GlyfAsset));
        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        var skData = SKData.CreateCopy(memory.ToArray());
        _skTypeface = SKTypeface.FromData(skData)
            ?? throw new InvalidOperationException("SkiaSharp failed to load the benchmark font.");

        _typeface = new GlyphTypeface(new SkiaTypeface(_skTypeface, FontSimulations.None));
        _glyph = _typeface.CharacterToGlyphMap['g'];

        var scale = PixelSize / _typeface.Metrics.DesignEmHeight;

        if (!_typeface.TryGetGlyphInkBounds(_glyph, out var box))
        {
            throw new InvalidOperationException("Benchmark glyph has no ink bounds.");
        }

        const int apron = 2;
        var maskLeft = (int)Math.Floor(box.XMin * scale) - apron;
        var maskTop = (int)Math.Floor(-box.YMax * scale) - apron;
        _width = (int)Math.Ceiling(box.XMax * scale) + apron - maskLeft;
        _height = (int)Math.Ceiling(-box.YMin * scale) + apron - maskTop;
        _offsetX = -maskLeft;
        _offsetY = -maskTop;
        _transform = new Matrix(scale, 0, 0, -scale, 0, 0);

        _builder = new GlyphPathBuilder();
        _destination = new byte[_width * _height];

        _skFont = new SKFont(_skTypeface, PixelSize)
        {
            Hinting = SKFontHinting.None,
            Subpixel = true,
            Edging = SKFontEdging.Antialias,
            BaselineSnap = false,
        };

        var blobBuilder = new SKTextBlobBuilder();
        var run = blobBuilder.AllocatePositionedRun(_skFont, 1);
        run.SetGlyphs(new[] { _glyph });
        run.SetPositions(new[] { new SKPoint(0, 0) });
        _skBlob = blobBuilder.Build()!;

        _skSurface = SKSurface.Create(new SKImageInfo(_width, _height, SKColorType.Alpha8, SKAlphaType.Premul))
            ?? SKSurface.Create(new SKImageInfo(_width, _height, SKColorType.Bgra8888))!;
    }

    [Benchmark(Baseline = true)]
    public void SkiaDrawGlyphMask()
    {
        var canvas = _skSurface.Canvas;
        canvas.Clear(SKColors.Transparent);

        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawText(_skBlob, _offsetX, _offsetY, paint);
    }

    [Benchmark]
    public void ManagedColdRasterize()
    {
        _builder.Reset();
        _typeface.TryBuildGlyphContours(_glyph, _transform, _builder);
        GlyphRasterizer.Rasterize(_builder, _width, _height, _offsetX, _offsetY, false, _destination);
    }

    /// <summary>
    /// The rasterization layer alone (captured path reused): the cost Phase 2's per-glyph mask
    /// cache pays per additional subpixel phase, with the table walk amortized away.
    /// </summary>
    [Benchmark]
    public void ManagedRasterizeCapturedPath()
    {
        GlyphRasterizer.Rasterize(_builder, _width, _height, _offsetX, _offsetY, false, _destination);
    }
}
