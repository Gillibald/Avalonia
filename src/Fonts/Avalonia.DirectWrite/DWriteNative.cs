using System;
using System.Runtime.InteropServices;

namespace Avalonia.Media.Fonts
{
    internal static class DWriteNative
    {
        // Values verified against the Windows SDK 10.0.26100 headers.
        public const int FactoryTypeShared = 0;

        public const int FontStyleNormal = 0;
        public const int FontStyleOblique = 1;
        public const int FontStyleItalic = 2;

        public const int FontSimulationsNone = 0;

        public static readonly Guid IID_IDWriteFactory = new("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");

        private const uint SPI_GETNONCLIENTMETRICS = 0x0029;

        [DllImport("dwrite.dll")]
        public static extern int DWriteCreateFactory(int factoryType, ref Guid iid, out IntPtr factory);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SystemParametersInfoW(uint action, uint param, ref NONCLIENTMETRICSW metrics, uint winIni);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct LOGFONTW
        {
            public int lfHeight;
            public int lfWidth;
            public int lfEscapement;
            public int lfOrientation;
            public int lfWeight;
            public byte lfItalic;
            public byte lfUnderline;
            public byte lfStrikeOut;
            public byte lfCharSet;
            public byte lfOutPrecision;
            public byte lfClipPrecision;
            public byte lfQuality;
            public byte lfPitchAndFamily;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string lfFaceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NONCLIENTMETRICSW
        {
            public uint cbSize;
            public int iBorderWidth;
            public int iScrollWidth;
            public int iScrollHeight;
            public int iCaptionWidth;
            public int iCaptionHeight;
            public LOGFONTW lfCaptionFont;
            public int iSmCaptionWidth;
            public int iSmCaptionHeight;
            public LOGFONTW lfSmCaptionFont;
            public int iMenuWidth;
            public int iMenuHeight;
            public LOGFONTW lfMenuFont;
            public LOGFONTW lfStatusFont;
            public LOGFONTW lfMessageFont;
            public int iPaddedBorderWidth;
        }

        /// <summary>
        /// Gets the family name of the system's message font (the Windows UI font), or
        /// <see langword="null"/> when the metrics cannot be read.
        /// </summary>
        public static string? GetMessageFontFamilyName()
        {
            var metrics = default(NONCLIENTMETRICSW);

            metrics.cbSize = (uint)Marshal.SizeOf<NONCLIENTMETRICSW>();

            if (!SystemParametersInfoW(SPI_GETNONCLIENTMETRICS, metrics.cbSize, ref metrics, 0))
            {
                return null;
            }

            return string.IsNullOrEmpty(metrics.lfMessageFont.lfFaceName) ? null : metrics.lfMessageFont.lfFaceName;
        }
    }
}
