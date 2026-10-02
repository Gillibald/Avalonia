using System;
using System.Runtime.CompilerServices;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// The GL entry points glyph atlas pages are kept and updated with on a GL-backed
    /// <see cref="GRContext"/>, looked up once per context by whoever created it.
    /// </summary>
    /// <remarks>
    /// A page texture is R8 and is updated from a sub-rectangle of the page's array, which needs
    /// <c>GL_UNPACK_ROW_LENGTH</c>: desktop GL 3.0 or GLES 3.0 and later have both. Contexts
    /// without a registration, or with older GL, keep drawing pages from raster images.
    /// </remarks>
    internal sealed unsafe class GlPageTextureApi
    {
        public const uint Texture2D = 0x0DE1;
        public const uint R8 = 0x8229;

        private const int Red = 0x1903;
        private const int UnsignedByte = 0x1401;
        private const int UnpackRowLength = 0x0CF2;
        private const int UnpackAlignment = 0x0CF5;
        private const int PixelUnpackBuffer = 0x88EC;
        private const int TextureMinFilter = 0x2801;
        private const int TextureMagFilter = 0x2800;
        private const int Nearest = 0x2600;

        private static readonly ConditionalWeakTable<GRContext, GlPageTextureApi> s_apis = new();

        private readonly delegate* unmanaged[Stdcall]<int, uint*, void> _genTextures;
        private readonly delegate* unmanaged[Stdcall]<int, uint*, void> _deleteTextures;
        private readonly delegate* unmanaged[Stdcall]<int, uint, void> _bindTexture;
        private readonly delegate* unmanaged[Stdcall]<int, uint, void> _bindBuffer;
        private readonly delegate* unmanaged[Stdcall]<int, int, int, void> _texParameteri;
        private readonly delegate* unmanaged[Stdcall]<int, int, void> _pixelStorei;
        private readonly delegate* unmanaged[Stdcall]<int, int, int, int, int, int, int, int, void*, void> _texImage2D;
        private readonly delegate* unmanaged[Stdcall]<int, int, int, int, int, int, int, int, void*, void> _texSubImage2D;

        private GlPageTextureApi(IntPtr[] entries)
        {
            _genTextures = (delegate* unmanaged[Stdcall]<int, uint*, void>)entries[0];
            _deleteTextures = (delegate* unmanaged[Stdcall]<int, uint*, void>)entries[1];
            _bindTexture = (delegate* unmanaged[Stdcall]<int, uint, void>)entries[2];
            _bindBuffer = (delegate* unmanaged[Stdcall]<int, uint, void>)entries[3];
            _texParameteri = (delegate* unmanaged[Stdcall]<int, int, int, void>)entries[4];
            _pixelStorei = (delegate* unmanaged[Stdcall]<int, int, void>)entries[5];
            _texImage2D = (delegate* unmanaged[Stdcall]<int, int, int, int, int, int, int, int, void*, void>)entries[6];
            _texSubImage2D = (delegate* unmanaged[Stdcall]<int, int, int, int, int, int, int, int, void*, void>)entries[7];
        }

        private static readonly string[] s_names =
        {
            "glGenTextures", "glDeleteTextures", "glBindTexture", "glBindBuffer", "glTexParameteri",
            "glPixelStorei", "glTexImage2D", "glTexSubImage2D",
        };

        /// <summary>
        /// Looks up the entry points for <paramref name="context"/> when its GL is
        /// <paramref name="majorVersion"/> 3 or later; otherwise, or when one is missing, pages on
        /// that context stay raster images.
        /// </summary>
        public static void Register(GRContext context, Func<string, IntPtr> getProcAddress, int majorVersion)
        {
            if (majorVersion < 3 || OperatingSystem.IsBrowser())
            {
                s_apis.Remove(context);
                return;
            }

            var entries = new IntPtr[s_names.Length];

            for (var i = 0; i < entries.Length; i++)
            {
                entries[i] = getProcAddress(s_names[i]);

                if (entries[i] == IntPtr.Zero)
                {
                    s_apis.Remove(context);
                    return;
                }
            }

            s_apis.AddOrUpdate(context, new GlPageTextureApi(entries));
        }

        /// <summary>The entry points registered for <paramref name="context"/>, unless it is lost.</summary>
        public static GlPageTextureApi? Get(GRContext context) =>
            !IsLost(context) && s_apis.TryGetValue(context, out var api) ? api : null;

        // A disposed context has no handle left to ask; its owner abandons it first.
        public static bool IsLost(GRContext context) => context.Handle == IntPtr.Zero || context.IsAbandoned;

        /// <summary>Makes an R8 texture holding <paramref name="height"/> rows of <paramref name="pixels"/>; 0 on failure.</summary>
        public uint Create(byte[] pixels, int width, int height)
        {
            uint id;

            _genTextures(1, &id);

            if (id == 0)
            {
                return 0;
            }

            _bindBuffer(PixelUnpackBuffer, 0);
            _bindTexture((int)Texture2D, id);
            _texParameteri((int)Texture2D, TextureMinFilter, Nearest);
            _texParameteri((int)Texture2D, TextureMagFilter, Nearest);
            _pixelStorei(UnpackAlignment, 1);
            _pixelStorei(UnpackRowLength, 0);

            fixed (byte* p = pixels)
            {
                _texImage2D((int)Texture2D, 0, (int)R8, width, height, 0, Red, UnsignedByte, p);
            }

            return id;
        }

        /// <summary>Uploads a rectangle of <paramref name="pixels"/>, rows <paramref name="stride"/> bytes apart, into the texture.</summary>
        public void Upload(uint id, byte[] pixels, int stride, int x, int y, int width, int height)
        {
            _bindBuffer(PixelUnpackBuffer, 0);
            _bindTexture((int)Texture2D, id);
            _pixelStorei(UnpackAlignment, 1);
            _pixelStorei(UnpackRowLength, stride);

            fixed (byte* p = pixels)
            {
                _texSubImage2D((int)Texture2D, 0, x, y, width, height, Red, UnsignedByte, p + y * stride + x);
            }

            _pixelStorei(UnpackRowLength, 0);
        }

        /// <summary>A GL texture of one context, deleted when Skia releases its wrapper.</summary>
        public sealed class Texture
        {
            private readonly GlPageTextureApi _gl;
            private readonly uint _id;
            private readonly GRContext _context;

            public Texture(GlPageTextureApi gl, uint id, GRContext context)
            {
                _gl = gl;
                _id = id;
                _context = context;
            }

            /// <summary>
            /// Deletes the texture; Skia calls this on the thread that drops its last use. A lost
            /// context took its textures with it.
            /// </summary>
            public void Delete()
            {
                if (IsLost(_context))
                {
                    return;
                }

                var id = _id;

                _gl._deleteTextures(1, &id);
            }
        }
    }
}
