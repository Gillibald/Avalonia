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
    /// A family's entry is chosen in the order Avalonia's font collections match faces on every
    /// platform (the CSS font matching order): the requested slant first, then the weight, with
    /// file order breaking ties. Character fallback follows the platform: the requested family,
    /// the default family, then the fallback families ordered by emoji preference, language match
    /// and variant (compact before elegant), accepting a face only when its character map covers
    /// the codepoint. The model is immutable after construction;
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
        private readonly HashSet<FaceKey> _facesInUse = new();
        private readonly HashSet<FaceKey> _ignoredFaces = new();
        private readonly Family? _defaultFamily;
        private readonly ConcurrentDictionary<FaceKey, FaceInfo> _faceInfos = new();
        private readonly ConcurrentDictionary<string, bool> _fileExists = new(StringComparer.Ordinal);
        private readonly Lazy<IReadOnlyList<string>> _familyNames;
        private readonly Lazy<Dictionary<string, List<FaceKey>>> _realNames;
        private readonly Lazy<Dictionary<string, List<FaceKey>>> _platformNames;
        private readonly HashSet<FaceKey> _configuredFaces;
        private readonly ConcurrentDictionary<string, List<FaceKey>> _matchedNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly AndroidSystemFontSource? _systemFonts;
        private readonly ConcurrentDictionary<string, string> _languageTags = new(StringComparer.Ordinal);

        // Without a readable configuration, the families the platform resolves by name.
        private static readonly string[] s_platformGenericFamilies = { "sans-serif", "serif", "monospace" };

        private AndroidFontCatalog(FontsXmlConfig main, IReadOnlyList<FontsXmlConfig> additions,
            AndroidSystemFontSource? systemFonts, IReadOnlyList<AndroidSystemFont> platformFonts)
        {
            _systemFonts = systemFonts;

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

            // Without a configuration, the platform's fonts are reachable by their real names.
            foreach (var font in platformFonts)
            {
                var key = new FaceKey(font.Path, font.Index);

                if (seenFaces.Add(key))
                {
                    _allFaces.Add(key);
                }
            }

            IsPlatformOnly = main.Families.Count == 0 && systemFonts != null;

            _configuredFaces = seenFaces;
            _familyNames = new Lazy<IReadOnlyList<string>>(CreateFamilyNames, LazyThreadSafetyMode.ExecutionAndPublication);
            _realNames = new Lazy<Dictionary<string, List<FaceKey>>>(() => CreateNameIndex(_allFaces),
                LazyThreadSafetyMode.ExecutionAndPublication);
            _platformNames = new Lazy<Dictionary<string, List<FaceKey>>>(CreatePlatformNameIndex,
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        /// <summary>
        /// Whether no configuration file was readable and the platform's fonts form the model.
        /// </summary>
        public bool IsPlatformOnly { get; }

        /// <summary>
        /// Builds the catalog from the first readable main configuration file and every readable
        /// addition, layered with the platform's character fallback where the platform provides
        /// it (API 29 and later). The platform's font list is read up front only when no
        /// configuration is readable, and otherwise on the first family name neither the
        /// configuration nor its fonts resolve: enumerating makes the platform map every installed
        /// font file into the process for the rest of its life.
        /// </summary>
        public static AndroidFontCatalog Create(AndroidFontConfiguration configuration,
            AndroidSystemFontSource? systemFonts)
        {
            ReadConfiguration(configuration, out var main, out var additions);

            IReadOnlyList<AndroidSystemFont> platformFonts = Array.Empty<AndroidSystemFont>();

            if (systemFonts != null && main.Families.Count == 0)
            {
                try
                {
                    platformFonts = systemFonts.GetFonts();
                }
                catch (Exception)
                {
                    // Character fallback through the platform still works.
                }
            }

            return new AndroidFontCatalog(main, additions, systemFonts, platformFonts);
        }

        public static AndroidFontCatalog Create(AndroidFontConfiguration configuration) => Create(configuration, null);

        private static void ReadConfiguration(AndroidFontConfiguration configuration, out FontsXmlConfig main,
            out List<FontsXmlConfig> additions)
        {
            main = FontsXmlConfig.Empty;

            foreach (var file in configuration.MainFiles)
            {
                if (FontsXmlParser.TryParseFile(file.Path, file.FontDirectory, out var config) &&
                    config.Families.Count > 0)
                {
                    main = config;

                    break;
                }
            }

            additions = new List<FontsXmlConfig>();

            foreach (var file in configuration.AdditionFiles)
            {
                if (FontsXmlParser.TryParseFile(file.Path, file.FontDirectory, out var config))
                {
                    additions.Add(config);
                }
            }
        }

        /// <summary>
        /// Gets the configured family names and aliases that have at least one existing font
        /// file, each family followed by its aliases, then the real family names of the faces in
        /// use that no configured name or alias already lists, in configuration order. Generic
        /// names and aliases stay listed, since they are the names Android exposes to
        /// applications; other names of a face (localized names, the legacy family name of a
        /// single weight) resolve without being listed.
        /// </summary>
        public IReadOnlyList<string> GetFamilyNames() => _familyNames.Value;

        public bool TryGetDefaultFace([NotNullWhen(true)] out SystemFontFace? face)
        {
            face = null;

            if (IsPlatformOnly)
            {
                return TryMatchPlatformFamily(s_platformGenericFamilies[0], 400, false, out face);
            }

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

            if (IsPlatformGenericFamily(familyName))
            {
                return TryMatchPlatformFamily(familyName, (int)weight, italic, out match);
            }

            return TryMatchRealFamily(familyName, (int)weight, italic, out match);
        }

        private bool IsPlatformGenericFamily(string familyName) =>
            IsPlatformOnly && Array.Exists(s_platformGenericFamilies,
                x => string.Equals(x, familyName, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Resolves a generic family through the platform's matcher, which picks the face for a
        /// basic Latin letter at the requested style.
        /// </summary>
        private bool TryMatchPlatformFamily(string familyName, int weight, bool italic,
            [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            if (!TryMatchPlatform(familyName.ToLowerInvariant(), 'a', weight, italic, string.Empty, out var key) ||
                !TryGetFaceInfo(key, out var info))
            {
                return false;
            }

            match = new SystemFontFace(familyName.ToLowerInvariant(), info.Style, info.Weight, info.Stretch, key.Path,
                key.Index, info.PostScriptName);

            return true;
        }

        /// <summary>
        /// Asks the platform which face it uses for the codepoint and accepts the answer only when
        /// the face exists and covers the codepoint: the platform answers with its default face
        /// when nothing covers it.
        /// </summary>
        private bool TryMatchPlatform(string familyName, int codepoint, int weight, bool italic, string languageTags,
            out FaceKey key)
        {
            key = default;

            if (_systemFonts is null)
            {
                return false;
            }

            try
            {
                if (!_systemFonts.TryMatch(familyName, codepoint, weight, italic, languageTags, out var font))
                {
                    return false;
                }

                key = new FaceKey(font.Path, font.Index);
            }
            catch (Exception)
            {
                return false;
            }

            return FileExists(key.Path) && Covers(key, codepoint);
        }

        /// <summary>
        /// The culture as language tags for the platform's matcher, with the script subtag made
        /// explicit (ja-Jpan-JP, zh-Hant-TW), since the matcher steers Han by script.
        /// </summary>
        private string GetLanguageTags(CultureInfo culture)
        {
            var name = culture.Name;

            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            return _languageTags.GetOrAdd(name, static (n, c) =>
            {
                var script = Bcp47ScriptResolver.GetScriptSubtag(c);
                var parts = n.Split('-');

                if (script is null || parts.Length > 1 && parts[1].Length == 4)
                {
                    return n;
                }

                return parts.Length == 1 ? n + "-" + script : parts[0] + "-" + script + n.Substring(parts[0].Length);
            }, culture);
        }

        public bool TryGetFamilyFaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<SystemFontFace>? faces)
        {
            faces = null;

            if (string.IsNullOrEmpty(familyName))
            {
                return false;
            }

            var result = new List<SystemFontFace>();

            if (IsPlatformGenericFamily(familyName))
            {
                if (TryMatchPlatformFamily(familyName, 400, false, out var face))
                {
                    result.Add(face);
                }
            }
            else if (TryResolveFamily(familyName, out var family, out var aliasWeight))
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
            else if (TryGetRealFaces(familyName, true, out var keys))
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
            string? platformFamily = null;

            culture ??= CultureInfo.CurrentUICulture;

            if (!string.IsNullOrEmpty(familyName))
            {
                if (TryResolveFamily(familyName, out requested, out var aliasWeight))
                {
                    if (TrySelectEntry(requested, aliasWeight, requestedWeight, italic, out var entry) &&
                        Covers(entry.Key, codepoint) && TryCreateFace(requested.Name!, entry, out match))
                    {
                        return true;
                    }

                    platformFamily = requested.Name;
                }
                else if (IsPlatformGenericFamily(familyName))
                {
                    platformFamily = familyName.ToLowerInvariant();
                }
                else if (TryMatchRealFamily(familyName, requestedWeight, italic, out var realMatch, enumerate: false) &&
                         Covers(new FaceKey(realMatch.FilePath!, realMatch.FaceIndex), codepoint))
                {
                    match = realMatch;

                    return true;
                }
            }

            // The platform's own fallback (API 29+) knows the installed fonts best, including
            // updated ones; its answer is verified, and the configuration walk remains for the rest.
            if (_systemFonts != null &&
                TryMatchPlatform(platformFamily ?? s_platformGenericFamilies[0], codepoint, requestedWeight, italic,
                    GetLanguageTags(culture), out var platformKey) &&
                TryCreateRealFace(platformKey, out match))
            {
                return true;
            }

            if (_defaultFamily is { } defaultFamily && !ReferenceEquals(defaultFamily, requested) &&
                TrySelectEntry(defaultFamily, null, requestedWeight, italic, out var defaultEntry) &&
                Covers(defaultEntry.Key, codepoint) && TryCreateFace(defaultFamily.Name!, defaultEntry, out match))
            {
                return true;
            }

            return TryMatchFallback(codepoint, requestedWeight, italic,
                requested is not null && IsSerif(requested), culture, out match);
        }

        private bool TryMatchFallback(int codepoint, int weight, bool italic, bool serif, CultureInfo culture,
            [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            var codepointInfo = new Codepoint((uint)codepoint);
            var script = codepointInfo.Script;
            var isEmoji = HasEmojiPresentation(codepointInfo, codepoint);

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

        /// <summary>
        /// Whether the codepoint is drawn as emoji by default, so emoji fonts come first. Symbols
        /// that default to text presentation (U+2603 snowman, U+2764 heart) follow file order like
        /// any other codepoint. Pictographs from U+1F100 up and regional indicators count as emoji
        /// presentation, plus the default-emoji pictographs of the Basic Multilingual Plane.
        /// </summary>
        private static bool HasEmojiPresentation(Codepoint codepointInfo, int codepoint)
        {
            var breakClass = codepointInfo.GraphemeBreakClass;

            if (breakClass == GraphemeBreakClass.RegionalIndicator)
            {
                return true;
            }

            if (breakClass != GraphemeBreakClass.ExtendedPictographic)
            {
                return false;
            }

            if (codepoint >= 0x1F100 || codepoint is 0x1F004 or 0x1F0CF)
            {
                return true;
            }

            foreach (var (start, end) in s_bmpEmojiPresentation)
            {
                if (codepoint >= start && codepoint <= end)
                {
                    return true;
                }
            }

            return false;
        }

        // The Emoji_Presentation=Yes ranges of the Basic Multilingual Plane (Unicode emoji-data).
        private static readonly (int Start, int End)[] s_bmpEmojiPresentation =
        {
            (0x231A, 0x231B), (0x23E9, 0x23EC), (0x23F0, 0x23F0), (0x23F3, 0x23F3), (0x25FD, 0x25FE),
            (0x2614, 0x2615), (0x2648, 0x2653), (0x267F, 0x267F), (0x2693, 0x2693), (0x26A1, 0x26A1),
            (0x26AA, 0x26AB), (0x26BD, 0x26BE), (0x26C4, 0x26C5), (0x26CE, 0x26CE), (0x26D4, 0x26D4),
            (0x26EA, 0x26EA), (0x26F2, 0x26F3), (0x26F5, 0x26F5), (0x26FA, 0x26FA), (0x26FD, 0x26FD),
            (0x2705, 0x2705), (0x270A, 0x270B), (0x2728, 0x2728), (0x274C, 0x274C), (0x274E, 0x274E),
            (0x2753, 0x2755), (0x2757, 0x2757), (0x2795, 0x2797), (0x27B0, 0x27B0), (0x27BF, 0x27BF),
            (0x2B1B, 0x2B1C), (0x2B50, 0x2B50), (0x2B55, 0x2B55),
        };

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

                if (family.Ignore)
                {
                    _ignoredFaces.Add(entry.Key);
                }
                else
                {
                    _facesInUse.Add(entry.Key);
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

            if (IsPlatformOnly)
            {
                foreach (var genericFamily in s_platformGenericFamilies)
                {
                    if (TryMatchPlatformFamily(genericFamily, 400, false, out _))
                    {
                        names.Add(genericFamily);
                    }
                }

                // Without configured names, the real family names are the only ones there are.
                AddRealFamilyNames(names, inUseOnly: false);

                return names;
            }

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

            AddRealFamilyNames(names, inUseOnly: true);

            return names;
        }

        /// <summary>
        /// Appends the family name each face reports, once per name and only when the name
        /// resolves; faces only families marked ignored hold are skipped when
        /// <paramref name="inUseOnly"/> is set. Reads every face's name table on the first call.
        /// </summary>
        private void AddRealFamilyNames(List<string> names, bool inUseOnly)
        {
            var realNames = _realNames.Value;
            var seen = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

            foreach (var key in _allFaces)
            {
                if (inUseOnly && !_facesInUse.Contains(key))
                {
                    continue;
                }

                if (TryGetFaceInfo(key, out var info) && realNames.ContainsKey(info.FamilyName) &&
                    seen.Add(info.FamilyName))
                {
                    names.Add(info.FamilyName);
                }
            }
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
                var score = GetMatchRank(entry.Weight, entry.IsItalic, weight, italic);

                // Strictly lower only: the entry listed first wins a tie.
                if (score < bestScore)
                {
                    bestScore = score;
                    selected = entry;
                }
            }

            return selected != null;
        }

        /// <summary>
        /// The rank of a face for a requested style in the font collections' matching order, lower
        /// first: any face with the requested slant before every other face; then, for a weight
        /// above 500, heavier weights ascending before lighter ones descending; below 400, lighter
        /// weights descending before heavier ones ascending; from 400 to 500, the weights up to
        /// 500 ascending, then lighter ones descending, then those above 500 ascending.
        /// </summary>
        private static int GetMatchRank(int weight, bool italic, int requestedWeight, bool requestedItalic)
        {
            const int Group = 1_000;

            var slant = italic == requestedItalic ? 0 : 3 * Group;
            var difference = weight - requestedWeight;

            if (requestedWeight > 500)
            {
                return slant + (difference >= 0 ? difference : Group - difference);
            }

            if (requestedWeight < 400)
            {
                return slant + (difference <= 0 ? -difference : Group + difference);
            }

            if (difference >= 0 && weight <= 500)
            {
                return slant + difference;
            }

            return slant + (difference < 0 ? Group - difference : 2 * Group + difference);
        }

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
            [NotNullWhen(true)] out SystemFontFace? match, bool enumerate = true)
        {
            match = null;

            if (!TryGetRealFaces(familyName, enumerate, out var keys))
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

                var score = GetMatchRank((int)info.Weight, info.Style != FontStyle.Normal, weight, italic);

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
        /// The faces carrying a real family name: those of the configuration first, then a face
        /// the platform's matcher resolves the name to, then the platform's fonts outside the
        /// configuration. The font list is read only on a family request the matcher answers with
        /// its default face, since reading it makes the platform map every installed font; character
        /// fallback, which receives placeholder names such as <c>$Default</c> for most text, uses
        /// only answers already found.
        /// </summary>
        private bool TryGetRealFaces(string familyName, bool enumerate, [NotNullWhen(true)] out List<FaceKey>? keys)
        {
            if (_realNames.Value.TryGetValue(familyName, out keys))
            {
                return true;
            }

            if (_matchedNames.TryGetValue(familyName, out keys))
            {
                return true;
            }

            if (_platformNames.IsValueCreated)
            {
                return _platformNames.Value.TryGetValue(familyName, out keys);
            }

            if (!enumerate)
            {
                return false;
            }

            if (TryMatchPlatformName(familyName, out var key))
            {
                keys = _matchedNames.GetOrAdd(familyName, new List<FaceKey> { key });

                return true;
            }

            return _platformNames.Value.TryGetValue(familyName, out keys);
        }

        /// <summary>
        /// Asks the platform's matcher for a family name. The matcher answers names it does not know
        /// with its default face, so an answer counts only when it carries the requested name or
        /// differs from the default face (a platform family such as an updated font's family).
        /// </summary>
        private bool TryMatchPlatformName(string familyName, out FaceKey key)
        {
            key = default;

            if (_systemFonts is null || IsPlatformOnly ||
                !TryMatchPlatform(familyName, 'a', 400, false, string.Empty, out key) ||
                !TryGetFaceInfo(key, out var info))
            {
                return false;
            }

            if (Array.Exists(info.Names, x => string.Equals(x, familyName, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return !TryMatchPlatform(s_platformGenericFamilies[0], 'a', 400, false, string.Empty, out var defaultKey) ||
                   defaultKey != key;
        }

        /// <summary>
        /// Indexes the platform's fonts the configuration does not name (updated fonts such as
        /// Google Sans under <c>/data/fonts/files</c>) by their real family names. The platform
        /// list is read once; a failure leaves the index empty.
        /// </summary>
        private Dictionary<string, List<FaceKey>> CreatePlatformNameIndex()
        {
            if (_systemFonts is null || IsPlatformOnly)
            {
                return new Dictionary<string, List<FaceKey>>(StringComparer.OrdinalIgnoreCase);
            }

            var faces = new List<FaceKey>();
            var seen = new HashSet<FaceKey>(_configuredFaces);

            try
            {
                foreach (var font in _systemFonts.GetFonts())
                {
                    var key = new FaceKey(font.Path, font.Index);

                    if (seen.Add(key))
                    {
                        faces.Add(key);
                    }
                }
            }
            catch (Exception)
            {
                faces.Clear();
            }

            return CreateNameIndex(faces);
        }

        /// <summary>
        /// Indexes faces by the family names in their name tables; the faces of one file are read
        /// while they share a single mapping of it, which is released afterwards.
        /// </summary>
        private Dictionary<string, List<FaceKey>> CreateNameIndex(IEnumerable<FaceKey> faces)
        {
            var index = new Dictionary<string, List<FaceKey>>(StringComparer.OrdinalIgnoreCase);
            var byPath = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var paths = new List<string>();

            foreach (var key in faces)
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

            // Legacy fonts stay on devices under the name of their successor in a family marked
            // ignored; a lookup by that name resolves to the face in use.
            foreach (var keys in index.Values)
            {
                if (keys.Count > 1)
                {
                    var inUse = keys.FindAll(IsInUse);

                    inUse.AddRange(keys.FindAll(x => !IsInUse(x)));
                    keys.Clear();
                    keys.AddRange(inUse);
                }
            }

            return index;
        }

        private bool IsInUse(FaceKey key) => !_ignoredFaces.Contains(key) || _facesInUse.Contains(key);

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

                    // Emoji and symbol families hold no script of their own; they qualify only for
                    // codepoints shared between scripts.
                    if (tag.Script is not ("Zsye" or "Zsym"))
                    {
                        scripts.Add(PropertyValueAliasHelper.GetScript(tag.Script));
                    }
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
                    // Variant subtags (Syre, Syrj, Syrn for Syriac) are no Unicode script codes;
                    // such a family cannot be ruled out without its fonts.
                    if (familyScript == Script.Unknown || familyScript == script ||
                        codepoint.HasScriptExtension(familyScript))
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
