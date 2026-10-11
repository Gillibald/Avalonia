using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests.Media
{
    /// <summary>
    /// Font data of the Skia system font provider: faces of one file share one data, and opening
    /// a face neither copies the file into managed memory nor, where Skia exposes the file memory,
    /// into native memory.
    /// </summary>
    public class SkiaFontProviderMemoryTests
    {
        [Win32Fact("Requires Windows fonts")]
        public void Faces_Of_One_Collection_File_Should_Share_One_File_Data()
        {
            using var provider = new SkiaFontProvider();

            // Cambria and Cambria Math are the two faces of cambria.ttc.
            var faces = new List<SfntFace>();

            try
            {
                foreach (var family in new[] { "Cambria", "Cambria Math", "Cambria" })
                {
                    Assert.True(provider.TryMatchFamily(family, FontStyle.Normal, FontWeight.Normal,
                        FontStretch.Normal, out var match));
                    Assert.True(match.TryOpenFontMemory(out var fontMemory));

                    faces.Add(Assert.IsType<SfntFace>(fontMemory));
                }

                Assert.NotEqual(faces[0].FaceIndex, faces[1].FaceIndex);

                Assert.True(faces[0].TryGetFontFileData(out var first, out _));

                foreach (var face in faces)
                {
                    Assert.True(face.TryGetFontFileData(out var data, out _));
                    Assert.True(first.Span == data.Span, "Faces of one file must share one file data.");
                }
            }
            finally
            {
                faces.ForEach(f => f.Dispose());
            }
        }

        [Fact]
        public void Opening_A_Face_Should_Not_Copy_The_File_Into_Managed_Memory()
        {
            using var provider = new SkiaFontProvider();

            Assert.True(provider.TryGetDefaultFontFace(out var face));

            // Warm up type loading and caches, so the measured open allocates only per open.
            Assert.True(face.TryOpenFontMemory(out var warmUp));
            warmUp.Dispose();

            var before = GC.GetAllocatedBytesForCurrentThread();

            Assert.True(face.TryOpenFontMemory(out var fontMemory));

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            using (fontMemory)
            {
                Assert.True(Assert.IsType<SfntFace>(fontMemory).TryGetFontFileData(out var data, out _));
                Assert.True(allocated < data.Length / 4,
                    $"Opening a {data.Length} byte font allocated {allocated} managed bytes.");
            }
        }

        [Fact]
        public void Faces_With_A_Skia_Memory_Base_Should_Not_Copy_The_File()
        {
            using var provider = new SkiaFontProvider();

            Assert.True(provider.TryGetDefaultFontFace(out var face));

            using (var typeface = SKFontManager.Default.MatchFamily(face.FamilyName))
            using (var stream = typeface.OpenStream())
            {
                Assert.SkipWhen(stream is null || stream.GetMemoryBase() == IntPtr.Zero,
                    "Skia's font manager exposes no memory base for this font here.");
            }

            Assert.True(face.TryOpenFontMemory(out var fontMemory));

            using (fontMemory)
            {
                Assert.True(Assert.IsType<SfntFace>(fontMemory).TryGetFontFileData(out var data, out _));
                Assert.True(MemoryMarshal.TryGetMemoryManager(data, out MemoryManager<byte>? owner));
                Assert.IsNotType<UnmanagedFontMemory>(owner);
            }
        }
    }
}
