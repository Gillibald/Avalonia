using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using Avalonia.Metadata;
using Avalonia.Platform;

namespace Avalonia.Media.Fonts
{
    /// <summary>
    /// System font provider over a static, app-registered font set, for environments without any
    /// platform font system: the Browser, headless rendering, and tests. Registered fonts become
    /// the "system" fonts; enumeration, matching and character fallback run entirely managed
    /// against the parsed faces.
    /// </summary>
    [Unstable]
    public sealed class StaticFontProvider : ISystemFontProvider
    {
        private readonly object _lock = new();
        private readonly List<FaceEntry> _faces = new();
        private bool _disposed;

        /// <summary>
        /// Initializes an empty provider; fonts are registered with <see cref="AddFont"/> or
        /// <see cref="AddFontSource"/>.
        /// </summary>
        public StaticFontProvider()
        {
        }

        /// <summary>
        /// Initializes the provider with the fonts of the specified source.
        /// </summary>
        /// <param name="fontSource">An <c>avares:</c> or <c>resm:</c> font source.</param>
        public StaticFontProvider(Uri fontSource)
        {
            AddFontSource(fontSource);
        }

        /// <summary>
        /// Gets or sets the family name of the default face. When unset, the first registered
        /// face is the default.
        /// </summary>
        public string? DefaultFamilyName { get; set; }

        /// <summary>
        /// Attempts to register the first face of the specified font stream.
        /// </summary>
        /// <param name="stream">A readable stream positioned at the beginning of the font data.</param>
        /// <returns><see langword="true"/> if the font was parsed and registered; otherwise, <see langword="false"/>.</returns>
        public bool AddFont(Stream stream)
        {
            if (!SfntFace.TryLoad(stream, out var face))
            {
                return false;
            }

            if (GlyphTypeface.TryCreate(face) is not { } glyphTypeface)
            {
                face.Dispose();

                return false;
            }

            lock (_lock)
            {
                if (_disposed)
                {
                    glyphTypeface.Dispose();

                    return false;
                }

                _faces.Add(new FaceEntry(glyphTypeface));
            }

            return true;
        }

        /// <summary>
        /// Attempts to register all font assets of the specified source.
        /// </summary>
        /// <param name="source">An <c>avares:</c> or <c>resm:</c> font source.</param>
        /// <returns><see langword="true"/> if at least one font was registered; otherwise, <see langword="false"/>.</returns>
        public bool AddFontSource(Uri source)
        {
            if (source is null || source.Scheme is not ("avares" or "resm"))
            {
                return false;
            }

            var assetLoader = AvaloniaLocator.Current.GetRequiredService<IAssetLoader>();
            var result = false;

            foreach (var fontAsset in FontFamilyLoader.LoadFontAssets(source))
            {
                using var stream = assetLoader.Open(fontAsset);

                if (AddFont(stream))
                {
                    result = true;
                }
            }

            return result;
        }

        public bool TryGetDefaultFontFace([NotNullWhen(true)] out SystemFontFace? face)
        {
            face = null;

            if (DefaultFamilyName is { } defaultFamilyName &&
                TryMatchFamily(defaultFamilyName, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out face))
            {
                return true;
            }

            lock (_lock)
            {
                if (_disposed || _faces.Count == 0)
                {
                    return false;
                }

                face = CreateFontFace(_faces[0].Face);

                return true;
            }
        }

        public IReadOnlyList<string> GetFontFamilyNames()
        {
            lock (_lock)
            {
                var names = new List<string>(_faces.Count);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in _faces)
                {
                    if (seen.Add(entry.Face.FamilyName))
                    {
                        names.Add(entry.Face.FamilyName);
                    }
                }

                return names;
            }
        }

