using System;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// R8 GL textures of one GL-backed <see cref="GRContext"/>, updated with
    /// <c>glTexSubImage2D</c>.
    /// </summary>
    /// <remarks>
    /// An update reads a sub-rectangle of a larger CPU buffer, which needs
    /// <c>GL_UNPACK_ROW_LENGTH</c>: desktop GL 3.0 or GLES 3.0 and later have both. Skia's cached
    /// GL state is reset after every call that binds a texture.
    /// </remarks>
    internal sealed unsafe class GlUpdatableTextureFeature : ISkiaUpdatableTextureFeature
    {
        private const uint Texture2D = 0x0DE1;
        private const uint R8 = 0x8229;

        private const int Red = 0x1903;
        private const int UnsignedByte = 0x1401;
        private const int UnpackRowLength = 0x0CF2;
        private const int UnpackAlignment = 0x0CF5;
        private const int PixelUnpackBuffer = 0x88EC;
        private const int TextureMinFilter = 0x2801;
        private const int TextureMagFilter = 0x2800;
        private const int Nearest = 0x2600;

        private static readonly string[] s_names =
        {
            "glGenTextures", "glDeleteTextures", "glBindTexture", "glBindBuffer", "glTexParameteri",
            "glPixelStorei", "glTexImage2D", "glTexSubImage2D",
        };

        private static readonly SKImageTextureReleaseDelegate s_release = static state =>
            ((Texture)state).Delete();

        private readonly GRContext _context;
        private readonly delegate* unmanaged[Stdcall]<int, uint*, void> _genTextures;
        private readonly delegate* unmanaged[Stdcall]<int, uint*, void> _deleteTextures;
        private readonly delegate* unmanaged[Stdcall]<int, uint, void> _bindTexture;
        private readonly delegate* unmanaged[Stdcall]<int, uint, void> _bindBuffer;
        private readonly delegate* unmanaged[Stdcall]<int, int, int, void> _texParameteri;
        private readonly delegate* unmanaged[Stdcall]<int, int, void> _pixelStorei;
        private readonly delegate* unmanaged[Stdcall]<int, int, int, int, int, int, int, int, void*, void> _texImage2D;
        private readonly delegate* unmanaged[Stdcall]<int, int, int, int, int, int, int, int, void*, void> _texSubImage2D;

        private GlUpdatableTextureFeature(GRContext context, IntPtr[] entries)
        {
            _context = context;
            _genTextures = (delegate* unmanaged[Stdcall]<int, uint*, void>)entries[0];
            _deleteTextures = (delegate* unmanaged[Stdcall]<int, uint*, void>)entries[1];
            _bindTexture = (delegate* unmanaged[Stdcall]<int, uint, void>)entries[2];
            _bindBuffer = (delegate* unmanaged[Stdcall]<int, uint, void>)entries[3];
            _texParameteri = (delegate* unmanaged[Stdcall]<int, int, int, void>)entries[4];
            _pixelStorei = (delegate* unmanaged[Stdcall]<int, int, void>)entries[5];
            _texImage2D = (delegate* unmanaged[Stdcall]<int, int, int, int, int, int, int, int, void*, void>)entries[6];
            _texSubImage2D = (delegate* unmanaged[Stdcall]<int, int, int, int, int, int, int, int, void*, void>)entries[7];
        }

        /// <summary>
        /// Looks up the entry points for <paramref name="context"/> when its GL is
        /// <paramref name="majorVersion"/> 3 or later.
        /// </summary>
        /// <param name="context">The context drawing the textures.</param>
        /// <param name="getProcAddress">Resolves a GL entry point of the context.</param>
        /// <param name="majorVersion">The major version of the context's GL or GLES.</param>
        /// <returns>The feature, or <c>null</c> for older GL, the browser, or a missing entry point.</returns>
        public static GlUpdatableTextureFeature? TryCreate(GRContext context, Func<string, IntPtr> getProcAddress,
            int majorVersion)
        {
            if (majorVersion < 3 || OperatingSystem.IsBrowser())
            {
                return null;
            }

            var entries = new IntPtr[s_names.Length];

            for (var i = 0; i < entries.Length; i++)
            {
                entries[i] = getProcAddress(s_names[i]);

                if (entries[i] == IntPtr.Zero)
                {
                    return null;
                }
            }

            return new GlUpdatableTextureFeature(context, entries);
        }

        public ISkiaUpdatableTexture? TryCreateAlpha8(int width, int height, ReadOnlySpan<byte> pixels, int rowBytes)
        {
            uint id;

            _genTextures(1, &id);

            if (id != 0)
            {
                _bindBuffer(PixelUnpackBuffer, 0);
                _bindTexture((int)Texture2D, id);
                _texParameteri((int)Texture2D, TextureMinFilter, Nearest);
                _texParameteri((int)Texture2D, TextureMagFilter, Nearest);
                _pixelStorei(UnpackAlignment, 1);
                _pixelStorei(UnpackRowLength, rowBytes == width ? 0 : rowBytes);

                fixed (byte* p = pixels)
                {
                    _texImage2D((int)Texture2D, 0, (int)R8, width, height, 0, Red, UnsignedByte, p);
                }

                if (rowBytes != width)
                {
                    _pixelStorei(UnpackRowLength, 0);
                }
            }

            _context.ResetContext(GRGlBackendState.All);

            if (id == 0)
            {
                return null;
            }

            var texture = new Texture(this, id);
            SKImage? image;

            using (var backend = new GRBackendTexture(width, height, false, new GRGlTextureInfo(Texture2D, id, R8)))
            {
                image = SKImage.FromTexture(_context, backend, GRSurfaceOrigin.TopLeft, SKColorType.Alpha8,
                    SKAlphaType.Premul, null, s_release, texture);
            }

            if (image is null)
            {
                texture.Delete();
                return null;
            }

            texture.Image = image;

            return texture;
        }

        private void Upload(uint id, PixelRect rect, ReadOnlySpan<byte> source, int rowBytes)
        {
            _bindBuffer(PixelUnpackBuffer, 0);
            _bindTexture((int)Texture2D, id);
            _pixelStorei(UnpackAlignment, 1);
            _pixelStorei(UnpackRowLength, rowBytes);

            fixed (byte* p = source)
            {
                _texSubImage2D((int)Texture2D, 0, rect.X, rect.Y, rect.Width, rect.Height, Red, UnsignedByte,
                    p + rect.Y * rowBytes + rect.X);
            }

            _pixelStorei(UnpackRowLength, 0);
            _context.ResetContext(GRGlBackendState.All);
        }

        /// <summary>A GL texture of the context, deleted when Skia releases its wrapper.</summary>
        private sealed class Texture : ISkiaUpdatableTexture
        {
            private readonly GlUpdatableTextureFeature _gl;
            private readonly uint _id;

            public Texture(GlUpdatableTextureFeature gl, uint id)
            {
                _gl = gl;
                _id = id;
            }

            public SKImage Image { get; set; } = null!;

            public void Update(PixelRect rect, ReadOnlySpan<byte> source, int rowBytes) =>
                _gl.Upload(_id, rect, source, rowBytes);

            /// <summary>Drops the wrapper; Skia deletes the texture once no recorded draw uses it.</summary>
            public void Dispose() => Image.Dispose();

            /// <summary>
            /// Deletes the texture; Skia calls this on the thread that drops its last use. A lost
            /// context took its textures with it.
            /// </summary>
            public void Delete()
            {
                if (SkiaUpdatableTextures.IsLost(_gl._context))
                {
                    return;
                }

                var id = _id;

                _gl._deleteTextures(1, &id);
            }
        }
    }
}
