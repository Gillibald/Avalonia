using System;
using System.Collections.Generic;

namespace Avalonia.Media.Svg;

/// <summary>
/// Format constants and varint primitives shared by <see cref="SvgBlobWriter"/>
/// and <see cref="SvgBlobReader"/>. The compiled blob is a flat, string-pooled,
/// pre-order serialization of the parsed <see cref="SvgElement"/> tree; the
/// layout is documented on <see cref="SvgBlobReader"/>.
/// </summary>
internal static class SvgBlobFormat
{
    /// <summary>Magic bytes 'A','S','B' followed by <see cref="Version"/>.</summary>
    internal const byte Magic0 = (byte)'A';
    internal const byte Magic1 = (byte)'S';
    internal const byte Magic2 = (byte)'B';

    /// <summary>Format version; the reader rejects any other value.</summary>
    internal const byte Version = 1;

    /// <summary>Tags for the items interleaved in an element's content list.</summary>
    internal const byte ContentText = 0;
    internal const byte ContentElement = 1;

    /// <summary>Appends <paramref name="value"/> as an unsigned LEB128 varint.</summary>
    internal static void WriteVarUInt(List<byte> buffer, uint value)
    {
        while (value >= 0x80)
        {
            buffer.Add((byte)(value | 0x80));
            value >>= 7;
        }

        buffer.Add((byte)value);
    }

    /// <summary>Reads an unsigned LEB128 varint, advancing <paramref name="position"/>.</summary>
    internal static uint ReadVarUInt(ReadOnlySpan<byte> data, ref int position)
    {
        uint result = 0;
        var shift = 0;
        byte current;
        do
        {
            current = data[position++];
            result |= (uint)(current & 0x7F) << shift;
            shift += 7;
        } while ((current & 0x80) != 0);

        return result;
    }
}
