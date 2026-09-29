using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts
{
    /// <summary>
    /// Resolution of system font descriptors that name instances of one variable font file, the
    /// way DirectWrite and fontconfig list every named instance as a face of its own.
    /// </summary>
    public sealed class SystemFontCollectionInstanceTests : IDisposable
    {
        private const string InterVariableUri = "resm:Avalonia.Base.UnitTests.Assets.InterVariable.ttf?assembly=Avalonia.Base.UnitTests";
        private const string InterRegularUri = "resm:Avalonia.Base.UnitTests.Assets.Inter-Regular.ttf?assembly=Avalonia.Base.UnitTests";
        private const string InterBoldUri = "resm:Avalonia.Base.UnitTests.Assets.Inter-Bold.ttf?assembly=Avalonia.Base.UnitTests";

        private static readonly OpenTypeTag s_wght = OpenTypeTag.Parse("wght");
        private static readonly OpenTypeTag s_opsz = OpenTypeTag.Parse("opsz");

        private readonly List<string> _files = new();

        [Fact]
        public void Instances_Of_One_File_Share_One_Root_Loaded_Once()
        {
            var provider = CreateInstanceProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            var results = new[] { FontWeight.Light, FontWeight.Normal, FontWeight.SemiBold, FontWeight.Bold, FontWeight.Black }
                .Select(weight => Get(collection, "Test Variable", FontStyle.Normal, weight))
                .ToArray();

            var root = results[1].WithVariation(default);

            foreach (var result in results)
            {
                Assert.Same(root, result.WithVariation(default));
                Assert.Equal(FontSimulations.None, result.FontSimulations);
            }

            Assert.Equal(1, provider.OpenCount);
        }

        [Fact]
        public void Instances_Resolve_To_Their_Named_Instance_Positions()
        {
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, CreateInstanceProvider());

            // With and without the axis values from the provider, each descriptor lands on the
            // fvar named instance with its weight.
            foreach (var weight in new[] { FontWeight.Light, FontWeight.Normal, FontWeight.SemiBold, FontWeight.Bold, FontWeight.Black })
            {
                var result = Get(collection, "Test Variable", FontStyle.Normal, weight);
                var root = result.WithVariation(default);
                var instance = root.NamedInstances.Single(i => i.Coordinates[s_wght] == (int)weight);

                Assert.Equal(weight, result.Weight);
                Assert.Equal(root.CreateNormalizedPosition(null, instance.Index), result.VariationPosition);
            }
        }

        [Fact]
        public void Bold_Resolves_To_The_Real_Instance()
        {
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, CreateInstanceProvider());

            var bold = Get(collection, "Test Variable", FontStyle.Normal, FontWeight.Bold);

            Assert.Equal(FontWeight.Bold, bold.Weight);
            Assert.Equal(FontSimulations.None, bold.FontSimulations);
            Assert.True(bold.TryGetUserAxisValue(bold.VariationPosition, s_wght, out var wght));
            Assert.Equal(700, wght, 0.5);
        }

        [Fact]
        public void First_Simulated_Request_Loads_The_File_Once()
        {
            var provider = CreateInstanceProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            var boldItalic = Get(collection, "Test Variable", FontStyle.Italic, FontWeight.Bold);

            Assert.Equal(FontSimulations.Oblique, boldItalic.FontSimulations);
            Assert.Equal(1, provider.OpenCount);
        }

        [Fact]
        public void Real_Face_From_The_Provider_Beats_Simulating_A_Cached_Face()
        {
            var regular = CreateFile(InterRegularUri);
            var bold = CreateFile(InterBoldUri);

            var provider = new InstanceProvider("Test Static", (style, weight) => (int)weight < 550
                ? new SystemFontFace("Test Static", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, regular, 0)
                : new SystemFontFace("Test Static", FontStyle.Normal, FontWeight.Bold, FontStretch.Normal, bold, 0));

            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            Get(collection, "Test Static", FontStyle.Normal, FontWeight.Normal);

            var semiBold = Get(collection, "Test Static", FontStyle.Normal, FontWeight.SemiBold);

            Assert.Equal(FontSimulations.None, semiBold.FontSimulations);
            Assert.Equal(FontWeight.Bold, semiBold.Weight);
        }

        [Fact]
        public void Family_Name_Selects_The_Optical_Size()
        {
            var file = CreateFile(InterVariableUri);

            // "Inter Variable Display" is the STAT-composed family of the opsz=32 instances; the
            // font has no named instance there, so the weights are axis values.
            var provider = new InstanceProvider("Inter Variable Display", (style, weight) =>
                new SystemFontFace("Inter Variable Display", FontStyle.Normal, weight, FontStretch.Normal, file, 0));

            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            foreach (var weight in new[] { FontWeight.Normal, FontWeight.Bold })
            {
                var result = Get(collection, "Inter Variable Display", FontStyle.Normal, weight);

                Assert.Equal(weight, result.Weight);
                Assert.Equal(FontSimulations.None, result.FontSimulations);
                Assert.True(result.TryGetUserAxisValue(result.VariationPosition, s_opsz, out var opsz));
                Assert.Equal(32, opsz, 0.01);
                Assert.True(result.TryGetInstanceNames(result.VariationPosition, out var names));
                Assert.Equal("Inter Variable Display", names.FamilyName);
            }
        }

        [Fact]
        public void Instance_Family_Of_A_Loaded_Face_Resolves_Without_The_Provider()
        {
            var file = CreateFile(InterVariableUri);

            // The provider lists the typographic family only, like a platform without a WWS model.
            var provider = new InstanceProvider("Inter Variable", (style, weight) =>
                new SystemFontFace("Inter Variable", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, file, 0));

            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            var regular = Get(collection, "Inter Variable", FontStyle.Normal, FontWeight.Normal);
            var matchCount = provider.MatchCount;

            var semiBold = Get(collection, "Inter Variable Display", FontStyle.Normal, FontWeight.SemiBold);

            Assert.Equal(matchCount, provider.MatchCount);
            Assert.Same(regular.WithVariation(default), semiBold.WithVariation(default));
            Assert.Equal(FontWeight.SemiBold, semiBold.Weight);
            Assert.Equal(FontSimulations.None, semiBold.FontSimulations);
            Assert.True(semiBold.TryGetUserAxisValue(semiBold.VariationPosition, s_opsz, out var opsz));
            Assert.True(semiBold.TryGetUserAxisValue(semiBold.VariationPosition, s_wght, out var wght));
            Assert.Equal(32, opsz, 0.01);
            Assert.Equal(600, wght, 0.5);
        }

        [Fact]
        public void Provider_Family_And_Instance_Family_Share_One_Bucket_And_Root()
        {
            var provider = CreateInstanceFamilyProvider();
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, provider);

            // DirectWrite reports the instance family itself; the STAT table derives the same name.
            var display = Get(collection, "Inter Variable Display", FontStyle.Normal, FontWeight.Normal);
            var text = Get(collection, "Inter Variable Text", FontStyle.Normal, FontWeight.Normal);

            Assert.True(collection.TryGetInstanceFamilyFaces("Inter Variable Display", out var displayFaces));
            Assert.Same(display, Assert.Single(displayFaces));

            Assert.Same(display.WithVariation(default), text.WithVariation(default));
            Assert.Equal(1, provider.OpenCount);

            Assert.True(display.TryGetUserAxisValue(display.VariationPosition, s_opsz, out var displayOpsz));
            Assert.True(text.TryGetUserAxisValue(text.VariationPosition, s_opsz, out var textOpsz));
            Assert.Equal(32, displayOpsz, 0.01);
            Assert.NotEqual(displayOpsz, textOpsz);
        }

        [Fact]
        public void Instances_Of_Other_Optical_Sizes_Stay_Out_Of_The_Root_Family()
        {
            var collection = new SystemFontCollection(FontManager.SystemFontsKey, CreateInstanceFamilyProvider());

            var display = Get(collection, "Inter Variable Display", FontStyle.Normal, FontWeight.Normal);

            // The root family answers at the root's own optical size, not with the Display
            // instance that was resolved first.
            var root = Get(collection, "Inter Variable", FontStyle.Normal, FontWeight.Normal);

            Assert.Same(display.WithVariation(default), root);
        }

        [Fact]
        public void Glyph_Typeface_Losing_A_Registration_Race_Is_Disposed()
        {
            var fontData = File.ReadAllBytes(CreateFile(InterRegularUri));
            var memories = new List<TrackingFontMemory>();

            var provider = new InstanceProvider("Test Sans", (style, weight) =>
                new StreamFontFace("Test Sans", fontData, memories));

            var collection = new PlatformMatchCollection(provider);

            // Two platform matches of the same face race to register it; the second loses.
            Assert.True(collection.MatchFromPlatform('A', out var first));
            Assert.True(collection.MatchFromPlatform('A', out var second));

            Assert.Same(first, second);
            Assert.Equal(2, memories.Count);
            Assert.False(memories[0].IsDisposed);
            Assert.True(memories[1].IsDisposed);
        }

        public void Dispose()
        {
            foreach (var file in _files)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Still mapped by a collection the test did not dispose.
                }
            }
        }

        private static GlyphTypeface Get(SystemFontCollection collection, string familyName, FontStyle style,
            FontWeight weight)
        {
            Assert.True(collection.TryGetGlyphTypeface(familyName, style, weight, FontStretch.Normal,
                out var glyphTypeface));

            return glyphTypeface;
        }

        /// <summary>
        /// A provider shaped like DirectWrite's view of Bahnschrift: one font file, a face per
        /// named instance, some reporting their axis values and some not.
        /// </summary>
        private InstanceProvider CreateInstanceProvider()
        {
            var file = CreateFile(InterVariableUri);
            var faces = new List<SystemFontFace>();
            var provider = new InstanceProvider("Test Variable", (style, weight) =>
                faces.OrderBy(f => Math.Abs((int)f.Weight - (int)weight)).First());

            faces.Add(Instance(FontWeight.Light, 300));
            faces.Add(Instance(FontWeight.Normal, null));
            faces.Add(Instance(FontWeight.SemiBold, null));
            faces.Add(Instance(FontWeight.Bold, 700));
            faces.Add(Instance(FontWeight.Black, null));

            return provider;

            SystemFontFace Instance(FontWeight weight, float? wght)
            {
                var axisValues = wght is { } value
                    ? new Dictionary<OpenTypeTag, float> { [s_wght] = value, [s_opsz] = 14 }
                    : null;

                return new CountingFontFace("Test Variable", weight, file, axisValues, () => provider.OpenCount++);
            }
        }

        private string CreateFile(string uri)
        {
            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ttf");

            using (var stream = SfntFaceTestHelper.OpenAsset(uri))
            using (var file = File.Create(path))
            {
                stream.CopyTo(file);
            }

            _files.Add(path);

            return path;
        }

        /// <summary>
        /// A provider shaped like DirectWrite's view of Inter Variable: one font file listed under
        /// the families of its optical sizes, the names the STAT table composes.
        /// </summary>
        private InstanceProvider CreateInstanceFamilyProvider()
        {
            var file = CreateFile(InterVariableUri);
            InstanceProvider? provider = null;

            provider = new InstanceProvider(new[] { "Inter Variable Text", "Inter Variable Display" },
                (familyName, style, weight) => new CountingFontFace(familyName, weight, file, null,
                    () => provider!.OpenCount++));

            return provider;
        }

        private sealed class InstanceProvider : ISystemFontProvider
        {
            private readonly IReadOnlyList<string> _familyNames;
            private readonly Func<string, FontStyle, FontWeight, SystemFontFace> _match;

            public InstanceProvider(string familyName, Func<FontStyle, FontWeight, SystemFontFace> match)
                : this(new[] { familyName }, (_, style, weight) => match(style, weight))
            {
            }

            public InstanceProvider(IReadOnlyList<string> familyNames,
                Func<string, FontStyle, FontWeight, SystemFontFace> match)
            {
                _familyNames = familyNames;
                _match = match;
            }

            public int OpenCount { get; set; }

            public int MatchCount { get; private set; }

            public bool TryGetDefaultFontFace([NotNullWhen(true)] out SystemFontFace? face)
            {
                face = null;

                return false;
            }

            public IReadOnlyList<string> GetFontFamilyNames() => _familyNames;

            public bool TryMatchFamily(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
                [NotNullWhen(true)] out SystemFontFace? match)
            {
                MatchCount++;

                var known = _familyNames.FirstOrDefault(name =>
                    string.Equals(name, familyName, StringComparison.OrdinalIgnoreCase));

                match = known is not null ? _match(known, style, weight) : null;

                return match != null;
            }

            public bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch,
                string? familyName, CultureInfo? culture, [NotNullWhen(true)] out SystemFontFace? match)
            {
                match = _match(_familyNames[0], style, weight);

                return true;
            }

            public bool TryGetFamilyFaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<SystemFontFace>? faces)
            {
                faces = null;

                return false;
            }

            public void Dispose()
            {
            }
        }

        private sealed class CountingFontFace : SystemFontFace
        {
            private readonly Action _onOpen;

            public CountingFontFace(string familyName, FontWeight weight, string filePath,
                IReadOnlyDictionary<OpenTypeTag, float>? axisValues, Action onOpen)
                : base(familyName, FontStyle.Normal, weight, FontStretch.Normal, filePath, 0, null, axisValues)
            {
                _onOpen = onOpen;
            }

            public override bool TryOpenFontMemory([NotNullWhen(true)] out IFontMemory? fontMemory)
            {
                _onOpen();

                return base.TryOpenFontMemory(out fontMemory);
            }
        }

        private sealed class StreamFontFace : SystemFontFace
        {
            private readonly byte[] _fontData;
            private readonly List<TrackingFontMemory> _memories;

            public StreamFontFace(string familyName, byte[] fontData, List<TrackingFontMemory> memories)
                : base(familyName, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal)
            {
                _fontData = fontData;
                _memories = memories;
            }

            public override bool TryOpenFontMemory([NotNullWhen(true)] out IFontMemory? fontMemory)
            {
                fontMemory = null;

                if (!SfntFace.TryLoad(new MemoryStream(_fontData), out var face))
                {
                    return false;
                }

                var memory = new TrackingFontMemory(face);

                _memories.Add(memory);
                fontMemory = memory;

                return true;
            }
        }

        private sealed class TrackingFontMemory : IFontMemory
        {
            private readonly IFontMemory _inner;

            public TrackingFontMemory(IFontMemory inner) => _inner = inner;

            public bool IsDisposed { get; private set; }

            public bool TryGetTable(OpenTypeTag tag, out ReadOnlyMemory<byte> table) => _inner.TryGetTable(tag, out table);

            public void Dispose()
            {
                IsDisposed = true;
                _inner.Dispose();
            }
        }

        private sealed class PlatformMatchCollection : SystemFontCollection
        {
            public PlatformMatchCollection(ISystemFontProvider provider)
                : base(FontManager.SystemFontsKey, provider)
            {
            }

            public bool MatchFromPlatform(int codepoint, [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
                => TryMatchCharacterFromPlatform(codepoint, default, null, null, out glyphTypeface);
        }
    }
}
