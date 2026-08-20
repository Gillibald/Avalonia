using System;
using System.Runtime.InteropServices;
using Avalonia.MicroCom;

namespace Avalonia.Media.Fonts
{
    /// <summary>
    /// Minimal reusable <c>IDWriteTextAnalysisSource</c> over a single codepoint and a locale,
    /// as required by <c>IDWriteFontFallback::MapCharacters</c>. One instance serves every match
    /// call: the character and locale are written into a persistent native buffer, so matching
    /// allocates nothing per call. The buffer is native because DirectWrite reads the returned
    /// pointers after the callbacks return. Callers must serialize the Set methods with the
    /// MapCharacters call they feed.
    /// </summary>
    internal sealed unsafe class TextAnalysisSource : CallbackBase, IDWriteTextAnalysisSource
    {
        private const int ReadingDirectionLeftToRight = 0;

        // The text is at most one surrogate pair; the locale holds LOCALE_NAME_MAX_LENGTH (85)
        // characters plus the null terminator DirectWrite expects.
        private const int TextCapacity = 2;
        private const int LocaleCapacity = 86;

        private readonly IntPtr _buffer;
        private uint _textLength;
        private string? _localeName;

        public TextAnalysisSource()
        {
            _buffer = Marshal.AllocHGlobal((TextCapacity + LocaleCapacity) * sizeof(char));

            Locale[0] = '\0';
        }

        private char* Text => (char*)_buffer;

        private char* Locale => (char*)_buffer + TextCapacity;

        /// <summary>
        /// Writes the codepoint's UTF-16 form into the text buffer and returns its length in
        /// code units.
        /// </summary>
        public uint SetCharacter(int codepoint)
        {
            var text = Text;

            if (codepoint <= char.MaxValue)
            {
                text[0] = (char)codepoint;
                _textLength = 1;
            }
            else
            {
                var value = codepoint - 0x10000;
                text[0] = (char)(0xD800 + (value >> 10));
                text[1] = (char)(0xDC00 + (value & 0x3FF));
                _textLength = 2;
            }

            return _textLength;
        }

        /// <summary>
        /// Copies the locale name into the locale buffer; a repeated name is a no-op.
        /// </summary>
        public void SetLocale(string localeName)
        {
            if (localeName == _localeName)
            {
                return;
            }

            var locale = Locale;

            // Valid Windows locale names fit the buffer; anything longer is unknown to
            // DirectWrite anyway and may truncate.
            var length = Math.Min(localeName.Length, LocaleCapacity - 1);

            for (var i = 0; i < length; i++)
            {
                locale[i] = localeName[i];
            }

            locale[length] = '\0';
            _localeName = localeName;
        }

        public int GetTextAtPosition(uint textPosition, char** textString, uint* textLength)
        {
            if (textPosition >= _textLength)
            {
                *textString = null;
                *textLength = 0;
            }
            else
            {
                *textString = Text + textPosition;
                *textLength = _textLength - textPosition;
            }

            return 0;
        }

        public int GetTextBeforePosition(uint textPosition, char** textString, uint* textLength)
        {
            if (textPosition == 0 || textPosition > _textLength)
            {
                *textString = null;
                *textLength = 0;
            }
            else
            {
                *textString = Text;
                *textLength = textPosition;
            }

            return 0;
        }

        public int ParagraphReadingDirection => ReadingDirectionLeftToRight;

        public int GetLocaleName(uint textPosition, uint* textLength, char** localeName)
        {
            *textLength = textPosition < _textLength ? _textLength - textPosition : 0;
            *localeName = Locale;

            return 0;
        }

        public int GetNumberSubstitution(uint textPosition, uint* textLength, void** numberSubstitution)
        {
            *textLength = textPosition < _textLength ? _textLength - textPosition : 0;
            *numberSubstitution = null;

            return 0;
        }

        protected override void Destroyed()
        {
            Marshal.FreeHGlobal(_buffer);
        }
    }
}
