using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Logging;
using Avalonia.Platform;

namespace Avalonia.Media.Fonts.Tables.Hvgl
{
    /// <summary>
    /// Outlines of an <c>hvgl</c> (Hierarchical Variable Font) table, drawn by Apple's libhvf, the
    /// system renderer for the format on macOS 15.4, iOS 18.4, tvOS 18.4 and Mac Catalyst 18.4 or later.
    /// Apple's system fonts are the only fonts that carry the table (PingFang UI, the Chinese system
    /// font), so a platform without the library gets no table and the face has no outlines.
    /// </summary>
    /// <remarks>
    /// One renderer per font, opened on the first glyph: it keeps the current part and axis values
    /// between calls, so every glyph is drawn under the table's lock. The table data must stay at
    /// a fixed, 8-byte aligned address while the renderer is open; it stays pinned until
    /// <see cref="Dispose"/>, which the owning typeface calls before it releases the font memory.
    /// </remarks>
    internal sealed unsafe class HvglTable : IDisposable
    {
        internal static OpenTypeTag Tag { get; } = OpenTypeTag.Parse("hvgl");

        internal static OpenTypeTag PartMapTag { get; } = OpenTypeTag.Parse("hvpm");

        // The renderer's part cache grows with every part drawn; the library advises clearing it
        // every dozen or so parts.
        private const int PartsPerCacheClear = 16;

        private const int ActionContinue = 0;
        private const int ActionStop = 2;

        private static readonly Lazy<HvfLibrary?> s_library = new(HvfLibrary.TryLoad, LazyThreadSafetyMode.ExecutionAndPublication);

        private readonly ReadOnlyMemory<byte> _hvgl;
        private readonly ReadOnlyMemory<byte> _hvpm;
        private readonly int _axisCount;
        private readonly object _lock = new();
        private readonly HvglOutlineSink _sink = new();

        private MemoryHandle _hvglPin;
        private MemoryHandle _hvpmPin;
        private void* _alignedHvgl;
        private void* _storage;
        private GCHandle _sinkHandle;
        private bool _opened;
        private bool _openFailed;
        private bool _disposed;
        private int _partsSinceCacheClear;

        private HvglTable(ReadOnlyMemory<byte> hvgl, ReadOnlyMemory<byte> hvpm, int axisCount)
        {
            _hvgl = hvgl;
            _hvpm = hvpm;
            _axisCount = axisCount;
        }

        /// <summary>
        /// Whether the system provides the hvgl renderer; checked once per process.
        /// </summary>
        internal static bool IsSupported => s_library.Value is not null;

        public static bool TryLoad(GlyphTypeface glyphTypeface, int axisCount, out HvglTable? table)
        {
            table = null;

            if (!glyphTypeface.FontMemory.TryGetTable(Tag, out var hvgl) || hvgl.Length < 24 || !IsSupported)
            {
                return false;
            }

            glyphTypeface.FontMemory.TryGetTable(PartMapTag, out var hvpm);

            table = new HvglTable(hvgl, hvpm, axisCount);

            return true;
        }

        /// <summary>
        /// Streams the outline of a glyph at the given normalized variation coordinates (fvar axis
        /// order; empty for the default instance) into <paramref name="context"/>, with
        /// <paramref name="transform"/> applied to every point.
        /// </summary>
        public bool TryBuildGlyphGeometry(int glyphIndex, Matrix transform, IGeometryContext context,
            ReadOnlySpan<float> activeCoords)
        {
            if (glyphIndex < 0)
            {
                return false;
            }

            context.SetFillRule(FillRule.NonZero);

            lock (_lock)
            {
                if (!EnsureOpen())
                {
                    return false;
                }

                var library = s_library.Value!;
                var renderer = _storage;

                if (library.SetRenderPart(renderer, (nuint)glyphIndex) != 0)
                {
                    return false;
                }

                // Selecting a part resets every axis to its default (0).
                var axisCount = Math.Min(activeCoords.Length, _axisCount);

                for (var axis = 0; axis < axisCount; axis++)
                {
                    var value = activeCoords[axis];

                    if (value != 0 && library.SetAxisValue(renderer, axis, value) != 0)
                    {
                        return false;
                    }
                }

                _sink.Begin(context, transform);

                int result;

                try
                {
                    result = library.RenderCurrentPart(renderer, &OnRenderInstruction,
                        (void*)GCHandle.ToIntPtr(_sinkHandle));
                }
                finally
                {
                    _sink.End();
                }

                if (++_partsSinceCacheClear >= PartsPerCacheClear)
                {
                    library.ClearPartCache(renderer);
                    _partsSinceCacheClear = 0;
                }

                if (result != 0 || _sink.Failed)
                {
                    if (Logger.TryGet(LogEventLevel.Warning, LogArea.Visual, out var log))
                    {
                        log.Log(null, "Failed to build hvgl glyph {GlyphIndex}: renderer result {Result}", glyphIndex, result);
                    }

                    return false;
                }

                return true;
            }
        }

