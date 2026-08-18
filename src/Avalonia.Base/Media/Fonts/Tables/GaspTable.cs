using System;

namespace Avalonia.Media.Fonts.Tables
{
    /// <summary>
    /// The grid-fitting and scan-conversion procedure table: per-ppem ranges telling the
    /// rasterizer whether the font wants hinting and/or smoothing at that size. Version 1
    /// adds the ClearType-aware flags; their absence on a grid-fitting range is the legacy
    /// signature (Courier New, pre-ClearType fonts) that DirectWrite answers with GDI-classic
    /// rendering.
    /// </summary>
    internal sealed class GaspTable
    {
        internal const string TableName = "gasp";
        internal static OpenTypeTag Tag { get; } = OpenTypeTag.Parse(TableName);

        internal const ushort GridFit = 0x0001;
        internal const ushort DoGray = 0x0002;
        internal const ushort SymmetricGridFit = 0x0004;
        internal const ushort SymmetricSmoothing = 0x0008;

        /// <summary>Shared instance for fonts without a usable gasp table.</summary>
        internal static GaspTable Empty { get; } = new(Array.Empty<(ushort, ushort)>());

        private readonly (ushort MaxPpem, ushort Behavior)[] _ranges;

        private GaspTable((ushort MaxPpem, ushort Behavior)[] ranges)
        {
            _ranges = ranges;
        }

        /// <summary>
        /// Loads the table, returning <see cref="Empty"/> when it is absent or malformed -
        /// hinting policy must never fail a font.
        /// </summary>
        public static GaspTable Load(GlyphTypeface fontFace)
        {
            if (!fontFace.PlatformTypeface.TryGetTable(Tag, out var table))
            {
                return Empty;
            }

            return Parse(table.Span);
        }

        internal static GaspTable Parse(ReadOnlySpan<byte> table)
        {
            if (table.Length < 4)
            {
                return Empty;
            }

            var reader = new BigEndianBinaryReader(table);

            reader.ReadUInt16();   // version; range semantics are identical, flags differ
            int numRanges = reader.ReadUInt16();

            if (numRanges <= 0 || table.Length < 4 + numRanges * 4)
            {
                return Empty;
            }

            var ranges = new (ushort MaxPpem, ushort Behavior)[numRanges];

            for (var i = 0; i < numRanges; i++)
            {
                var maxPpem = reader.ReadUInt16();
                var behavior = reader.ReadUInt16();

                ranges[i] = (maxPpem, behavior);
            }

            // The spec requires ascending rangeMaxPPEM order; sort defensively so a sloppy
            // font cannot shadow its own ranges.
            Array.Sort(ranges, static (a, b) => a.MaxPpem.CompareTo(b.MaxPpem));

            return new GaspTable(ranges);
        }

        /// <summary>
        /// The behavior flags for the given ppem: the first range whose max ppem is at or
        /// above it, zero when the table has no covering range.
        /// </summary>
        public ushort GetBehavior(double pixelsPerEm)
        {
            foreach (var (maxPpem, behavior) in _ranges)
            {
                if (pixelsPerEm <= maxPpem)
                {
                    return behavior;
                }
            }

            return 0;
        }

        /// <summary>
        /// Whether the font asks for the classic full grid fit at this size: the range sets
        /// <see cref="GridFit"/> and nothing else. Grid-fit-only is the hinted bi-level
        /// request of legacy fonts at small sizes (Courier New up to 36 ppem) that GDI and
        /// DirectWrite answer with fully snapped rendering. Any smoothing flag on the range -
        /// <see cref="DoGray"/> or the ClearType-aware pair - means the font expects
        /// antialiased natural rendering, which is the light treatment.
        /// </summary>
        public bool WantsFullGridFit(double pixelsPerEm)
        {
            var behavior = GetBehavior(pixelsPerEm);

            return (behavior & GridFit) != 0 &&
                   (behavior & (DoGray | SymmetricGridFit | SymmetricSmoothing)) == 0;
        }

        /// <summary>
        /// Whether the font asks for its instructions to drive the grid fit at this size:
        /// <see cref="GridFit"/> set with neither grayscale-smoothing flag. Unlike
        /// <see cref="WantsFullGridFit"/> this admits <see cref="SymmetricGridFit"/> - the
        /// ClearType-era strong-hinting signature (Tahoma and Verdana declare it from 9 to
        /// 16 ppem with no <see cref="DoGray"/>), where GDI renders hinted bi-level. Only
        /// meaningful when a bytecode interpreter actually stands behind the request; the
        /// auto-hinter cannot honor it faithfully, which is why the caller gates on that.
        /// </summary>
        public bool WantsBytecodeGridFit(double pixelsPerEm)
        {
            var behavior = GetBehavior(pixelsPerEm);

            return (behavior & GridFit) != 0 &&
                   (behavior & (DoGray | SymmetricSmoothing)) == 0;
        }

        /// <summary>
        /// Whether this size sits below the font's hinted floor: the covering range requests
        /// no grid fitting while some larger size does. That shape is the designer's
        /// small-size veto (Tahoma declares it at 8 ppem and below), which DirectWrite
        /// honors by rendering unhinted. A table that never grid-fits anywhere stays out -
        /// hostile webfont-era tables say never-gridfit wholesale, and honoring those would
        /// wash body text.
        /// </summary>
        public bool IsBelowHintingFloor(double pixelsPerEm)
        {
            if ((GetBehavior(pixelsPerEm) & GridFit) != 0)
            {
                return false;
            }

            foreach (var (maxPpem, behavior) in _ranges)
            {
                if (maxPpem > pixelsPerEm && (behavior & GridFit) != 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
