using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using Avalonia.Media.Fonts.Tables.Variation;
using Avalonia.Platform;

namespace Avalonia.Media.Fonts
{
    /// <summary>
    /// System font collection over an <see cref="ISystemFontProvider"/> binding. The provider
    /// enumerates and matches system fonts as <see cref="SystemFontFace"/> descriptors; the
    /// collection materializes glyph typefaces through the managed loader, applies the shared
    /// simulation policy on top of the designed properties, and owns all caching. Enumeration is
    /// deferred until the first query, so constructing (and registering) the collection does no
    /// native work.
    /// </summary>
    /// <remarks>
    /// Every face of a font is parsed once: descriptors of the same font identity (the file path,
    /// or the identity a path-less descriptor supplies) and face index share one root glyph
    /// typeface, a named instance is a variation of that root, and a simulated face is a
    /// simulated variant of the face it simulates.
    /// </remarks>
    internal class SystemFontCollection : FontCollectionBase
    {
        private readonly Uri _key;
        private readonly ISystemFontProvider _provider;
        private readonly object _familiesLock = new();
        private volatile bool _familiesInitialized;
        private volatile bool _defaultResolved;
        private FontFamily? _defaultFontFamily;

        // One root per font identity and face index. Lazy guarantees a single load per face under
        // concurrent first requests, so no racing thread maps and parses the same font again.
        private readonly ConcurrentDictionary<(object Identity, int FaceIndex), FontFileFace> _fileFaces = new();

        // Roots of descriptors without an identity, which cannot be shared between descriptors.
        private readonly ConcurrentBag<GlyphTypeface> _unsharedRoots = new();

