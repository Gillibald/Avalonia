using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Avalonia.Android.Platform.Fonts;

namespace Avalonia.Android.UnitTests
{
    /// <summary>
    /// A private copy of an Android font layout in a temporary directory: <c>system/etc</c> with the
    /// configuration files, <c>system/fonts</c> with the fixture fonts and the collections the
    /// vendor configuration names, and <c>product/fonts</c> for the product customization.
    /// </summary>
    internal sealed class AndroidFontFixture : IDisposable
    {
        public AndroidFontFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "androidfonts-" + Guid.NewGuid().ToString("N"));
            EtcDirectory = Path.Combine(Root, "system", "etc");
            FontDirectory = Path.Combine(Root, "system", "fonts");
            ProductDirectory = Path.Combine(Root, "product", "fonts");

            Directory.CreateDirectory(EtcDirectory);
            Directory.CreateDirectory(FontDirectory);
            Directory.CreateDirectory(ProductDirectory);

            foreach (var font in new[]
                     {
                         "Inter-Regular.ttf", "Inter-Bold.ttf", "NotoSans-Italic.ttf", "NotoMono-Regular.ttf",
                         "NotoSansHebrew-Regular.ttf", "NotoSansTamil-Regular.ttf",
                     })
            {
                File.Copy(Fixtures.GetFontPath(font), Path.Combine(FontDirectory, font));
            }

            var japanese = File.ReadAllBytes(Fixtures.GetFontPath("NotoSansJP-Subset.ttf"));
            var simplified = File.ReadAllBytes(Fixtures.GetFontPath("NotoSansSC-Subset.ttf"));
            var arabic = File.ReadAllBytes(Fixtures.GetFontPath("NotoSansArabic-Regular.ttf"));
            var emoji = File.ReadAllBytes(Fixtures.GetFontPath("TwitterColorEmoji-SVGinOT.ttf"));

            File.WriteAllBytes(Path.Combine(FontDirectory, "CJK.ttc"),
                BuildCollection(japanese, japanese, simplified, simplified));
            File.WriteAllBytes(Path.Combine(FontDirectory, "ArabicTriple.ttc"), BuildCollection(arabic, arabic, arabic));
            File.WriteAllBytes(Path.Combine(FontDirectory, "EmojiPair.ttc"), BuildCollection(emoji, emoji));

            File.Copy(Fixtures.GetFontPath("NotoMono-Regular.ttf"), Path.Combine(ProductDirectory, "ProductSans-Regular.ttf"));

            foreach (var xml in new[]
                     {
                         "vendor-fonts.xml", "vendor-additional-fonts.xml", "product-customization.xml",
                         "aosp-api30-fonts.xml",
                     })
            {
                File.Copy(Fixtures.GetPath(xml), Path.Combine(EtcDirectory, xml));
            }
        }

        public string Root { get; }

        public string EtcDirectory { get; }

        public string FontDirectory { get; }

        public string ProductDirectory { get; }

        public string FontPath(string fileName) => Path.Combine(FontDirectory, fileName);

        /// <summary>
        /// The vendor configuration as the main file, optionally with the vendor addition and the
        /// product customization.
        /// </summary>
        public AndroidFontConfiguration VendorConfiguration(bool withAdditions = false)
        {
            var additions = new List<AndroidFontConfigurationFile>();

            if (withAdditions)
            {
                additions.Add(new(Path.Combine(EtcDirectory, "vendor-additional-fonts.xml"), FontDirectory));
                additions.Add(new(Path.Combine(EtcDirectory, "product-customization.xml"), ProductDirectory));
            }

            return new AndroidFontConfiguration(
                new[]
                {
                    // Unreadable or absent first candidates fall through to the next one.
                    new AndroidFontConfigurationFile(Path.Combine(EtcDirectory, "font_fallback.xml"), FontDirectory),
                    new AndroidFontConfigurationFile(Path.Combine(EtcDirectory, "vendor-fonts.xml"), FontDirectory),
                },
                additions);
        }

        /// <summary>
        /// The AOSP API 30 configuration over empty placeholder files for every font it names, for
        /// tests of the model that do not read font data.
        /// </summary>
        public AndroidFontConfiguration AospConfiguration()
        {
            var directory = Path.Combine(Root, "aosp", "fonts");

            Directory.CreateDirectory(directory);

            var path = Path.Combine(EtcDirectory, "aosp-api30-fonts.xml");

            if (FontsXmlParser.TryParseFile(path, directory, out var config))
            {
                foreach (var family in config.Families)
                {
                    foreach (var font in family.Fonts)
                    {
                        if (!File.Exists(font.Path))
                        {
                            File.WriteAllBytes(font.Path, Array.Empty<byte>());
                        }
                    }
                }
            }

            return new AndroidFontConfiguration(new[] { new AndroidFontConfigurationFile(path, directory) },
                Array.Empty<AndroidFontConfigurationFile>());
        }

        /// <summary>
        /// Builds a TrueType collection with one face per supplied font, each with its own tables
        /// and table directory.
        /// </summary>
        public static byte[] BuildCollection(params byte[][] fonts)
        {
            var headerSize = 12 + 4 * fonts.Length;
            var offsets = new int[fonts.Length];
            var size = Align(headerSize);

            for (var i = 0; i < fonts.Length; i++)
            {
                offsets[i] = size;
                size = Align(size + fonts[i].Length);
            }

            var result = new byte[size];

            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0), 0x74746366); // 'ttcf'
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), 0x00010000);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), (uint)fonts.Length);

            for (var i = 0; i < fonts.Length; i++)
            {
                var start = offsets[i];

                BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12 + 4 * i), (uint)start);

                fonts[i].CopyTo(result.AsSpan(start));

                // Table record offsets are absolute file offsets: rebase them by the face's start.
                var numTables = BinaryPrimitives.ReadUInt16BigEndian(result.AsSpan(start + 4));

                for (var t = 0; t < numTables; t++)
                {
                    var offsetPosition = start + 12 + t * 16 + 8;
                    var offset = BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(offsetPosition));

                    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(offsetPosition), offset + (uint)start);
                }
            }

            return result;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
                // A face a failed assertion left alive still maps a file; the OS temp cleanup gets it.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static int Align(int value) => (value + 3) & ~3;
    }
}
