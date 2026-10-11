using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Compares the Skia system font provider with the DirectWrite one on the installed fonts.
    /// Both end in DirectWrite on Windows, so they must resolve requests to the same physical
    /// faces; only the designed properties a descriptor reports may differ (Skia reports the
    /// properties of a DirectWrite-simulated face, DirectWrite those of the physical face).
    /// </summary>
    public class SystemFontProviderParityTests
    {
        private static readonly (FontStyle Style, FontWeight Weight, FontStretch Stretch)[] s_styles =
        {
            (FontStyle.Normal, FontWeight.Normal, FontStretch.Normal),
            (FontStyle.Normal, FontWeight.Bold, FontStretch.Normal),
            (FontStyle.Italic, FontWeight.Normal, FontStretch.Normal),
            (FontStyle.Italic, FontWeight.Bold, FontStretch.Normal),
            (FontStyle.Normal, FontWeight.Light, FontStretch.Normal),
            (FontStyle.Normal, FontWeight.SemiBold, FontStretch.Normal),
            (FontStyle.Normal, FontWeight.Black, FontStretch.Normal),
            (FontStyle.Normal, FontWeight.Normal, FontStretch.Condensed),
        };

        [Win32Fact("Requires DirectWrite")]
        public void Every_Skia_Family_Should_Resolve_Through_DirectWrite()
        {
            using var skia = new SkiaFontProvider();
            using var directWrite = new DirectWriteFontProvider();

            var unresolved = skia.GetFontFamilyNames()
                .Where(name => !directWrite.TryMatchFamily(name, FontStyle.Normal, FontWeight.Normal,
                    FontStretch.Normal, out _))
                .ToList();

            Assert.Empty(unresolved);
        }

        [Win32Fact("Requires DirectWrite")]
        public void Default_Faces_Should_Be_The_Same_Face()
        {
            using var skia = new SkiaFontProvider();
            using var directWrite = new DirectWriteFontProvider();
            var identities = new FaceIdentities();

            Assert.True(skia.TryGetDefaultFontFace(out var skiaFace));
            Assert.True(directWrite.TryGetDefaultFontFace(out var directWriteFace));

            Assert.Equal(identities.Of(skiaFace), identities.Of(directWriteFace));
        }

        [Win32Fact("Requires DirectWrite")]
        public void Family_Faces_Should_Be_The_Same_Faces()
        {
            using var skia = new SkiaFontProvider();
            using var directWrite = new DirectWriteFontProvider();
            var identities = new FaceIdentities();
            var mismatches = new List<string>();

            foreach (var name in skia.GetFontFamilyNames())
            {
                var skiaFaces = skia.TryGetFamilyFaces(name, out var s)
                    ? s.Select(identities.Of).ToHashSet()
                    : new HashSet<string>();
                var directWriteFaces = directWrite.TryGetFamilyFaces(name, out var d)
                    ? d.Select(identities.Of).ToHashSet()
                    : new HashSet<string>();

                if (!skiaFaces.SetEquals(directWriteFaces))
                {
                    mismatches.Add($"{name}: Skia {skiaFaces.Count} faces, DirectWrite {directWriteFaces.Count} faces");
                }
            }

            Assert.Empty(mismatches);
        }

        [Win32Fact("Requires DirectWrite")]
        public void Family_Matches_Should_Resolve_To_The_Same_Face()
        {
            using var skia = new SkiaFontProvider();
            using var directWrite = new DirectWriteFontProvider();
            var identities = new FaceIdentities();
            var mismatches = new List<string>();

            foreach (var name in skia.GetFontFamilyNames())
            {
                foreach (var (style, weight, stretch) in s_styles)
                {
                    if (!skia.TryMatchFamily(name, style, weight, stretch, out var skiaFace))
                    {
                        continue;
                    }

                    var request = $"{name} {(int)weight} {style} {stretch}";

                    if (!directWrite.TryMatchFamily(name, style, weight, stretch, out var directWriteFace))
                    {
                        mismatches.Add($"{request}: DirectWrite has no match");
                    }
                    else if (identities.Of(skiaFace) != identities.Of(directWriteFace))
                    {
                        mismatches.Add($"{request}: different faces");
                    }
                }
            }

            Assert.Empty(mismatches);
        }

        /// <summary>
        /// Identifies the physical face behind a descriptor by its loaded font data: file length,
        /// a hash of the first 64 KB (the table directories) and the face index. Only descriptors
        /// with a file path are cached: Skia descriptors carry no path, and two installed copies of
        /// one family (a system font and an app-packaged one) share every designed property.
        /// </summary>
        private sealed class FaceIdentities
        {
            private readonly Dictionary<(string, int), string> _cache = new();

            public string Of(SystemFontFace face)
            {
                string? identity;

                if (face.FilePath is { } path && _cache.TryGetValue((path, face.FaceIndex), out identity))
                {
                    return identity;
                }

                Assert.True(face.TryOpenFontMemory(out var fontMemory), $"{face.FamilyName} does not load");

                using (fontMemory)
                {
                    var sfntFace = Assert.IsType<SfntFace>(fontMemory);

                    Assert.True(sfntFace.TryGetFontFileData(out var data, out var faceIndex));

                    var head = data.Span.Slice(0, Math.Min(data.Length, 65536));

                    identity = $"{data.Length}:{Convert.ToHexString(SHA256.HashData(head))}#{faceIndex}";
                }

                if (face.FilePath is { } filePath)
                {
                    _cache[(filePath, face.FaceIndex)] = identity;
                }

                return identity;
            }
        }
    }
}
