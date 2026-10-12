using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Fonts.Tables.Hvgl;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.CoreText.UnitTests
{
    /// <summary>
    /// hvgl outlines (Apple's PingFang UI, the Chinese system font on macOS and iOS) against the
    /// outlines CoreText draws for the same face, glyph and variation position.
    /// </summary>
    public unsafe class HvglOutlineTests
    {
        private const string PingFangUIPath =
            "/System/Library/PrivateFrameworks/FontServices.framework/Resources/Reserved/PingFangUI.ttc";

        private const double Tolerance = 1e-3;

        private static readonly string s_sample = "中國字永漢語書寫あア한Ag";

        [MacOSFact]
        public void PingFang_UI_Should_Have_Hvgl_Outlines()
        {
            SkipUnlessAvailable();

            using var face = LoadFace(0);
            var glyphTypeface = face.Typeface;

            Assert.Equal(GlyphOutlineType.Hvgl, glyphTypeface.OutlineType);
            Assert.NotEqual(0, glyphTypeface.CharacterToGlyphMap[0x4E2D]);

            Assert.True(glyphTypeface.TryGetGlyphInkBounds(glyphTypeface.CharacterToGlyphMap[0x4E2D], out var box));
            Assert.True(box.XMax > box.XMin && box.YMax > box.YMin);
        }

        [MacOSFact]
        public void Every_Face_Should_Draw_The_Outlines_CoreText_Draws()
        {
            SkipUnlessAvailable();

            var mismatches = new List<string>();
            var compared = 0;

            foreach (var (postScriptName, descriptor) in CoreTextFaces())
            {
                Assert.True(SfntNameReader.TryResolveFaceIndex(PingFangUIPath, postScriptName, out var faceIndex));

                using var face = LoadFace(faceIndex);

                // Each face of the collection is a named instance of one variable font; CoreText
                // draws it at the instance's position.
                var glyphTypeface = face.Typeface.WithVariations(new FontVariationSettings(InstancePosition(descriptor)));

                var font = CTNative.CTFontCreateWithFontDescriptor(descriptor, glyphTypeface.Metrics.DesignEmHeight, IntPtr.Zero);

                try
                {
                    foreach (var glyph in SampleGlyphs(glyphTypeface))
                    {
                        compared++;
                        Compare(postScriptName, glyphTypeface, font, glyph, mismatches);
                    }
                }
                finally
                {
                    CTNative.CFRelease(font);
                }
            }

            Assert.True(compared > 100, $"only {compared} glyphs compared");
            Assert.True(mismatches.Count == 0,
                $"{mismatches.Count} of {compared} glyphs differ: " +
                string.Join(", ", mismatches.GroupBy(m => m.Split(' ')[0]).Select(g => $"{g.Key} {g.Count()}")) +
                "\n" + string.Join("\n", mismatches.Take(20)));
        }

        [MacOSFact]
        public void Variation_Positions_Should_Draw_The_Outlines_CoreText_Draws()
        {
            SkipUnlessAvailable();

            var (postScriptName, descriptor) = CoreTextFaces().First();

            Assert.True(SfntNameReader.TryResolveFaceIndex(PingFangUIPath, postScriptName, out var faceIndex));

            using var face = LoadFace(faceIndex);
            var source = face.Typeface;

            var axes = source.VariationAxes;

            Assert.NotEmpty(axes);

            var mismatches = new List<string>();

            // Each axis at its extremes, then all axes off their defaults at once.
            var positions = new List<FontVariation[]>();

            foreach (var axis in axes)
            {
                positions.Add(new[] { new FontVariation(axis.Tag, axis.MinimumValue) });
                positions.Add(new[] { new FontVariation(axis.Tag, axis.MaximumValue) });
            }

            positions.Add(axes.Select(a => new FontVariation(a.Tag, (a.DefaultValue + a.MaximumValue) / 2)).ToArray());
            positions.Add(axes.Select(a => new FontVariation(a.Tag, (a.DefaultValue + a.MinimumValue) / 2)).ToArray());

            // CoreText keeps the face's instance position for the axes a variation leaves out.
            var instance = InstancePosition(descriptor);

            foreach (var position in positions)
            {
                var merged = instance.Where(i => position.All(p => p.Tag != i.Tag)).Concat(position);
                var varied = source.WithVariations(new FontVariationSettings(merged));
                var font = CreateVariedFont(descriptor, source.Metrics.DesignEmHeight, position);

                try
                {
                    foreach (var glyph in SampleGlyphs(source).Take(12))
                    {
                        Compare(string.Join(" ", position.Select(p => p.ToString())), varied, font, glyph, mismatches);
                    }
                }
                finally
                {
                    CTNative.CFRelease(font);
                }
            }

            Assert.True(mismatches.Count == 0, string.Join("\n", mismatches.Take(20)));
        }

        private static void SkipUnlessAvailable()
        {
            Assert.SkipUnless(File.Exists(PingFangUIPath), "PingFang UI is not installed.");
            Assert.SkipUnless(HvglTable.IsSupported, "The system has no hvgl renderer (macOS 15.4 or later).");
        }

        private static LoadedFace LoadFace(int faceIndex)
        {
            Assert.True(SfntFace.TryLoad(PingFangUIPath, faceIndex, out var face));

            return new LoadedFace(new GlyphTypeface(face));
        }

        private sealed class LoadedFace(GlyphTypeface typeface) : IDisposable
        {
            public GlyphTypeface Typeface { get; } = typeface;

            public void Dispose() => Typeface.Dispose();
        }

        private static IEnumerable<ushort> SampleGlyphs(GlyphTypeface glyphTypeface)
        {
            var seen = new HashSet<ushort>();

            foreach (var rune in s_sample.EnumerateRunes())
            {
                var glyph = glyphTypeface.CharacterToGlyphMap[rune.Value];

                if (glyph != 0 && seen.Add(glyph))
                {
                    yield return glyph;
                }
            }

            // A spread over the whole glyph range reaches parts the characters above do not.
            for (var glyph = 1; glyph < glyphTypeface.GlyphCount; glyph += 997)
            {
                if (seen.Add((ushort)glyph))
                {
                    yield return (ushort)glyph;
                }
            }
        }

        private static void Compare(string label, GlyphTypeface glyphTypeface, IntPtr font, ushort glyph, List<string> mismatches)
        {
            var ours = new PathRecorder();

            Assert.True(glyphTypeface.TryBuildGlyphContours(glyph, Matrix.Identity, ours), $"{label} glyph {glyph}");

            var expected = CoreTextPath(font, glyph);

            if (!SamePath(ours.Elements, expected, out var detail))
            {
                mismatches.Add($"{label} glyph {glyph}: {detail}");
            }
        }

        private static bool SamePath(List<(char Kind, double[] Points)> actual, List<(char Kind, double[] Points)> expected,
            out string detail)
        {
            detail = string.Empty;

            if (actual.Count != expected.Count)
            {
                detail = $"{actual.Count} elements, CoreText {expected.Count}; first: {Describe(actual)} | {Describe(expected)}";
                return false;
            }

            for (var i = 0; i < actual.Count; i++)
            {
                var a = actual[i];
                var e = expected[i];

                if (a.Kind != e.Kind || a.Points.Length != e.Points.Length ||
                    a.Points.Zip(e.Points).Any(p => Math.Abs(p.First - p.Second) > Tolerance))
                {
                    detail = $"element {i}: {a.Kind} {string.Join(",", a.Points)} vs CoreText {e.Kind} {string.Join(",", e.Points)}";
                    return false;
                }
            }

            return true;
        }

        private static string Describe(List<(char Kind, double[] Points)> path)
            => string.Join(" ", path.Take(4).Select(e => e.Kind + string.Join(",", e.Points.Select(v => v.ToString("0.##", CultureInfo.InvariantCulture)))));

        private static IEnumerable<(string PostScriptName, IntPtr Descriptor)> CoreTextFaces()
        {
            Assert.True(CTNative.TryInitialize());

            var path = CTNative.CreateString(PingFangUIPath);
            var url = CFURLCreateWithFileSystemPath(IntPtr.Zero, path, 0, false);
            var descriptors = CTFontManagerCreateFontDescriptorsFromURL(url);

            CTNative.CFRelease(path);
            CTNative.CFRelease(url);

            Assert.NotEqual(IntPtr.Zero, descriptors);

            var faces = new List<(string, IntPtr)>();

            for (nint i = 0; i < CTNative.CFArrayGetCount(descriptors); i++)
            {
                var descriptor = CTNative.CFArrayGetValueAtIndex(descriptors, i);
                var name = CTNative.CTFontDescriptorCopyAttribute(descriptor, CTNative.FontNameAttribute);

                faces.Add((CTNative.GetString(name)!, descriptor));
                CTNative.CFRelease(name);
            }

            // The array keeps the descriptors alive for the test run.
            return faces;
        }

        private static List<FontVariation> InstancePosition(IntPtr descriptor)
        {
            var position = new List<FontVariation>();
            var variation = CTNative.CTFontDescriptorCopyAttribute(descriptor, CTNative.FontVariationAttribute);

            if (variation == IntPtr.Zero)
            {
                return position;
            }

            var count = (int)CTNative.CFDictionaryGetCount(variation);
            var keys = new IntPtr[count];
            var values = new IntPtr[count];

            fixed (IntPtr* keysPtr = keys)
            fixed (IntPtr* valuesPtr = values)
            {
                CTNative.CFDictionaryGetKeysAndValues(variation, keysPtr, valuesPtr);
            }

            for (var i = 0; i < count; i++)
            {
                if (CTNative.TryGetNumber(keys[i], out int tag) && CTNative.TryGetNumber(values[i], out double value))
                {
                    position.Add(new FontVariation((uint)tag, value));
                }
            }

            CTNative.CFRelease(variation);

            return position;
        }

        private static IntPtr CreateVariedFont(IntPtr descriptor, double size, FontVariation[] position)
        {
            var variation = CTNative.CFDictionaryCreateMutable(IntPtr.Zero, position.Length,
                CTNative.TypeDictionaryKeyCallBacks, CTNative.TypeDictionaryValueCallBacks);

            foreach (var axis in position)
            {
                var key = CTNative.CreateNumber((int)(uint)axis.Tag);
                var value = CTNative.CreateNumber(axis.Value);

                CTNative.CFDictionarySetValue(variation, key, value);
                CTNative.CFRelease(key);
                CTNative.CFRelease(value);
            }

            var attributes = CTNative.CFDictionaryCreateMutable(IntPtr.Zero, 1,
                CTNative.TypeDictionaryKeyCallBacks, CTNative.TypeDictionaryValueCallBacks);

            CTNative.CFDictionarySetValue(attributes, CTNative.FontVariationAttribute, variation);

            var variedDescriptor = CTFontDescriptorCreateCopyWithAttributes(descriptor, attributes);
            var font = CTNative.CTFontCreateWithFontDescriptor(variedDescriptor, size, IntPtr.Zero);

            CTNative.CFRelease(variedDescriptor);
            CTNative.CFRelease(attributes);
            CTNative.CFRelease(variation);

            return font;
        }

        private static List<(char Kind, double[] Points)> CoreTextPath(IntPtr font, ushort glyph)
        {
            var elements = new List<(char, double[])>();
            var path = CTFontCreatePathForGlyph(font, glyph, IntPtr.Zero);

            if (path == IntPtr.Zero)
            {
                return elements;
            }

            var handle = GCHandle.Alloc(elements);

            try
            {
                CGPathApply(path, GCHandle.ToIntPtr(handle), &OnPathElement);
            }
            finally
            {
                handle.Free();
                CGPathRelease(path);
            }

            return elements;
        }

        [UnmanagedCallersOnly]
        private static void OnPathElement(IntPtr info, CGPathElement* element)
        {
            var elements = (List<(char, double[])>)GCHandle.FromIntPtr(info).Target!;

            var (kind, count) = element->Type switch
            {
                0 => ('M', 1),
                1 => ('L', 1),
                2 => ('Q', 2),
                3 => ('C', 3),
                _ => ('Z', 0)
            };

            var points = new double[count * 2];

            for (var i = 0; i < count; i++)
            {
                points[i * 2] = element->Points[i * 2];
                points[i * 2 + 1] = element->Points[i * 2 + 1];
            }

            elements.Add((kind, points));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CGPathElement
        {
            public int Type;
            public double* Points;
        }

        private sealed class PathRecorder : IGeometryContext
        {
            public List<(char Kind, double[] Points)> Elements { get; } = new();

            public void BeginFigure(Point startPoint, bool isFilled = true) => Elements.Add(('M', new[] { startPoint.X, startPoint.Y }));

            public void LineTo(Point point, bool isStroked = true) => Elements.Add(('L', new[] { point.X, point.Y }));

            public void QuadraticBezierTo(Point controlPoint, Point endPoint, bool isStroked = true)
                => Elements.Add(('Q', new[] { controlPoint.X, controlPoint.Y, endPoint.X, endPoint.Y }));

            public void CubicBezierTo(Point controlPoint1, Point controlPoint2, Point endPoint, bool isStroked = true)
                => Elements.Add(('C', new[] { controlPoint1.X, controlPoint1.Y, controlPoint2.X, controlPoint2.Y, endPoint.X, endPoint.Y }));

            public void ArcTo(Point point, Size size, double rotationAngle, bool isLargeArc, SweepDirection sweepDirection,
                bool isStroked = true) => throw new NotSupportedException();

            public void EndFigure(bool isClosed) => Elements.Add(('Z', Array.Empty<double>()));

            public void SetFillRule(FillRule fillRule)
            {
            }

            public void Dispose()
            {
            }
        }

        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const string CoreTextLibrary = "/System/Library/Frameworks/CoreText.framework/CoreText";
        private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFURLCreateWithFileSystemPath(IntPtr allocator, IntPtr filePath, nint pathStyle,
            [MarshalAs(UnmanagedType.U1)] bool isDirectory);

        [DllImport(CoreTextLibrary)]
        private static extern IntPtr CTFontManagerCreateFontDescriptorsFromURL(IntPtr fileUrl);

        [DllImport(CoreTextLibrary)]
        private static extern IntPtr CTFontDescriptorCreateCopyWithAttributes(IntPtr original, IntPtr attributes);

        [DllImport(CoreTextLibrary)]
        private static extern IntPtr CTFontCreatePathForGlyph(IntPtr font, ushort glyph, IntPtr matrix);

        [DllImport(CoreGraphics)]
        private static extern void CGPathApply(IntPtr path, IntPtr info, delegate* unmanaged<IntPtr, CGPathElement*, void> function);

        [DllImport(CoreGraphics)]
        private static extern void CGPathRelease(IntPtr path);
    }
}
