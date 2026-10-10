using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Rendering.Composition.Drawing;
using Avalonia.Rendering.Composition.Server;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia.Helpers;
using Avalonia.Utilities;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Colour glyphs drawn from a recording made once per glyph: the recording is cached in the
    /// typeface's glyph cache, replayed by every draw byte for byte like the live drawing,
    /// charged to the glyph cache budget and disposed when the cache gives it up.
    /// </summary>
    public class ColorGlyphRecordingTests
    {
        private const int Width = 240;
        private const int Height = 120;
        private const double EmSize = 32;
        private const double Advance = 40;

        private static readonly Point s_origin = new(12, 64);

        // A coloured text brush, so a paint that resolves the foreground sentinel draws colour too.
        private static readonly IBrush s_text = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromRgb(0xD0, 0x40, 0x10));

        public enum Font
        {
            V1Solid,
            V1Sentinel,
            V0AndV1,
            V0Backend,
            BitmapBackend,
            SegoeUiEmoji,
        }

        public enum Route
        {
            /// <summary>The text layout split drawing straight into a platform context.</summary>
            Split,

            /// <summary>The text layout split recorded into render data, then replayed.</summary>
            SplitRenderData,

            /// <summary>A direct glyph run draw, cut at its colour glyphs on the drawing thread.</summary>
            Direct,
        }

        public enum State
        {
            Plain,
            Rotated,
            OpacityAndClip,
        }

        public static IEnumerable<object[]> IdentityCases()
        {
            foreach (Font font in Enum.GetValues(typeof(Font)))
            {
                foreach (Route route in Enum.GetValues(typeof(Route)))
                {
                    // Backend mode hands direct runs to the backend's native text path.
                    if (route == Route.Direct && IsBackend(font))
                    {
                        continue;
                    }

                    foreach (State state in Enum.GetValues(typeof(State)))
                    {
                        yield return new object[] { font, route, state };
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(IdentityCases))]
        public void Recorded_Colour_Glyphs_Render_The_Same_Bytes_As_Their_Live_Drawings_On_A_Raster_Context(
            Font font, Route route, State state)
        {
            using var scope = CreateEnvironment(managed: !IsBackend(font));
            var typeface = CreateTypeface(font, out var glyphs);

            AssertRecordedMatchesLive(typeface, glyphs, route, state, RenderOnRaster);
        }

        [Theory]
        [InlineData(GpuBackend.NativeGl, Font.SegoeUiEmoji, Route.Split)]
        [InlineData(GpuBackend.NativeGl, Font.SegoeUiEmoji, Route.SplitRenderData)]
        [InlineData(GpuBackend.NativeGl, Font.SegoeUiEmoji, Route.Direct)]
        [InlineData(GpuBackend.NativeGl, Font.V0AndV1, Route.Direct)]
        [InlineData(GpuBackend.NativeGl, Font.BitmapBackend, Route.SplitRenderData)]
        [InlineData(GpuBackend.Angle, Font.SegoeUiEmoji, Route.Split)]
        [InlineData(GpuBackend.Angle, Font.SegoeUiEmoji, Route.SplitRenderData)]
        [InlineData(GpuBackend.Angle, Font.SegoeUiEmoji, Route.Direct)]
        [InlineData(GpuBackend.Angle, Font.V0AndV1, Route.Direct)]
        [InlineData(GpuBackend.Angle, Font.BitmapBackend, Route.SplitRenderData)]
        public void Recorded_Colour_Glyphs_Render_The_Same_Bytes_As_Their_Live_Drawings_On_A_Gpu_Context(
            GpuBackend backend, Font font, Route route)
        {
            using var gpu = GpuTestContext.TryCreate(backend, out var reason);

            Assert.SkipWhen(gpu is null, $"No usable {backend} context: {reason}");

            using var scope = CreateEnvironment(managed: !IsBackend(font));
            var typeface = CreateTypeface(font, out var glyphs);

            foreach (State state in Enum.GetValues(typeof(State)))
            {
                AssertRecordedMatchesLive(typeface, glyphs, route, state,
                    (transform, draw) => RenderOnGpu(gpu!, transform, draw));
            }
        }

        [Theory]
        [InlineData(Font.V1Solid)]
        [InlineData(Font.V0AndV1)]
        [InlineData(Font.V0Backend)]
        [InlineData(Font.BitmapBackend)]
        [InlineData(Font.SegoeUiEmoji)]
        public void A_Colour_Glyph_Is_Recorded_Once(Font font)
        {
            using var scope = CreateEnvironment(managed: !IsBackend(font));
            var typeface = CreateTypeface(font, out var glyphs);
            var colorGlyph = glyphs[1];

            var first = typeface.GetGlyphRecording(colorGlyph, null);
            var second = typeface.GetGlyphRecording(colorGlyph, null);

            Assert.NotNull(first);
            Assert.Same(first, second);
            Assert.False(first!.Recording.IsDisposed);
            // .notdef has no colour drawing in any of the fonts.
            Assert.Null(typeface.GetGlyphRecording(0, null));
        }

        [Fact]
        public void Foregrounds_Share_A_Recording_Unless_The_Paint_Uses_The_Sentinel()
        {
            using var scope = CreateEnvironment(managed: true);
            var plain = CreateTypeface(Font.V1Solid, out var plainGlyphs);
            var sentinel = CreateTypeface(Font.V1Sentinel, out var sentinelGlyphs);
            var red = new GlyphDrawingOptions { Foreground = Colors.Red };
            var green = new GlyphDrawingOptions { Foreground = Colors.Green };

            var plainNone = plain.GetGlyphRecording(plainGlyphs[1], null);

            Assert.NotNull(plainNone);
            Assert.False(plainNone!.UsesForeground);
            Assert.Same(plainNone, plain.GetGlyphRecording(plainGlyphs[1], red));
            Assert.Same(plainNone, plain.GetGlyphRecording(plainGlyphs[1], green));

            var sentinelRed = sentinel.GetGlyphRecording(sentinelGlyphs[1], red);
            var sentinelGreen = sentinel.GetGlyphRecording(sentinelGlyphs[1], green);

            Assert.NotNull(sentinelRed);
            Assert.NotNull(sentinelGreen);
            Assert.NotSame(sentinelRed, sentinelGreen);
            Assert.Same(sentinelRed, sentinel.GetGlyphRecording(sentinelGlyphs[1], red));
            Assert.True(sentinel.GetGlyphRecording(sentinelGlyphs[1], null)!.UsesForeground);
        }

        [Fact]
        public void Splitting_A_Run_Into_Render_Data_Draws_Each_Colour_Glyph_As_One_Recording_Node()
        {
            using var scope = CreateEnvironment(managed: true);
            var typeface = CreateTypeface(Font.V1Solid, out var glyphs);
            var run = CreateGlyphRun(typeface, glyphs);

            using var context = new RenderDataDrawingContext(null);

            Assert.True(ColorGlyphRunSplitter.TryDraw(context, run, Brushes.Black));

            var stream = context.GetRenderStream();

            try
            {
                var visitor = new NodeCounter();
                stream.Visit<NodeCounter, int>(ref visitor);

                var recorded = typeface.GetGlyphRecording(glyphs[1], new GlyphDrawingOptions
                {
                    Foreground = Colors.Black
                });

                Assert.NotNull(recorded);
                Assert.Equal(2, visitor.Recordings);
                Assert.Equal(0, visitor.Geometries);
                Assert.Equal(2, visitor.RecordedStreams.Count);
                Assert.All(visitor.RecordedStreams, s => Assert.Same(recorded!.Recording.Stream, s));
            }
            finally
            {
                stream.DisposeResources();
                stream.Dispose();
            }
        }

        [Fact]
        public void Releasing_The_Glyph_Cache_Disposes_Its_Recordings()
        {
            using var scope = CreateEnvironment(managed: true);
            var typeface = CreateTypeface(Font.SegoeUiEmoji, out var glyphs);
            var recorded = typeface.GetGlyphRecording(glyphs[1], null);

            Assert.NotNull(recorded);

            typeface.GlyphCache!.Release();

            Assert.True(recorded!.Recording.IsDisposed);
        }

        [Fact]
        public void Disposing_The_Typeface_Disposes_Its_Recordings()
        {
            using var scope = CreateEnvironment(managed: true);
            var typeface = CreateTypeface(Font.V1Solid, out var glyphs);
            var recorded = typeface.GetGlyphRecording(glyphs[1], null);

            Assert.NotNull(recorded);

            typeface.Dispose();

            Assert.True(recorded!.Recording.IsDisposed);
        }

        [Fact]
        public void Budget_Eviction_Disposes_A_Recording_And_Credits_Its_Cost()
        {
            using var scope = CreateEnvironment(managed: true);
            var typeface = CreateTypeface(Font.SegoeUiEmoji, out var glyphs);

            // The drawing first, so the cost the recording adds is measured on its own.
            Assert.NotNull(typeface.GetGlyphDrawing(glyphs[1]));
            var cache = typeface.GlyphCache!;
            var before = cache.TotalCost;

            var recorded = typeface.GetGlyphRecording(glyphs[1], null);

            Assert.NotNull(recorded);

            var charged = cache.TotalCost - before;

            Assert.True(charged >= recorded!.Recording.Stream!.OpcodeLength,
                $"the recording charged {charged} bytes for {recorded.Recording.Stream.OpcodeLength} opcode bytes");

            var freed = ((IGlyphCachePool)cache).EvictOldest(long.MaxValue, long.MaxValue);

            Assert.True(freed >= charged);
            Assert.True(recorded.Recording.IsDisposed);
        }

        [Fact]
        public void A_Leased_Recording_Is_Disposed_At_Its_Last_Release()
        {
            using var scope = CreateEnvironment(managed: true);
            var typeface = CreateTypeface(Font.V1Solid, out var glyphs);
            var recorded = typeface.GetGlyphRecording(glyphs[1], null);

            Assert.NotNull(recorded);
            Assert.True(recorded!.TryAcquire());
            Assert.True(recorded.TryAcquire());

            typeface.GlyphCache!.Release();

            Assert.False(recorded.Recording.IsDisposed);
            Assert.False(recorded.TryAcquire());

            recorded.Release();
            Assert.False(recorded.Recording.IsDisposed);

            recorded.Release();
            Assert.True(recorded.Recording.IsDisposed);
        }

        [Fact]
        public void Render_Data_Keeps_Drawing_A_Recording_The_Cache_Released()
        {
            using var scope = CreateEnvironment(managed: true);
            var typeface = CreateTypeface(Font.SegoeUiEmoji, out var glyphs);
            var run = CreateGlyphRun(typeface, glyphs);

            using var context = new RenderDataDrawingContext(null);

            Assert.True(ColorGlyphRunSplitter.TryDraw(context, run, Brushes.Black));

            var stream = context.GetRenderStream();

            try
            {
                var before = RenderOnRaster(Matrix.Identity, (_, impl) => stream.Replay(impl));
                var recorded = typeface.GetGlyphRecording(glyphs[1], new GlyphDrawingOptions
                {
                    Foreground = Colors.Black
                });

                typeface.GlyphCache!.Release();

                Assert.NotNull(recorded);
                Assert.True(recorded!.Recording.IsDisposed);

                var after = RenderOnRaster(Matrix.Identity, (_, impl) => stream.Replay(impl));

                AssertSamePixels(before, after);
                Assert.True(CountColored(after) > 20, "the replayed render data drew no colour glyph");
            }
            finally
            {
                stream.DisposeResources();
                stream.Dispose();
            }
        }

        private static void AssertRecordedMatchesLive(GlyphTypeface typeface, ushort[] glyphs, Route route,
            State state, Func<Matrix, Action<DrawingContext, IDrawingContextImpl>, byte[]> render)
        {
            var transform = state == State.Rotated
                ? Matrix.CreateRotation(Math.PI * 17 / 180) * Matrix.CreateTranslation(30, -20)
                : Matrix.CreateTranslation(0.25, 0.5);

            byte[] Draw(bool recordings)
            {
                var previous = ColorGlyphRunSplitter.UseRecordings;
                ColorGlyphRunSplitter.UseRecordings = recordings;

                try
                {
                    using var run = CreateGlyphRun(typeface, glyphs);

                    return render(transform, (context, impl) =>
                    {
                        using (state == State.OpacityAndClip ? context.PushOpacity(0.6) : default(DrawingContext.PushedState?))
                        using (state == State.OpacityAndClip
                                   ? context.PushClip(new Rect(20, 10, 120, 70))
                                   : default(DrawingContext.PushedState?))
                        {
                            DrawRoute(context, impl, run, route);
                        }
                    });
                }
                finally
                {
                    ColorGlyphRunSplitter.UseRecordings = previous;
                }
            }

            var live = Draw(recordings: false);
            var recorded = Draw(recordings: true);

            Assert.True(CountColored(live) > 20, "the live drawings drew no colour");
            AssertSamePixels(live, recorded);
        }

        private static void DrawRoute(DrawingContext context, IDrawingContextImpl impl, GlyphRun run, Route route)
        {
            switch (route)
            {
                case Route.Split:
                    Assert.True(ColorGlyphRunSplitter.TryDraw(context, run, s_text));
                    break;
                case Route.SplitRenderData:
                {
                    using var recording = new RenderDataDrawingContext(null);

                    Assert.True(ColorGlyphRunSplitter.TryDraw(recording, run, s_text));

                    var stream = recording.GetRenderStream();

                    try
                    {
                        stream.Replay(impl);
                    }
                    finally
                    {
                        stream.DisposeResources();
                        stream.Dispose();
                    }

                    break;
                }
                case Route.Direct:
                    Assert.IsType<ManagedGlyphRunImpl>(run.PlatformImpl.Item);
                    context.DrawGlyphRun(s_text, run);
                    break;
            }
        }

        private static GlyphRun CreateGlyphRun(GlyphTypeface typeface, ushort[] glyphs)
        {
            var infos = new List<GlyphInfo>();

            for (var i = 0; i < glyphs.Length; i++)
            {
                infos.Add(new GlyphInfo(glyphs[i], i, Advance));
            }

            return new GlyphRun(typeface, EmSize, default, infos, s_origin);
        }

        private static bool IsBackend(Font font) => font is Font.V0Backend or Font.BitmapBackend;

        /// <summary>
        /// The typeface for <paramref name="font"/> and a run pattern of outline glyph, colour
        /// glyph, outline glyph, colour glyph (Segoe UI Emoji: four different emoji).
        /// </summary>
        private static GlyphTypeface CreateTypeface(Font font, out ushort[] glyphs)
        {
            GlyphTypeface typeface;
            ushort color;

            switch (font)
            {
                case Font.V1Solid:
                    typeface = ColorGlyphV1SplitTests.CreateV1Typeface(out color);
                    break;
                case Font.V1Sentinel:
                    typeface = ColorGlyphV1SplitTests.CreateV1Typeface(out color, paletteIndex: 0xFFFF);
                    break;
                case Font.V0AndV1:
                    typeface = ColorGlyphV1SplitTests.CreateV0AndV1Typeface(out color);
                    break;
                case Font.V0Backend:
                    typeface = ColorGlyphV1SplitTests.CreateV0Typeface(out color);
                    break;
                case Font.BitmapBackend:
                    typeface = BitmapGlyphRenderingTests.CreateBitmapTypeface(out color, out _);
                    break;
                default:
                {
                    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
                        "seguiemj.ttf");

                    Assert.SkipUnless(File.Exists(path), "Segoe UI Emoji is not installed.");

                    typeface = TestGlyphTypefaces.FromSKTypeface(SKTypeface.FromFile(path));

                    var map = typeface.CharacterToGlyphMap;

                    // A face (gradients), a fox (layers and transforms), a rainbow and a party popper.
                    glyphs = new[] { map[0x1F600], map[0x1F98A], map[0x1F308], map[0x1F389] };
                    Assert.DoesNotContain((ushort)0, glyphs);

                    return typeface;
                }
            }

            var outline = typeface.CharacterToGlyphMap['A'];
            var other = typeface.CharacterToGlyphMap['B'];

            glyphs = new[] { outline, color, other, color };

            return typeface;
        }

        // Recordings are the vector path: under managed rasterization the upright tier would draw
        // the v1 glyphs from colour masks instead.
        private static IDisposable CreateEnvironment(bool managed)
        {
            var masksOff = ColorGlyphMaskTests.SwitchColorMasksOff();
            var scope = AvaloniaLocator.EnterScope();

            AvaloniaLocator.CurrentMutable
                .Bind<IPlatformRenderInterface>().ToConstant(new PlatformRenderInterface());
            AvaloniaLocator.CurrentMutable
                .Bind<IBitmapGlyphDecoder>().ToConstant(new SkiaBitmapGlyphDecoder());
            AvaloniaLocator.CurrentMutable
                .Bind<FontManagerOptions>().ToConstant(new FontManagerOptions
                {
                    TextRasterizationMode = managed ? TextRasterizationMode.Managed : TextRasterizationMode.Backend,
                });

            return new CompositeDisposable(scope, masksOff);
        }

        private sealed class CompositeDisposable(params IDisposable[] parts) : IDisposable
        {
            public void Dispose()
            {
                foreach (var part in parts)
                {
                    part.Dispose();
                }
            }
        }

        private static byte[] RenderOnRaster(Matrix transform, Action<DrawingContext, IDrawingContextImpl> draw)
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);

            canvas.Clear(SKColors.White);

            using (var impl = (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96)))
            using (var context = new PlatformDrawingContext(impl, ownsImpl: false))
            {
                impl.Transform = transform;
                draw(context, impl);
            }

            return bitmap.GetPixelSpan().ToArray();
        }

        private static byte[] RenderOnGpu(GpuTestContext gpu, Matrix transform, Action<DrawingContext, IDrawingContextImpl> draw)
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            var readInfo = info.WithColorType(SKColorType.Bgra8888);

            using var surface = SKSurface.Create(gpu.GrContext, true, info);

            using (var impl = TransformedAtlasTests.CreateContext(gpu, surface))
            using (var context = new PlatformDrawingContext(impl, ownsImpl: false))
            {
                surface!.Canvas.Clear(SKColors.White);
                impl.Transform = transform;
                draw(context, impl);
            }

            gpu.GrContext.Flush();

            var pixels = new byte[readInfo.BytesSize];

            unsafe
            {
                fixed (byte* p = pixels)
                {
                    Assert.True(surface!.ReadPixels(readInfo, (IntPtr)p, readInfo.RowBytes, 0, 0));
                }
            }

            return pixels;
        }

        // Pixels that are neither white, grey nor black: some colour glyph drew there.
        private static int CountColored(byte[] bgra)
        {
            var count = 0;

            for (var i = 0; i < bgra.Length; i += 4)
            {
                int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];

                if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 60)
                {
                    count++;
                }
            }

            return count;
        }

        private static void AssertSamePixels(byte[] expected, byte[] actual)
        {
            Assert.Equal(expected.Length, actual.Length);

            var differing = 0;
            var first = -1;

            for (var i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                {
                    differing++;

                    if (first < 0)
                    {
                        first = i;
                    }
                }
            }

            Assert.True(differing == 0,
                $"{differing} bytes differ from the live drawing, first at pixel " +
                $"({first / 4 % Width}, {first / 4 / Width})");
        }

        private struct NodeCounter : IRenderDataVisitor<int>
        {
            public int Recordings;
            public int Geometries;
            public List<RenderDataStream?> RecordedStreams;

            public bool StopVisiting => false;

            public void OnDrawLine(IPen? serverPen, IPen? clientPen, Point p1, Point p2) { }
            public void OnDrawRectangle(IBrush? serverBrush, IPen? serverPen, IPen? clientPen, RoundedRect rect,
                BoxShadows boxShadows) { }
            public void OnDrawEllipse(IBrush? serverBrush, IPen? serverPen, IPen? clientPen, Rect rect) { }
            public void OnDrawGeometry(IBrush? serverBrush, IPen? serverPen, IPen? clientPen, IGeometryImpl? geometry)
                => Geometries++;
            public void OnDrawGlyphRun(IBrush? serverBrush, IRef<IGlyphRunImpl>? glyphRun) { }
            public void OnDrawBitmap(IRef<IBitmapImpl>? bitmap, double opacity, Rect sourceRect, Rect destRect) { }
            public void OnDrawCustom(ICustomDrawOperation? operation) { }

            public void OnDrawRecording(ServerCompositionRenderData? server, CompositionRenderData? client,
                RenderDataStream? stream, Matrix transform)
            {
                Recordings++;
                (RecordedStreams ??= new List<RenderDataStream?>()).Add(stream);
            }

            public int OnPushClip(RoundedRect clip) => 0;
            public int OnPushGeometryClip(IGeometryImpl? geometry) => 0;
            public int OnPushOpacity(double opacity) => 0;
            public int OnPushOpacityMask(IBrush? brush, Rect bounds) => 0;
            public int OnPushTransform(Matrix matrix) => 0;
            public int OnPushRenderOptions(RenderOptions options) => 0;
            public int OnPushTextOptions(TextOptions options) => 0;
            public int OnPushEffect(IEffect? effect, Rect bounds) => 0;
            public int OnPushLayer(LayerOptions options) => 0;
            public void OnPop(in int scope) { }
        }
    }
}
