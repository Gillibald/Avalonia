using System;
using System.Buffers.Binary;

namespace Avalonia.Media.Fonts.Rasterization.TrueType
{
    /// <summary>
    /// The per-size hinting context: the font program executed to register functions, the
    /// control value program executed to derive scaled control values, storage and graphics
    /// state defaults, all latched exactly once. A failed program latches the error and the
    /// size renders through the fallback ladder instead — matching the reference model where
    /// fpgm/prep failures stick to the size object.
    ///
    /// The post-prep CVT, storage and graphics state are pristine: glyph programs run against
    /// copy-on-write views (see <see cref="TrueTypeInterpreter.RunGlyphProgram"/>), so cached
    /// mask builds can never observe another glyph's writes and build order stays irrelevant.
    /// </summary>
    internal sealed class TrueTypeSizeState
    {
        private TrueTypeSizeState(TrueTypeInterpreter? interpreter, TrueTypeError error)
        {
            Interpreter = interpreter;
            Error = error;
        }

        /// <summary>The VM holding the pristine post-prep arrays; null when setup failed.</summary>
        public TrueTypeInterpreter? Interpreter { get; }

        public TrueTypeError Error { get; }

        public bool IsValid => Error == TrueTypeError.None && Interpreter is not null;

        /// <summary>The graphics state every glyph run starts from.</summary>
        public TrueTypeGraphicsState DefaultGraphicsState { get; private set; }

        /// <summary>
        /// The post-prep INSTCTRL bits as the programs left them, captured before the bit-2
        /// revert below. Bit 1: the font asks for glyph instructions to be skipped entirely
        /// at this size (rendered unhinted, not auto-hinted — the font explicitly disclaimed
        /// fitting). Bit 4: the native-ClearType waiver consulted for v40 gating, read from
        /// the post-revert state the way the reference does, so a bit-2 revert also clears a
        /// waiver the prep had set.
        /// </summary>
        public byte InstructControl { get; private set; }

        public bool GlyphHintingDisabled => (InstructControl & 1) != 0;

        public bool NativeClearTypeWaiver => (DefaultGraphicsState.InstructControl & 4) != 0;

        /// <summary>
        /// Runs a glyph instruction stream against this size's pristine state. The working
        /// graphics state starts from the post-prep defaults; CVT and storage writes go to
        /// per-run copies.
        /// </summary>
        public bool RunGlyphProgram(ReadOnlyMemory<byte> code, int backwardCompatibility)
        {
            if (Interpreter is not { } interpreter)
            {
                return false;
            }

            interpreter.GraphicsState = DefaultGraphicsState;
            interpreter.BackwardCompatibility = backwardCompatibility;

            return interpreter.RunGlyphProgram(code);
        }

        public static TrueTypeSizeState Create(
            TrueTypeProgramTables tables,
            int unitsPerEm,
            int pixelsPerEm26Dot6,
            int maxStorage,
            int maxFunctionDefs,
            int maxInstructionDefs,
            int maxStackElements,
            TrueTypeRenderClass renderClass,
            bool isVariation)
        {
            if (unitsPerEm <= 0 || pixelsPerEm26Dot6 <= 0)
            {
                return new TrueTypeSizeState(null, TrueTypeError.BadArgument);
            }

            // 16.16 factor taking font units to 26.6 pixels, and the integer ppem the
            // range-sensitive instructions (MPPEM, DELTA, SCANCTRL) compare against.
            var scale = (int)(((long)pixelsPerEm26Dot6 << 16) / unitsPerEm);
            var ppem = (pixelsPerEm26Dot6 + 32) >> 6;

            // 26.6 points at the Windows 96 dpi convention (72 points per 96 pixels); fonts
            // consult MPS against MPPEM to detect scaling tricks, and this keeps the ratio
            // the one Windows rasterizers report.
            var pointSize = ppem * 48;

            var rawCvt = tables.ControlValues.Span;
            var cvt = new int[rawCvt.Length / 2];

            for (var i = 0; i < cvt.Length; i++)
            {
                cvt[i] = F26Dot6.MulFix(
                    BinaryPrimitives.ReadInt16BigEndian(rawCvt.Slice(i * 2, 2)), scale);
            }

            var storage = new int[maxStorage];

            var interpreter = new TrueTypeInterpreter(
                tables.FontProgram,
                tables.ControlValueProgram,
                cvt,
                storage,
                maxFunctionDefs,
                maxInstructionDefs,
                maxStackElements,
                ppem,
                pointSize,
                scale,
                renderClass,
                isVariation);

            // The font program registers functions; graphics state and storage effects are
            // discarded below, matching the reference (storage clears at prep entry, the
            // graphics state starts prep from spec defaults).
            interpreter.GraphicsState = TrueTypeGraphicsState.Default;

            if (!tables.FontProgram.IsEmpty && !interpreter.RunFontProgram())
            {
                return new TrueTypeSizeState(null, interpreter.Error);
            }

            // Prep starts clean: spec-default graphics state and zeroed storage, so a font
            // program's scratch writes never leak into the size snapshot.
            interpreter.GraphicsState = TrueTypeGraphicsState.Default;
            Array.Clear(storage);

            if (!tables.ControlValueProgram.IsEmpty && !interpreter.RunControlValueProgram())
            {
                return new TrueTypeSizeState(null, interpreter.Error);
            }

            var state = new TrueTypeSizeState(interpreter, TrueTypeError.None)
            {
                InstructControl = interpreter.GraphicsState.InstructControl,
            };

            // INSTCTRL bit 2 asks for prep's graphics-state changes to be discarded; the
            // revert also wipes the waiver bit, exactly as the reference reads it.
            state.DefaultGraphicsState = (state.InstructControl & 2) != 0
                ? TrueTypeGraphicsState.Default
                : interpreter.GraphicsState;

            return state;
        }
    }
}
