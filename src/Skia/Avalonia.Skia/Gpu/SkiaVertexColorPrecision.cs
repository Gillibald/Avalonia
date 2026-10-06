using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// Remembers the GPU contexts on which Skia draws a translucent per-vertex colour exactly as
    /// it draws the same colour given as the paint colour.
    /// </summary>
    /// <remarks>
    /// Skia premultiplies a per-vertex colour in the fragment shader, from a <c>half4</c>
    /// varying, while it premultiplies a paint colour on the CPU in single precision. Where the
    /// GPU computes <c>half</c> at single precision both give the same bytes. Where it computes
    /// <c>half</c> in 16 bits, as Vulkan and Metal drivers may for relaxed-precision values and
    /// GLES drivers do for <c>mediump</c>, a translucent colour comes out one level off in places;
    /// opaque colours premultiply by one and stay exact. A context nobody registered counts as
    /// inexact.
    /// </remarks>
    internal static unsafe class SkiaVertexColorPrecision
    {
        private const int GlVersion = 0x1F02;
        private const int GlFragmentShader = 0x8B30;
        private const int GlMediumFloat = 0x8DF1;

        // Bits of mantissa a single-precision float carries.
        private const int SinglePrecisionBits = 23;

        private static readonly ConditionalWeakTable<GRContext, object> s_exact = new();
        private static readonly object s_marker = new();

        // Mono on WebAssembly calls a native function pointer through a trampoline generated per
        // signature at build time, from P/Invokes and delegates marked with
        // UnmanagedFunctionPointer only. These delegates are never used; they declare the
        // signatures of the two GL calls below.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte* WasmGetString(int name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void WasmShaderPrecision(int shader, int precision, int* range, int* bits);

        /// <summary>Records whether <paramref name="context"/> draws translucent per-vertex colours exactly.</summary>
        public static void Register(GRContext context, bool exact)
        {
            if (exact)
            {
                s_exact.AddOrUpdate(context, s_marker);
            }
            else
            {
                s_exact.Remove(context);
            }
        }

        /// <summary>Whether <paramref name="context"/> was registered as drawing translucent per-vertex colours exactly.</summary>
        public static bool IsExact(GRContext context) => s_exact.TryGetValue(context, out _);

        /// <summary>
        /// Whether Skia's shaders on the current GL context compute <c>half</c> at single
        /// precision: always on desktop GL, whose GLSL has no lower precision, and on GLES when
        /// the fragment shader's <c>mediump</c> float carries a single-precision mantissa.
        /// </summary>
        /// <param name="getProcAddress">Resolves a GL entry point of the current context.</param>
        [DynamicDependency("Invoke", typeof(WasmGetString))]
        [DynamicDependency("Invoke", typeof(WasmShaderPrecision))]
        public static bool IsExactOnGl(Func<string, IntPtr> getProcAddress)
        {
            var getString = (delegate* unmanaged[Stdcall]<int, byte*>)getProcAddress("glGetString");

            if (getString is null)
            {
                return false;
            }

            var version = Marshal.PtrToStringAnsi((IntPtr)getString(GlVersion));

            if (version is null)
            {
                return false;
            }

            if (!version.StartsWith("OpenGL ES", StringComparison.Ordinal))
            {
                return true;
            }

            var getPrecision = (delegate* unmanaged[Stdcall]<int, int, int*, int*, void>)
                getProcAddress("glGetShaderPrecisionFormat");

            if (getPrecision is null)
            {
                return false;
            }

            var range = stackalloc int[2];
            var bits = 0;

            getPrecision(GlFragmentShader, GlMediumFloat, range, &bits);

            return bits >= SinglePrecisionBits;
        }
    }
}
