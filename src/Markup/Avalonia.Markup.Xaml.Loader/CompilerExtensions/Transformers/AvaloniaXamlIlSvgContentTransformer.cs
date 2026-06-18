using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Avalonia.Markup.Xaml.XamlIl.CompilerExtensions.AstNodes;
using XamlX;
using XamlX.Ast;
using XamlX.Transform;
using XamlX.TypeSystem;

namespace Avalonia.Markup.Xaml.XamlIl.CompilerExtensions.Transformers
{
    /// <summary>
    /// Compiles SVG markup pasted into XAML into a document factory. The markup
    /// reaches this transformer as a text value of a property typed
    /// <c>Avalonia.Media.Svg.SvgDocument</c> (matched by name — the XAML compiler takes
    /// no reference on Avalonia.Svg), whether it was written as CDATA content, an
    /// escaped attribute string, or a verbatim inline <c>&lt;svg&gt;</c> element
    /// captured as raw content by the parser (see <c>rawContentNamespaces</c> in
    /// <c>XDocumentXamlParser</c>). The markup is XML-validated at compile time so
    /// malformed content fails the build at the XAML position, minified, and
    /// emitted as a <c>SvgDocument.FromXamlContent("…")</c> call. Runs before XAML
    /// whitespace normalization so character data inside the markup survives
    /// verbatim. Non-markup strings (URI sources) and non-literal values pass
    /// through to the regular conversion pipeline.
    /// </summary>
    class AvaloniaXamlIlSvgContentTransformer : IXamlAstTransformer
    {
        public IXamlAstNode Transform(AstTransformationContext context, IXamlAstNode node)
        {
            if (node is not XamlAstXamlPropertyValueNode propertyValue
                || propertyValue.Property is not XamlAstClrProperty clrProperty)
                return node;

            if (clrProperty.Getter?.ReturnType is not { } propertyType
                || !SvgDocumentContentHelper.IsSvgDocumentType(propertyType))
                return node;

            if (propertyValue.Values.Count != 1 || propertyValue.Values[0] is not XamlAstTextNode text
                || !SvgDocumentContentHelper.IsMarkup(text.Text))
                return node;

            propertyValue.Values[0] = SvgDocumentContentHelper.CreateFactoryNode(context, propertyType, text);
            return node;
        }
    }

    /// <summary>
    /// Shared between <see cref="AvaloniaXamlIlSvgContentTransformer"/> (the
    /// early content path) and the <c>CustomValueConverter</c> hook (attribute
    /// values, setters — any string-to-SvgDocument conversion).
    /// </summary>
    internal static class SvgDocumentContentHelper
    {
        private const string DocumentTypeFullName = "Avalonia.Media.Svg.SvgDocument";
        private const string FactoryMethodName = "FromCompiledBlob";

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

        public static bool IsSvgDocumentType(IXamlType type) => type.FullName == DocumentTypeFullName;

        public static bool IsMarkup(string text)
        {
            foreach (var c in text)
            {
                if (c == '<')
                    return true;
                if (!char.IsWhiteSpace(c))
                    return false;
            }

            return false;
        }

        /// <summary>
        /// Validates and strips the markup, serializes it to the compiled-blob
        /// byte format and wraps it into a node that emits a
        /// <c>SvgDocument.FromCompiledBlob(ReadOnlySpan&lt;byte&gt;)</c> call.
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2122",
            Justification = "Resolving well-known BCL types by name for IL emission.")]
        public static IXamlAstValueNode CreateFactoryNode(
            AstTransformationContext context, IXamlType documentType, XamlAstTextNode text)
        {
            XElement root;
            var diagnostics = new List<SvgInlineDiagnostic>();
            try
            {
                root = ValidateAndStrip(text.Text, diagnostics);
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

            // Reference-integrity findings are warnings: the markup is valid SVG
            // and still compiles, but a reference resolves to nothing at runtime.
            // The diagnostic anchors at the content in the XAML file; the message
            // carries the position inside the SVG markup.
            foreach (var diagnostic in diagnostics)
            {
                var message = diagnostic.Line > 0
                    ? $"{diagnostic.Message} (inline SVG line {diagnostic.Line}, column {diagnostic.Column})"
                    : diagnostic.Message;

                context.ReportDiagnostic(new XamlDiagnostic(
                    diagnostic.Code, XamlDiagnosticSeverity.Warning, message, text));
            }

            var blob = SvgBlobBuilder.Write(root);

            var typeSystem = context.Configuration.TypeSystem;
            var spanOfByte = typeSystem.GetType("System.ReadOnlySpan`1")
                .MakeGenericType(typeSystem.GetType("System.Byte"));
            var factory = documentType.FindMethod(m =>
                    m.IsPublic && m.IsStatic && m.Name == FactoryMethodName
                    && m.ReturnType.Equals(documentType)
                    && m.Parameters.Count == 1 && m.Parameters[0].Equals(spanOfByte))
                ?? throw new XamlTransformException(
                    $"{DocumentTypeFullName}.{FactoryMethodName}(ReadOnlySpan<byte>) was not found; the referenced Avalonia.Svg version does not support compiled inline content.",
                    text);

            return new AvaloniaXamlIlSvgBlobAstNode(text, documentType, factory, blob);
        }

        /// <summary>
        /// Validates and strips SVG markup and serializes it to the compiled-blob
        /// byte format — the resource-compilation counterpart of
        /// <see cref="CreateFactoryNode"/>, used by the build task to pre-compile
        /// <c>.svg</c> resources into <see cref="SvgBlobBuilder"/> blobs that the
        /// runtime loads via <c>SvgDocument.FromCompiledBlob</c>. Throws on
        /// malformed markup; reference-integrity findings are ignored here (a
        /// resource is not authored in the XAML being compiled).
        /// </summary>
        public static byte[] CompileToBlob(string markup)
        {
            var root = ValidateAndStrip(markup, new List<SvgInlineDiagnostic>());
            return SvgBlobBuilder.Write(root);
        }

        private sealed class InvalidSvgContentException : Exception
        {
            public InvalidSvgContentException(string message) : base(message)
            {
            }
        }

        private static XElement ValidateAndStrip(string markup, List<SvgInlineDiagnostic> diagnostics)
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
                // SetLineInfo so reference diagnostics can point inside the markup.
                document = XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            }

            var root = document.Root;
            if (root == null || root.Name.LocalName != "svg")
                throw new InvalidSvgContentException(
                    "Inline SVG content must have an <svg> root element.");

            // Validate the authored document (references, value grammar) before the
            // strip drops editor cruft and whitespace.
            SvgInlineValidator.Collect(document, diagnostics);

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
            return root;
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
