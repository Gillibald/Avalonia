using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Platform;

namespace Avalonia.Media.Fonts
{
    /// <summary>
    /// System font provider over fontconfig, for Linux and BSD systems. Fonts are enumerated and
    /// matched through libfontconfig and returned as descriptors; the font system loads the files
    /// through the managed loader and applies its own simulation policy.
    /// </summary>
    public sealed class FontconfigFontProvider : ISystemFontProvider
    {
        private const string SansSerif = "sans-serif";

        // Generic families fontconfig configurations alias and expand. A request for one of them
        // always resolves; for concrete families they mark the point in a substituted pattern's
        // family list where the configuration's generic fallback expansion begins.
        private static readonly string[] s_genericFamilies =
        {
            "sans-serif", "serif", "monospace", "system-ui", "ui-monospace", "cursive", "fantasy",
            "emoji", "math",
        };

        // False after the first call proves fontconfig < 2.12.5, so the missing entry point does
        // not throw on every match.
        private static bool s_hasBindingQuery = true;

        private readonly object _lock = new();
        private IntPtr _config;
        private bool _initialized;
        private bool _disposed;
        private LangCache? _langCache;

        // The fvar data of each variable font file face, read on first use; null for faces
        // without a readable fvar table.
        private readonly ConcurrentDictionary<(string File, int Index), VariableFace?> _variableFaces = new();

        /// <summary>
        /// Initializes fontconfig lazily on first use: constructing (and registering) the provider
        /// does no native work, and a missing libfontconfig turns every query into a miss instead
        /// of an error.
        /// </summary>
        private bool TryGetConfig(out IntPtr config)
        {
            lock (_lock)
            {
                if (!_initialized)
                {
                    _initialized = true;

                    try
                    {
                        _config = FcNative.FcInitLoadConfigAndFonts();
                    }
                    catch (DllNotFoundException)
                    {
                        _config = IntPtr.Zero;
                    }
                    catch (EntryPointNotFoundException)
                    {
                        _config = IntPtr.Zero;
                    }
                }

                config = _config;

                return !_disposed && config != IntPtr.Zero;
            }
        }

        public bool TryGetDefaultFontFace([NotNullWhen(true)] out SystemFontFace? face)
        {
            // The "sans-serif" alias resolves through the user's fontconfig configuration to the
            // distribution's default UI font (DejaVu Sans, Noto Sans, Cantarell, Ubuntu, ...).
            return TryMatch(SansSerif, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                codepoint: 0, culture: null, out face);
        }

        public IReadOnlyList<string> GetFontFamilyNames()
        {
            if (!TryGetConfig(out var config))
            {
                return Array.Empty<string>();
            }

            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pattern = FcNative.FcPatternCreate();
            var objectSet = FcNative.FcObjectSetCreate();

            try
            {
                FcNative.FcObjectSetAdd(objectSet, FcNative.Family);

                var fontSet = FcNative.FcFontList(config, pattern, objectSet);

                if (fontSet == IntPtr.Zero)
                {
                    return names;
                }

                try
                {
                    foreach (var font in FcNative.GetFontSetPatterns(fontSet))
                    {
                        // Every value of the family element counts: fontconfig stores localized
                        // family names as additional values, and localized lookup depends on them.
                        foreach (var name in FcNative.GetStrings(font, FcNative.Family))
                        {
                            if (!string.IsNullOrEmpty(name) && seen.Add(name))
                            {
                                names.Add(name);
                            }
                        }
                    }
                }
                finally
                {
                    FcNative.FcFontSetDestroy(fontSet);
                }
            }
            finally
            {
                FcNative.FcObjectSetDestroy(objectSet);
                FcNative.FcPatternDestroy(pattern);
            }

            return names;
        }

