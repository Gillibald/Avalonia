// Copyright (c) Six Labors.
// Licensed under the Apache License, Version 2.0.
// Ported from: https://github.com/SixLabors/Fonts/blob/034a440aece357341fcc6b02db58ffbe153e54ef/src/SixLabors.Fonts

using System;

namespace Avalonia.Media.Fonts.Tables.Name
{
    internal enum NameEncoding
    {
        Utf16BigEndian,
        Latin1,
        MacRoman
    }

    internal readonly struct NameRecord
    {
        private readonly ReadOnlyMemory<byte> _stringStorage;

        public NameRecord(
            ReadOnlyMemory<byte> stringStorage,
            PlatformID platform,
            ushort languageId,
            KnownNameIds nameId,
            ushort offset,
            ushort length,
            NameEncoding encoding)
        {
            _stringStorage = stringStorage;

            Platform = platform;
            LanguageID = languageId;
            NameID = nameId;
            Offset = offset;
            Length = length;
            Encoding = encoding;
        }

        public PlatformID Platform { get; }

        public ushort LanguageID { get; }

        public KnownNameIds NameID { get; }

        public ushort Offset { get; }

        public ushort Length { get; }

        public NameEncoding Encoding { get; }

        public string GetValue()
        {
            if (Length == 0)
            {
                return string.Empty;
            }

            // Offset/Length come straight from the untrusted 'name' record. NameTable.Load validates
            // the record array but not each record's storage slice, and GetValue runs later during
            // typeface construction, so a record pointing past the string storage must degrade to an
            // empty value rather than throw out of the GlyphTypeface constructor and deny the font.
            // Offset and Length are both ushort, so the sum cannot overflow uint.
            if ((uint)Offset + Length > (uint)_stringStorage.Length)
            {
                return string.Empty;
            }

            var span = _stringStorage.Span.Slice(Offset, Length);

            if (Encoding == NameEncoding.MacRoman)
            {
                return MacRomanDecoder.GetString(span);
            }

            var encoding = Encoding == NameEncoding.Latin1 ?
                System.Text.Encoding.Latin1 :
                System.Text.Encoding.BigEndianUnicode;

            // These encodings substitute U+FFFD for malformed bytes, but guard against an
            // exception-throwing decoder fallback so a corrupt record degrades to an empty value
            // instead of denying the font.
            try
            {
                return encoding.GetString(span);
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }
    }
}
