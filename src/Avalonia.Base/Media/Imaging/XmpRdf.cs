using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Avalonia.Media.Imaging;

/// <summary>
/// The kinds of XMP value WIC distinguishes; the container kinds are WIC's block formats.
/// </summary>
internal enum XmpNodeKind
{
    Simple,
    Struct,
    Bag,
    Seq,
    Alt,
}

/// <summary>
/// One XMP value: a text value, a structure of properties, or an array of items. The packet's
/// top level is a structure.
/// </summary>
internal sealed class XmpNode
{
    public XmpNode(XmpNodeKind kind, string? value = null)
    {
        Kind = kind;
        Value = value;
    }

    public XmpNodeKind Kind { get; }

    /// <summary>The text of a simple value.</summary>
    public string? Value { get; set; }

    /// <summary>The <c>xml:lang</c> of an item of an alternative.</summary>
    public string? Language { get; set; }

    /// <summary>The properties of a structure, in document order.</summary>
    public List<XmpProperty> Properties { get; } = new();

    /// <summary>The items of a bag, sequence or alternative.</summary>
    public List<XmpNode> Items { get; } = new();

    public bool IsArray => Kind is XmpNodeKind.Bag or XmpNodeKind.Seq or XmpNodeKind.Alt;

    /// <summary>WIC's metadata format name of a container node.</summary>
    public string Format => Kind switch
    {
        XmpNodeKind.Struct => "xmpstruct",
        XmpNodeKind.Bag => "xmpbag",
        XmpNodeKind.Seq => "xmpseq",
        XmpNodeKind.Alt => "xmpalt",
        _ => "xmp",
    };

    public XmpNode Clone()
    {
        var copy = new XmpNode(Kind, Value) { Language = Language };

        foreach (var property in Properties)
            copy.Properties.Add(new XmpProperty(property.Namespace, property.Name, property.Node.Clone()));

        foreach (var item in Items)
            copy.Items.Add(item.Clone());

        return copy;
    }

    /// <summary>The child a canonical path segment names, or null.</summary>
    public XmpNode? Find(string segment)
    {
        switch (Kind)
        {
            case XmpNodeKind.Struct:
                if (!XmpNamespaces.TryParse(segment, out var ns, out var name))
                    return null;

                foreach (var property in Properties)
                {
                    if (property.Namespace == ns && property.Name == name)
                        return property.Node;
                }

                return null;
            case XmpNodeKind.Bag:
            case XmpNodeKind.Seq:
            case XmpNodeKind.Alt:
                var index = FindItem(segment);
                return index >= 0 && index < Items.Count ? Items[index] : null;
            default:
                return null;
        }
    }

    /// <summary>The path segments of the children, as WIC spells them.</summary>
    public IEnumerable<string> ChildSegments()
    {
        if (Kind == XmpNodeKind.Struct)
        {
            foreach (var property in Properties)
                yield return XmpNamespaces.Spell(property.Namespace, property.Name);
        }
        else if (Kind == XmpNodeKind.Alt)
        {
            foreach (var item in Items)
                yield return item.Language ?? "{}";
        }
        else if (IsArray)
        {
            for (var i = 0; i < Items.Count; i++)
                yield return "{ulong=" + i.ToString(CultureInfo.InvariantCulture) + "}";
        }
    }

    /// <summary>
    /// Puts <paramref name="value"/> at the child a segment names. Arrays take an index up to
    /// their length, which appends; alternatives also take a language, which adds or replaces
    /// that item. Returns false when the segment cannot name a child here.
    /// </summary>
    public bool TrySet(string segment, XmpNode value)
    {
        switch (Kind)
        {
            case XmpNodeKind.Struct:
                if (!XmpNamespaces.TryParse(segment, out var ns, out var name))
                    return false;

                for (var i = 0; i < Properties.Count; i++)
                {
                    if (Properties[i].Namespace == ns && Properties[i].Name == name)
                    {
                        Properties[i] = new XmpProperty(ns, name, value);
                        return true;
                    }
                }

                Properties.Add(new XmpProperty(ns, name, value));
                return true;
            case XmpNodeKind.Bag:
            case XmpNodeKind.Seq:
            case XmpNodeKind.Alt:
                if (Kind == XmpNodeKind.Alt && !IsIndex(segment, out _))
                {
                    var language = segment == "{}" ? null : segment;
                    var existing = FindItem(segment);

                    value.Language = language;

                    if (existing >= 0)
                        Items[existing] = value;
                    else
                        Items.Add(value);

                    return true;
                }

                if (!IsIndex(segment, out var index) || index > Items.Count)
                    return false;

                if (index == Items.Count)
                {
                    Items.Add(value);
                }
                else
                {
                    value.Language = Items[index].Language;
                    Items[index] = value;
                }

                return true;
            default:
                return false;
        }
    }