        public bool TryMatchFamily(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
            [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            if (string.IsNullOrEmpty(familyName))
            {
                return false;
            }

            return TryMatch(familyName, style, weight, stretch, codepoint: 0, culture: null, out match);
        }

        public bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch,
            string? familyName, CultureInfo? culture, [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            if (codepoint <= 0)
            {
                return false;
            }

            return TryMatch(string.IsNullOrEmpty(familyName) ? null : familyName, style, weight, stretch,
                codepoint, culture, out match);
        }

        public bool TryGetFamilyFaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<SystemFontFace>? faces)
        {
            faces = null;

            if (string.IsNullOrEmpty(familyName) || !TryGetConfig(out var config))
            {
                return false;
            }

            var result = new List<SystemFontFace>();
            var pattern = FcNative.FcPatternCreate();
            var objectSet = FcNative.FcObjectSetCreate();

            try
            {
                FcNative.FcPatternAddString(pattern, FcNative.Family, familyName);

                FcNative.FcObjectSetAdd(objectSet, FcNative.Family);
                FcNative.FcObjectSetAdd(objectSet, FcNative.Slant);
                FcNative.FcObjectSetAdd(objectSet, FcNative.Weight);
                FcNative.FcObjectSetAdd(objectSet, FcNative.Width);
                FcNative.FcObjectSetAdd(objectSet, FcNative.File);
                FcNative.FcObjectSetAdd(objectSet, FcNative.Index);
                FcNative.FcObjectSetAdd(objectSet, FcNative.PostScriptName);
                FcNative.FcObjectSetAdd(objectSet, FcNative.Variable);

                var fontSet = FcNative.FcFontList(config, pattern, objectSet);

                if (fontSet == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    var patterns = FcNative.GetFontSetPatterns(fontSet);

                    // Fontconfig lists a variable font face once per named instance (the instance
                    // at the default position under the plain face index) and once more as the
                    // variable font itself, whose weight, width and slant are ranges instead of
                    // one style. That pattern would report a fallback style colliding with the
                    // default instance's, so it is listed only for a face without any other
                    // pattern, a variable font without named instances.
                    var variableFaces = new HashSet<(string, int)>();
                    var instanceFaces = new HashSet<(string, int)>();

                    foreach (var font in patterns)
                    {
                        if (TryGetFaceKey(font, out var key, out var isVariable))
                        {
                            (isVariable ? variableFaces : instanceFaces).Add(key);
                        }
                    }

                    foreach (var font in patterns)
                    {
                        if (!TryGetFaceKey(font, out var key, out var isVariable) ||
                            (isVariable && instanceFaces.Contains(key)))
                        {
                            continue;
                        }

                        // Every named instance of a variable font is a face of its own; the font
                        // system loads each file face once and moves it to the instances.
                        if (CreateFontFace(font, variableFaces.Contains(key)) is { } face)
                        {
                            result.Add(face);
                        }
                    }
                }
                finally
                {
                    FcNative.FcFontSetDestroy(fontSet);
                }
            }
            finally
            {
                FcNative.FcObjectSetDestroy(objectSet);
                FcNative.FcPatternDestroy(pattern);
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
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                if (_config != IntPtr.Zero)
                {
                    FcNative.FcConfigDestroy(_config);
                    _config = IntPtr.Zero;
                }
            }
        }

        private bool TryMatch(string? familyName, FontStyle style, FontWeight weight, FontStretch stretch,
            int codepoint, CultureInfo? culture, [NotNullWhen(true)] out SystemFontFace? match)
        {
            match = null;

            if (!TryGetConfig(out var config))
            {
                return false;
            }

            var pattern = FcNative.FcPatternCreate();
            var charSet = IntPtr.Zero;

            try
            {
                if (familyName != null)
                {
                    FcNative.FcPatternAddString(pattern, FcNative.Family, familyName);
                }

                FcNative.FcPatternAddInteger(pattern, FcNative.Slant, FcMapping.SlantFromFontStyle(style));
                FcNative.FcPatternAddInteger(pattern, FcNative.Weight, FcMapping.WeightFromOpenType((int)weight));
                FcNative.FcPatternAddInteger(pattern, FcNative.Width, FcMapping.WidthFromFontStretch(stretch));

                if (codepoint > 0)
                {
                    charSet = FcNative.FcCharSetCreate();
                    FcNative.FcCharSetAddChar(charSet, (uint)codepoint);
                    // The charset is copied into the pattern; ours is destroyed below.
                    FcNative.FcPatternAddCharSet(pattern, FcNative.Charset, charSet);

                    if (!string.IsNullOrEmpty(culture?.Name))
                    {
                        FcNative.FcPatternAddString(pattern, FcNative.Lang, GetLang(culture!.Name));
                    }
                }

                FcNative.FcConfigSubstitute(config, pattern, FcNative.FcMatchPattern);
                FcNative.FcDefaultSubstitute(pattern);

                var matched = FcNative.FcFontMatch(config, pattern, out _);

                if (matched == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    if (codepoint > 0)
                    {
                        // Character matches must have real coverage; the matcher may fall back to
                        // a font that cannot display the codepoint.
                        if (FcNative.FcPatternGetCharSet(matched, FcNative.Charset, 0, out var matchedCharSet) != FcNative.FcResult.Match ||
                            FcNative.FcCharSetHasChar(matchedCharSet, (uint)codepoint) == 0)
                        {
                            return false;
                        }
                    }
                    else if (familyName != null && !IsGenericFamily(familyName) &&
                             !MatchesRequestedFamily(matched, pattern, familyName))
                    {
                        return false;
                    }

                    match = CreateFontFace(matched, listedVariableFace: false);

                    return match != null;
                }
                finally
                {
                    FcNative.FcPatternDestroy(matched);
                }
            }
            finally
            {
                if (charSet != IntPtr.Zero)
                {
                    FcNative.FcCharSetDestroy(charSet);
                }

                FcNative.FcPatternDestroy(pattern);
            }
        }

