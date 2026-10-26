using System;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.TextFormatting;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    public class GlyphTypefaceLigatureCaretTests
    {
        private const string NotoSansItalic = "Avalonia.Skia.UnitTests.Assets.NotoSans-Italic.ttf";
        private const string NotoSansHebrew = "Avalonia.Skia.UnitTests.Assets.NotoSansHebrew-Regular.ttf";
        private const string DejaVuSans = "Avalonia.Skia.UnitTests.Fonts.DejaVuSans.ttf";
        private const string Manrope = "Avalonia.Skia.UnitTests.Fonts.Manrope-Light.ttf";

        [Fact]
        public void Shaped_Ffi_Ligature_Should_Return_The_Font_Carets()
        {
            using (Start())
            {
                var glyphTypeface = Load(NotoSansItalic);

                var ffi = ShapeSingleGlyph(glyphTypeface, "ffi");

                Assert.Equal(new[] { 247, 557 }, glyphTypeface.GetLigatureCaretPositions(ffi).ToArray());
            }
        }

        [Fact]
        public void Ligature_Without_Carets_In_A_Font_With_A_LigCaretList_Should_Return_None()
        {
            using (Start())
            {
                var glyphTypeface = Load(NotoSansItalic);

                var ff = ShapeSingleGlyph(glyphTypeface, "ff");

                Assert.True(glyphTypeface.GetLigatureCaretPositions(ff).IsEmpty);
            }
        }

        [Fact]
        public void Ligature_Of_A_Font_With_An_Empty_LigCaretList_Should_Return_None()
        {
            using (Start())
            {
                var glyphTypeface = Load(DejaVuSans);

                Assert.True(glyphTypeface.CharacterToGlyphMap.TryGetGlyph(0xFB03, out var ffi));

                Assert.True(glyphTypeface.GetLigatureCaretPositions(ffi).IsEmpty);
            }
        }

        [Fact]
        public void Hebrew_Alef_Lamed_Should_Return_The_Font_Caret()
        {
            using (Start())
            {
                var glyphTypeface = Load(NotoSansHebrew);

                Assert.True(glyphTypeface.CharacterToGlyphMap.TryGetGlyph(0xFB4F, out var alefLamed));

                Assert.Equal(new[] { 299 }, glyphTypeface.GetLigatureCaretPositions(alefLamed).ToArray());
            }
        }

        [Fact]
        public void Font_Without_A_GDEF_Table_Should_Return_None()
        {
            using (Start())
            {
                var glyphTypeface = Load(Manrope);

                var shapedBuffer = TextShaper.Current.ShapeText("ff",
                    new TextShaperOptions(glyphTypeface, 12, 0, CultureInfo.InvariantCulture));

                Assert.True(glyphTypeface.GetLigatureCaretPositions(shapedBuffer[0].GlyphIndex).IsEmpty);
            }
        }

        [Fact]
        public void Disposed_Typeface_Should_Return_None()
        {
            using (Start())
            {
                var glyphTypeface = Load(NotoSansItalic);

                var ffi = ShapeSingleGlyph(glyphTypeface, "ffi");

                glyphTypeface.Dispose();

                Assert.True(glyphTypeface.GetLigatureCaretPositions(ffi).IsEmpty);
            }
        }

        [Fact]
        public void Repeated_Calls_Should_Return_The_Same_Positions()
        {
            using (Start())
            {
                var glyphTypeface = Load(NotoSansItalic);

                var ffi = ShapeSingleGlyph(glyphTypeface, "ffi");

                var first = glyphTypeface.GetLigatureCaretPositions(ffi);
                var second = glyphTypeface.GetLigatureCaretPositions(ffi);

                Assert.Equal(new[] { 247, 557 }, first.ToArray());
                Assert.Equal(first.ToArray(), second.ToArray());

                // The carets are decoded once and then served from the same memory.
                Assert.True(Unsafe.AreSame(ref MemoryMarshal.GetReference(first), ref MemoryMarshal.GetReference(second)));
            }
        }

        [Fact]
        public void Concurrent_First_Calls_Should_Agree()
        {
            using (Start())
            {
                var glyphTypeface = Load(NotoSansItalic);

                var ffi = ShapeSingleGlyph(glyphTypeface, "ffi");

                const int threadCount = 8;

                using var barrier = new Barrier(threadCount);

                var results = new int[threadCount][];

                var threads = Enumerable.Range(0, threadCount)
                    .Select(index => new Thread(() =>
                    {
                        barrier.SignalAndWait();

                        results[index] = glyphTypeface.GetLigatureCaretPositions(ffi).ToArray();
                    }))
                    .ToArray();

                foreach (var thread in threads)
                {
                    thread.Start();
                }

                foreach (var thread in threads)
                {
                    thread.Join();
                }

                foreach (var result in results)
                {
                    Assert.Equal(new[] { 247, 557 }, result);
                }
            }
        }

        private static ushort ShapeSingleGlyph(GlyphTypeface glyphTypeface, string text)
        {
            var shapedBuffer = TextShaper.Current.ShapeText(text,
                new TextShaperOptions(glyphTypeface, 12, 0, CultureInfo.InvariantCulture));

            Assert.Equal(1, shapedBuffer.Length);

            return shapedBuffer[0].GlyphIndex;
        }

        private static GlyphTypeface Load(string resourceName)
        {
            var loader = AvaloniaLocator.Current.GetRequiredService<Platform.IAssetLoader>();

            using var stream = loader.Open(new Uri($"resm:{resourceName}?assembly=Avalonia.Skia.UnitTests", UriKind.Absolute));

            Assert.True(SfntFace.TryLoad(stream, out var face));

            return new GlyphTypeface(face);
        }

        private static IDisposable Start()
        {
            return UnitTestApplication.Start(TestServices.MockPlatformRenderInterface
                .With(renderInterface: new PlatformRenderInterface()));
        }
    }
}
