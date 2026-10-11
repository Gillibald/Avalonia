using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Threading;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Fonts.Tables;
using Avalonia.Media.Fonts.Tables.Cmap;
using Avalonia.Media.Fonts.Tables.Name;
using Avalonia.Media.TextFormatting.Unicode;
using Avalonia.Platform;

namespace Avalonia.Android.Platform.Fonts
{
    /// <summary>
    /// The system font model of an Android device, built from its font configuration files:
    /// named families and aliases, the fallback chain, and the real family names of the font
    /// files. Answers family and character matches with path-based descriptors, so faces load
    /// through the shared, memory-mapped font file data.
    /// </summary>
    /// <remarks>
    /// Matching follows the platform's rules: the nearest entry of a family by weight steps, with
    /// a slant mismatch costing two steps and file order breaking ties; character fallback tries
    /// the requested family, the default family, then the fallback families ordered by emoji
    /// preference, language match and variant (compact before elegant), and accepts a face only
    /// when its character map covers the codepoint. The model is immutable after construction;
    /// the caches are concurrent, so the catalog is safe for concurrent use.
    /// </remarks>
    internal sealed class AndroidFontCatalog
    {
        private const string SerifFamilyName = "serif";
        private const ushort UsEnglishLanguageId = 0x0409;

        private readonly Dictionary<string, Family> _families = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Alias> _aliases = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _declaredNames = new();
        private readonly List<Family> _fallbackFamilies = new();
        private readonly List<FaceKey> _allFaces = new();
        private readonly Family? _defaultFamily;
        private readonly ConcurrentDictionary<FaceKey, FaceInfo> _faceInfos = new();
        private readonly ConcurrentDictionary<string, bool> _fileExists = new(StringComparer.Ordinal);
        private readonly Lazy<IReadOnlyList<string>> _familyNames;
        private readonly Lazy<Dictionary<string, List<FaceKey>>> _realNames;

