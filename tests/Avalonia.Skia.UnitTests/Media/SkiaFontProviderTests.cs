using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Base.UnitTests.Media.Fonts;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    public class SkiaFontProviderTests
    {
        [Fact]
        public void Should_Enumerate_Installed_Families()
        {
            using var provider = new SkiaFontProvider();

            Assert.NotEmpty(provider.GetFontFamilyNames());
        }

        [Fact]
        public void Should_Provide_Default_Face()
        {
            using var provider = new SkiaFontProvider();

            Assert.True(provider.TryGetDefaultFontFace(out var face));
            Assert.False(string.IsNullOrEmpty(face.FamilyName));

            // The default face loads through the managed loader end to end.
            Assert.True(face.TryOpenFontMemory(out var fontMemory));

            var glyphTypeface = new GlyphTypeface(fontMemory);

            Assert.True(glyphTypeface.GlyphCount > 0);

            glyphTypeface.Dispose();
        }

        [Fact]
        public void Should_Reject_Unknown_Family()
        {
            using var provider = new SkiaFontProvider();

            Assert.False(provider.TryMatchFamily("Definitely Unknown Family 12345", FontStyle.Normal,
                FontWeight.Normal, FontStretch.Normal, out _));
        }

        [Win32Fact("Requires Windows fonts")]
        public void Should_Resolve_Ttc_Face_Through_Managed_Loader()
        {
            using var provider = new SkiaFontProvider();

            // Yu Gothic UI lives inside a TrueType collection; OpenStream reports the collection
            // face index, and the managed loader must resolve exactly that face.
            Assert.True(provider.TryMatchFamily("Yu Gothic UI", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out var match));

            Assert.True(match.TryOpenFontMemory(out var fontMemory));

            var glyphTypeface = new GlyphTypeface(fontMemory);

            Assert.Equal("Yu Gothic UI", glyphTypeface.FamilyName);

            glyphTypeface.Dispose();
        }

        [Win32Fact("Requires Windows fonts")]
        public void Should_Report_Designed_Properties_Of_Simulated_Matches()
        {
            using var provider = new SkiaFontProvider();

            // Gabriola has a regular face only; DirectWrite answers a bold request with a
            // simulated bold variant of it, and Impact answers italic with a simulated oblique.
            Assert.True(provider.TryMatchFamily("Gabriola", FontStyle.Normal, FontWeight.Bold,
                FontStretch.Normal, out var gabriola));
            Assert.True(provider.TryMatchFamily("Impact", FontStyle.Italic, FontWeight.Normal,
                FontStretch.Normal, out var impact));

            Assert.Equal(FontWeight.Normal, gabriola.Weight);
            Assert.Equal(FontStyle.Normal, impact.Style);
        }

        [Win32Fact("Requires Windows fonts")]
        public void Simulated_Matches_Should_Get_The_Collection_Simulation()
        {
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, new SkiaFontProvider());

            try
            {
                Assert.True(collection.TryGetGlyphTypeface("Gabriola", FontStyle.Normal, FontWeight.Bold,
                    FontStretch.Normal, out var gabriola));
                Assert.True(collection.TryGetGlyphTypeface("Impact", FontStyle.Italic, FontWeight.Normal,
                    FontStretch.Normal, out var impact));

                Assert.Equal(FontSimulations.Bold, gabriola.FontSimulations);
                Assert.Equal(FontSimulations.Oblique, impact.FontSimulations);
            }
            finally
            {
                ((IDisposable)collection).Dispose();
            }
        }

        [Win32Fact("Requires Windows fonts")]
        public void Should_Match_Designed_Bold_Face()
        {
            using var provider = new SkiaFontProvider();

            Assert.True(provider.TryMatchFamily("Arial", FontStyle.Normal, FontWeight.Bold,
                FontStretch.Normal, out var bold));

            Assert.Equal(FontWeight.Bold, bold.Weight);
        }

        [Fact]
        public void Should_Match_Character_With_Coverage()
        {
            using var provider = new SkiaFontProvider();

            Assert.True(provider.TryMatchCharacter('A', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                null, null, out var match));

            Assert.True(match.TryOpenFontMemory(out var fontMemory));

            var glyphTypeface = new GlyphTypeface(fontMemory);

            Assert.True(glyphTypeface.CharacterToGlyphMap.TryGetGlyph('A', out _));

            glyphTypeface.Dispose();
        }

        [Fact]
        public void Descriptors_Of_One_Face_Should_Share_A_Stable_Identity()
        {
            using var provider = new SkiaFontProvider();

            Assert.True(provider.TryGetDefaultFontFace(out var first));
            Assert.True(provider.TryMatchFamily(first.FamilyName, first.Style, first.Weight, first.Stretch,
                out var second));

            Assert.NotNull(first.Identity);
            Assert.Equal(first.Identity, second.Identity);
        }

        [Win32Fact("Requires Windows fonts")]
        public void Faces_Of_One_Collection_File_Should_Have_Distinct_Identities()
        {
            using var provider = new SkiaFontProvider();

            // Yu Gothic UI and Yu Gothic are faces of one TrueType collection file.
            Assert.True(provider.TryMatchFamily("Yu Gothic UI", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out var ui));
            Assert.True(provider.TryMatchFamily("Yu Gothic", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out var text));

            Assert.NotNull(ui.Identity);
            Assert.NotNull(text.Identity);
            Assert.NotEqual(ui.Identity, text.Identity);
        }

        [Fact]
        public void Should_Get_Family_Faces()
        {
            using var provider = new SkiaFontProvider();

            Assert.True(provider.TryGetDefaultFontFace(out var defaultFace));
            Assert.True(provider.TryGetFamilyFaces(defaultFace.FamilyName, out var faces));
            Assert.NotEmpty(faces);
        }

        [Fact]
        public void Family_Faces_Should_List_Each_Position_Of_A_File_Face_Once()
        {
            using var provider = new SkiaFontProvider();

            var duplicates = new List<string>();

            foreach (var family in provider.GetFontFamilyNames())
            {
                if (!provider.TryGetFamilyFaces(family, out var faces))
                {
                    continue;
                }

                // Held open together, faces of one file share one data owner. A named instance of
                // a variable font reports its own position's properties, so it is a face of its own.
                var opened = new List<IFontMemory>();
                var seen = new HashSet<(MemoryManager<byte>, int, FontWeight, FontStyle, FontStretch)>();

                try
                {
                    foreach (var face in faces)
                    {
                        if (!face.TryOpenFontMemory(out var fontMemory))
                        {
                            continue;
                        }

                        opened.Add(fontMemory);

                        var sfntFace = Assert.IsType<SfntFace>(fontMemory);

                        Assert.True(sfntFace.TryGetFontFileData(out var data, out _));
                        Assert.True(MemoryMarshal.TryGetMemoryManager(data, out MemoryManager<byte>? owner));

                        if (!seen.Add((owner!, sfntFace.FaceIndex, face.Weight, face.Style, face.Stretch)))
                        {
                            duplicates.Add($"{family}: face {sfntFace.FaceIndex} listed again as {(int)face.Weight} {face.Style} {face.Stretch}");
                        }
                    }
                }
                finally
                {
                    opened.ForEach(x => x.Dispose());
                }
            }

            Assert.Empty(duplicates);
        }

        [Fact]
        public void Every_Family_Face_With_OpenType_Data_Should_Open()
        {
            using var provider = new SkiaFontProvider();

            var failures = new List<string>();

            foreach (var family in provider.GetFontFamilyNames())
            {
                // The distinct file faces of Skia's own typefaces of the family whose data the
                // managed loader can read; fonts without OpenType tables (Type 1 fonts listed by
                // fontconfig) do not count, and simulated variants of one face count once.
                var fileFaces = new HashSet<(long, string, int)>();

                using (var styleSet = SKFontManager.Default.GetFontStyles(family))
                {
                    for (var i = 0; i < styleSet.Count; i++)
                    {
                        using var typeface = styleSet.CreateTypeface(i);

                        var ttcIndex = 0;
                        using var stream = typeface?.OpenStream(out ttcIndex);

                        if (stream is not null && IsOpenTypeData(stream, out var head))
                        {
                            fileFaces.Add((stream.Length, head, ttcIndex));
                        }
                    }
                }

                var expected = fileFaces.Count;

                var opened = 0;

                if (provider.TryGetFamilyFaces(family, out var faces))
                {
                    foreach (var face in faces)
                    {
                        if (face.TryOpenFontMemory(out var fontMemory))
                        {
                            opened++;
                            fontMemory.Dispose();
                        }
                    }
                }

                if (opened < expected)
                {
                    failures.Add($"{family}: {opened} of {expected} faces opened");
                }
            }

            Assert.Empty(failures);

            static bool IsOpenTypeData(SKStreamAsset stream, out string head)
            {
                var header = new byte[(int)Math.Min(stream.Length, 4096)];

                head = string.Empty;

                if (header.Length < 4 || stream.Read(header, header.Length) != header.Length)
                {
                    return false;
                }

                head = Convert.ToHexString(header);

                var tag = (uint)(header[0] << 24 | header[1] << 16 | header[2] << 8 | header[3]);

                // TrueType, CFF (OTTO), Apple TrueType (true) and collections (ttcf).
                return tag is 0x00010000 or 0x4F54544F or 0x74727565 or 0x74746366;
            }
        }
    }

    public class SkiaFontProviderContractTests : SystemFontProviderContractTests
    {
        protected override ISystemFontProvider CreateProvider() => new SkiaFontProvider();

        protected override string KnownFamilyName
        {
            get
            {
                using var provider = new SkiaFontProvider();

                return provider.TryGetDefaultFontFace(out var face) ? face.FamilyName : "Arial";
            }
        }
    }
}