        public SystemFontCollection(Uri key, ISystemFontProvider provider)
        {
            _key = key ?? throw new ArgumentNullException(nameof(key));
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        public override Uri Key => _key;

        private protected override void EnsureFamilies()
        {
            if (_familiesInitialized)
            {
                return;
            }

            lock (_familiesLock)
            {
                if (_familiesInitialized)
                {
                    return;
                }

                foreach (var familyName in _provider.GetFontFamilyNames())
                {
                    if (!string.IsNullOrEmpty(familyName))
                    {
                        AddFontFamily(familyName);
                    }
                }

                _familiesInitialized = true;
            }
        }

        public override bool TryGetDefaultFontFamily([NotNullWhen(true)] out FontFamily? fontFamily)
        {
            fontFamily = _defaultFontFamily;

            if (fontFamily != null)
            {
                return true;
            }

            if (_defaultResolved)
            {
                return false;
            }

            lock (_familiesLock)
            {
                if (_defaultFontFamily is { } existing)
                {
                    fontFamily = existing;

                    return true;
                }

                if (_defaultResolved)
                {
                    return false;
                }

                if (!_provider.TryGetDefaultFontFace(out var face))
                {
                    _defaultResolved = true;

                    return false;
                }

                // Pin the descriptor: load the face and register it in the cache up front, so
                // lookups by the default family name resolve through the cache even when the name
                // is a private one the provider would not serve through TryMatchFamily (the macOS
                // ".AppleSystemUIFont" case).
                ResolveAndRegister(face, null);

                fontFamily = _defaultFontFamily = new FontFamily(face.FamilyName);
                _defaultResolved = true;

                return true;
            }
        }

        public override bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight,
            FontStretch stretch, [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
        {
            EnsureFamilies();

            var typeface = new Typeface(familyName, style, weight, stretch).Normalize(out familyName);
            var key = typeface.ToFontCollectionKey();

            // Find an exact match first
            if (TryGetGlyphTypeface(familyName, key, allowNearestMatch: false, out glyphTypeface))
            {
                return true;
            }

            // A negative entry records that neither the provider nor the family's faces could
            // answer the key.
            if (_glyphTypefaceCache.TryGetValue(familyName, out var glyphTypefaces) &&
                glyphTypefaces.TryGetValue(key, out glyphTypeface))
            {
                return glyphTypeface != null;
            }

            // A family the STAT table of a loaded variable face derives is answered by that face
            // without asking the provider when a position of it has the requested style. A style
            // the face reaches only by simulation falls through, so a real face the provider
            // offers, such as an italic in another file, still wins.
            if (TryGetInstanceFamilyFaces(familyName, out var instanceFaces))
            {
                foreach (var instanceFace in instanceFaces)
                {
                    var varied = instanceFace.WithVariation(GetStylePosition(instanceFace, key));

                    if (varied.ToFontCollectionKey().StyleEquals(key))
                    {
                        TryAddGlyphTypeface(familyName, key, varied);

                        glyphTypeface = varied;

                        return true;
                    }
                }
            }

            // The platform's pick joins the family's cached faces as one more candidate instead of
            // answering alone: a real face it offers beats a simulation of a cached one, and the
            // named instances and axes of the family's variable faces are matched before anything
            // is simulated.
            var matched = _provider.TryMatchFamily(familyName, style, weight, stretch, out var face) &&
                          ResolveAndRegister(face, familyName) is not null;

            // A family the provider does not know is answered only by faces already registered
            // under its name, such as the pinned default face.
            if ((matched || _glyphTypefaceCache.ContainsKey(familyName)) &&
                TryGetGlyphTypeface(familyName, key, allowNearestMatch: true, out glyphTypeface))
            {
                return true;
            }

            //Add null to cache to avoid future calls
            TryAddGlyphTypeface(familyName, key, null);

            return false;
        }

        public override bool TryGetFamilyTypefaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<Typeface>? familyTypefaces)
        {
            familyTypefaces = null;

            if (!_provider.TryGetFamilyFaces(familyName, out var faces))
            {
                return false;
            }

            var typefaces = new Typeface[faces.Count];

            for (var i = 0; i < faces.Count; i++)
            {
                var face = faces[i];

                typefaces[i] = new Typeface(new FontFamily(Key + "#" + face.FamilyName), face.Style, face.Weight, face.Stretch);
            }

            familyTypefaces = typefaces;

            return true;
        }

        protected override bool TryMatchCharacterFromPlatform(
            int codepoint,
            FontCollectionKey key,
            string? familyName,
            CultureInfo? culture,
            [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
        {
            glyphTypeface = null;

            if (!_provider.TryMatchCharacter(codepoint, key.Style, key.Weight, key.Stretch, familyName, culture, out var face))
            {
                return false;
            }

            // Registered so future lookups can short-circuit through TryMatchCharacter's Tier C
            // without re-invoking the provider.
            glyphTypeface = ResolveAndRegister(face, null);

            return glyphTypeface != null;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            // Roots own the variations and variants the cache holds; disposing a face twice is a no-op.
            foreach (var fileFace in _fileFaces.Values)
            {
                fileFace.Dispose();
            }

            foreach (var root in _unsharedRoots)
            {
                root.Dispose();
            }

            // The collection owns its provider.
            _provider.Dispose();
        }

        /// <summary>
        /// Materializes <paramref name="face"/> and registers it under its style key: under the
        /// provider's family name and the requested family name. The root of its font file face
        /// is registered under the root's own names.
        /// </summary>
        /// <remarks>
        /// A face at another position than the root's belongs to the provider's family, not to
        /// the root's: the instances of one file carry the root's names, so faces of different
        /// optical sizes would collide on one key there. Registering the root also registers the
        /// families its STAT table derives, which are the families the provider reports for the
        /// instances (DirectWrite's "Segoe UI Variable Display"), so both names reach one bucket
        /// holding faces of one root.
        /// </remarks>
        /// <returns>
        /// The face's glyph typeface, or the one registered first under the provider's family
        /// name and the face's key; <see langword="null"/> when the face cannot be loaded.
        /// </returns>
        private GlyphTypeface? ResolveAndRegister(SystemFontFace face, string? requestedFamilyName)
        {
            if (!TryResolveFace(face, out var glyphTypeface, out var root, out var fileFace))
            {
                return null;
            }

            var key = new FontCollectionKey(glyphTypeface.Style, glyphTypeface.Weight, glyphTypeface.Stretch);

            var added = TryAddGlyphTypeface(face.FamilyName, key, glyphTypeface);

            if (requestedFamilyName != null &&
                !string.Equals(requestedFamilyName, face.FamilyName, StringComparison.OrdinalIgnoreCase))
            {
                added |= TryAddGlyphTypeface(requestedFamilyName, key, glyphTypeface);
            }

            if (fileFace is not null)
            {
                fileFace.EnsureRootRegistered(this);

                return glyphTypeface;
            }

            added |= TryAddGlyphTypeface(root, root.ToFontCollectionKey());

            if (added)
            {
                _unsharedRoots.Add(root);

                return glyphTypeface;
            }

            // Another thread registered the face first; nothing references this load.
            root.Dispose();

            return _glyphTypefaceCache.TryGetValue(face.FamilyName, out var glyphTypefaces) &&
                   glyphTypefaces.TryGetValue(key, out var existing)
                ? existing
                : null;
        }

        /// <summary>
        /// Resolves a descriptor to the root glyph typeface of its font file face, moved to the
        /// named instance the descriptor describes.
        /// </summary>
        /// <param name="face">The descriptor.</param>
        /// <param name="glyphTypeface">The glyph typeface of the descriptor.</param>
        /// <param name="root">The root glyph typeface <paramref name="glyphTypeface"/> is a variation of.</param>
        /// <param name="fileFace">
        /// The font file face that owns <paramref name="root"/>; <see langword="null"/> for a
        /// descriptor without an identity, whose root the caller owns.
        /// </param>
        private bool TryResolveFace(SystemFontFace face, [NotNullWhen(true)] out GlyphTypeface? glyphTypeface,
            [NotNullWhen(true)] out GlyphTypeface? root, out FontFileFace? fileFace)
        {
            glyphTypeface = null;
            fileFace = null;

            if (face.Identity is { } identity)
            {
                fileFace = _fileFaces.GetOrAdd((identity, face.FaceIndex), static (_, f) => new FontFileFace(f), face);
                root = fileFace.Root;
            }
            else
            {
                root = TryCreateGlyphTypeface(face);
            }

            if (root is null)
            {
                return false;
            }

            glyphTypeface = root.VariationAxes.Count == 0
                ? root
                : root.WithVariation(GetInstancePosition(root, fileFace, face));

            return true;
        }

        /// <summary>
        /// Gets the position of the named instance a descriptor describes: the axis values the
        /// provider reports, or else the instance derived from the font. The derivation mirrors
        /// the WWS family model: the family name, matched against the STAT-composed family names,
        /// selects the non-style axes (optical size, custom axes), and the designed properties
        /// select a named instance at those axes, or else the weight, width and slope axis values.
        /// </summary>
        private NormalizedVariationPosition GetInstancePosition(GlyphTypeface root, FontFileFace? fileFace,
            SystemFontFace face)
        {
            if (face.AxisValues is { Count: > 0 } axisValues)
            {
                var settings = new List<FontVariation>(axisValues.Count);

                foreach (var axisValue in axisValues)
                {
                    settings.Add(new FontVariation(axisValue.Key, axisValue.Value));
                }

                return root.CreateNormalizedPosition(new FontVariationSettings(settings), default(NormalizedVariationPosition));
            }

            var familyPosition = fileFace is not null
                ? fileFace.GetFamilyPosition(face.FamilyName)
                : GetFamilyPosition(root, face.FamilyName);

            var key = new FontCollectionKey(face.Style, face.Weight, face.Stretch);

            return GetStylePosition(root.WithVariation(familyPosition), key);
        }

        /// <summary>
        /// Gets the position of the non-style axes at which the STAT-composed family name of a
        /// variable font equals <paramref name="familyName"/>, or the default position when no
        /// candidate composes that name. Candidates are the default instance, the named instances
        /// and the values STAT names on those axes.
        /// </summary>
        private static NormalizedVariationPosition GetFamilyPosition(GlyphTypeface root, string familyName)
        {
            foreach (var candidate in GetFamilyCandidates(root))
            {
                if (root.TryGetInstanceNames(candidate, out var names) &&
                    string.Equals(names.FamilyName, familyName, StringComparison.OrdinalIgnoreCase))
                {
                    var settings = new List<FontVariation>(candidate.Count);

                    foreach (var value in candidate)
                    {
                        settings.Add(new FontVariation(value.Key, value.Value));
                    }

                    return root.CreateNormalizedPosition(new FontVariationSettings(settings), default(NormalizedVariationPosition));
                }
            }

            return default;
        }

        private static IEnumerable<IReadOnlyDictionary<OpenTypeTag, float>> GetFamilyCandidates(GlyphTypeface root)
        {
            var axes = root.VariationAxes;

            // Every axis at its default.
            yield return new Dictionary<OpenTypeTag, float>();

            foreach (var instance in root.NamedInstances)
            {
                var candidate = new Dictionary<OpenTypeTag, float>();

                foreach (var coordinate in instance.Coordinates)
                {
                    if (!IsStyleAxis(coordinate.Key))
                    {
                        candidate[coordinate.Key] = coordinate.Value;
                    }
                }

                yield return candidate;
            }

            if (root.StatTable is not { } stat)
            {
                yield break;
            }

            foreach (var axisValue in stat.AxisValues)
            {
                if (axisValue.IsOlderSiblingFontAttribute)
                {
                    continue;
                }

                var candidate = new Dictionary<OpenTypeTag, float>();

                foreach (var record in axisValue.Records)
                {
                    var tag = stat.DesignAxes[record.AxisIndex].Tag;

                    if (!IsStyleAxis(tag) && HasAxis(axes, tag))
                    {
                        candidate[tag] = record.Value;
                    }
                }

                if (candidate.Count > 0)
                {
                    yield return candidate;
                }
            }
        }

        private static bool HasAxis(IReadOnlyList<FontVariationAxis> axes, OpenTypeTag tag)
        {
            foreach (var axis in axes)
            {
                if (axis.Tag == tag)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsStyleAxis(OpenTypeTag tag)
            => tag == FvarAxisTags.Weight || tag == FvarAxisTags.Width ||
               tag == FvarAxisTags.Italic || tag == FvarAxisTags.Slant;

        private static GlyphTypeface? TryCreateGlyphTypeface(SystemFontFace face)
        {
            if (!face.TryOpenFontMemory(out var fontMemory))
            {
                return null;
            }

            var glyphTypeface = GlyphTypeface.TryCreate(fontMemory);

            if (glyphTypeface is null)
            {
                fontMemory.Dispose();
            }

            return glyphTypeface;
        }

        /// <summary>
        /// The root glyph typeface of one face of a font, loaded on first use, and the
        /// family positions derived for the family names its descriptors carry.
        /// </summary>
        private sealed class FontFileFace
        {
            private readonly Lazy<GlyphTypeface?> _root;
            private readonly ConcurrentDictionary<string, NormalizedVariationPosition> _familyPositions =
                new(StringComparer.OrdinalIgnoreCase);
            private readonly object _registrationLock = new();
            private volatile bool _rootRegistered;

            public FontFileFace(SystemFontFace face)
            {
                _root = new Lazy<GlyphTypeface?>(() => TryCreateGlyphTypeface(face),
                    LazyThreadSafetyMode.ExecutionAndPublication);
            }

            public GlyphTypeface? Root => _root.Value;

            /// <summary>
            /// Registers the root under its own names once, which also registers the families
            /// its STAT table derives. Deriving them walks the name and STAT tables, so it is
            /// not repeated for every descriptor of the file face.
            /// </summary>
            public void EnsureRootRegistered(SystemFontCollection collection)
            {
                if (_rootRegistered)
                {
                    return;
                }

                lock (_registrationLock)
                {
                    if (_rootRegistered)
                    {
                        return;
                    }

                    var root = Root!;

                    collection.TryAddGlyphTypeface(root, root.ToFontCollectionKey());

                    _rootRegistered = true;
                }
            }

            public NormalizedVariationPosition GetFamilyPosition(string familyName)
                => _familyPositions.GetOrAdd(familyName, static (name, root) => SystemFontCollection.GetFamilyPosition(root, name), Root!);

            public void Dispose()
            {
                if (_root.IsValueCreated)
                {
                    _root.Value?.Dispose();
                }
            }
        }
    }
}
