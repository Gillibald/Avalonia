using System;
using System.IO;

namespace Avalonia.Platform.Internal
{
    /// <summary>
    /// Stream plumbing shared by the imaging backends.
    /// </summary>
    internal static class ImagingStreamHelper
    {
        /// <summary>
        /// Reads up to <paramref name="maxBytes"/> from the stream, tolerating streams
        /// that serve partial reads.
        /// </summary>
        public static byte[] ReadPrefix(Stream stream, int maxBytes)
        {
            var buffer = new byte[maxBytes];
            var total = 0;

            while (total < maxBytes)
            {
                var read = stream.Read(buffer, total, maxBytes - total);

                if (read == 0)
                    break;

                total += read;
            }

            if (total == maxBytes)
                return buffer;

            var exact = new byte[total];

            Buffer.BlockCopy(buffer, 0, exact, 0, total);

            return exact;
        }
    }
}
