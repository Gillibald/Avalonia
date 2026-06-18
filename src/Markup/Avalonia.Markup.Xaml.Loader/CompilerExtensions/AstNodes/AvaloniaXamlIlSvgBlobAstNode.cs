using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using XamlX.Ast;
using XamlX.Emit;
using XamlX.IL;
using XamlX.TypeSystem;

namespace Avalonia.Markup.Xaml.XamlIl.CompilerExtensions.AstNodes
{
    /// <summary>
    /// Emits a compiled inline-SVG payload as a call to
    /// <c>SvgDocument.FromCompiledBlob(ReadOnlySpan&lt;byte&gt;)</c>. The bytes are
    /// embedded with zero managed allocation when the backend supports field RVA
    /// (the Cecil build task — <see cref="IXamlIlFieldRvaEmitter"/>):
    /// <c>ldtoken &lt;rva-field&gt;</c> + <c>RuntimeHelpers.CreateSpan&lt;byte&gt;</c>
    /// hands the reader a span over the loaded image. Otherwise (the runtime
    /// System.Reflection.Emit loader, which cannot produce a field RVA) it falls
    /// back to a base64 string literal decoded with <c>Convert.FromBase64String</c>.
    /// </summary>
    class AvaloniaXamlIlSvgBlobAstNode : XamlAstNode, IXamlAstValueNode, IXamlAstILEmitableNode
    {
        private readonly IXamlMethod _fromCompiledBlob;
        private readonly byte[] _blob;

        public AvaloniaXamlIlSvgBlobAstNode(
            IXamlLineInfo lineInfo, IXamlType documentType, IXamlMethod fromCompiledBlob, byte[] blob)
            : base(lineInfo)
        {
            _fromCompiledBlob = fromCompiledBlob;
            _blob = blob;
            Type = new XamlAstClrTypeReference(lineInfo, documentType, false);
        }

        public IXamlAstTypeReference Type { get; }

        [UnconditionalSuppressMessage("Trimming", "IL2122",
            Justification = "Resolving well-known BCL types by name for IL emission.")]
        public XamlILNodeEmitResult Emit(
            XamlEmitContext<IXamlILEmitter, XamlILNodeEmitResult> context, IXamlILEmitter codeGen)
        {
            var types = codeGen.TypeSystem;
            var byteType = types.GetType("System.Byte");
            var spanOfByte = types.GetType("System.ReadOnlySpan`1").MakeGenericType(byteType);

            // Field-RVA path (Cecil build task): the blob lives in the assembly's
            // data section and CreateSpan<byte> reads it back zero-copy. CreateSpan
            // is net7+ (the supported floor); fall back to a base64 string literal
            // when it's absent or the emitter can't lay out field data (the runtime
            // System.Reflection.Emit loader). DefineFieldRvaData is queried through
            // the emitter (forwarded by wrappers such as CheckingILEmitter) and
            // returns null when unsupported. RuntimeHelpers has a single CreateSpan
            // overload — match by name/static/arity, since the Cecil backend doesn't
            // report a generic-definition flag for an externally-referenced method.
            var createSpan = types.GetType("System.Runtime.CompilerServices.RuntimeHelpers").FindMethod(m =>
                m.Name == "CreateSpan" && m.IsPublic && m.IsStatic && m.Parameters.Count == 1);
            var rvaField = createSpan is null
                ? null
                : (codeGen as IXamlIlFieldRvaEmitter)?.DefineFieldRvaData(_blob);

            if (rvaField is not null)
            {
                codeGen
                    .Emit(OpCodes.Ldtoken, rvaField)
                    .EmitCall(createSpan!.MakeGenericMethod(new[] { byteType }));
            }
            else
            {
                var fromBase64 = types.GetType("System.Convert").GetMethod(m =>
                    m.Name == "FromBase64String" && m.IsPublic && m.IsStatic && m.Parameters.Count == 1);
                var spanFromArray = spanOfByte.Constructors.First(c =>
                    c.IsPublic && c.Parameters.Count == 1 && c.Parameters[0].IsArray);

                codeGen
                    .Ldstr(Convert.ToBase64String(_blob))
                    .EmitCall(fromBase64)
                    .Newobj(spanFromArray);
            }

            codeGen.EmitCall(_fromCompiledBlob);
            return XamlILNodeEmitResult.Type(0, Type.GetClrType());
        }
    }
}