        /// <summary>The control-point box of a glyph at the given variation coordinates.</summary>
        public bool TryGetGlyphBounds(int glyphIndex, ReadOnlySpan<float> activeCoords, out GlyphBounds bounds)
        {
            bounds = default;

            var context = new BoundsGeometryContext();

            if (!TryBuildGlyphGeometry(glyphIndex, Matrix.Identity, context, activeCoords))
            {
                return false;
            }

            bounds = context.ToGlyphBounds();

            return true;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                if (_opened)
                {
                    s_library.Value!.CloseRenderer(_storage);
                    _opened = false;
                }

                if (_storage != null)
                {
                    NativeMemory.AlignedFree(_storage);
                    _storage = null;
                }

                if (_alignedHvgl != null)
                {
                    NativeMemory.AlignedFree(_alignedHvgl);
                    _alignedHvgl = null;
                }

                _hvglPin.Dispose();
                _hvpmPin.Dispose();

                if (_sinkHandle.IsAllocated)
                {
                    _sinkHandle.Free();
                }
            }
        }

        /// <summary>Opens the renderer on first use; called under the lock.</summary>
        private bool EnsureOpen()
        {
            if (_opened)
            {
                return true;
            }

            if (_openFailed || _disposed)
            {
                return false;
            }

            var library = s_library.Value!;

            try
            {
                _hvglPin = _hvgl.Pin();

                var hvgl = _hvglPin.Pointer;

                // The renderer reads the table as doubles in place. Apple's fonts keep the table on
                // an 8-byte boundary, which a mapped file preserves; a table at any other address
                // is copied once.
                if (((nuint)hvgl & 7) != 0)
                {
                    _alignedHvgl = NativeMemory.AlignedAlloc((nuint)_hvgl.Length, 8);
                    _hvgl.Span.CopyTo(new Span<byte>(_alignedHvgl, _hvgl.Length));
                    hvgl = _alignedHvgl;
                }

                void* hvpm = null;

                if (!_hvpm.IsEmpty)
                {
                    _hvpmPin = _hvpm.Pin();
                    hvpm = _hvpmPin.Pointer;
                }

                var storageSize = library.StorageSize();

                _storage = NativeMemory.AlignedAlloc(storageSize, 16);

                if (library.OpenRenderer(hvgl, (nuint)_hvgl.Length, hvpm, (nuint)_hvpm.Length, _storage, storageSize) != 0)
                {
                    _openFailed = true;

                    return false;
                }

                _sinkHandle = GCHandle.Alloc(_sink);
                _opened = true;

                return true;
            }
            catch (Exception ex)
            {
                _openFailed = true;

                if (Logger.TryGet(LogEventLevel.Warning, LogArea.Visual, out var log))
                {
                    log.Log(null, "Failed to open the hvgl renderer: {Message}", ex.Message);
                }

                return false;
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static int OnRenderInstruction(int instruction, double* parameters, void* callerData)
        {
            try
            {
                var sink = (HvglOutlineSink)GCHandle.FromIntPtr((IntPtr)callerData).Target!;

                // The parameter block is a union whose largest member, a cubic, is six doubles.
                var count = (HvglRenderInstruction)instruction switch
                {
                    HvglRenderInstruction.AddPoint or HvglRenderInstruction.AddLine => 2,
                    HvglRenderInstruction.AddQuad => 4,
                    HvglRenderInstruction.AddCubic => 6,
                    _ => 0
                };

                var span = count > 0 && parameters != null
                    ? new ReadOnlySpan<double>(parameters, count)
                    : ReadOnlySpan<double>.Empty;

                return sink.Apply((HvglRenderInstruction)instruction, span) ? ActionContinue : ActionStop;
            }
            catch
            {
                return ActionStop;
            }
        }

        /// <summary>The entry points of libhvf, resolved once.</summary>
        private sealed class HvfLibrary
        {
            private const string LibraryPath = "/usr/lib/libhvf.dylib";

            private readonly delegate* unmanaged[Cdecl]<nuint> _storageSize;
            private readonly delegate* unmanaged[Cdecl]<void*, nuint, void*, nuint, void*, nuint, int> _openRenderer;
            private readonly delegate* unmanaged[Cdecl]<void*, nuint, int> _setRenderPart;
            private readonly delegate* unmanaged[Cdecl]<void*, int, double, int> _setAxisValue;
            private readonly delegate* unmanaged[Cdecl]<void*, delegate* unmanaged[Cdecl]<int, double*, void*, int>, void*, int> _renderCurrentPart;
            private readonly delegate* unmanaged[Cdecl]<void*, int> _clearPartCache;
            private readonly delegate* unmanaged[Cdecl]<void*, int> _closeRenderer;

            private HvfLibrary(IntPtr handle)
            {
                _storageSize = (delegate* unmanaged[Cdecl]<nuint>)NativeLibrary.GetExport(handle, "HVF_part_renderer_storage_size");
                _openRenderer = (delegate* unmanaged[Cdecl]<void*, nuint, void*, nuint, void*, nuint, int>)NativeLibrary.GetExport(handle, "HVF_open_part_renderer");
                _setRenderPart = (delegate* unmanaged[Cdecl]<void*, nuint, int>)NativeLibrary.GetExport(handle, "HVF_set_render_part");
                _setAxisValue = (delegate* unmanaged[Cdecl]<void*, int, double, int>)NativeLibrary.GetExport(handle, "HVF_set_axis_value");
                _renderCurrentPart = (delegate* unmanaged[Cdecl]<void*, delegate* unmanaged[Cdecl]<int, double*, void*, int>, void*, int>)NativeLibrary.GetExport(handle, "HVF_render_current_part");
                _clearPartCache = (delegate* unmanaged[Cdecl]<void*, int>)NativeLibrary.GetExport(handle, "HVF_clear_part_cache");
                _closeRenderer = (delegate* unmanaged[Cdecl]<void*, int>)NativeLibrary.GetExport(handle, "HVF_close_part_renderer");
            }

            public static HvfLibrary? TryLoad()
            {
                if (!OperatingSystem.IsMacOSVersionAtLeast(15, 4) &&
                    !OperatingSystem.IsIOSVersionAtLeast(18, 4) &&
                    !OperatingSystem.IsTvOSVersionAtLeast(18, 4) &&
                    !OperatingSystem.IsMacCatalystVersionAtLeast(18, 4))
                {
                    return null;
                }

                try
                {
                    return NativeLibrary.TryLoad(LibraryPath, out var handle) ? new HvfLibrary(handle) : null;
                }
                catch (EntryPointNotFoundException)
                {
                    return null;
                }
            }

            public nuint StorageSize() => _storageSize();

            public int OpenRenderer(void* hvgl, nuint hvglSize, void* hvpm, nuint hvpmSize, void* storage, nuint storageSize)
                => _openRenderer(hvgl, hvglSize, hvpm, hvpmSize, storage, storageSize);

            public int SetRenderPart(void* renderer, nuint part) => _setRenderPart(renderer, part);

            public int SetAxisValue(void* renderer, int axis, double value) => _setAxisValue(renderer, axis, value);

            public int RenderCurrentPart(void* renderer, delegate* unmanaged[Cdecl]<int, double*, void*, int> context, void* callerData)
                => _renderCurrentPart(renderer, context, callerData);

            public int ClearPartCache(void* renderer) => _clearPartCache(renderer);

            public int CloseRenderer(void* renderer) => _closeRenderer(renderer);
        }
    }
}