        public bool TryMatchFamily(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
            [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            if (string.IsNullOrEmpty(familyName))
            {
                return false;
            }

            lock (_lock)
            {
                if (_disposed)
                {
                    return false;
                }

                GlyphTypeface? nearest = null;
                var nearestDistance = int.MaxValue;

                foreach (var entry in _faces)
                {
                    if (!entry.MatchesFamilyName(familyName))
                    {
                        continue;
                    }

                    var distance = GetDistance(entry.Face, style, weight, stretch);

                    if (distance < nearestDistance)
                    {
                        nearestDistance = distance;
                        nearest = entry.Face;
                    }
                }

                if (nearest is null)
                {
                    return false;
                }

                match = CreateFontFace(nearest);

                return true;
            }
        }

        public bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch,
            string? familyName, CultureInfo? culture, [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            if (codepoint <= 0)
            {
                return false;
            }

            lock (_lock)
            {
                if (_disposed)
                {
                    return false;
                }

                GlyphTypeface? best = null;
                var bestDistance = int.MaxValue;
                var bestMatchesFamily = false;

                foreach (var entry in _faces)
                {
                    if (!entry.Face.CharacterToGlyphMap.TryGetGlyph(codepoint, out _))
                    {
                        continue;
                    }

                    // The family name is a hint: prefer a covering face of the hinted family, then
                    // the nearest covering face overall.
                    var matchesFamily = familyName != null && entry.MatchesFamilyName(familyName);
                    var distance = GetDistance(entry.Face, style, weight, stretch);

                    if (best is null ||
                        (matchesFamily && !bestMatchesFamily) ||
                        (matchesFamily == bestMatchesFamily && distance < bestDistance))
                    {
                        best = entry.Face;
                        bestDistance = distance;
                        bestMatchesFamily = matchesFamily;
                    }
                }

                if (best is null)
                {
                    return false;
                }

                match = CreateFontFace(best);

                return true;
            }
        }

        public bool TryGetFamilyFaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<SystemFontFace>? faces)
        {
            faces = null;

            if (string.IsNullOrEmpty(familyName))
            {
                return false;
            }

            lock (_lock)
            {
                if (_disposed)
                {
                    return false;
                }

                List<SystemFontFace>? result = null;

                foreach (var entry in _faces)
                {
                    if (entry.MatchesFamilyName(familyName))
                    {
                        (result ??= new List<SystemFontFace>()).Add(CreateFontFace(entry.Face));
                    }
                }

                if (result is null)
                {
                    return false;
                }

                faces = result;

                return true;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                foreach (var entry in _faces)
                {
                    entry.Face.Dispose();
                }

                _faces.Clear();
            }
        }

        private static int GetDistance(GlyphTypeface face, FontStyle style, FontWeight weight, FontStretch stretch)
        {
            var weightDelta = Math.Abs((int)face.Weight - (int)weight);
            var stretchDelta = Math.Abs((int)face.Stretch - (int)stretch);
            var styleDelta = face.Style == style ? 0 : 1;

            return weightDelta + stretchDelta * 100 + styleDelta * 10_000;
        }

        private static SystemFontFace CreateFontFace(GlyphTypeface face)
        {
            return new StaticSystemFontFace(face);
        }

        /// <summary>
        /// A registered face plus its flattened match names (family, typographic and localized),
        /// precomputed at registration so name matching walks a plain array on the hot path.
        /// </summary>
        private readonly struct FaceEntry
        {
            private readonly string[] _matchNames;

            public FaceEntry(GlyphTypeface face)
            {
                Face = face;

                var names = new List<string>(2 + face.FamilyNames.Count) { face.FamilyName };

                AddIfMissing(names, face.TypographicFamilyName);

                foreach (var localizedName in face.FamilyNames)
                {
                    AddIfMissing(names, localizedName.Value);
                }

                _matchNames = names.ToArray();
            }

            public GlyphTypeface Face { get; }

            public bool MatchesFamilyName(string familyName)
            {
                foreach (var name in _matchNames)
                {
                    if (string.Equals(name, familyName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }

            private static void AddIfMissing(List<string> names, string? name)
            {
                if (string.IsNullOrEmpty(name))
                {
                    return;
                }

                foreach (var existing in names)
                {
                    if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }

                names.Add(name!);
            }
        }

        /// <summary>
        /// A descriptor over one of the provider's parsed faces. Opening the font memory clones
        /// the face's shared file data, so consumers own their view while the provider keeps its
        /// own reference.
        /// </summary>
        private sealed class StaticSystemFontFace : SystemFontFace
        {
            private readonly GlyphTypeface _face;

            public StaticSystemFontFace(GlyphTypeface face)
                : base(face.FamilyName, face.Style, face.Weight, face.Stretch)
            {
                _face = face;
            }

            public override bool TryOpenFontMemory([NotNullWhen(true)] out IFontMemory? fontMemory)
            {
                fontMemory = null;

                if (_face.FontMemory is not SfntFace sfntFace)
                {
                    return false;
                }

                try
                {
                    fontMemory = sfntFace.Clone();
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }

                return true;
            }
        }
    }
}
