using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using XamlX;
using XamlX.Ast;
using XamlX.Transform;

namespace Avalonia.Markup.Xaml.XamlIl.CompilerExtensions.Transformers
{
    /// <summary>
    /// Preprocesses literal values of properties marked with
    /// <c>Avalonia.Svg.SvgContentAttribute</c> (matched by name — the XAML
    /// compiler takes no reference on Avalonia.Svg): the SVG markup, typically
    /// pasted as CDATA content, is XML-validated at compile time so malformed
    /// markup fails the build at the XAML position, and minified so the
    /// runtime parses a compact string. Non-literal values (bindings, markup
    /// extensions) pass through untouched.
    /// </summary>
    class AvaloniaXamlIlSvgContentTransformer : IXamlAstTransformer
    {
        private const string AttributeFullName = "Avalonia.Svg.SvgContentAttribute";

        private static readonly XNamespace SvgNs = "http://www.w3.org/2000/svg";
        private static readonly XNamespace XlinkNs = "http://www.w3.org/1999/xlink";
        private static readonly XNamespace XmlNs = "http://www.w3.org/XML/1998/namespace";

        /// <summary>
        /// Elements whose character data is significant; whitespace inside
        /// them is left untouched.
        /// </summary>
        private static readonly HashSet<string> TextContentElements = new HashSet<string>(StringComparer.Ordinal)
        {
            "text", "tspan", "textPath", "title", "desc", "style",
        };

        public IXamlAstNode Transform(AstTransformationContext context, IXamlAstNode node)
        {
            if (node is not XamlAstXamlPropertyValueNode propertyValue
                || propertyValue.Property is not XamlAstClrProperty clrProperty)
                return node;

            if (!clrProperty.CustomAttributes.Any(a => a.Type.FullName == AttributeFullName))
                return node;

            if (propertyValue.Values.Count != 1 || propertyValue.Values[0] is not XamlAstTextNode text)
                return node;

            string minified;
            try
            {
                minified = MinifySvg(text.Text);
            }
            catch (XmlException exception)
            {
                throw new XamlTransformException(
                    $"Invalid inline SVG markup at content line {exception.LineNumber}, column {exception.LinePosition}: {exception.Message}",
                    text, exception);
            }
            catch (InvalidSvgContentException exception)
            {
                throw new XamlTransformException(exception.Message, text);
            }

            propertyValue.Values[0] = new XamlAstTextNode(text, minified, true,
                context.Configuration.WellKnownTypes.String);
            return node;
        }

        private sealed class InvalidSvgContentException : Exception
        {
            public InvalidSvgContentException(string message) : base(message)
            {
            }
        }

        private static string MinifySvg(string markup)
        {
            XDocument document;
            var settings = new XmlReaderSettings
            {
                // Some editors export a DOCTYPE; tolerate and drop it.
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreProcessingInstructions = true,
                IgnoreComments = true,
            };

            using (var stringReader = new StringReader(markup))
            using (var xmlReader = XmlReader.Create(stringReader, settings))
            {
                document = XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace);
            }

            var root = document.Root;
            if (root == null || root.Name.LocalName != "svg")
                throw new InvalidSvgContentException(
                    "Inline SVG content must have an <svg> root element.");

            // Fragments pasted without a namespace get the SVG namespace
            // injected so the runtime parser accepts them.
            if (root.Name.Namespace == XNamespace.None)
            {
                foreach (var element in root.DescendantsAndSelf())
                {
                    if (element.Name.Namespace == XNamespace.None)
                        element.Name = SvgNs + element.Name.LocalName;
                }
            }
            else if (root.Name.Namespace != SvgNs)
            {
                throw new InvalidSvgContentException(
                    $"Inline SVG content must use the SVG namespace; the root element is in '{root.Name.Namespace}'.");
            }

            Strip(root, preserveWhitespace: false);

            // Re-declare the namespaces that survived the strip; XLinq emits
            // child elements prefix-free under the default declaration.
            root.SetAttributeValue("xmlns", SvgNs.NamespaceName);
            if (root.DescendantsAndSelf().Any(e => e.Attributes().Any(a => a.Name.Namespace == XlinkNs)))
                root.SetAttributeValue(XNamespace.Xmlns + "xlink", XlinkNs.NamespaceName);

            return root.ToString(SaveOptions.DisableFormatting);
        }

        private static void Strip(XElement element, bool preserveWhitespace)
        {
            var space = (string?)element.Attribute(XmlNs + "space");
            if (space == "preserve")
                preserveWhitespace = true;
            else if (space == "default")
                preserveWhitespace = false;

            if (TextContentElements.Contains(element.Name.LocalName))
                preserveWhitespace = true;

            // Editor cruft (inkscape:*, sodipodi:*, RDF metadata, …) is
            // ignored by the runtime parser anyway; drop it together with all
            // namespace declarations — the survivors are re-declared on the
            // root afterwards.
            foreach (var attribute in element.Attributes().ToArray())
            {
                var ns = attribute.Name.Namespace;
                if (attribute.IsNamespaceDeclaration
                    || (ns != XNamespace.None && ns != SvgNs && ns != XlinkNs && ns != XmlNs))
                    attribute.Remove();
            }

            foreach (var node in element.Nodes().ToArray())
            {
                switch (node)
                {
                    case XElement child when child.Name.Namespace != SvgNs:
                        node.Remove();
                        break;

                    case XElement child:
                        Strip(child, preserveWhitespace);
                        break;

                    case XText textNode when !preserveWhitespace && string.IsNullOrWhiteSpace(textNode.Value):
                        node.Remove();
                        break;
                }
            }
        }
    }
}