    /// <summary>Removes the child a segment names; returns whether there was one.</summary>
    public bool Remove(string segment)
    {
        if (Kind == XmpNodeKind.Struct)
        {
            if (!XmpNamespaces.TryParse(segment, out var ns, out var name))
                return false;

            return Properties.RemoveAll(p => p.Namespace == ns && p.Name == name) > 0;
        }

        var index = FindItem(segment);

        if (index < 0 || index >= Items.Count)
            return false;

        Items.RemoveAt(index);
        return true;
    }

    private int FindItem(string segment)
    {
        if (IsIndex(segment, out var index))
            return index;

        if (Kind != XmpNodeKind.Alt)
            return -1;

        for (var i = 0; i < Items.Count; i++)
        {
            if (segment == "{}" ? Items[i].Language is null : string.Equals(Items[i].Language, segment, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static bool IsIndex(string segment, out int index)
    {
        index = -1;

        if (!segment.StartsWith("{", StringComparison.Ordinal) || !segment.EndsWith("}", StringComparison.Ordinal))
            return false;

        var equals = segment.IndexOf('=');

        return equals > 0 && int.TryParse(segment.AsSpan(equals + 1, segment.Length - equals - 2), NumberStyles.None,
            CultureInfo.InvariantCulture, out index);
    }
}

internal readonly struct XmpProperty
{
    public XmpProperty(string ns, string name, XmpNode node)
    {
        Namespace = ns;
        Name = name;
        Node = node;
    }

    public string Namespace { get; }

    public string Name { get; }

    public XmpNode Node { get; }
}

/// <summary>
/// WIC's XMP schema prefixes. A path names a property <c>prefix:name</c> with one of these
/// prefixes, or with its namespace URI, escaping <c>\</c>, <c>/</c> and <c>:</c> with a
/// backslash, or as <c>{wstr=URI}:name</c>. Prefixes match without case; names with case.
/// </summary>
internal static class XmpNamespaces
{
    public const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    public const string Meta = "adobe:ns:meta/";

    private static readonly (string Prefix, string Uri)[] s_registered =
    {
        ("dc", "http://purl.org/dc/elements/1.1/"),
        ("xmp", "http://ns.adobe.com/xap/1.0/"),
        ("xmpRights", "http://ns.adobe.com/xap/1.0/rights/"),
        ("xmpMM", "http://ns.adobe.com/xap/1.0/mm/"),
        ("xmpBJ", "http://ns.adobe.com/xap/1.0/bj/"),
        ("xmpTPg", "http://ns.adobe.com/xap/1.0/t/pg/"),
        ("xapGImg", "http://ns.adobe.com/xap/1.0/g/img/"),
        ("xmpidq", "http://ns.adobe.com/xmp/Identifier/qual/1.0/"),
        ("pdf", "http://ns.adobe.com/pdf/1.3/"),
        ("photoshop", "http://ns.adobe.com/photoshop/1.0/"),
        ("tiff", "http://ns.adobe.com/tiff/1.0/"),
        ("exif", "http://ns.adobe.com/exif/1.0/"),
        ("aux", "http://ns.adobe.com/exif/1.0/aux/"),
        ("crs", "http://ns.adobe.com/camera-raw-settings/1.0/"),
        ("stDim", "http://ns.adobe.com/xap/1.0/sType/Dimensions#"),
        ("stEvt", "http://ns.adobe.com/xap/1.0/sType/ResourceEvent#"),
        ("stJob", "http://ns.adobe.com/xap/1.0/sType/Job#"),
        ("stRef", "http://ns.adobe.com/xap/1.0/sType/ResourceRef#"),
        ("stVer", "http://ns.adobe.com/xap/1.0/sType/Version#"),
        ("xmpDM", "http://ns.adobe.com/xmp/1.0/DynamicMedia/"),
        ("MicrosoftPhoto", "http://ns.microsoft.com/photo/1.0/"),
        ("MP", "http://ns.microsoft.com/photo/1.2/"),
        ("MPRI", "http://ns.microsoft.com/photo/1.2/t/RegionInfo#"),
        ("MPReg", "http://ns.microsoft.com/photo/1.2/t/Region#"),
        ("Iptc4xmpCore", "http://iptc.org/std/Iptc4xmpCore/1.0/xmlns/"),
        ("exifEX", "http://cipa.jp/exif/1.0/"),
    };

    /// <summary>The registered prefix of a namespace, or null.</summary>
    public static string? PrefixOf(string uri)
    {
        foreach (var (prefix, registered) in s_registered)
        {
            if (registered == uri)
                return prefix;
        }

        return null;
    }

    /// <summary>The path segment of a property.</summary>
    public static string Spell(string ns, string name) => (PrefixOf(ns) ?? Escape(ns)) + ":" + Escape(name);

    /// <summary>
    /// Splits a property segment into namespace and name. A prefix WIC does not register is
    /// taken as the namespace itself.
    /// </summary>
    public static bool TryParse(string segment, out string ns, out string name)
    {
        ns = name = string.Empty;

        var colon = LastUnescaped(segment, ':');

        if (colon <= 0 || colon == segment.Length - 1)
            return false;

        var space = segment.Substring(0, colon);

        name = Unescape(segment.Substring(colon + 1));

        if (space.StartsWith("{", StringComparison.Ordinal) && space.EndsWith("}", StringComparison.Ordinal) &&
            space.IndexOf('=') is var equals and > 0)
        {
            ns = space.Substring(equals + 1, space.Length - equals - 2);
            return true;
        }

        var unescaped = Unescape(space);

        foreach (var (prefix, uri) in s_registered)
        {
            if (string.Equals(prefix, unescaped, StringComparison.OrdinalIgnoreCase))
            {
                ns = uri;
                return true;
            }
        }

        ns = unescaped;
        return true;
    }

    /// <summary>The canonical spelling of a segment below an XMP block.</summary>
    public static string Normalize(string segment)
    {
        if (segment.StartsWith("{", StringComparison.Ordinal) && segment.EndsWith("}", StringComparison.Ordinal))
        {
            if (segment.Length == 2)
                return segment;

            var equals = segment.IndexOf('=');

            if (equals > 0 && ulong.TryParse(segment.AsSpan(equals + 1, segment.Length - equals - 2), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var index))
            {
                return "{ulong=" + index.ToString(CultureInfo.InvariantCulture) + "}";
            }
        }

        // A language of an alternative, or a property.
        return TryParse(segment, out var ns, out var name) ? Spell(ns, name) : segment;
    }

    private static int LastUnescaped(string segment, char c)
    {
        var found = -1;
        var depth = 0;

        for (var i = 0; i < segment.Length; i++)
        {
            switch (segment[i])
            {
                case '\\':
                    i++;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    break;
                default:
                    if (segment[i] == c && depth == 0)
                        found = i;
                    break;
            }
        }

        return found;
    }

    private static string Escape(string text)
    {
        if (text.IndexOfAny(new[] { '\\', '/', ':' }) < 0)
            return text;

        var builder = new StringBuilder(text.Length + 8);

        foreach (var c in text)
        {
            if (c is '\\' or '/' or ':')
                builder.Append('\\');

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static string Unescape(string text)
    {
        if (text.IndexOf('\\') < 0)
            return text;

        var builder = new StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
                i++;

            builder.Append(text[i]);
        }

        return builder.ToString();
    }
}

/// <summary>
/// An XMP packet parsed into WIC's model: RDF descriptions merged into one structure, arrays
/// with their items, structures with their fields, every value text. Written back the way
/// WIC writes packets.
/// </summary>
internal static class XmpRdf
{
    private static readonly XNamespace s_rdf = XmpNamespaces.Rdf;
    private static readonly XNamespace s_xml = XNamespace.Xml;

    // WIC's fixed rdf:about for the descriptions it writes.
    private const string About = "uuid:faf5bdd5-ba3d-11da-ad31-d33d75182f1b";

    /// <summary>The top-level structure of a packet, or null when it is not RDF/XML.</summary>
    public static XmpNode? Parse(ReadOnlySpan<byte> packet)
    {
        XDocument document;

        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

            using var reader = XmlReader.Create(new MemoryStream(packet.ToArray()), settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        var root = new XmpNode(XmpNodeKind.Struct);

        foreach (var rdf in document.Descendants(s_rdf + "RDF"))
        {
            foreach (var description in rdf.Elements(s_rdf + "Description"))
                AddProperties(description, root);

            break;
        }

        return root;
    }

    /// <summary>Writes a top-level structure as a packet.</summary>
    public static byte[] Serialize(XmpNode root)
    {
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Encoding = new UTF8Encoding(false),
            NamespaceHandling = NamespaceHandling.OmitDuplicates,
        };
        var stream = new MemoryStream();

        using (var writer = XmlWriter.Create(stream, settings))
        {
            var prefixes = new Dictionary<string, string>(StringComparer.Ordinal);

            writer.WriteProcessingInstruction("xpacket", "begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"");
            writer.WriteStartElement("x", "xmpmeta", XmpNamespaces.Meta);
            writer.WriteStartElement("rdf", "RDF", XmpNamespaces.Rdf);

            // One description per namespace, in the order the namespaces first appear.
            var order = new List<string>();

            foreach (var property in root.Properties)
            {
                if (!order.Contains(property.Namespace))
                    order.Add(property.Namespace);
            }

            foreach (var ns in order)
            {
                writer.WriteStartElement("rdf", "Description", XmpNamespaces.Rdf);
                writer.WriteAttributeString("rdf", "about", XmpNamespaces.Rdf, About);

                foreach (var property in root.Properties)
                {
                    if (property.Namespace == ns)
                        WriteProperty(writer, prefixes, property.Namespace, property.Name, property.Node);
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteProcessingInstruction("xpacket", "end=\"w\"");
        }

        return stream.ToArray();
    }

    private static void AddProperties(XElement element, XmpNode target)
    {
        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration || attribute.Name.Namespace == s_rdf || attribute.Name.Namespace == s_xml ||
                attribute.Name.Namespace == XNamespace.None)
            {
                continue;
            }

            target.Properties.Add(new XmpProperty(attribute.Name.NamespaceName, attribute.Name.LocalName,
                new XmpNode(XmpNodeKind.Simple, attribute.Value)));
        }

        foreach (var child in element.Elements())
        {
            if (child.Name.Namespace == s_rdf)
                continue;

            target.Properties.Add(new XmpProperty(child.Name.NamespaceName, child.Name.LocalName, ParseValue(child)));
        }
    }

    private static XmpNode ParseValue(XElement element)
    {
        // WIC reads a resource URI as empty text.
        if (element.Attribute(s_rdf + "resource") is not null)
            return new XmpNode(XmpNodeKind.Simple, string.Empty);

        if ((string?)element.Attribute(s_rdf + "parseType") == "Resource")
        {
            var resource = new XmpNode(XmpNodeKind.Struct);
            AddProperties(element, resource);
            return resource;
        }

        foreach (var child in element.Elements())
        {
            var kind = child.Name == s_rdf + "Bag" ? XmpNodeKind.Bag
                : child.Name == s_rdf + "Seq" ? XmpNodeKind.Seq
                : child.Name == s_rdf + "Alt" ? XmpNodeKind.Alt
                : (XmpNodeKind?)null;

            if (kind is { } array)
            {
                var node = new XmpNode(array);

                foreach (var li in child.Elements(s_rdf + "li"))
                {
                    var item = ParseValue(li);

                    if (array == XmpNodeKind.Alt)
                        item.Language = (string?)li.Attribute(s_xml + "lang");

                    node.Items.Add(item);
                }

                return node;
            }

            if (child.Name == s_rdf + "Description")
            {
                var description = new XmpNode(XmpNodeKind.Struct);
                AddProperties(child, description);
                return description;
            }

            // Fields given as elements of the property itself.
            var fields = new XmpNode(XmpNodeKind.Struct);
            AddProperties(element, fields);
            return fields;
        }

        foreach (var attribute in element.Attributes())
        {
            if (!attribute.IsNamespaceDeclaration && attribute.Name.Namespace != s_rdf && attribute.Name.Namespace != s_xml &&
                attribute.Name.Namespace != XNamespace.None)
            {
                // Fields given as attributes of the property.
                var fields = new XmpNode(XmpNodeKind.Struct);
                AddProperties(element, fields);
                return fields;
            }
        }

        return new XmpNode(XmpNodeKind.Simple, element.Value);
    }

    private static void WriteProperty(XmlWriter writer, Dictionary<string, string> prefixes, string ns, string name, XmpNode node)
    {
        writer.WriteStartElement(PrefixFor(prefixes, ns), name, ns);
        WriteValue(writer, prefixes, node);
        writer.WriteEndElement();
    }

    private static void WriteValue(XmlWriter writer, Dictionary<string, string> prefixes, XmpNode node)
    {
        switch (node.Kind)
        {
            case XmpNodeKind.Simple:
                writer.WriteString(node.Value ?? string.Empty);
                break;
            case XmpNodeKind.Struct:
                writer.WriteStartElement("rdf", "Description", XmpNamespaces.Rdf);

                foreach (var property in node.Properties)
                    WriteProperty(writer, prefixes, property.Namespace, property.Name, property.Node);

                writer.WriteEndElement();
                break;
            default:
                writer.WriteStartElement("rdf", node.Kind.ToString(), XmpNamespaces.Rdf);

                foreach (var item in node.Items)
                {
                    writer.WriteStartElement("rdf", "li", XmpNamespaces.Rdf);

                    if (item.Language is { } language)
                        writer.WriteAttributeString("xml", "lang", null, language);

                    WriteValue(writer, prefixes, item);
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
                break;
        }
    }

    // WIC names namespaces it does not register prefix0, prefix1, ...
    private static string PrefixFor(Dictionary<string, string> prefixes, string ns)
    {
        if (XmpNamespaces.PrefixOf(ns) is { } registered)
            return registered;

        if (!prefixes.TryGetValue(ns, out var prefix))
        {
            prefix = "prefix" + prefixes.Count.ToString(CultureInfo.InvariantCulture);
            prefixes[ns] = prefix;
        }

        return prefix;
    }
}
