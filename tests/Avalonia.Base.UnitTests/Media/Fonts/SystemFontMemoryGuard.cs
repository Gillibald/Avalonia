using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Fonts
{
    /// <summary>
    /// Memory guard for path-based system font providers. Every face of every listed family is
    /// opened from its descriptor and resolved through a system font collection, and all of them
    /// are held at once: the font data behind them must be one memory-mapped data per font file,
    /// sized like the file, so native font data stays bounded by the distinct files in use.
    /// </summary>
    internal static class SystemFontMemoryGuard
    {
        public static void AssertOneMappedDataPerFile(Func<ISystemFontProvider> createProvider)
        {
            var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

            var provider = createProvider();
            var collection = new SystemFontCollection(new Uri("fonts:MemoryGuard"), createProvider());
            var opened = new List<IFontMemory>();

            try
            {
                var ownersByFile = new Dictionary<string, HashSet<MemoryManager<byte>>>(comparer);
                var collectionOwners = new HashSet<MemoryManager<byte>>();

                foreach (var family in provider.GetFontFamilyNames())
                {
                    if (!provider.TryGetFamilyFaces(family, out var faces))
                    {
                        continue;
                    }

                    foreach (var face in faces)
                    {
                        if (face.FilePath is null || !face.TryOpenFontMemory(out var fontMemory))
                        {
                            continue;
                        }

                        opened.Add(fontMemory);

                        var file = Canonicalize(face.FilePath);

                        if (!ownersByFile.TryGetValue(file, out var owners))
                        {
                            ownersByFile[file] = owners = new HashSet<MemoryManager<byte>>();
                        }

                        owners.Add(GetOwner(fontMemory, file));

                        if (collection.TryGetGlyphTypeface(family, face.Style, face.Weight, face.Stretch,
                                out var glyphTypeface))
                        {
                            collectionOwners.Add(GetOwner(glyphTypeface.FontMemory, family));
                        }
                    }
                }

                Assert.NotEmpty(ownersByFile);

                foreach (var (file, owners) in ownersByFile)
                {
                    var owner = Assert.Single(owners);

                    Assert.IsType<FontFileMemory>(owner);
                    Assert.Equal(new FileInfo(file).Length, owner.Memory.Length);
                }

                // Typefaces the collection resolves share the data of the files held open.
                var fileOwners = ownersByFile.Values.SelectMany(o => o).ToHashSet();

                Assert.All(collectionOwners, owner => Assert.Contains(owner, fileOwners));
            }
            finally
            {
                foreach (var fontMemory in opened)
                {
                    fontMemory.Dispose();
                }

                ((IDisposable)collection).Dispose();
                provider.Dispose();
            }
        }

        private static MemoryManager<byte> GetOwner(IFontMemory fontMemory, string source)
        {
            var face = Assert.IsType<SfntFace>(fontMemory);

            Assert.True(face.TryGetFontFileData(out var data, out _), source);
            Assert.True(MemoryMarshal.TryGetMemoryManager(data, out MemoryManager<byte>? owner), source);

            return owner!;
        }

        private static string Canonicalize(string path)
        {
            var fullPath = Path.GetFullPath(path);

            try
            {
                return File.ResolveLinkTarget(fullPath, returnFinalTarget: true)?.FullName ?? fullPath;
            }
            catch (IOException)
            {
                return fullPath;
            }
        }
    }
}
