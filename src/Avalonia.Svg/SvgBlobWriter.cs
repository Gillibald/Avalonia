using System;
using System.Collections.Generic;
using System.Text;

namespace Avalonia.Media.Svg;

/// <summary>
/// Serializes a parsed <see cref="SvgElement"/> tree to the compiled blob format
/// read back by <see cref="SvgBlobReader"/>. Strings (names, attribute keys and
/// values, text) are pooled so duplicates are stored once.
/// </summary>
/// <remarks>
/// This writer walks the runtime <see cref="SvgElement"/> tree and is used by
/// round-trip tests and benchmarks. The XAML build task serializes the
/// equivalent XML tree to the same format without constructing
/// <see cref="SvgElement"/>s.
/// </remarks>
internal static class SvgBlobWriter
{
    internal static byte[] Write(SvgDocument document) => Write(document.Root);

    internal static byte[] Write(SvgElement root)
    {
        var pool = new List<string>();
        var poolIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var body = new List<byte>(256);

        int Intern(string value)
        {
            if (!poolIndex.TryGetValue(value, out var index))
            {
                index = pool.Count;
                pool.Add(value);
                poolIndex.Add(value, index);
            }

            return index;
        }

        void WriteElement(SvgElement element)
        {
            SvgBlobFormat.WriteVarUInt(body, (uint)Intern(element.Name));

            var attributes = element.Attributes;
            SvgBlobFormat.WriteVarUInt(body, (uint)attributes.Count);
            foreach (var attribute in attributes)
            {
                SvgBlobFormat.WriteVarUInt(body, (uint)Intern(attribute.Key));
                SvgBlobFormat.WriteVarUInt(body, (uint)Intern(attribute.Value));
            }

            var content = element.Content;
            if (content is null)
            {
                SvgBlobFormat.WriteVarUInt(body, 0);
                return;
            }

            SvgBlobFormat.WriteVarUInt(body, (uint)content.Count);
            foreach (var item in content)
            {
                if (item is string text)
                {
                    body.Add(SvgBlobFormat.ContentText);
                    SvgBlobFormat.WriteVarUInt(body, (uint)Intern(text));
                }
                else if (item is SvgElement child)
                {
                    body.Add(SvgBlobFormat.ContentElement);
                    WriteElement(child);
                }
            }
        }

        WriteElement(root);

        var output = new List<byte>(body.Count + (pool.Count * 8) + 8)
        {
            SvgBlobFormat.Magic0, SvgBlobFormat.Magic1, SvgBlobFormat.Magic2, SvgBlobFormat.Version,
        };
        SvgBlobFormat.WriteVarUInt(output, (uint)pool.Count);
        foreach (var value in pool)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            SvgBlobFormat.WriteVarUInt(output, (uint)bytes.Length);
            output.AddRange(bytes);
        }

        output.AddRange(body);
        return output.ToArray();
    }
}
