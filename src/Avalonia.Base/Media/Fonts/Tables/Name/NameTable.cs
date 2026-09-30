// Copyright (c) Six Labors.
// Licensed under the Apache License, Version 2.0.
// Ported from: https://github.com/SixLabors/Fonts/blob/034a440aece357341fcc6b02db58ffbe153e54ef/src/SixLabors.Fonts

using System;
using System.Collections;
using System.Collections.Generic;
using Avalonia.Utilities;

namespace Avalonia.Media.Fonts.Tables.Name
{
    internal class NameTable : IEnumerable<NameRecord>
    {
        internal const string TableName = "name";
        internal static readonly OpenTypeTag Tag = OpenTypeTag.Parse(TableName);

        private const ushort USEnglishLanguageId = 0x0409;
        private const ushort InvariantLanguageId = 0x007F;
        private const ushort PrimaryLanguageMask = 0x03FF;
        private const ushort EnglishPrimaryLanguageId = 0x0009;
        private const ushort MacEnglishLanguageId = 0;

        private readonly NameRecord[] _names;

        internal NameTable(NameRecord[] names)
        {
            _names = names;
        }

        /// <summary>
        /// Gets the name of the font.
        /// </summary>
        /// <value>
        /// The name of the font.
        /// </value>
        public string Id(ushort culture)
            => GetNameById(culture, KnownNameIds.UniqueFontID);

        /// <summary>
        /// Gets the name of the font.
        /// </summary>
        /// <value>
        /// The name of the font.
        /// </value>
        public string FontName(ushort culture)
            => GetNameById(culture, KnownNameIds.FullFontName);

        /// <summary>
        /// Gets the name of the font.
        /// </summary>
        /// <value>
        /// The name of the font.
        /// </value>
        public string FontFamilyName(ushort culture)
            => GetNameById(culture, KnownNameIds.FontFamilyName);

        /// <summary>
        /// Gets the name of the font.
        /// </summary>
        /// <value>
        /// The name of the font.
        /// </value>
        public string FontSubFamilyName(ushort culture)
            => GetNameById(culture, KnownNameIds.FontSubfamilyName);

        /// <summary>
        /// Gets the name with the given id, preferring the Windows record for <paramref name="culture"/>.
        /// </summary>
        /// <remarks>
        /// Without an exact match, and always for the invariant culture, an English name is preferred:
        /// US English, then any other English locale, then the Mac Roman English record, then a Unicode
        /// platform record. Only when none of those exist does the first Windows record, then the first
        /// record of any platform, win. Records are sorted by language ID, so taking the first Windows
        /// record straight away would name a font with Chinese or Japanese records after those.
        /// </remarks>
        public string GetNameById(ushort culture, KnownNameIds nameId)
        {
            var hasExactLanguage = culture != InvariantLanguageId;
            NameRecord? usEnglish = null;
            NameRecord? otherEnglish = null;
            NameRecord? macEnglish = null;
            NameRecord? unicode = null;
            NameRecord? firstWindows = null;
            NameRecord? first = null;

            foreach (var name in _names)
            {
                if (name.NameID != nameId)
                {
                    continue;
                }

                first ??= name;

                switch (name.Platform)
                {
                    case PlatformID.Windows:
                    {
                        if (hasExactLanguage && name.LanguageID == culture)
                        {
                            return name.GetValue();
                        }

                        firstWindows ??= name;

                        if (name.LanguageID == USEnglishLanguageId)
                        {
                            usEnglish ??= name;
                        }
                        else if ((name.LanguageID & PrimaryLanguageMask) == EnglishPrimaryLanguageId)
                        {
                            otherEnglish ??= name;
                        }

                        break;
                    }
                    case PlatformID.Macintosh when name.LanguageID == MacEnglishLanguageId:
                        macEnglish ??= name;
                        break;
                    case PlatformID.Unicode:
                        unicode ??= name;
                        break;
                }
            }

            var match = usEnglish ?? otherEnglish ?? macEnglish ?? unicode ?? firstWindows ?? first;

            return match?.GetValue() ?? string.Empty;
        }

        public string GetNameById(ushort culture, ushort nameId)
            => GetNameById(culture, (KnownNameIds)nameId);

        public static NameTable? Load(GlyphTypeface glyphTypeface)
        {
            if (!glyphTypeface.PlatformTypeface.TryGetTable(Tag, out var table))
            {
                return null;
            }

            try
            {
                var reader = new BigEndianBinaryReader(table.Span);

                reader.ReadUInt16();
                var count = reader.ReadUInt16();
                var storageOffset = reader.ReadUInt16();

                const int headerSize = 6;
                const int recordSize = 12;

                if (table.Length < headerSize)
                {
                    return null;
                }

                var recordsSize = count * recordSize;
                if (recordsSize > table.Length - headerSize)
                {
                    return null;
                }

                if (storageOffset > table.Length)
                {
                    return null;
                }

                var nameStorage = table.Slice(storageOffset);

                var names = new NameRecord[count];

                for (var i = 0; i < count; i++)
                {
                    var platform = reader.ReadUInt16<PlatformID>();
                    var encodingId = reader.ReadUInt16<EncodingIDs>();
                    var encoding = encodingId.AsEncoding();
                    var languageID = reader.ReadUInt16();
                    var nameID = reader.ReadUInt16<KnownNameIds>();
                    var length = reader.ReadUInt16();
                    var offset = reader.ReadUInt16();

                    names[i] = new NameRecord(nameStorage, platform, languageID, nameID, offset, length, encoding);
                }

                return new NameTable(names);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
            {
                // A present-but-malformed 'name' table must not deny the font; callers fall back to a
                // default family name, the same outcome as an absent 'name'. Only the parsing-related
                // exceptions are swallowed (end-of-span from BigEndianBinaryReader, out-of-range from
                // Memory.Slice) so genuine/fatal failures still surface.
                return null;
            }
        }

        public IEnumerator<NameRecord> GetEnumerator()
        {
            return new ImmutableReadOnlyListStructEnumerator<NameRecord>(_names);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
