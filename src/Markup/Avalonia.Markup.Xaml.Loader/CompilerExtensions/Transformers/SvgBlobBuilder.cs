using System;
using System.Collections.Generic;
using System.Text;
using System.Xml.Linq;

namespace Avalonia.Markup.Xaml.XamlIl.CompilerExtensions.Transformers
{
    /// <summary>
    /// Serializes a validated, stripped inline-SVG <see cref="XElement"/> tree to
    /// the compiled-blob byte format read at runtime by
    /// <c>Avalonia.Media.Svg.SvgBlobReader</c>. Strings (names, attribute keys and
    /// values, text) are pooled so duplicates are stored once. The normalization
    /// mirrors <c>SvgDocument.Load</c> — element names become local names, and
    /// attribute keys map to <c>xlink:href</c>, <c>xml:*</c> or the bare local name
    /// (foreign attributes dropped) — so the rebuilt tree is exactly what the
    /// renderer expects. The constants must match
    /// <c>Avalonia.Media.Svg.SvgBlobFormat</c>; the version byte guards skew.
    /// </summary>
    internal static class SvgBlobBuilder
    {
        private const byte Magic0 = (byte)'A';
        private const byte Magic1 = (byte)'S';
        private const byte Magic2 = (byte)'B';
        private const byte Version = 1;
        private const byte ContentText = 0;
        private const byte ContentElement = 1;

        private static readonly XNamespace s_xlinkNs = "http://www.w3.org/1999/xlink";
        private static readonly XNamespace s_xmlNs = "http://www.w3.org/XML/1998/namespace";

        public static byte[] Write(XElement root)
        {
            var pool = new List<string>();
            var poolIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var body = new List<byte>(256);

            WriteElement(root, body, pool, poolIndex);

            var output = new List<byte>(body.Count + (pool.Count * 8) + 8)
            {
                Magic0, Magic1, Magic2, Version,
            };
            WriteVarUInt(output, (uint)pool.Count);
            foreach (var value in pool)
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                WriteVarUInt(output, (uint)bytes.Length);
                output.AddRange(bytes);
            }

            output.AddRange(body);
            return output.ToArray();
        }

        private static void WriteElement(
            XElement element, List<byte> body, List<string> pool, Dictionary<string, int> poolIndex)
        {
            WriteVarUInt(body, (uint)Intern(element.Name.LocalName, pool, poolIndex));

            // Keep only the attributes SvgDocument.Load keeps, with the same key
            // normalization. Two passes: the count is written before the entries.
            var attributes = new List<KeyValuePair<string, string>>();
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration)
                    continue;

                var ns = attribute.Name.Namespace;
                string key;
                if (ns == XNamespace.None)
                    key = attribute.Name.LocalName;
                else if (ns == s_xlinkNs && attribute.Name.LocalName == "href")
                    key = "xlink:href";
                else if (ns == s_xmlNs)
                    key = "xml:" + attribute.Name.LocalName;
                else
                    continue; // foreign attribute, dropped like Load does

                attributes.Add(new KeyValuePair<string, string>(key, attribute.Value));
            }

            WriteVarUInt(body, (uint)attributes.Count);
            foreach (var attribute in attributes)
            {
                WriteVarUInt(body, (uint)Intern(attribute.Key, pool, poolIndex));
                WriteVarUInt(body, (uint)Intern(attribute.Value, pool, poolIndex));
            }

            // Content: non-empty text (XText covers CDATA) and child elements, in
            // document order. Strip already removed comments, foreign elements and
            // insignificant whitespace.
            var content = new List<XNode>();
            foreach (var node in element.Nodes())
            {
                if (node is XText text)
                {
                    if (text.Value.Length > 0)
                        content.Add(text);
                }
                else if (node is XElement)
                {
                    content.Add(node);
                }
            }

            WriteVarUInt(body, (uint)content.Count);
            foreach (var node in content)
            {
                if (node is XText text)
                {
                    body.Add(ContentText);
                    WriteVarUInt(body, (uint)Intern(text.Value, pool, poolIndex));
                }
                else
                {
                    body.Add(ContentElement);
                    WriteElement((XElement)node, body, pool, poolIndex);
                }
            }
        }

        private static int Intern(string value, List<string> pool, Dictionary<string, int> poolIndex)
        {
            if (!poolIndex.TryGetValue(value, out var index))
            {
                index = pool.Count;
                pool.Add(value);
                poolIndex.Add(value, index);
            }

            return index;
        }

        private static void WriteVarUInt(List<byte> buffer, uint value)
        {
            while (value >= 0x80)
            {
                buffer.Add((byte)(value | 0x80));
                value >>= 7;
            }

            buffer.Add((byte)value);
        }
    }
}
