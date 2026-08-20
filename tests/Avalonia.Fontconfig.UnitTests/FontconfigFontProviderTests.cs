using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Base.UnitTests.Media.Fonts;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Fontconfig.UnitTests
{
    public class LinuxFactAttribute : FactAttribute
    {
        public LinuxFactAttribute(
            [CallerFilePath] string? sourceFilePath = null,
            [CallerLineNumber] int sourceLineNumber = -1)
            : base(sourceFilePath, sourceLineNumber)
        {
            if (!OperatingSystem.IsLinux())
            {
                Skip = "Requires fontconfig on Linux.";
            }
        }
    }

    public class FontconfigFontProviderTests
    {
        [LinuxFact]
        public void Should_Enumerate_Installed_Families()
        {
            using var provider = new FontconfigFontProvider();

            var names = provider.GetFontFamilyNames();

            Assert.NotEmpty(names);
        }

        [LinuxFact]
        public void Should_Provide_Default_Face()
        {
            using var provider = new FontconfigFontProvider();

            Assert.True(provider.TryGetDefaultFontFace(out var face));
            Assert.False(string.IsNullOrEmpty(face.FamilyName));
            Assert.True(File.Exists(face.FilePath));

            // The default face loads through the managed loader end to end.
            Assert.True(face.TryOpenFontMemory(out var fontMemory));

            var glyphTypeface = new GlyphTypeface(fontMemory);

            Assert.False(string.IsNullOrEmpty(glyphTypeface.FamilyName));
            Assert.True(glyphTypeface.GlyphCount > 0);

            glyphTypeface.Dispose();
        }

        [LinuxFact]
        public void Should_Match_Generic_Aliases()
        {
            using var provider = new FontconfigFontProvider();

            Assert.True(provider.TryMatchFamily("sans-serif", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out var sansSerif));
            Assert.True(File.Exists(sansSerif.FilePath));

            Assert.True(provider.TryMatchFamily("monospace", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out var monospace));
            Assert.True(File.Exists(monospace.FilePath));
        }

        [LinuxFact]
        public void Should_Reject_Unknown_Family()
        {
            using var provider = new FontconfigFontProvider();

            Assert.False(provider.TryMatchFamily("Definitely Unknown Family 12345", FontStyle.Normal,
                FontWeight.Normal, FontStretch.Normal, out _));
        }

        [LinuxFact]
        public void Should_Match_Character_With_Coverage()
        {
            using var provider = new FontconfigFontProvider();

            Assert.True(provider.TryMatchCharacter('A', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                null, null, out var match));
            Assert.True(File.Exists(match.FilePath));

            // Plane-16 private-use codepoints are not covered by any installed font; the charset
            // verification must reject the matcher's unconditional fallback.
            Assert.False(provider.TryMatchCharacter(0x10FF00, FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, null, null, out _));
        }

        [LinuxFact]
        public void Should_Get_Family_Faces()
        {
            using var provider = new FontconfigFontProvider();

            Assert.True(provider.TryGetDefaultFontFace(out var defaultFace));
            Assert.True(provider.TryGetFamilyFaces(defaultFace.FamilyName, out var faces));
            Assert.NotEmpty(faces);

            foreach (var face in faces)
            {
                Assert.True(File.Exists(face.FilePath));
            }
        }

        [LinuxFact]
        public void Should_Match_Bold_Face_When_Available()
        {
            using var provider = new FontconfigFontProvider();

            Assert.SkipUnless(provider.TryMatchFamily("DejaVu Sans", FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, out _), "DejaVu Sans is not installed.");

            Assert.True(provider.TryMatchFamily("DejaVu Sans", FontStyle.Normal, FontWeight.Bold,
                FontStretch.Normal, out var bold));

            // Designed weight of the matched face, not a simulation.
            Assert.Equal(FontWeight.Bold, bold.Weight);
        }

        [LinuxFact]
        public void Variable_Family_Faces_Should_Be_Distinct_Instances()
        {
            using var provider = new FontconfigFontProvider();

            IReadOnlyList<SystemFontFace>? faces = null;

            // Distributions ship some of these families as static files, so take the first one
            // whose faces come from a variable font.
            foreach (var familyName in new[] { "Cantarell", "Ubuntu Sans", "Ubuntu" })
            {
                if (provider.TryGetFamilyFaces(familyName, out var candidates) &&
                    candidates.Any(face => face.AxisValues is not null))
                {
                    faces = candidates;
                    break;
                }
            }

            Assert.SkipWhen(faces is null, "No variable font family with named instances is installed.");

            // Fontconfig lists the variable font itself next to its named instances; it has no
            // single style of its own, so it must not appear as another face of the family.
            var keys = new HashSet<(FontStyle, FontWeight, FontStretch)>();

            foreach (var face in faces!)
            {
                Assert.True(keys.Add((face.Style, face.Weight, face.Stretch)),
                    $"{face.FamilyName} {face.Style} {face.Weight} {face.Stretch} is listed twice.");
                Assert.NotNull(face.AxisValues);
            }
        }
    }

    public class FcMappingTests
    {
        [Theory]
        [InlineData(100, 0)]
        [InlineData(400, 80)]
        [InlineData(450, 90)]
        [InlineData(600, 180)]
        [InlineData(650, 190)]
        [InlineData(700, 200)]
        [InlineData(1000, 215)]
        public void Should_Map_OpenType_Weight_To_Fontconfig(int openType, int fontconfig)
        {
            Assert.Equal(fontconfig, FcMapping.WeightFromOpenType(openType));
        }

        [Theory]
        [InlineData(0, 100)]
        [InlineData(80, 400)]
        [InlineData(90, 450)]
        [InlineData(180, 600)]
        [InlineData(200, 700)]
        [InlineData(215, 1000)]
        public void Should_Map_Fontconfig_Weight_To_OpenType(int fontconfig, int openType)
        {
            Assert.Equal(openType, FcMapping.WeightToOpenType(fontconfig));
        }

        [Theory]
        [InlineData(FontStyle.Normal, 0)]
        [InlineData(FontStyle.Italic, 100)]
        [InlineData(FontStyle.Oblique, 110)]
        public void Should_Map_Style_Round_Trip(FontStyle style, int slant)
        {
            Assert.Equal(slant, FcMapping.SlantFromFontStyle(style));
            Assert.Equal(style, FcMapping.SlantToFontStyle(slant));
        }

        [Theory]
        [InlineData(FontStretch.UltraCondensed, 50)]
        [InlineData(FontStretch.Condensed, 75)]
        [InlineData(FontStretch.Normal, 100)]
        [InlineData(FontStretch.Expanded, 125)]
        [InlineData(FontStretch.UltraExpanded, 200)]
        public void Should_Map_Stretch_Round_Trip(FontStretch stretch, int width)
        {
            Assert.Equal(width, FcMapping.WidthFromFontStretch(stretch));
            Assert.Equal(stretch, FcMapping.WidthToFontStretch(width));
        }

        [Fact]
        public void Should_Map_Width_To_Nearest_Stretch()
        {
            Assert.Equal(FontStretch.Expanded, FcMapping.WidthToFontStretch(122));
            Assert.Equal(FontStretch.SemiExpanded, FcMapping.WidthToFontStretch(110));
        }
    }

    public class FontconfigNamedInstanceTests
    {
        private static readonly OpenTypeTag s_wght = OpenTypeTag.Parse("wght");
        private static readonly OpenTypeTag s_opsz = OpenTypeTag.Parse("opsz");

        [Fact]
        public void Should_Read_Named_Instance_Coordinates_From_Fvar()
        {
            // Two axes and two instances, the second with the optional PostScript name ID.
            var fvar = BuildFvar(instanceSize: 14, (400f, 14f), (700f, 10.5f));

            var instances = FontconfigFontProvider.ReadNamedInstances(fvar);

            Assert.NotNull(instances);
            Assert.Equal(2, instances.Length);
            Assert.Equal(400f, instances[0][s_wght]);
            Assert.Equal(14f, instances[0][s_opsz]);
            Assert.Equal(700f, instances[1][s_wght]);
            Assert.Equal(10.5f, instances[1][s_opsz]);
        }

        [Fact]
        public void Should_Reject_Truncated_Fvar()
        {
            var fvar = BuildFvar(instanceSize: 12, (400f, 14f));

            Assert.Null(FontconfigFontProvider.ReadNamedInstances(fvar.AsSpan(0, fvar.Length - 1)));
        }

        [Fact]
        public void Should_Read_Default_Coordinates_From_Fvar()
        {
            var fvar = BuildFvar(instanceSize: 12, (700f, 10.5f));

            var coordinates = FontconfigFontProvider.ReadDefaultCoordinates(fvar);

            Assert.NotNull(coordinates);
            Assert.Equal(DefaultWght, coordinates[s_wght]);
            Assert.Equal(DefaultOpsz, coordinates[s_opsz]);

            Assert.Null(FontconfigFontProvider.ReadDefaultCoordinates(fvar.AsSpan(0, 16 + 20)));
        }

        [Theory]
        [InlineData("wght", 700f, FontStyle.Normal, 700, FontStretch.Normal)]
        [InlineData("wght", 350f, FontStyle.Normal, 350, FontStretch.Normal)]
        [InlineData("wdth", 75f, FontStyle.Normal, 400, FontStretch.Condensed)]
        [InlineData("wdth", 112.5f, FontStyle.Normal, 400, FontStretch.SemiExpanded)]
        [InlineData("ital", 1f, FontStyle.Italic, 400, FontStretch.Normal)]
        [InlineData("slnt", -10f, FontStyle.Oblique, 400, FontStretch.Normal)]
        [InlineData("slnt", 0f, FontStyle.Normal, 400, FontStretch.Normal)]
        [InlineData("opsz", 12f, FontStyle.Normal, 400, FontStretch.Normal)]
        public void Default_Instance_Style_Should_Project_The_Style_Axes(string axis, float value,
            FontStyle style, int weight, FontStretch stretch)
        {
            var coordinates = new Dictionary<OpenTypeTag, float> { [OpenTypeTag.Parse(axis)] = value };

            Assert.Equal((style, (FontWeight)weight, stretch),
                FontconfigFontProvider.GetDefaultInstanceStyle(coordinates));
        }

        private const int DefaultWght = 400;
        private const int DefaultOpsz = 14;

        private static byte[] BuildFvar(int instanceSize, params (float Wght, float Opsz)[] instances)
        {
            const int AxesOffset = 16;
            const int AxisSize = 20;

            var data = new byte[AxesOffset + 2 * AxisSize + instances.Length * instanceSize];
            var span = data.AsSpan();

            BinaryPrimitives.WriteUInt16BigEndian(span, 1);
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(4), AxesOffset);
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(6), 2);
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(8), 2);
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(10), AxisSize);
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(12), (ushort)instances.Length);
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(14), (ushort)instanceSize);

            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(AxesOffset), (uint)s_wght);
            BinaryPrimitives.WriteInt32BigEndian(span.Slice(AxesOffset + 8), DefaultWght * 65536);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(AxesOffset + AxisSize), (uint)s_opsz);
            BinaryPrimitives.WriteInt32BigEndian(span.Slice(AxesOffset + AxisSize + 8), DefaultOpsz * 65536);

            for (var i = 0; i < instances.Length; i++)
            {
                var record = span.Slice(AxesOffset + 2 * AxisSize + i * instanceSize + 4);

                BinaryPrimitives.WriteInt32BigEndian(record, (int)(instances[i].Wght * 65536));
                BinaryPrimitives.WriteInt32BigEndian(record.Slice(4), (int)(instances[i].Opsz * 65536));
            }

            return data;
        }
    }

    public class FontconfigProviderContractTests : SystemFontProviderContractTests
    {
        protected override bool IsSupported => OperatingSystem.IsLinux();

        protected override ISystemFontProvider CreateProvider() => new FontconfigFontProvider();

        protected override string KnownFamilyName
        {
            get
            {
                using var provider = new FontconfigFontProvider();

                return provider.TryGetDefaultFontFace(out var face) ? face.FamilyName : "sans-serif";
            }
        }
    }
}
