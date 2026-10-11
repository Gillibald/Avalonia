using System;
using System.Collections.Generic;
using System.Globalization;
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

        private static readonly int[] s_codepoints =
        {
            0x61, 0xE9, 0x3B1, 0x416, 0x531, 0x10D0, 0x5D0, 0x628, 0x710, 0x780, 0x915, 0x995, 0xB95, 0xE01,
            0xE81, 0x1780, 0x1000, 0x1200, 0xF40, 0x1820, 0xA000, 0x4E2D, 0x9AA8, 0xAC00, 0x3042, 0x30A2,
            0x1F600, 0x2764, 0x1F1E9, 0x2603, 0x1D400, 0xE000, 0x13A0, 0x1401, 0x1681, 0x16A0, 0x2801,
            0x20000, 0x10330,
        };

        private static readonly string?[] s_cultures = { null, "ja-JP", "zh-CN", "zh-TW", "ko-KR" };

        private static readonly string?[] s_fallbackFamilies = { null, "Segoe UI", "Arial", "Times New Roman", "Consolas" };

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

        [Win32Fact("Requires DirectWrite")]
        public void Character_Matches_Should_Resolve_To_The_Same_Face()
        {
            using var skia = new SkiaFontProvider();
            using var directWrite = new DirectWriteFontProvider();
            var identities = new FaceIdentities();
            var mismatches = new List<string>();

            foreach (var codepoint in s_codepoints)
            {
                foreach (var cultureName in s_cultures)
                {
                    var culture = cultureName is null ? null : CultureInfo.GetCultureInfo(cultureName);

                    foreach (var familyName in s_fallbackFamilies)
                    {
                        foreach (var weight in new[] { FontWeight.Normal, FontWeight.Bold })
                        {
                            var skiaMatched = skia.TryMatchCharacter(codepoint, FontStyle.Normal, weight,
                                FontStretch.Normal, familyName, culture, out var skiaFace);
                            var directWriteMatched = directWrite.TryMatchCharacter(codepoint, FontStyle.Normal, weight,
                                FontStretch.Normal, familyName, culture, out var directWriteFace);

                            var skiaIdentity = skiaMatched ? identities.Of(skiaFace!) : "none";
                            var directWriteIdentity = directWriteMatched ? identities.Of(directWriteFace!) : "none";

                            if (skiaIdentity != directWriteIdentity)
                            {
                                mismatches.Add($"U+{codepoint:X4} {cultureName ?? "-"} {familyName ?? "-"} {(int)weight}: " +
                                               $"Skia {skiaFace?.FamilyName ?? "none"}, DirectWrite {directWriteFace?.FamilyName ?? "none"}");
                            }
                        }
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
