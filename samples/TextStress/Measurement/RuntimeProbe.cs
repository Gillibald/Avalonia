using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Skia;

namespace TextStress.Measurement
{
    /// <summary>
    /// The runtime and the SIMD paths the managed rasterizer and blitters resolved to. On a
    /// runtime that executes the vector APIs in software the vector paths are slower than the
    /// scalar one, so every measurement records what it ran on.
    /// </summary>
    internal static class RuntimeProbe
    {
        public static IReadOnlyList<(string Key, string Value)> Describe()
        {
            var entries = new List<(string, string)>
            {
                ("runtime_flavor", Flavor()),
                ("runtime", RuntimeInformation.FrameworkDescription),
                ("runtime_identifier", RuntimeInformation.RuntimeIdentifier),
                ("os", RuntimeInformation.OSDescription),
                ("arch", RuntimeInformation.ProcessArchitecture.ToString()),
                ("dynamic_code_supported", RuntimeFeature.IsDynamicCodeSupported.ToString()),
                ("dynamic_code_compiled", RuntimeFeature.IsDynamicCodeCompiled.ToString()),
                ("vector128_accelerated", Vector128.IsHardwareAccelerated.ToString()),
                ("vector256_accelerated", Vector256.IsHardwareAccelerated.ToString()),
                ("armbase", ArmBase.IsSupported.ToString()),
                ("armbase_arm64", ArmBase.Arm64.IsSupported.ToString()),
                ("advsimd", AdvSimd.IsSupported.ToString()),
                ("advsimd_arm64", AdvSimd.Arm64.IsSupported.ToString()),
                ("sse41", Sse41.IsSupported.ToString()),
                ("avx2", Avx2.IsSupported.ToString()),
                ("raster_path", GlyphRasterizer.Path.ToString()),
                ("raster_portable_min_cells",
                    GlyphRasterizer.GetPortableMinimumCells(RuntimeInformation.ProcessArchitecture).ToString()),
                ("blit_path", GlyphMaskBlitter.Path.ToString()),
                ("blit_arithmetic", BlitArithmetic())
            };

            return entries;
        }

        /// <summary>Mono defines <c>Mono.RuntimeStructs</c>; CoreCLR and NativeAOT do not.</summary>
        private static string Flavor()
        {
            if (Type.GetType("Mono.RuntimeStructs") is not null)
            {
                return "mono";
            }

            return RuntimeFeature.IsDynamicCodeSupported ? "coreclr" : "nativeaot";
        }

        /// <summary>
        /// The rounding the Skia surfaces' blitter assumes, per surface byte order. The choice is a
        /// private detail of <see cref="DrawingContextImpl"/>, read here through reflection so the
        /// probe shows what the renderer resolves rather than a copy of its rule.
        /// </summary>
        private static string BlitArithmetic()
        {
            try
            {
                var type = typeof(DrawingContextImpl);
                var kind = type.GetNestedType("BlitSurfaceKind", BindingFlags.NonPublic);
                var method = type.GetMethod("GetBlitArithmetic", BindingFlags.NonPublic | BindingFlags.Static);

                if (kind is null || method is null)
                {
                    return "unknown";
                }

                return "bgra " + method.Invoke(null, new[] { Enum.Parse(kind, "Bgra") }) +
                       ", rgba " + method.Invoke(null, new[] { Enum.Parse(kind, "Rgba") });
            }
            catch (Exception e)
            {
                return "error: " + e.GetType().Name;
            }
        }
    }
}
