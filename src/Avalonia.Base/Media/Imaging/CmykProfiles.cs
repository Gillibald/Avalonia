using System;
using System.IO;

namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// CMYK ICC profiles shipped with Avalonia, for <see cref="ImagingOptions.CmykProfile"/>.
    /// </summary>
    public static class CmykProfiles
    {
        /// <summary>
        /// Gets a copy of a SWOP coated printer profile (U.S. web offset publication printing on grade 3
        /// coated paper), built from the ANSI CGATS/SWOP TR 003-2007 characterization data:
        /// relative colorimetric, with black point compensation so the deepest ink is black.
        /// </summary>
        /// <remarks>
        /// It renders CMYK images close to Windows' default CMYK conversion, which uses a SWOP
        /// profile of its own: CMYK through this profile lands a median of 7 sRGB levels from WIC,
        /// where the analytic conversion lands 19.
        /// </remarks>
        public static byte[] SwopCoated => Load("SwopCoated.icc");

        private static byte[] Load(string name)
        {
            using var stream = typeof(CmykProfiles).Assembly.GetManifestResourceStream(
                "Avalonia.Media.Imaging.Profiles." + name)
                ?? throw new InvalidOperationException($"The {name} profile resource is missing.");
            using var copy = new MemoryStream((int)stream.Length);

            stream.CopyTo(copy);

            return copy.ToArray();
        }
    }
}