        private AndroidFontCatalog(FontsXmlConfig main, IReadOnlyList<FontsXmlConfig> additions)
        {
            var seenFaces = new HashSet<FaceKey>();

            foreach (var xmlFamily in main.Families)
            {
                var family = new Family(xmlFamily);

                AddFaces(family, seenFaces);

                if (family.Name is null)
                {
                    _fallbackFamilies.Add(family);

                    continue;
                }

                if (_families.TryAdd(family.Name, family))
                {
                    _declaredNames.Add(family.Name);
                    _defaultFamily ??= family;
                }
            }

            AddAliases(main.Aliases);

            // Additions repeat much of the main configuration (Samsung's fonts_additional.xml is
            // nearly a copy of fonts.xml): only names the main file lacks are taken from them, and
            // their unnamed families never join the fallback chain.
            foreach (var addition in additions)
            {
                foreach (var xmlFamily in addition.Families)
                {
                    if (xmlFamily.Name is null || IsKnownName(xmlFamily.Name))
                    {
                        continue;
                    }

                    var family = new Family(xmlFamily);

                    AddFaces(family, seenFaces);

                    _families.Add(family.Name!, family);
                    _declaredNames.Add(family.Name!);
                }

                AddAliases(addition.Aliases);
            }

            _familyNames = new Lazy<IReadOnlyList<string>>(CreateFamilyNames, LazyThreadSafetyMode.ExecutionAndPublication);
            _realNames = new Lazy<Dictionary<string, List<FaceKey>>>(CreateRealNameIndex,
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        /// <summary>
        /// Builds the catalog from the first readable main configuration file and every readable
        /// addition.
        /// </summary>
        public static AndroidFontCatalog Create(AndroidFontConfiguration configuration)
        {
            var main = FontsXmlConfig.Empty;

            foreach (var file in configuration.MainFiles)
            {
                if (FontsXmlParser.TryParseFile(file.Path, file.FontDirectory, out var config) &&
                    config.Families.Count > 0)
                {
                    main = config;

                    break;
                }
            }

            var additions = new List<FontsXmlConfig>();

            foreach (var file in configuration.AdditionFiles)
            {
                if (FontsXmlParser.TryParseFile(file.Path, file.FontDirectory, out var config))
                {
                    additions.Add(config);
                }
            }

            return new AndroidFontCatalog(main, additions);
        }

        /// <summary>
        /// Gets the configured family names and aliases that have at least one existing font
        /// file, each family followed by its aliases. Real family names of the font files resolve
        /// through <see cref="TryMatchFamily"/> but are not listed: the configuration names are
        /// the ones Android exposes to applications.
        /// </summary>
        public IReadOnlyList<string> GetFamilyNames() => _familyNames.Value;

        public bool TryGetDefaultFace([NotNullWhen(true)] out SystemFontFace? face)
        {
            face = null;

            return _defaultFamily is { } family &&
                   TrySelectEntry(family, null, 400, false, out var entry) &&
                   TryCreateFace(family.Name!, entry, out face);
        }

        public bool TryMatchFamily(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
            [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            if (string.IsNullOrEmpty(familyName))
            {
                return false;
            }

            var italic = style != FontStyle.Normal;

            if (TryResolveFamily(familyName, out var family, out var aliasWeight))
            {
                return TrySelectEntry(family, aliasWeight, (int)weight, italic, out var entry) &&
                       TryCreateFace(family.Name!, entry, out match);
            }

            return TryMatchRealFamily(familyName, (int)weight, italic, out match);
        }

        public bool TryGetFamilyFaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<SystemFontFace>? faces)
        {
            faces = null;

            if (string.IsNullOrEmpty(familyName))
            {
                return false;
            }

            var result = new List<SystemFontFace>();

            if (TryResolveFamily(familyName, out var family, out var aliasWeight))
            {
                var seen = new HashSet<FaceKey>();

                foreach (var entry in GetCandidates(family, aliasWeight))
                {
                    if (seen.Add(entry.Key) && TryCreateFace(family.Name!, entry, out var face))
                    {
                        result.Add(face);
                    }
                }
            }
            else if (_realNames.Value.TryGetValue(familyName, out var keys))
            {
                foreach (var key in keys)
                {
                    if (TryCreateRealFace(key, out var face))
                    {
                        result.Add(face);
                    }
                }
            }

            if (result.Count == 0)
            {
                return false;
            }

            faces = result;

            return true;
        }

        public bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch,
            string? familyName, CultureInfo? culture, [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            if (codepoint <= 0 || codepoint > 0x10FFFF)
            {
                return false;
            }

            var italic = style != FontStyle.Normal;
            var requestedWeight = (int)weight;
            Family? requested = null;

            if (!string.IsNullOrEmpty(familyName))
            {
                if (TryResolveFamily(familyName, out requested, out var aliasWeight))
                {
                    if (TrySelectEntry(requested, aliasWeight, requestedWeight, italic, out var entry) &&
                        Covers(entry.Key, codepoint) && TryCreateFace(requested.Name!, entry, out match))
                    {
                        return true;
                    }
                }
                else if (TryMatchRealFamily(familyName, requestedWeight, italic, out var realMatch) &&
                         Covers(new FaceKey(realMatch.FilePath!, realMatch.FaceIndex), codepoint))
                {
                    match = realMatch;

                    return true;
                }
            }

            if (_defaultFamily is { } defaultFamily && !ReferenceEquals(defaultFamily, requested) &&
                TrySelectEntry(defaultFamily, null, requestedWeight, italic, out var defaultEntry) &&
                Covers(defaultEntry.Key, codepoint) && TryCreateFace(defaultFamily.Name!, defaultEntry, out match))
            {
                return true;
            }

            return TryMatchFallback(codepoint, requestedWeight, italic,
                requested is not null && IsSerif(requested), culture ?? CultureInfo.CurrentUICulture, out match);
        }

        private bool TryMatchFallback(int codepoint, int weight, bool italic, bool serif, CultureInfo culture,
            [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            var codepointInfo = new Codepoint((uint)codepoint);
            var script = codepointInfo.Script;
            var isEmoji = codepointInfo.GraphemeBreakClass is GraphemeBreakClass.ExtendedPictographic
                or GraphemeBreakClass.RegionalIndicator;
            var language = new LanguageTag(culture.Name, Bcp47ScriptResolver.GetScriptSubtag(culture));
            var candidates = new List<(Family Family, int Rank)>();

            for (var i = 0; i < _fallbackFamilies.Count; i++)
            {
                var family = _fallbackFamilies[i];

                if (family.Ignore || !family.MayCover(codepointInfo, script))
                {
                    continue;
                }

                // Lower ranks first: emoji fonts for emoji, then the language match, then the
                // variant, with file order breaking ties through the stable sort below.
                var rank = (isEmoji && family.IsEmoji ? 0 : 8) + (2 - family.MatchLanguage(language)) * 2 +
                           (family.Variant == FontsXmlVariant.Elegant ? 1 : 0);

                candidates.Add((family, rank));
            }

            // List<T>.Sort is not stable; the index keeps file order among equal ranks.
            var ordered = new (Family Family, int Rank, int Index)[candidates.Count];

            for (var i = 0; i < ordered.Length; i++)
            {
                ordered[i] = (candidates[i].Family, candidates[i].Rank, i);
            }

            Array.Sort(ordered, static (x, y) => x.Rank != y.Rank ? x.Rank.CompareTo(y.Rank) : x.Index.CompareTo(y.Index));

            foreach (var (family, _, _) in ordered)
            {
                var useSerif = serif && family.HasSerifSubstitutes;

                if (!TrySelectEntry(family, null, weight, italic, out var entry, useSerif ? SerifFamilyName : null) ||
                    !Covers(entry.Key, codepoint))
                {
                    continue;
                }

                if (TryGetFaceInfo(entry.Key, out var info) &&
                    TryCreateFace(info.FamilyName, entry, out match))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsSerif(Family family) =>
            string.Equals(family.Name, SerifFamilyName, StringComparison.OrdinalIgnoreCase);

        private bool IsKnownName(string name) => _families.ContainsKey(name) || _aliases.ContainsKey(name);

        private void AddFaces(Family family, HashSet<FaceKey> seenFaces)
        {
            foreach (var entry in family.Entries)
            {
                if (seenFaces.Add(entry.Key))
                {
                    _allFaces.Add(entry.Key);
                }
            }
        }

        private void AddAliases(IReadOnlyList<FontsXmlAlias> aliases)
        {
            foreach (var alias in aliases)
            {
                if (IsKnownName(alias.Name) || !_families.TryGetValue(alias.To, out var target))
                {
                    continue;
                }

                _aliases.Add(alias.Name, new Alias(alias.Name, target, alias.Weight));
            }
        }

        private bool TryResolveFamily(string name, [NotNullWhen(true)] out Family? family, out int? aliasWeight)
        {
            if (_families.TryGetValue(name, out family))
            {
                aliasWeight = null;

                return HasExistingEntry(family);
            }

            if (_aliases.TryGetValue(name, out var alias))
            {
                family = alias.Target;
                aliasWeight = alias.Weight;

                return HasExistingEntry(family);
            }

            aliasWeight = null;

            return false;
        }

        private IReadOnlyList<string> CreateFamilyNames()
        {
            var names = new List<string>();

            foreach (var name in _declaredNames)
            {
                if (!HasExistingEntry(_families[name]))
                {
                    continue;
                }

                names.Add(name);

                foreach (var alias in _aliases.Values)
                {
                    if (ReferenceEquals(alias.Target, _families[name]))
                    {
                        names.Add(alias.Name);
                    }
                }
            }

            return names;
        }

        private bool HasExistingEntry(Family family)
        {
            foreach (var entry in family.Entries)
            {
                if (entry.FallbackFor is null && FileExists(entry.Key.Path))
                {
                    return true;
                }
            }

            return false;
        }

        private bool FileExists(string path) => _fileExists.GetOrAdd(path, static p => File.Exists(p));

        /// <summary>
        /// The entries a family offers for a request: those without a <c>fallbackFor</c> target
        /// (or only those substituting for <paramref name="fallbackFor"/>), with existing files,
        /// restricted to the entries nearest an alias weight.
        /// </summary>
        private List<Entry> GetCandidates(Family family, int? aliasWeight, string? fallbackFor = null)
        {
            var candidates = new List<Entry>();

            foreach (var entry in family.Entries)
            {
                if (string.Equals(entry.FallbackFor, fallbackFor, StringComparison.OrdinalIgnoreCase) &&
                    FileExists(entry.Key.Path))
                {
                    candidates.Add(entry);
                }
            }

            if (aliasWeight is { } restrictTo && candidates.Count > 1)
            {
                var nearest = int.MaxValue;

                foreach (var entry in candidates)
                {
                    nearest = Math.Min(nearest, Math.Abs(entry.Weight - restrictTo));
                }

                candidates.RemoveAll(x => Math.Abs(x.Weight - restrictTo) != nearest);
            }

            return candidates;
        }

        private bool TrySelectEntry(Family family, int? aliasWeight, int weight, bool italic,
            [NotNullWhen(true)] out Entry? selected, string? fallbackFor = null)
        {
            selected = null;

            var bestScore = int.MaxValue;

            foreach (var entry in GetCandidates(family, aliasWeight, fallbackFor))
            {
                var score = GetDistance(entry.Weight, entry.IsItalic, weight, italic);

                // Strictly lower only: the entry listed first wins a tie, as on the platform.
                if (score < bestScore)
                {
                    bestScore = score;
                    selected = entry;
                }
            }

            return selected != null;
        }

        /// <summary>
        /// The platform's style distance: whole weight steps of 100 between the two weights, plus
        /// two steps when the slant differs.
        /// </summary>
        private static int GetDistance(int weight, bool italic, int requestedWeight, bool requestedItalic) =>
            Math.Abs(weight / 100 - requestedWeight / 100) + (italic != requestedItalic ? 2 : 0);

        private bool TryCreateFace(string familyName, Entry entry, [NotNullWhen(true)] out SystemFontFace? face)
        {
            // Designed properties come from the font's tables: entries of a variable font list the
            // instances the platform renders, while the face loads with its default instance and
            // the font system simulates the requested style on top of it.
            if (TryGetFaceInfo(entry.Key, out var info))
            {
                face = new SystemFontFace(familyName, info.Style, info.Weight, info.Stretch, entry.Key.Path,
                    entry.Key.Index, info.PostScriptName);

                return true;
            }

            face = new SystemFontFace(familyName, entry.IsItalic ? FontStyle.Italic : FontStyle.Normal,
                (FontWeight)entry.Weight, FontStretch.Normal, entry.Key.Path, entry.Key.Index);

            return true;
        }

        private bool TryMatchRealFamily(string familyName, int weight, bool italic,
            [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            if (!_realNames.Value.TryGetValue(familyName, out var keys))
            {
                return false;
            }

            FaceKey? best = null;
            var bestScore = int.MaxValue;

            foreach (var key in keys)
            {
                if (!TryGetFaceInfo(key, out var info))
                {
                    continue;
                }

                var score = GetDistance((int)info.Weight, info.Style != FontStyle.Normal, weight, italic);

                if (score < bestScore)
                {
                    bestScore = score;
                    best = key;
                }
            }

            return best is { } found && TryCreateRealFace(found, out match);
        }

        private bool TryCreateRealFace(FaceKey key, [NotNullWhen(true)] out SystemFontFace? face)
        {
            face = null;

            if (!TryGetFaceInfo(key, out var info))
            {
                return false;
            }

            face = new SystemFontFace(info.FamilyName, info.Style, info.Weight, info.Stretch, key.Path, key.Index,
                info.PostScriptName);

            return true;
        }

        /// <summary>
        /// Indexes every face the configuration names by the family names in its name table. Runs
        /// once, on the first request for a name the configuration does not declare; the faces of
        /// one file are read while they share a single mapping of it.
        /// </summary>
        private Dictionary<string, List<FaceKey>> CreateRealNameIndex()
        {
            var index = new Dictionary<string, List<FaceKey>>(StringComparer.OrdinalIgnoreCase);
            var byPath = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var paths = new List<string>();

            foreach (var key in GetIndexedFaces())
            {
                if (!byPath.TryGetValue(key.Path, out var indices))
                {
                    byPath.Add(key.Path, indices = new List<int>());
                    paths.Add(key.Path);
                }

                if (!indices.Contains(key.Index))
                {
                    indices.Add(key.Index);
                }
            }

            foreach (var path in paths)
            {
                if (!FileExists(path))
                {
                    continue;
                }

                // Faces of a collection are listed in face order, so a name shared by several
                // faces matches the first face that has it.
                byPath[path].Sort();

                var opened = new List<SfntFace>();

                try
                {
                    foreach (var faceIndex in byPath[path])
                    {
                        if (SfntFace.TryLoad(path, faceIndex, out var face))
                        {
                            opened.Add(face);

                            var key = new FaceKey(path, faceIndex);

                            if (!_faceInfos.TryGetValue(key, out var info))
                            {
                                info = _faceInfos.GetOrAdd(key, ReadFaceInfo(face, path));
                            }

                            foreach (var name in info.Names)
                            {
                                if (!index.TryGetValue(name, out var keys))
                                {
                                    index.Add(name, keys = new List<FaceKey>());
                                }

                                keys.Add(key);
                            }
                        }
                    }
                }
                finally
                {
                    foreach (var face in opened)
                    {
                        face.Dispose();
                    }
                }
            }

            return index;
        }

        private IEnumerable<FaceKey> GetIndexedFaces() => _allFaces;

        private bool TryGetFaceInfo(FaceKey key, [NotNullWhen(true)] out FaceInfo? info)
        {
            if (!_faceInfos.TryGetValue(key, out info))
            {
                if (!FileExists(key.Path) || !SfntFace.TryLoad(key.Path, key.Index, out var face))
                {
                    info = FaceInfo.Unreadable;
                }
                else
                {
                    using (face)
                    {
                        info = ReadFaceInfo(face, key.Path);
                    }
                }

                info = _faceInfos.GetOrAdd(key, info);
            }

            return info.IsReadable;
        }

        private static FaceInfo ReadFaceInfo(SfntFace face, string path)
        {
            try
            {
                if (!GlyphTypeface.TryGetDesignedProperties(face, out var style, out var weight, out var stretch))
                {
                    return FaceInfo.Unreadable;
                }

                var names = new List<string>();
                string? typographicFamily = null;
                string? family = null;
                string? postScriptName = null;
                var typographicIsEnglish = false;
                var familyIsEnglish = false;

                if (NameTable.Load(face) is { } nameTable)
                {
                    foreach (var record in nameTable)
                    {
                        // Macintosh records carry legacy encodings; Unicode and Windows records
                        // hold the same names reliably.
                        if (record.Platform is not (Avalonia.Media.Fonts.Tables.PlatformID.Unicode or
                            Avalonia.Media.Fonts.Tables.PlatformID.Windows))
                        {
                            continue;
                        }

                        if (record.NameID is not (KnownNameIds.FontFamilyName or KnownNameIds.TypographicFamilyName or
                            KnownNameIds.PostscriptName))
                        {
                            continue;
                        }

                        var value = record.GetValue();

                        if (string.IsNullOrWhiteSpace(value))
                        {
                            continue;
                        }

                        if (record.NameID == KnownNameIds.PostscriptName)
                        {
                            postScriptName ??= value;

                            continue;
                        }

                        if (!names.Exists(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)))
                        {
                            names.Add(value);
                        }

                        // The reported family name is the US English one where the font has it.
                        var isEnglish = record.LanguageID == UsEnglishLanguageId;

                        if (record.NameID == KnownNameIds.TypographicFamilyName)
                        {
                            if (typographicFamily is null || (isEnglish && !typographicIsEnglish))
                            {
                                typographicFamily = value;
                                typographicIsEnglish = isEnglish;
                            }
                        }
                        else if (family is null || (isEnglish && !familyIsEnglish))
                        {
                            family = value;
                            familyIsEnglish = isEnglish;
                        }
                    }
                }

                var familyName = typographicFamily ?? family ?? Path.GetFileNameWithoutExtension(path);

                return new FaceInfo(familyName, names.ToArray(), style, weight, stretch, postScriptName);
            }
            catch (Exception)
            {
                // Damaged tables: the face is matched by its configuration entry only.
                return FaceInfo.Unreadable;
            }
        }

        private static bool Covers(FaceKey key, int codepoint)
        {
            if (!SfntFace.TryLoad(key.Path, key.Index, out var face))
            {
                return false;
            }

            using (face)
            {
                try
                {
                    return CmapTable.Load(face).ContainsGlyph(codepoint);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        private readonly record struct FaceKey(string Path, int Index);

        private sealed class Entry
        {
            public Entry(FontsXmlFont font)
            {
                Key = new FaceKey(font.Path, font.Index);
                Weight = font.Weight;
                IsItalic = font.IsItalic;
                FallbackFor = font.FallbackFor;
            }

            public FaceKey Key { get; }

            public int Weight { get; }

            public bool IsItalic { get; }

            public string? FallbackFor { get; }
        }

        private sealed class Alias
        {
            public Alias(string name, Family target, int? weight)
            {
                Name = name;
                Target = target;
                Weight = weight;
            }

            public string Name { get; }

            public Family Target { get; }

            public int? Weight { get; }
        }

        private sealed class Family
        {
            private readonly LanguageTag[] _languages;
            private readonly Script[] _scripts;
            private readonly bool _isScriptOnly;

            public Family(FontsXmlFamily family)
            {
                Name = family.Name;
                Variant = family.Variant;
                Ignore = family.Ignore;

                var entries = new Entry[family.Fonts.Count];

                for (var i = 0; i < entries.Length; i++)
                {
                    entries[i] = new Entry(family.Fonts[i]);
                    HasSerifSubstitutes |= string.Equals(entries[i].FallbackFor, SerifFamilyName,
                        StringComparison.OrdinalIgnoreCase);
                }

                Entries = entries;

                _languages = new LanguageTag[family.Languages.Count];

                var scripts = new List<Script>();

                _isScriptOnly = _languages.Length > 0;

                for (var i = 0; i < _languages.Length; i++)
                {
                    var tag = LanguageTag.Parse(family.Languages[i]);

                    _languages[i] = tag;

                    if (tag.Script is "Zsye")
                    {
                        IsEmoji = true;
                    }

                    if (tag.Language != "und" || tag.Script is null)
                    {
                        _isScriptOnly = false;

                        continue;
                    }

                    scripts.Add(PropertyValueAliasHelper.GetScript(tag.Script));
                }

                _scripts = scripts.ToArray();
            }

            public string? Name { get; }

            public FontsXmlVariant Variant { get; }

            public bool Ignore { get; }

            public bool IsEmoji { get; }

            public bool HasSerifSubstitutes { get; }

            public IReadOnlyList<Entry> Entries { get; }

            /// <summary>
            /// Whether a family declared only for scripts can hold the codepoint: a codepoint of a
            /// specific script skips the families of other scripts without opening their fonts.
            /// Families declared for languages, and codepoints shared between scripts, always
            /// qualify; coverage decides.
            /// </summary>
            public bool MayCover(Codepoint codepoint, Script script)
            {
                if (!_isScriptOnly || script is Script.Common or Script.Inherited or Script.Unknown)
                {
                    return true;
                }

                foreach (var familyScript in _scripts)
                {
                    if (familyScript == script || (familyScript != Script.Unknown && codepoint.HasScriptExtension(familyScript)))
                    {
                        return true;
                    }
                }

                return false;
            }

            /// <summary>
            /// 2 when a declared language is the requested language with a compatible script, 0
            /// otherwise. A script mismatch disqualifies the language (zh-Hant fonts for a zh-CN
            /// request).
            /// </summary>
            public int MatchLanguage(LanguageTag requested)
            {
                if (requested.Language.Length == 0)
                {
                    return 0;
                }

                foreach (var language in _languages)
                {
                    if (language.Language == "und" ||
                        !string.Equals(language.Language, requested.Language, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (language.Script is null || requested.Script is null ||
                        string.Equals(language.Script, requested.Script, StringComparison.OrdinalIgnoreCase))
                    {
                        return 2;
                    }
                }

                return 0;
            }
        }

        private readonly struct LanguageTag
        {
            public LanguageTag(string name, string? script)
            {
                var separator = name.IndexOfAny(new[] { '-', '_' });

                Language = (separator < 0 ? name : name.Substring(0, separator)).ToLowerInvariant();
                Script = script;
            }

            private LanguageTag(string language, string? script, bool _)
            {
                Language = language;
                Script = script;
            }

            public string Language { get; }

            public string? Script { get; }

            public static LanguageTag Parse(string tag)
            {
                var parts = tag.Split('-', '_');
                var language = parts[0].ToLowerInvariant();
                string? script = null;

                for (var i = 1; i < parts.Length; i++)
                {
                    if (parts[i].Length == 4)
                    {
                        script = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1).ToLowerInvariant();

                        break;
                    }
                }

                return new LanguageTag(language, script, false);
            }
        }

        private sealed class FaceInfo
        {
            public static readonly FaceInfo Unreadable = new();

            public FaceInfo(string familyName, string[] names, FontStyle style, FontWeight weight, FontStretch stretch,
                string? postScriptName)
            {
                FamilyName = familyName;
                Names = names;
                Style = style;
                Weight = weight;
                Stretch = stretch;
                PostScriptName = postScriptName;
                IsReadable = true;
            }

            private FaceInfo()
            {
                FamilyName = string.Empty;
                Names = Array.Empty<string>();
            }

            public bool IsReadable { get; }

            public string FamilyName { get; }

            public string[] Names { get; }

            public FontStyle Style { get; }

            public FontWeight Weight { get; }

            public FontStretch Stretch { get; }

            public string? PostScriptName { get; }
        }
    }

    /// <summary>
    /// The configuration files a catalog reads: the first readable main file, then every readable
    /// addition.
    /// </summary>
    internal sealed class AndroidFontConfiguration
    {
        public AndroidFontConfiguration(IReadOnlyList<AndroidFontConfigurationFile> mainFiles,
            IReadOnlyList<AndroidFontConfigurationFile> additionFiles)
        {
            MainFiles = mainFiles;
            AdditionFiles = additionFiles;
        }

        public IReadOnlyList<AndroidFontConfigurationFile> MainFiles { get; }

        public IReadOnlyList<AndroidFontConfigurationFile> AdditionFiles { get; }
    }

    /// <summary>
    /// A configuration file and the directory its font file names resolve against.
    /// </summary>
    internal readonly record struct AndroidFontConfigurationFile(string Path, string FontDirectory);
}
