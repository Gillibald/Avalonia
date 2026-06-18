using System;
using System.Collections.Generic;
using System.Text;

namespace Avalonia.Media.Svg;

/// <summary>
/// Rebuilds a parsed <see cref="SvgElement"/> tree from a compiled blob produced
/// by <see cref="SvgBlobWriter"/>, with no XML tokenization. Layout:
/// <code>
/// header : 'A' 'S' 'B' version
/// pool   : varint count, then [varint utf8Len, utf8 bytes] * count
/// element: varint nameIdx
///          varint attrCount, [varint keyIdx, varint valueIdx] * attrCount
///          varint contentCount,
///          [byte tag; text(0): varint textIdx; element(1): nested element] * contentCount
/// </code>
/// Every string is a pool index, so a value repeated across the document (a
/// shared fill colour, a stroke width) costs one allocation, not one per use —
/// that, plus the absence of an <c>XmlReader</c>, is where the allocation win
/// over <see cref="SvgDocument.Parse"/> comes from.
/// </summary>
internal static class SvgBlobReader
{
    internal static SvgElement Read(ReadOnlySpan<byte> data, Dictionary<string, SvgElement> elementsById)
    {
        if (data.Length < 4
            || data[0] != SvgBlobFormat.Magic0
            || data[1] != SvgBlobFormat.Magic1
            || data[2] != SvgBlobFormat.Magic2)
        {
            throw new FormatException("The data is not a compiled SVG blob.");
        }

        if (data[3] != SvgBlobFormat.Version)
        {
            throw new NotSupportedException(
                $"Unsupported compiled SVG blob version {data[3]}; expected {SvgBlobFormat.Version}.");
        }

        var position = 4;
        var poolCount = (int)SvgBlobFormat.ReadVarUInt(data, ref position);
        var pool = new string[poolCount];
        for (var i = 0; i < poolCount; i++)
        {
            var length = (int)SvgBlobFormat.ReadVarUInt(data, ref position);
            pool[i] = Encoding.UTF8.GetString(data.Slice(position, length));
            position += length;
        }

        return ReadElement(data, ref position, pool, parent: null, elementsById);
    }

    private static SvgElement ReadElement(
        ReadOnlySpan<byte> data,
        ref int position,
        string[] pool,
        SvgElement? parent,
        Dictionary<string, SvgElement> elementsById)
    {
        var name = pool[(int)SvgBlobFormat.ReadVarUInt(data, ref position)];

        var attributeCount = (int)SvgBlobFormat.ReadVarUInt(data, ref position);
        var attributes = new Dictionary<string, string>(attributeCount, StringComparer.Ordinal);
        for (var i = 0; i < attributeCount; i++)
        {
            var key = pool[(int)SvgBlobFormat.ReadVarUInt(data, ref position)];
            var value = pool[(int)SvgBlobFormat.ReadVarUInt(data, ref position)];
            attributes[key] = value;
        }

        var element = new SvgElement(name, parent, attributes);
        parent?.AddChild(element);

        // First id wins, matching SvgDocument.Load.
        if (element.Id is { Length: > 0 } id && !elementsById.ContainsKey(id))
            elementsById.Add(id, element);

        var contentCount = (int)SvgBlobFormat.ReadVarUInt(data, ref position);
        for (var i = 0; i < contentCount; i++)
        {
            var tag = data[position++];
            if (tag == SvgBlobFormat.ContentText)
                element.AddText(pool[(int)SvgBlobFormat.ReadVarUInt(data, ref position)]);
            else
                ReadElement(data, ref position, pool, element, elementsById);
        }

        return element;
    }
}
