#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// System font provider over Skia's font manager. This is the render subsystem's default for
    /// every platform without a native binding (and the platforms that keep using Skia for font
    /// discovery): SKFontManager answers enumeration and matching through the platform's own font
    /// system, while the font data itself flows through the managed loader.
    /// </summary>
    internal sealed class SkiaFontProvider : ISystemFontProvider
    {
        [ThreadStatic] private static string[]? t_languageTagBuffer;

        // Matching is a hot path and SKFontStyle wraps a native object: cache one per requested
        // style triple instead of creating (and finalizing) one per match. Applications use a
        // handful of triples, so the cache stays tiny and is never evicted.
        private static readonly ConcurrentDictionary<int, SKFontStyle> s_fontStyles = new();

        private readonly SKFontManager _fontManager = SKFontManager.Default;

        public bool TryGetDefaultFontFace([NotNullWhen(true)] out SystemFontFace? face)
        {
            face = null;

            var familyName = SKTypeface.Default.FamilyName;

            if (string.IsNullOrEmpty(familyName))
            {
                return false;
            }

            return TryMatchFamily(familyName, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out face);
        }

        public IReadOnlyList<string> GetFontFamilyNames() => _fontManager.GetFontFamilies();

        public bool TryMatchFamily(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
            [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            if (string.IsNullOrEmpty(familyName))
            {
                return false;
            }

            var skTypeface = _fontManager.MatchFamily(familyName, GetFontStyle(style, weight, stretch));

            if (skTypeface is null)
            {
                return false;
            }

            match = SkiaSystemFontFace.Create(skTypeface);

            return true;
        }

        public bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch,
            string? familyName, CultureInfo? culture, [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            if (codepoint <= 0)
            {
                return false;
            }

            culture ??= CultureInfo.CurrentUICulture;

            t_languageTagBuffer ??= new string[1];
            t_languageTagBuffer[0] = culture.Name;

            var skTypeface = _fontManager.MatchCharacter(string.IsNullOrEmpty(familyName) ? null : familyName,
                GetFontStyle(style, weight, stretch), t_languageTagBuffer, codepoint);

            if (skTypeface is null)
            {
                return false;
            }

            match = SkiaSystemFontFace.Create(skTypeface);

            return true;
        }

        public bool TryGetFamilyFaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<SystemFontFace>? faces)
        {
            faces = null;

            if (string.IsNullOrEmpty(familyName))
            {
                return false;
            }

            using var styleSet = _fontManager.GetFontStyles(familyName);

            if (styleSet.Count == 0)
            {
                return false;
            }

            var result = new List<SystemFontFace>(styleSet.Count);

            for (var i = 0; i < styleSet.Count; i++)
            {
                if (styleSet.CreateTypeface(i) is { } skTypeface)
                {
                    result.Add(SkiaSystemFontFace.Create(skTypeface));
                }
            }

            if (result.Count == 0)
            {
                return false;
            }

            faces = result;

            return true;
        }

        public void Dispose()
        {
            // SKFontManager.Default is a shared singleton; descriptors own their typefaces.
        }

        private static SKFontStyle GetFontStyle(FontStyle style, FontWeight weight, FontStretch stretch)
        {
            var slant = style.ToSkia();
            var key = ((int)weight << 8) | ((int)stretch << 4) | (int)slant;

            return s_fontStyles.GetOrAdd(key,
                static (_, s) => new SKFontStyle((SKFontStyleWeight)s.weight, (SKFontStyleWidth)s.stretch, s.slant),
                (weight, stretch, slant));
        }

        /// <summary>
        /// A descriptor over a matched SKTypeface. Skia does not expose the font's file path, so
        /// the font data is served through the override hatch from the typeface's underlying
        /// stream (with its collection face index). The stream's content identifies the file, so
        /// all faces of one file share one data: the stream's own memory where Skia exposes it
        /// (a view of the font file), otherwise a single native copy per file.
        /// </summary>
        private sealed class SkiaSystemFontFace : SystemFontFace
        {
            private readonly SKTypeface _skTypeface;
            private object? _identity;
            private bool _identityResolved;

            private SkiaSystemFontFace(SKTypeface skTypeface, FontStyle style, FontWeight weight, FontStretch stretch)
                : base(skTypeface.FamilyName, style, weight, stretch, skTypeface.PostScriptName)
            {
                _skTypeface = skTypeface;
            }

            // Every match returns a new SKTypeface, and Skia does not guarantee one handle per
            // font, so the handle cannot identify the font data. The content key of the
            // typeface's stream identifies the font file, as it does for the shared file data,
            // and the collection index selects the face within it. Resolved on first use because
            // both are only known by opening the typeface's stream.
            internal override object? Identity
            {
                get
                {
                    if (!_identityResolved)
                    {
                        _identity = CreateIdentity();
                        _identityResolved = true;
                    }

                    return _identity;
                }
            }

            private object? CreateIdentity()
            {
                try
                {
                    using var stream = _skTypeface.OpenStream(out var ttcIndex);

                    return stream is not null && stream.Length > 0 && TryCreateContentKey(stream, out var key)
                        ? (key, ttcIndex)
                        : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            /// <summary>
            /// Creates a descriptor reporting the face's designed properties. Skia's style is that
            /// of the typeface it matched, which on DirectWrite is a simulated bold or oblique
            /// variant when the family lacks the requested face; the collection applies its own
            /// simulation on top of the designed properties, so they are read from the font's
            /// tables, as the glyph typeface over the face reads them.
            /// </summary>
            public static SkiaSystemFontFace Create(SKTypeface skTypeface)
            {
                try
                {
                    if (TryGetDesignedProperties(skTypeface, out var style, out var weight, out var stretch))
                    {
                        return new SkiaSystemFontFace(skTypeface, style, weight, stretch);
                    }
                }
                catch (Exception)
                {
                    // Damaged tables: fall back to the properties Skia reports.
                }

                // Fonts without SFNT tables (Type 1 fonts listed by fontconfig).
                return new SkiaSystemFontFace(skTypeface, skTypeface.FontStyle.Slant.ToAvalonia(),
                    (FontWeight)skTypeface.FontWeight, (FontStretch)skTypeface.FontWidth);
            }

            private static bool TryGetDesignedProperties(SKTypeface skTypeface, out FontStyle style,
                out FontWeight weight, out FontStretch stretch)
            {
                // Read from the stream's memory where Skia exposes it. Skia's own table access
                // creates a FreeType face for the typeface on FreeType-based font managers, which
                // keeps a mapping of the whole file alive for as long as the font manager caches
                // the typeface, that is, for the rest of the process.
                var stream = skTypeface.OpenStream(out var ttcIndex);

                if (stream is not null && SkiaStreamMemory.TryCreate(stream, out var streamMemory))
                {
                    var data = new SharedFontData(streamMemory!, streamMemory!.Memory);

                    if (!SfntFace.TryCreate(data, ttcIndex, out var face))
                    {
                        data.Release();

                        style = FontStyle.Normal;
                        weight = FontWeight.Normal;
                        stretch = FontStretch.Normal;

                        return false;
                    }

                    using (face)
                    {
                        return GlyphTypeface.TryGetDesignedProperties(face, out style, out weight, out stretch);
                    }
                }

                stream?.Dispose();

                return GlyphTypeface.TryGetDesignedProperties(new SkiaTypefaceTables(skTypeface), out style,
                    out weight, out stretch);
            }

            public override bool TryOpenFontMemory([NotNullWhen(true)] out IFontMemory? fontMemory)
            {
                fontMemory = null;

                SKStreamAsset? stream = null;

                try
                {
                    stream = _skTypeface.OpenStream(out var ttcIndex);

                    if (stream is null || stream.Length <= 0 || !TryCreateContentKey(stream, out var key))
                    {
                        return false;
                    }

                    // The factory takes over the stream opened for the key; only a lost race for the
                    // same file makes it run again, with a stream of its own.
                    var pending = stream;

                    var found = FontFileCache.TryGetOrCreate(key,
                        () => CreateFontData(Interlocked.Exchange(ref pending, null) ?? _skTypeface.OpenStream()),
                        out var data);

                    // On a hit the cache already holds this file: the stream is not needed.
                    stream = pending;

                    if (!found)
                    {
                        return false;
                    }

                    if (!SfntFace.TryCreate(data!, ttcIndex, out var face))
                    {
                        data!.Release();

                        return false;
                    }

                    fontMemory = face;

                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
                finally
                {
                    stream?.Dispose();
                }
            }

            private static unsafe bool TryCreateContentKey(SKStreamAsset stream, out FontContentKey key)
            {
                var memoryBase = stream.GetMemoryBase();

                if (memoryBase != IntPtr.Zero)
                {
                    return FontContentKey.TryCreate(new ReadOnlySpan<byte>((void*)memoryBase, stream.Length), out key);
                }

                return FontContentKey.TryCreate(stream.Length, (offset, destination) =>
                {
                    if (offset > int.MaxValue || !stream.Seek((int)offset))
                    {
                        return false;
                    }

                    fixed (byte* buffer = destination)
                    {
                        return stream.Read((IntPtr)buffer, destination.Length) == destination.Length;
                    }
                }, out key);
            }

            /// <summary>
            /// Serves the leading part of a few tables of a typeface, enough for the designed
            /// properties, without opening the font data.
            /// </summary>
            private sealed class SkiaTypefaceTables : IFontMemory
            {
                // OS/2 is read whole (about 100 bytes); of head and post only the fixed headers
                // that hold the style fields are needed, while a post table can carry kilobytes of
                // glyph names.
                private static readonly OpenTypeTag s_os2Tag = OpenTypeTag.Parse("OS/2");
                private static readonly OpenTypeTag s_headTag = OpenTypeTag.Parse("head");
                private static readonly OpenTypeTag s_postTag = OpenTypeTag.Parse("post");

                private readonly SKTypeface _skTypeface;

                public SkiaTypefaceTables(SKTypeface skTypeface) => _skTypeface = skTypeface;

                public unsafe bool TryGetTable(OpenTypeTag tag, out ReadOnlyMemory<byte> table)
                {
                    table = default;

                    int limit;

                    if (tag == s_os2Tag)
                    {
                        limit = 256;
                    }
                    else if (tag == s_headTag)
                    {
                        limit = 54;
                    }
                    else if (tag == s_postTag)
                    {
                        limit = 32;
                    }
                    else
                    {
                        return false;
                    }

                    var length = Math.Min(_skTypeface.GetTableSize(tag), limit);

                    if (length <= 0)
                    {
                        return false;
                    }

                    var buffer = new byte[length];

                    fixed (byte* data = buffer)
                    {
                        if (!_skTypeface.TryGetTableData(tag, 0, length, (IntPtr)data))
                        {
                            return false;
                        }
                    }

                    table = buffer;

                    return true;
                }

                public void Dispose()
                {
                }
            }

            private static (IDisposable Owner, ReadOnlyMemory<byte> Memory)? CreateFontData(SKStreamAsset? stream)
            {
                if (stream is null)
                {
                    return null;
                }

                if (SkiaStreamMemory.TryCreate(stream, out var streamMemory))
                {
                    return (streamMemory!, streamMemory!.Memory);
                }

                // Without a memory base the stream is read once, straight into native memory.
                using (stream)
                {
                    if (!stream.Rewind())
                    {
                        return null;
                    }

                    var copy = UnmanagedFontMemory.TryCreate(stream.Length,
                        (buffer, length) => stream.Read(buffer, length) == length);

                    return copy is null ? null : (copy, copy.Memory);
                }
            }
        }
    }
}
