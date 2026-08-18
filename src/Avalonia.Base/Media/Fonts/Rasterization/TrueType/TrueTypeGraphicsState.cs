namespace Avalonia.Media.Fonts.Rasterization.TrueType
{
    /// <summary>How distances round; selected by the round-state opcodes.</summary>
    internal enum TrueTypeRoundState : byte
    {
        ToHalfGrid,
        ToGrid,
        ToDoubleGrid,
        DownToGrid,
        UpToGrid,
        Off,
        Super,
        Super45,
    }

    /// <summary>
    /// The interpreter's graphics state: everything the GS-setter opcodes mutate. A struct
    /// so the per-size defaults snapshot (post-prep) and the per-run working copy are plain
    /// value copies. Super-round parameters live here because SROUND sets them together with
    /// the round state; note that neither survives the prep - the size state hands only the
    /// sanctioned scalar set from the cvt program to glyph runs.
    /// </summary>
    internal struct TrueTypeGraphicsState
    {
        // Zone pointers: 0 = twilight, 1 = glyph. Reference points are point indices.
        public byte Zp0, Zp1, Zp2;
        public int Rp0, Rp1, Rp2;

        // Direction vectors in 2.14; the projection/dual pair measures, freedom moves.
        public short FreedomX, FreedomY;
        public short ProjectionX, ProjectionY;
        public short DualX, DualY;

        public TrueTypeRoundState RoundState;
        public int SuperPeriod;
        public int SuperPhase;
        public int SuperThreshold;

        public int Loop;
        public int MinimumDistance;
        public int ControlValueCutIn;
        public int SingleWidthCutIn;
        public int SingleWidthValue;
        public ushort DeltaBase;
        public ushort DeltaShift;
        public bool AutoFlip;
        public byte InstructControl;
        public bool ScanControl;
        public int ScanType;

        /// <summary>
        /// The spec defaults every run starts from before prep customizes them: glyph zones,
        /// x-axis vectors, round-to-grid, loop 1, minimum distance 1 px, CVT cut-in 17/16 px,
        /// delta base 9 and shift 3, auto flip on.
        /// </summary>
        public static TrueTypeGraphicsState Default => new()
        {
            Zp0 = 1,
            Zp1 = 1,
            Zp2 = 1,
            FreedomX = 0x4000,
            ProjectionX = 0x4000,
            DualX = 0x4000,
            RoundState = TrueTypeRoundState.ToGrid,
            Loop = 1,
            MinimumDistance = 64,
            ControlValueCutIn = 68,
            DeltaBase = 9,
            DeltaShift = 3,
            AutoFlip = true,
        };
    }
}
