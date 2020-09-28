using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Media.TextFormatting.Unicode;
using Avalonia.Platform;
using Avalonia.Utilities;
using HarfBuzzSharp;
using Buffer = HarfBuzzSharp.Buffer;

namespace Avalonia.Skia
{
    internal class TextShaperImpl : ITextShaperImpl
    {
        public GlyphRun ShapeText(ReadOnlySlice<char> text, Typeface typeface, double fontRenderingEmSize, CultureInfo culture)
        {
            using (var buffer = new Buffer())
            {
                FillBuffer(buffer, text);

                buffer.Language = new Language(culture ?? CultureInfo.CurrentCulture);

                buffer.GuessSegmentProperties();

                var glyphTypeface = typeface.GlyphTypeface;

                var font = ((GlyphTypefaceImpl)glyphTypeface.PlatformImpl).Font;

                font.Shape(buffer);

                font.GetScale(out var scaleX, out _);

                var textScale = fontRenderingEmSize / scaleX;

                var bufferLength = buffer.Length;

                var glyphInfos = buffer.GetGlyphInfoSpan();

                var glyphPositions = buffer.GetGlyphPositionSpan();

                var glyphIndices = new ushort[bufferLength];

                var clusters = new ushort[bufferLength];

                double[] glyphAdvances = null;

                Vector[] glyphOffsets = null;

                for (var i = 0; i < bufferLength; i++)
                {
                    glyphIndices[i] = (ushort)glyphInfos[i].Codepoint;

                    clusters[i] = (ushort)glyphInfos[i].Cluster;

                    if (!glyphTypeface.IsFixedPitch)
                    {
                        SetAdvance(glyphPositions, i, textScale, ref glyphAdvances);
                    }

                    SetOffset(glyphPositions, i, textScale, ref glyphOffsets);
                }

                return new GlyphRun(glyphTypeface, fontRenderingEmSize,
                    new ReadOnlySlice<ushort>(glyphIndices),
                    new ReadOnlySlice<double>(glyphAdvances),
                    new ReadOnlySlice<Vector>(glyphOffsets),
                    text,
                    new ReadOnlySlice<ushort>(clusters));
            }
        }

        private static void FillBuffer(Buffer buffer, ReadOnlySlice<char> text)
        {
            buffer.ContentType = ContentType.Unicode;

            var i = 0;

            while (i < text.Length)
            {
                var codepoint = Codepoint.ReadAt(text, i, out var count);

                var cluster = (uint)(text.Start + i);

                if (codepoint.IsBreakChar)
                {
                    if (i + 1 < text.Length)
                    {
                        var nextCodepoint = Codepoint.ReadAt(text, i + 1, out _);

                        if (nextCodepoint == '\r' && codepoint == '\n' || nextCodepoint == '\n' && codepoint == '\r')
                        {
                            count++;

                            buffer.Add('\u200C', cluster);

                            buffer.Add('\u200D', cluster);
                        }
                        else
                        {
                            buffer.Add('\u200C', cluster);
                        }
                    }
                    else
                    {
                        buffer.Add('\u200C', cluster);
                    }
                }
                else
                {
                    buffer.Add(codepoint, cluster);
                }

                i += count;
            }
        }

        private static void SetOffset(ReadOnlySpan<GlyphPosition> glyphPositions, int index, double textScale,
            ref Vector[] offsetBuffer)
        {
            var position = glyphPositions[index];

            if (position.XOffset == 0 && position.YOffset == 0)
            {
                return;
            }

            offsetBuffer ??= new Vector[glyphPositions.Length];

            var offsetX = position.XOffset * textScale;

            var offsetY = position.YOffset * textScale;

            offsetBuffer[index] = new Vector(offsetX, offsetY);
        }

        private static void SetAdvance(ReadOnlySpan<GlyphPosition> glyphPositions, int index, double textScale,
            ref double[] advanceBuffer)
        {
            advanceBuffer ??= new double[glyphPositions.Length];

            // Depends on direction of layout
            // advanceBuffer[index] = buffer.GlyphPositions[index].YAdvance * textScale;
            advanceBuffer[index] = glyphPositions[index].XAdvance * textScale;
        }
    }

    public class TextBuffer : IEnumerable<GlyphRun>
    {
        private readonly List<ShapeableTextCharacters> _textCharacters = new List<ShapeableTextCharacters>(1);

        private ShapeableTextCharacters _currentTextCharacters;

        public bool IsShaped { get; private set; }

        public bool TryAdd(ShapeableTextCharacters textCharacters)
        {
            if (_currentTextCharacters != null && !textCharacters.CanShapeTogether(_currentTextCharacters))
            {
                return false;
            }

            _textCharacters.Add(textCharacters);

            _currentTextCharacters = textCharacters;

            return true;
        }

        public void Shape()
        {
            IsShaped = true;
        }

        public IEnumerator<GlyphRun> GetEnumerator()
        {
            return new GlyphRunEnumerator(this);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        private struct GlyphRunEnumerator : IEnumerator, IEnumerator<GlyphRun>
        {
            private readonly TextBuffer _buffer;

            private int _currentPosition;

            public GlyphRunEnumerator(TextBuffer buffer)
            {
                _buffer = buffer;

                _currentPosition = 0;

                Current = null;
            }

            public GlyphRun Current { get; private set; }

            object IEnumerator.Current => Current;

            public void Dispose() { }

            public bool MoveNext()
            {
                if (_currentPosition >= _buffer._textCharacters.Count)
                {
                    return false;
                }

                var textCharacters = _buffer._textCharacters[_currentPosition];

                Current = CreateGlyphRun(textCharacters);

                _currentPosition++;

                return true;
            }

            public void Reset()
            {
                _currentPosition = 0;

                Current = null;
            }

            private GlyphRun CreateGlyphRun(ShapeableTextCharacters textCharacters)
            {
                return null;
            }
        }
    }
}