        /// <summary>
        /// Returns the lowercased fontconfig lang tag for a culture name, cached because layout
        /// asks for the same culture over and over.
        /// </summary>
        private string GetLang(string cultureName)
        {
            var cache = _langCache;

            if (cache is null || !string.Equals(cache.CultureName, cultureName, StringComparison.Ordinal))
            {
                _langCache = cache = new LangCache(cultureName, cultureName.ToLowerInvariant());
            }

            return cache.Lang;
        }

        private static bool IsGenericFamily(string familyName)
        {
            foreach (var genericFamily in s_genericFamilies)
            {
                if (string.Equals(familyName, genericFamily, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// A family match must resolve to a requested family, because FcFontMatch never fails - it
        /// falls back to some font for entirely unknown families. After substitution only the
        /// non-weak family values of the pattern count as requested: the request itself and its
        /// aliases are strong/same-bound, while configurations append their fallback expansions
        /// weakly bound (Ubuntu's language-selector appends dozens of concrete families to every
        /// pattern). The values are compared natively with fontconfig's own case folding, so
        /// verification materializes no managed strings.
        /// </summary>
        private static bool MatchesRequestedFamily(IntPtr matched, IntPtr pattern, string requestedFamilyName)
        {
            if (s_hasBindingQuery)
            {
                try
                {
                    var sawStrong = false;

                    for (var id = 0; ; id++)
                    {
                        var result = FcNative.FcPatternGetWithBinding(pattern, FcNative.Family, id, out var value, out var binding);

                        if (result != FcNative.FcResult.Match)
                        {
                            break;
                        }

                        if (binding == FcNative.FcValueBindingWeak ||
                            value.Type != FcNative.FcTypeString ||
                            value.Value == IntPtr.Zero)
                        {
                            continue;
                        }

                        sawStrong = true;

                        if (MatchedFamiliesContain(matched, value.Value))
                        {
                            return true;
                        }
                    }

                    if (sawStrong)
                    {
                        return false;
                    }
                }
                catch (EntryPointNotFoundException)
                {
                    s_hasBindingQuery = false;
                }
            }

            // fontconfig < 2.12.5 has no binding query (or the pattern carried no strong family,
            // which cannot happen for our own patterns); verify against the literal request only.
            foreach (var matchedFamily in FcNative.GetStrings(matched, FcNative.Family))
            {
                if (string.Equals(matchedFamily, requestedFamilyName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool MatchedFamiliesContain(IntPtr matched, IntPtr requestedValue)
        {
            for (var n = 0; FcNative.FcPatternGetString(matched, FcNative.Family, n, out var matchedValue) == FcNative.FcResult.Match; n++)
            {
                if (matchedValue != IntPtr.Zero && FcNative.FcStrCmpIgnoreCase(matchedValue, requestedValue) == 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Reads the font file face a pattern belongs to, and whether it is the pattern of the
        /// variable font itself rather than one of its instances.
        /// </summary>
        private static bool TryGetFaceKey(IntPtr pattern, out (string File, int Index) key, out bool isVariable)
        {
            var file = FcNative.GetString(pattern, FcNative.File, 0);
            var rawIndex = FcNative.GetInteger(pattern, FcNative.Index) ?? 0;

            key = (file ?? string.Empty, rawIndex & 0xFFFF);
            isVariable = rawIndex >> 16 == 0 && FcNative.GetBool(pattern, FcNative.Variable) == true;

            return !string.IsNullOrEmpty(file);
        }

        /// <param name="pattern">A matched or listed pattern.</param>
        /// <param name="listedVariableFace">
        /// Whether the pattern was listed among the patterns of a variable font face. A face at
        /// the default position then reports the default coordinates as its axis values, and the
        /// variable font's own pattern reports the style of its default instance.
        /// </param>
        private SystemFontFace? CreateFontFace(IntPtr pattern, bool listedVariableFace)
        {
            var file = FcNative.GetString(pattern, FcNative.File, 0);
            var family = FcNative.GetString(pattern, FcNative.Family, 0);

            if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(family))
            {
                return null;
            }

            // The low 16 bits of the index select the face of a font collection; the high bits
            // hold the one-based number of a variable font's named instance, zero for the font
            // itself at its default position.
            var rawIndex = FcNative.GetInteger(pattern, FcNative.Index) ?? 0;
            var index = rawIndex & 0xFFFF;
            var instance = (rawIndex >> 16) - 1;
            var isVariable = instance < 0 && FcNative.GetBool(pattern, FcNative.Variable) == true;

            // The variable font's own pattern carries ranges, which do not read as integers; a
            // match resolves them to values, a listed pattern keeps them.
            var slant = FcNative.GetInteger(pattern, FcNative.Slant);
            var weight = FcNative.GetInteger(pattern, FcNative.Weight);
            var width = FcNative.GetInteger(pattern, FcNative.Width);

            var style = FcMapping.SlantToFontStyle(slant ?? 0);
            var fontWeight = (FontWeight)FcMapping.WeightToOpenType(weight ?? 80);
            var stretch = FcMapping.WidthToFontStretch(width ?? 100);

            IReadOnlyDictionary<OpenTypeTag, float>? axisValues = null;

            if (instance >= 0)
            {
                axisValues = GetNamedInstance(file!, index, instance);
            }
            else if ((isVariable || listedVariableFace) && GetVariableFace(file!, index) is { } variableFace)
            {
                if (listedVariableFace)
                {
                    axisValues = variableFace.DefaultCoordinates;
                }

                if (isVariable)
                {
                    var (defaultStyle, defaultWeight, defaultStretch) =
                        GetDefaultInstanceStyle(variableFace.DefaultCoordinates);

                    if (slant is null || listedVariableFace)
                    {
                        style = defaultStyle;
                    }

                    if (weight is null || listedVariableFace)
                    {
                        fontWeight = defaultWeight;
                    }

                    if (width is null || listedVariableFace)
                    {
                        stretch = defaultStretch;
                    }
                }
            }

            var postScriptName = FcNative.GetString(pattern, FcNative.PostScriptName, 0);

            return new SystemFontFace(family!, style, fontWeight, stretch, file!, index, postScriptName, axisValues);
        }

        private IReadOnlyDictionary<OpenTypeTag, float>? GetNamedInstance(string file, int index, int instance)
        {
            var instances = GetVariableFace(file, index)?.NamedInstances;

            return instances is not null && instance < instances.Length ? instances[instance] : null;
        }

        private VariableFace? GetVariableFace(string file, int index)
            => _variableFaces.GetOrAdd((file, index), static key => ReadVariableFace(key.File, key.Index));

        private static VariableFace? ReadVariableFace(string file, int index)
        {
            // The descriptor opens the file through the managed loader; only its name is unused.
            var face = new SystemFontFace(string.Empty, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                file, index);

            if (!face.TryOpenFontMemory(out var fontMemory))
            {
                return null;
            }

            using (fontMemory)
            {
                if (!fontMemory.TryGetTable(OpenTypeTag.Parse("fvar"), out var fvar) ||
                    ReadDefaultCoordinates(fvar.Span) is not { } defaultCoordinates ||
                    ReadNamedInstances(fvar.Span) is not { } namedInstances)
                {
                    return null;
                }

                return new VariableFace(defaultCoordinates, namedInstances);
            }
        }

        /// <summary>
        /// Gets the designed properties a variable font has at the specified user-space
        /// coordinates: <c>wght</c> is the weight, <c>wdth</c> (a percentage of the normal width)
        /// the stretch, a non-zero <c>ital</c> italic and a non-zero <c>slnt</c> oblique. A
        /// missing axis maps to the Regular value.
        /// </summary>
        internal static (FontStyle Style, FontWeight Weight, FontStretch Stretch) GetDefaultInstanceStyle(
            IReadOnlyDictionary<OpenTypeTag, float> coordinates)
        {
            var weight = coordinates.TryGetValue(OpenTypeTag.Parse("wght"), out var wght)
                ? (FontWeight)Math.Clamp((int)Math.Round(wght), 1, 1000)
                : FontWeight.Normal;

            // Fontconfig's width scale is the same percentage of the normal width as wdth.
            var stretch = coordinates.TryGetValue(OpenTypeTag.Parse("wdth"), out var wdth)
                ? FcMapping.WidthToFontStretch((int)Math.Round(wdth))
                : FontStretch.Normal;

            var style = FontStyle.Normal;

            if (coordinates.TryGetValue(OpenTypeTag.Parse("ital"), out var ital) && ital >= 0.5f)
            {
                style = FontStyle.Italic;
            }
            else if (coordinates.TryGetValue(OpenTypeTag.Parse("slnt"), out var slnt) && slnt != 0)
            {
                style = FontStyle.Oblique;
            }

            return (style, weight, stretch);
        }

        /// <summary>
        /// Reads the user-space default value of every axis from an <c>fvar</c> table;
        /// <see langword="null"/> when the table is malformed.
        /// </summary>
        internal static IReadOnlyDictionary<OpenTypeTag, float>? ReadDefaultCoordinates(ReadOnlySpan<byte> fvar)
        {
            const int HeaderSize = 16;
            const int AxisRecordSize = 20;

            if (fvar.Length < HeaderSize)
            {
                return null;
            }

            int axesOffset = BinaryPrimitives.ReadUInt16BigEndian(fvar.Slice(4));
            int axisCount = BinaryPrimitives.ReadUInt16BigEndian(fvar.Slice(8));
            int axisSize = BinaryPrimitives.ReadUInt16BigEndian(fvar.Slice(10));

            if (axisSize < AxisRecordSize || axesOffset + axisCount * axisSize > fvar.Length)
            {
                return null;
            }

            var coordinates = new Dictionary<OpenTypeTag, float>(axisCount);

            for (var i = 0; i < axisCount; i++)
            {
                // Axis record: tag, then minValue, defaultValue and maxValue as 16.16 fixed.
                var record = fvar.Slice(axesOffset + i * axisSize);

                coordinates[new OpenTypeTag(BinaryPrimitives.ReadUInt32BigEndian(record))] =
                    BinaryPrimitives.ReadInt32BigEndian(record.Slice(8)) / 65536f;
            }

            return coordinates;
        }

        /// <summary>
        /// Reads the user-space coordinates of every named instance from an <c>fvar</c> table,
        /// in instance order; <see langword="null"/> when the table is malformed.
        /// </summary>
        internal static IReadOnlyDictionary<OpenTypeTag, float>[]? ReadNamedInstances(ReadOnlySpan<byte> fvar)
        {
            const int HeaderSize = 16;
            const int AxisRecordSize = 20;

            if (fvar.Length < HeaderSize)
            {
                return null;
            }

            int axesOffset = BinaryPrimitives.ReadUInt16BigEndian(fvar.Slice(4));
            int axisCount = BinaryPrimitives.ReadUInt16BigEndian(fvar.Slice(8));
            int axisSize = BinaryPrimitives.ReadUInt16BigEndian(fvar.Slice(10));
            int instanceCount = BinaryPrimitives.ReadUInt16BigEndian(fvar.Slice(12));
            int instanceSize = BinaryPrimitives.ReadUInt16BigEndian(fvar.Slice(14));

            var instancesOffset = axesOffset + axisCount * axisSize;

            if (axisSize < AxisRecordSize || instanceSize < 4 + 4 * axisCount ||
                instancesOffset + instanceCount * instanceSize > fvar.Length)
            {
                return null;
            }

            var tags = new OpenTypeTag[axisCount];

            for (var i = 0; i < axisCount; i++)
            {
                tags[i] = new OpenTypeTag(BinaryPrimitives.ReadUInt32BigEndian(fvar.Slice(axesOffset + i * axisSize)));
            }

            var instances = new IReadOnlyDictionary<OpenTypeTag, float>[instanceCount];

            for (var i = 0; i < instanceCount; i++)
            {
                // subfamilyNameID and flags precede the coordinates, which are 16.16 fixed.
                var record = fvar.Slice(instancesOffset + i * instanceSize + 4);
                var coordinates = new Dictionary<OpenTypeTag, float>(axisCount);

                for (var j = 0; j < axisCount; j++)
                {
                    coordinates[tags[j]] = BinaryPrimitives.ReadInt32BigEndian(record.Slice(4 * j)) / 65536f;
                }

                instances[i] = coordinates;
            }

            return instances;
        }

        private sealed class VariableFace
        {
            public VariableFace(IReadOnlyDictionary<OpenTypeTag, float> defaultCoordinates,
                IReadOnlyDictionary<OpenTypeTag, float>[] namedInstances)
            {
                DefaultCoordinates = defaultCoordinates;
                NamedInstances = namedInstances;
            }

            public IReadOnlyDictionary<OpenTypeTag, float> DefaultCoordinates { get; }

            public IReadOnlyDictionary<OpenTypeTag, float>[] NamedInstances { get; }
        }

        private sealed class LangCache
        {
            public LangCache(string cultureName, string lang)
            {
                CultureName = cultureName;
                Lang = lang;
            }

            public string CultureName { get; }

            public string Lang { get; }
        }
    }
}
