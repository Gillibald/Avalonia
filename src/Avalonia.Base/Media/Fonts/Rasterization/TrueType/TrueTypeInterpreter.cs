using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Avalonia.Media.Fonts.Rasterization.TrueType
{
    /// <summary>Which program a piece of code belongs to; functions remember where they live.</summary>
    internal enum TrueTypeCodeRange : byte
    {
        FontProgram,
        ControlValueProgram,
        Glyph,
    }

    /// <summary>Where the mask pipeline is rendering to; GETINFO answers depend on it.</summary>
    internal enum TrueTypeRenderClass : byte
    {
        Grayscale,
        Subpixel,
        Aliased,
    }

    /// <summary>
    /// The TrueType instruction VM: stack, storage, control values, functions and graphics
    /// state, executing fpgm/prep today and glyph programs once the point engine exists.
    /// Numeric behavior mirrors FreeType's interpreter (the reference the v40 semantics were
    /// audited from) on 64-bit intermediates with 32-bit values, so runs are bit-deterministic
    /// across platforms. Errors never throw: any fault halts the run with a
    /// <see cref="TrueTypeError"/> and the caller vetoes to a fallback. Bounds faults follow
    /// the reference's non-pedantic behavior (reads yield zero, writes are ignored), which is
    /// what shipped fonts are written against.
    /// </summary>
    internal sealed class TrueTypeInterpreter
    {
        /// <summary>Absolute opcode budget per run, the FreeType default.</summary>
        public const int MaxRunnableOpcodes = 1_000_000;

        private const int MaxCallDepth = 32;
        private const int MaxStackSize = 8192;

        private struct CallRecord
        {
            public TrueTypeCodeRange CallerRange;
            public int CallerIp;
            public int Count;
            public TrueTypeFunctionDef Def;
        }

        internal readonly struct TrueTypeFunctionDef
        {
            public TrueTypeFunctionDef(TrueTypeCodeRange range, int start, int end)
            {
                Range = range;
                Start = start;
                End = end;
            }

            public TrueTypeCodeRange Range { get; }

            /// <summary>First instruction of the body (the byte after FDEF/IDEF).</summary>
            public int Start { get; }

            /// <summary>Position of the terminating ENDF.</summary>
            public int End { get; }
        }

        private readonly ReadOnlyMemory<byte> _fontProgram;
        private readonly ReadOnlyMemory<byte> _cvtProgram;
        private ReadOnlyMemory<byte> _glyphProgram;

        // Pristine arrays are owned by the size state; glyph-range writes copy-on-write into
        // the scratch pair so no glyph can leak state into another (the reference engines
        // converged on the same scoping; our deterministic caches additionally require it).
        private readonly int[] _cvt;
        private readonly int[] _storage;
        private int[]? _glyphCvt;
        private int[]? _glyphStorage;
        private int[] _activeCvt;
        private int[] _activeStorage;
        private bool _cvtCopied;
        private bool _storageCopied;

        private readonly int[] _stack;
        private int _top;

        private readonly Dictionary<int, TrueTypeFunctionDef> _functions = new();
        private readonly Dictionary<byte, TrueTypeFunctionDef> _instructionDefs = new();
        private readonly int _maxFunctionDefs;
        private readonly int _maxInstructionDefs;
        private readonly CallRecord[] _callStack = new CallRecord[MaxCallDepth];
        private int _callTop;

        private readonly int _ppem;
        private readonly int _pointSize;
        private readonly int _scale;
        private readonly TrueTypeRenderClass _renderClass;
        private readonly bool _isVariation;

        private TrueTypeCodeRange _initialRange;
        private TrueTypeCodeRange _currentRange;
        private ReadOnlyMemory<byte> _code;
        private int _ip;
        private int _nextIp;

        private int _instructionCount;
        private long _loopcallCounter;
        private long _loopcallCounterMax;
        private long _negJumpCounter;

        public TrueTypeInterpreter(
            ReadOnlyMemory<byte> fontProgram,
            ReadOnlyMemory<byte> cvtProgram,
            int[] cvt,
            int[] storage,
            int maxFunctionDefs,
            int maxInstructionDefs,
            int maxStackElements,
            int ppem,
            int pointSize26Dot6,
            int scale16Dot16,
            TrueTypeRenderClass renderClass,
            bool isVariation)
        {
            _fontProgram = fontProgram;
            _cvtProgram = cvtProgram;
            _cvt = cvt;
            _storage = storage;
            _activeCvt = cvt;
            _activeStorage = storage;
            _maxFunctionDefs = maxFunctionDefs;
            _maxInstructionDefs = maxInstructionDefs;
            _stack = new int[Math.Clamp(maxStackElements + 32, 64, MaxStackSize)];
            _ppem = ppem;
            _pointSize = pointSize26Dot6;
            _scale = scale16Dot16;
            _renderClass = renderClass;
            _isVariation = isVariation;
            GraphicsState = TrueTypeGraphicsState.Default;
        }

        public TrueTypeGraphicsState GraphicsState;

        /// <summary>
        /// The v40 compatibility state for the current glyph run: bit 2 active plus the
        /// per-axis IUP bits. Zero while running fpgm/prep (the reference always runs the
        /// control programs without compatibility hacks).
        /// </summary>
        public int BackwardCompatibility;

        public TrueTypeError Error { get; private set; }

        public ReadOnlySpan<int> Stack => _stack.AsSpan(0, _top);

        public ReadOnlySpan<int> ActiveCvt => _activeCvt;

        public ReadOnlySpan<int> ActiveStorage => _activeStorage;

        public ReadOnlySpan<int> PristineCvt => _cvt;

        public ReadOnlySpan<int> PristineStorage => _storage;

        public bool RunFontProgram() => Execute(TrueTypeCodeRange.FontProgram, _fontProgram);

        public bool RunControlValueProgram() => Execute(TrueTypeCodeRange.ControlValueProgram, _cvtProgram);

        /// <summary>
        /// Runs a glyph instruction stream. CVT and storage writes land in per-run copies;
        /// the pristine size-state arrays stay untouched, so build order can never influence
        /// a cached mask.
        /// </summary>
        public bool RunGlyphProgram(ReadOnlyMemory<byte> code)
        {
            _glyphProgram = code;
            _activeCvt = _cvt;
            _activeStorage = _storage;
            _cvtCopied = false;
            _storageCopied = false;

            return Execute(TrueTypeCodeRange.Glyph, code);
        }

        private bool Execute(TrueTypeCodeRange range, ReadOnlyMemory<byte> code)
        {
            Error = TrueTypeError.None;
            _initialRange = range;
            _currentRange = range;
            _code = code;
            _ip = 0;
            _top = 0;
            _callTop = 0;
            _instructionCount = 0;
            _loopcallCounter = 0;
            _negJumpCounter = 0;

            // The reference heuristic for runs without outline points; recomputed with point
            // counts when the point engine lands.
            _loopcallCounterMax = 300 + 22L * _cvt.Length;

            while (true)
            {
                var span = _code.Span;

                if (_ip >= span.Length)
                {
                    if (_callTop > 0)
                    {
                        // Fell off a code range with calls pending: an unterminated function.
                        return Fail(TrueTypeError.CodeOverflow);
                    }

                    return true;
                }

                if (++_instructionCount > MaxRunnableOpcodes)
                {
                    return Fail(TrueTypeError.ExecutionTooLong);
                }

                var opcode = span[_ip];
                var length = InstructionLength(span, _ip);

                if (length < 0)
                {
                    return Fail(TrueTypeError.CodeOverflow);
                }

                _nextIp = _ip + length;

                Dispatch(opcode, span);

                if (Error != TrueTypeError.None)
                {
                    return false;
                }

                _ip = _nextIp;
            }
        }

        /// <summary>
        /// Total encoded length of the instruction at <paramref name="ip"/> including inline
        /// push data, or -1 when the data would run past the end of the range.
        /// </summary>
        private static int InstructionLength(ReadOnlySpan<byte> code, int ip)
        {
            var opcode = code[ip];

            var length = opcode switch
            {
                0x40 => ip + 1 < code.Length ? 2 + code[ip + 1] : -1,          // NPUSHB
                0x41 => ip + 1 < code.Length ? 2 + 2 * code[ip + 1] : -1,      // NPUSHW
                >= 0xB0 and <= 0xB7 => 2 + (opcode - 0xB0),                    // PUSHB[n]
                >= 0xB8 and <= 0xBF => 3 + 2 * (opcode - 0xB8),                // PUSHW[n]
                _ => 1,
            };

            return length < 0 || ip + length > code.Length ? -1 : length;
        }

        private bool Fail(TrueTypeError error)
        {
            if (Error == TrueTypeError.None)
            {
                Error = error;
            }

            return false;
        }

        private bool Push(int value)
        {
            if (_top >= _stack.Length)
            {
                return Fail(TrueTypeError.StackOverflow);
            }

            _stack[_top++] = value;
            return true;
        }

        private bool Pop(out int value)
        {
            if (_top == 0)
            {
                value = 0;
                return Fail(TrueTypeError.TooFewArguments);
            }

            value = _stack[--_top];
            return true;
        }

        /// <summary>Pops two values; <paramref name="a"/> was pushed first, <paramref name="b"/> was on top.</summary>
        private bool Pop2(out int a, out int b) => Pop(out b) & Pop(out a);

        private void EnsureCvtWritable()
        {
            if (_initialRange != TrueTypeCodeRange.Glyph || _cvtCopied)
            {
                return;
            }

            if (_glyphCvt is null || _glyphCvt.Length < _cvt.Length)
            {
                _glyphCvt = new int[_cvt.Length];
            }

            _cvt.AsSpan().CopyTo(_glyphCvt);
            _activeCvt = _glyphCvt;
            _cvtCopied = true;
        }

        private void EnsureStorageWritable()
        {
            if (_initialRange != TrueTypeCodeRange.Glyph || _storageCopied)
            {
                return;
            }

            if (_glyphStorage is null || _glyphStorage.Length < _storage.Length)
            {
                _glyphStorage = new int[_storage.Length];
            }

            _storage.AsSpan().CopyTo(_glyphStorage);
            _activeStorage = _glyphStorage;
            _storageCopied = true;
        }

        /// <summary>
        /// Rounds a distance under the current round state with the given engine
        /// compensation, the exact reference formulas including the toward-zero clamps.
        /// </summary>
        public int RoundValue(int distance, int compensation)
        {
            ref var gs = ref GraphicsState;
            long value;

            switch (gs.RoundState)
            {
                case TrueTypeRoundState.Off:
                    if (distance >= 0)
                    {
                        value = distance + (long)compensation;
                        if (value < 0)
                            value = 0;
                    }
                    else
                    {
                        value = distance - (long)compensation;
                        if (value > 0)
                            value = 0;
                    }
                    return (int)value;

                case TrueTypeRoundState.ToGrid:
                    if (distance >= 0)
                    {
                        value = (distance + (long)compensation + 32) & ~63L;
                        if (value < 0)
                            value = 0;
                    }
                    else
                    {
                        value = -(((long)compensation - distance + 32) & ~63L);
                        if (value > 0)
                            value = 0;
                    }
                    return (int)value;

                case TrueTypeRoundState.ToHalfGrid:
                    if (distance >= 0)
                    {
                        value = ((distance + (long)compensation) & ~63L) + 32;
                        if (value < 0)
                            value = 32;
                    }
                    else
                    {
                        value = -((((long)compensation - distance) & ~63L) + 32);
                        if (value > 0)
                            value = -32;
                    }
                    return (int)value;

                case TrueTypeRoundState.DownToGrid:
                    if (distance >= 0)
                    {
                        value = (distance + (long)compensation) & ~63L;
                        if (value < 0)
                            value = 0;
                    }
                    else
                    {
                        value = -(((long)compensation - distance) & ~63L);
                        if (value > 0)
                            value = 0;
                    }
                    return (int)value;

                case TrueTypeRoundState.UpToGrid:
                    if (distance >= 0)
                    {
                        value = (distance + (long)compensation + 63) & ~63L;
                        if (value < 0)
                            value = 0;
                    }
                    else
                    {
                        value = -(((long)compensation - distance + 63) & ~63L);
                        if (value > 0)
                            value = 0;
                    }
                    return (int)value;

                case TrueTypeRoundState.ToDoubleGrid:
                    if (distance >= 0)
                    {
                        value = (distance + (long)compensation + 16) & ~31L;
                        if (value < 0)
                            value = 0;
                    }
                    else
                    {
                        value = -(((long)compensation - distance + 16) & ~31L);
                        if (value > 0)
                            value = 0;
                    }
                    return (int)value;

                case TrueTypeRoundState.Super:
                    if (distance >= 0)
                    {
                        value = (distance + (gs.SuperThreshold - gs.SuperPhase + (long)compensation)) & -gs.SuperPeriod;
                        value += gs.SuperPhase;
                        if (value < 0)
                            value = gs.SuperPhase;
                    }
                    else
                    {
                        value = -((gs.SuperThreshold - gs.SuperPhase + (long)compensation - distance) & -gs.SuperPeriod);
                        value -= gs.SuperPhase;
                        if (value > 0)
                            value = -gs.SuperPhase;
                    }
                    return (int)value;

                case TrueTypeRoundState.Super45:
                    if (gs.SuperPeriod == 0)
                    {
                        return distance;
                    }

                    if (distance >= 0)
                    {
                        value = ((distance + (gs.SuperThreshold - gs.SuperPhase + (long)compensation)) / gs.SuperPeriod) * gs.SuperPeriod;
                        value += gs.SuperPhase;
                        if (value < 0)
                            value = gs.SuperPhase;
                    }
                    else
                    {
                        value = -(((gs.SuperThreshold - gs.SuperPhase + (long)compensation - distance) / gs.SuperPeriod) * gs.SuperPeriod);
                        value -= gs.SuperPhase;
                        if (value > 0)
                            value = -gs.SuperPhase;
                    }
                    return (int)value;

                default:
                    return distance;
            }
        }

        /// <summary>
        /// SROUND/S45ROUND parameter decode. The grid period arrives pre-scaled by 256
        /// (0x4000 for the square grid, 0x2D41 for the 45-degree diagonal) and the results
        /// shift down into 26.6, matching the reference exactly.
        /// </summary>
        private void SetSuperRound(int gridPeriod, int selector)
        {
            ref var gs = ref GraphicsState;

            gs.SuperPeriod = (selector & 0xC0) switch
            {
                0x00 => gridPeriod / 2,
                0x40 => gridPeriod,
                0x80 => gridPeriod * 2,
                _ => gridPeriod,
            };

            gs.SuperPhase = (selector & 0x30) switch
            {
                0x00 => 0,
                0x10 => gs.SuperPeriod / 4,
                0x20 => gs.SuperPeriod / 2,
                _ => gs.SuperPeriod * 3 / 4,
            };

            if ((selector & 0x0F) == 0)
            {
                gs.SuperThreshold = gs.SuperPeriod - 1;
            }
            else
            {
                gs.SuperThreshold = ((selector & 0x0F) - 4) * gs.SuperPeriod / 8;
            }

            gs.SuperPeriod >>= 8;
            gs.SuperPhase >>= 8;
            gs.SuperThreshold >>= 8;
        }

        private void Dispatch(byte opcode, ReadOnlySpan<byte> span)
        {
            ref var gs = ref GraphicsState;
            int a, b;

            switch (opcode)
            {
                // ---- vectors -------------------------------------------------------------

                case 0x00:  // SVTCA[y]
                case 0x01:  // SVTCA[x]
                {
                    var x = (short)(opcode == 0x01 ? 0x4000 : 0);
                    var y = (short)(opcode == 0x01 ? 0 : 0x4000);
                    gs.FreedomX = x;
                    gs.FreedomY = y;
                    gs.ProjectionX = x;
                    gs.ProjectionY = y;
                    gs.DualX = x;
                    gs.DualY = y;
                    break;
                }

                case 0x02:  // SPVTCA[y]
                case 0x03:  // SPVTCA[x]
                {
                    var x = (short)(opcode == 0x03 ? 0x4000 : 0);
                    var y = (short)(opcode == 0x03 ? 0 : 0x4000);
                    gs.ProjectionX = x;
                    gs.ProjectionY = y;
                    gs.DualX = x;
                    gs.DualY = y;
                    break;
                }

                case 0x04:  // SFVTCA[y]
                case 0x05:  // SFVTCA[x]
                    gs.FreedomX = (short)(opcode == 0x05 ? 0x4000 : 0);
                    gs.FreedomY = (short)(opcode == 0x05 ? 0 : 0x4000);
                    break;

                case 0x0A:  // SPVFS: pops y (top), x
                    if (Pop2(out a, out b) && Normalize(a, b, out var px, out var py))
                    {
                        gs.ProjectionX = px;
                        gs.ProjectionY = py;
                        gs.DualX = px;
                        gs.DualY = py;
                    }
                    break;

                case 0x0B:  // SFVFS
                    if (Pop2(out a, out b) && Normalize(a, b, out var fx, out var fy))
                    {
                        gs.FreedomX = fx;
                        gs.FreedomY = fy;
                    }
                    break;

                case 0x0C:  // GPV
                    if (Push(gs.ProjectionX))
                        Push(gs.ProjectionY);
                    break;

                case 0x0D:  // GFV
                    if (Push(gs.FreedomX))
                        Push(gs.FreedomY);
                    break;

                case 0x0E:  // SFVTPV
                    gs.FreedomX = gs.ProjectionX;
                    gs.FreedomY = gs.ProjectionY;
                    break;

                // ---- reference points, zones, simple GS setters --------------------------

                case 0x10:  // SRP0
                    if (Pop(out a))
                        gs.Rp0 = a;
                    break;

                case 0x11:  // SRP1
                    if (Pop(out a))
                        gs.Rp1 = a;
                    break;

                case 0x12:  // SRP2
                    if (Pop(out a))
                        gs.Rp2 = a;
                    break;

                case 0x13:  // SZP0
                    if (Pop(out a) && (uint)a < 2)
                        gs.Zp0 = (byte)a;
                    break;

                case 0x14:  // SZP1
                    if (Pop(out a) && (uint)a < 2)
                        gs.Zp1 = (byte)a;
                    break;

                case 0x15:  // SZP2
                    if (Pop(out a) && (uint)a < 2)
                        gs.Zp2 = (byte)a;
                    break;

                case 0x16:  // SZPS
                    if (Pop(out a) && (uint)a < 2)
                    {
                        gs.Zp0 = (byte)a;
                        gs.Zp1 = (byte)a;
                        gs.Zp2 = (byte)a;
                    }
                    break;

                case 0x17:  // SLOOP
                    if (Pop(out a))
                    {
                        if (a < 0)
                            Fail(TrueTypeError.BadArgument);
                        else
                            gs.Loop = Math.Min(a, 0xFFFF);
                    }
                    break;

                case 0x18:  // RTG
                    gs.RoundState = TrueTypeRoundState.ToGrid;
                    break;

                case 0x19:  // RTHG
                    gs.RoundState = TrueTypeRoundState.ToHalfGrid;
                    break;

                case 0x1A:  // SMD
                    if (Pop(out a))
                        gs.MinimumDistance = a;
                    break;

                case 0x1B:  // ELSE: the true branch ended; skip to the matching EIF
                    SkipToEndIf(span);
                    break;

                case 0x1C:  // JMPR
                    if (Pop(out a))
                        Jump(a);
                    break;

                case 0x1D:  // SCVTCI
                    if (Pop(out a))
                        gs.ControlValueCutIn = a;
                    break;

                case 0x1E:  // SSWCI
                    if (Pop(out a))
                        gs.SingleWidthCutIn = a;
                    break;

                case 0x1F:  // SSW: single width arrives in font units
                    if (Pop(out a))
                        gs.SingleWidthValue = F26Dot6.MulFix(a, _scale);
                    break;

                // ---- stack ---------------------------------------------------------------

                case 0x20:  // DUP
                    if (Pop(out a) && Push(a))
                        Push(a);
                    break;

                case 0x21:  // POP
                    Pop(out _);
                    break;

                case 0x22:  // CLEAR
                    _top = 0;
                    break;

                case 0x23:  // SWAP
                    if (Pop2(out a, out b) && Push(b))
                        Push(a);
                    break;

                case 0x24:  // DEPTH
                    Push(_top);
                    break;

                case 0x25:  // CINDEX: copy the k-th element to the top; invalid index copies 0
                    if (Pop(out a))
                        Push(a <= 0 || a > _top ? 0 : _stack[_top - a]);
                    break;

                case 0x26:  // MINDEX: move the k-th element to the top; invalid index only pops
                    if (Pop(out a) && a > 0 && a <= _top)
                        MoveIndexToTop(a);
                    break;

                case 0x8A:  // ROLL = MINDEX with k fixed at 3
                    if (_top >= 3)
                        MoveIndexToTop(3);
                    else
                        Fail(TrueTypeError.TooFewArguments);
                    break;

                // ---- functions -----------------------------------------------------------

                case 0x2A:  // LOOPCALL: pops function number (top), count
                    if (Pop2(out a, out b))
                        CallFunction(b, a, countsTowardLoopBudget: true);
                    break;

                case 0x2B:  // CALL
                    if (Pop(out a))
                        CallFunction(a, 1, countsTowardLoopBudget: false);
                    break;

                case 0x2C:  // FDEF
                    if (Pop(out a))
                        DefineFunction(a, span);
                    break;

                case 0x2D:  // ENDF
                    EndFunction();
                    break;

                case 0x89:  // IDEF
                    if (Pop(out a))
                        DefineInstruction(a, span);
                    break;

                // ---- rounding ------------------------------------------------------------

                case 0x3D:  // RTDG
                    gs.RoundState = TrueTypeRoundState.ToDoubleGrid;
                    break;

                case 0x7A:  // ROFF
                    gs.RoundState = TrueTypeRoundState.Off;
                    break;

                case 0x7C:  // RUTG
                    gs.RoundState = TrueTypeRoundState.UpToGrid;
                    break;

                case 0x7D:  // RDTG
                    gs.RoundState = TrueTypeRoundState.DownToGrid;
                    break;

                case 0x76:  // SROUND
                    if (Pop(out a))
                    {
                        SetSuperRound(0x4000, a);
                        gs.RoundState = TrueTypeRoundState.Super;
                    }
                    break;

                case 0x77:  // S45ROUND
                    if (Pop(out a))
                    {
                        SetSuperRound(0x2D41, a);
                        gs.RoundState = TrueTypeRoundState.Super45;
                    }
                    break;

                case >= 0x68 and <= 0x6B:  // ROUND[ab]; engine compensations are all zero
                    if (Pop(out a))
                        Push(RoundValue(a, 0));
                    break;

                case >= 0x6C and <= 0x6F:  // NROUND[ab]: compensation only, no rounding
                    if (Pop(out a))
                        Push(a);
                    break;

                // ---- push data -----------------------------------------------------------

                case 0x40:  // NPUSHB
                    PushBytes(span, _ip + 2, span[_ip + 1]);
                    break;

                case 0x41:  // NPUSHW
                    PushWords(span, _ip + 2, span[_ip + 1]);
                    break;

                case >= 0xB0 and <= 0xB7:  // PUSHB[n]
                    PushBytes(span, _ip + 1, opcode - 0xB0 + 1);
                    break;

                case >= 0xB8 and <= 0xBF:  // PUSHW[n]
                    PushWords(span, _ip + 1, opcode - 0xB8 + 1);
                    break;

                // ---- storage and control values ------------------------------------------

                case 0x42:  // WS
                    if (Pop2(out a, out b) && (uint)a < (uint)_activeStorage.Length)
                    {
                        EnsureStorageWritable();
                        _activeStorage[a] = b;
                    }
                    break;

                case 0x43:  // RS
                    if (Pop(out a))
                        Push((uint)a < (uint)_activeStorage.Length ? _activeStorage[a] : 0);
                    break;

                case 0x44:  // WCVTP
                    if (Pop2(out a, out b) && (uint)a < (uint)_activeCvt.Length)
                    {
                        EnsureCvtWritable();
                        _activeCvt[a] = b;
                    }
                    break;

                case 0x45:  // RCVT
                    if (Pop(out a))
                        Push((uint)a < (uint)_activeCvt.Length ? _activeCvt[a] : 0);
                    break;

                case 0x70:  // WCVTF: value arrives in font units
                    if (Pop2(out a, out b) && (uint)a < (uint)_activeCvt.Length)
                    {
                        EnsureCvtWritable();
                        _activeCvt[a] = F26Dot6.MulFix(b, _scale);
                    }
                    break;

                case >= 0x73 and <= 0x75:  // DELTAC1..3
                    DeltaC(opcode);
                    break;

                // ---- measurement and info ------------------------------------------------

                case 0x4B:  // MPPEM
                    Push(_ppem);
                    break;

                case 0x4C:  // MPS: 26.6 points; derived at the Windows 96 dpi convention
                    Push(_pointSize);
                    break;

                case 0x88:  // GETINFO
                    if (Pop(out a))
                        Push(GetInfo(a));
                    break;

                case 0x4F:  // DEBUG must never execute in production fonts
                    Pop(out _);
                    Fail(TrueTypeError.DebugOpcode);
                    break;

                // ---- flags and controls --------------------------------------------------

                case 0x4D:  // FLIPON
                    gs.AutoFlip = true;
                    break;

                case 0x4E:  // FLIPOFF
                    gs.AutoFlip = false;
                    break;

                case 0x5E:  // SDB
                    if (Pop(out a))
                        gs.DeltaBase = (ushort)a;
                    break;

                case 0x5F:  // SDS
                    if (Pop(out a))
                    {
                        if ((uint)a > 6)
                            Fail(TrueTypeError.BadArgument);
                        else
                            gs.DeltaShift = (ushort)a;
                    }
                    break;

                case 0x85:  // SCANCTRL
                    if (Pop(out a))
                        ScanControl(a);
                    break;

                case 0x8D:  // SCANTYPE
                    if (Pop(out a) && a >= 0)
                        gs.ScanType = a & 0xFFFF;
                    break;

                case 0x8E:  // INSTCTRL: pops selector (top), value
                    if (Pop2(out a, out b))
                        InstructControl(value: a, selector: b);
                    break;

                case 0x7E:  // SANGW: deprecated, argument discarded
                case 0x7F:  // AA: deprecated, argument discarded
                    Pop(out _);
                    break;

                // ---- logic ---------------------------------------------------------------

                case 0x50:  // LT
                    if (Pop2(out a, out b))
                        Push(a < b ? 1 : 0);
                    break;

                case 0x51:  // LTEQ
                    if (Pop2(out a, out b))
                        Push(a <= b ? 1 : 0);
                    break;

                case 0x52:  // GT
                    if (Pop2(out a, out b))
                        Push(a > b ? 1 : 0);
                    break;

                case 0x53:  // GTEQ
                    if (Pop2(out a, out b))
                        Push(a >= b ? 1 : 0);
                    break;

                case 0x54:  // EQ
                    if (Pop2(out a, out b))
                        Push(a == b ? 1 : 0);
                    break;

                case 0x55:  // NEQ
                    if (Pop2(out a, out b))
                        Push(a != b ? 1 : 0);
                    break;

                case 0x56:  // ODD
                    if (Pop(out a))
                        Push((RoundValue(a, 0) & 64) == 64 ? 1 : 0);
                    break;

                case 0x57:  // EVEN
                    if (Pop(out a))
                        Push((RoundValue(a, 0) & 64) == 0 ? 1 : 0);
                    break;

                case 0x58:  // IF
                    if (Pop(out a) && a == 0)
                        SkipFalseBranch(span);
                    break;

                case 0x59:  // EIF: end marker of a taken branch
                    break;

                case 0x5A:  // AND
                    if (Pop2(out a, out b))
                        Push(a != 0 && b != 0 ? 1 : 0);
                    break;

                case 0x5B:  // OR
                    if (Pop2(out a, out b))
                        Push(a != 0 || b != 0 ? 1 : 0);
                    break;

                case 0x5C:  // NOT
                    if (Pop(out a))
                        Push(a == 0 ? 1 : 0);
                    break;

                // ---- arithmetic ----------------------------------------------------------

                case 0x60:  // ADD
                    if (Pop2(out a, out b))
                        Push(unchecked(a + b));
                    break;

                case 0x61:  // SUB
                    if (Pop2(out a, out b))
                        Push(unchecked(a - b));
                    break;

                case 0x62:  // DIV
                    if (Pop2(out a, out b))
                    {
                        if (b == 0)
                            Fail(TrueTypeError.DivideByZero);
                        else
                            Push(F26Dot6.MulDivTruncated(a, 64, b));
                    }
                    break;

                case 0x63:  // MUL
                    if (Pop2(out a, out b))
                        Push(F26Dot6.MulDivRounded(a, b, 64));
                    break;

                case 0x64:  // ABS
                    if (Pop(out a))
                        Push(a < 0 ? unchecked(-a) : a);
                    break;

                case 0x65:  // NEG
                    if (Pop(out a))
                        Push(unchecked(-a));
                    break;

                case 0x66:  // FLOOR
                    if (Pop(out a))
                        Push(F26Dot6.Floor(a));
                    break;

                case 0x67:  // CEILING
                    if (Pop(out a))
                        Push(F26Dot6.Ceiling(a));
                    break;

                case 0x8B:  // MAX
                    if (Pop2(out a, out b))
                        Push(Math.Max(a, b));
                    break;

                case 0x8C:  // MIN
                    if (Pop2(out a, out b))
                        Push(Math.Min(a, b));
                    break;

                // ---- jumps ---------------------------------------------------------------

                case 0x78:  // JROT: pops condition (top), offset
                    if (Pop2(out a, out b) && b != 0)
                        Jump(a);
                    break;

                case 0x79:  // JROF
                    if (Pop2(out a, out b) && b == 0)
                        Jump(a);
                    break;

                // ---- the point engine (not built yet) ------------------------------------

                case >= 0x06 and <= 0x09:  // SPVTL, SFVTL
                case 0x0F:                 // ISECT
                case 0x27:                 // ALIGNPTS
                case 0x29:                 // UTP
                case 0x2E or 0x2F:         // MDAP
                case >= 0x30 and <= 0x3C:  // IUP, SHP, SHC, SHZ, SHPIX, IP, MSIRP, ALIGNRP
                case 0x3E or 0x3F:         // MIAP
                case >= 0x46 and <= 0x4A:  // GC, SCFS, MD
                case 0x5D:                 // DELTAP1
                case 0x71 or 0x72:         // DELTAP2, DELTAP3
                case >= 0x80 and <= 0x82:  // FLIPPT, FLIPRGON, FLIPRGOFF
                case 0x86 or 0x87:         // SDPVTL
                case >= 0xC0:              // MDRP, MIRP
                    Fail(TrueTypeError.UnsupportedOpcode);
                    break;

                default:
                    // Unassigned opcode: an IDEF may cover it, otherwise the font is asking
                    // for an engine this interpreter is not.
                    if (_instructionDefs.TryGetValue(opcode, out var idef))
                        EnterCall(idef, 1);
                    else
                        Fail(TrueTypeError.InvalidOpcode);
                    break;
            }
        }

        private void MoveIndexToTop(int k)
        {
            var value = _stack[_top - k];

            for (var i = _top - k; i < _top - 1; i++)
            {
                _stack[i] = _stack[i + 1];
            }

            _stack[_top - 1] = value;
        }

        private void PushBytes(ReadOnlySpan<byte> span, int offset, int count)
        {
            for (var i = 0; i < count; i++)
            {
                if (!Push(span[offset + i]))
                {
                    return;
                }
            }
        }

        private void PushWords(ReadOnlySpan<byte> span, int offset, int count)
        {
            for (var i = 0; i < count; i++)
            {
                if (!Push(BinaryPrimitives.ReadInt16BigEndian(span.Slice(offset + i * 2, 2))))
                {
                    return;
                }
            }
        }

        private bool Normalize(int x, int y, out short unitX, out short unitY)
        {
            unitX = 0;
            unitY = 0;

            var sx = (short)x;
            var sy = (short)y;

            if (sx == 0 && sy == 0)
            {
                return !Fail(TrueTypeError.BadArgument);
            }

            // Math.Sqrt is IEEE-correctly-rounded, so this stays bit-deterministic.
            var length = Math.Sqrt((double)sx * sx + (double)sy * sy);

            unitX = (short)Math.Floor(sx * 16384.0 / length + 0.5);
            unitY = (short)Math.Floor(sy * 16384.0 / length + 0.5);
            return true;
        }

        private void Jump(int offset)
        {
            // A zero offset on an empty stack can only spin forever.
            if (offset == 0 && _top == 0)
            {
                Fail(TrueTypeError.BadArgument);
                return;
            }

            var target = _ip + offset;

            if (target < 0 ||
                (_callTop > 0 && target > _callStack[_callTop - 1].Def.End))
            {
                Fail(TrueTypeError.BadArgument);
                return;
            }

            if (offset < 0 && ++_negJumpCounter > _loopcallCounterMax)
            {
                Fail(TrueTypeError.ExecutionTooLong);
                return;
            }

            _nextIp = target;
        }

        private void DefineFunction(int number, ReadOnlySpan<byte> span)
        {
            if (_initialRange == TrueTypeCodeRange.Glyph)
            {
                Fail(TrueTypeError.DefInGlyphProgram);
                return;
            }

            if ((uint)number > 0xFFFF ||
                (!_functions.ContainsKey(number) && _functions.Count >= _maxFunctionDefs))
            {
                Fail(TrueTypeError.TooManyDefs);
                return;
            }

            if (SkipDefinitionBody(span, _nextIp, out var end))
            {
                _functions[number] = new TrueTypeFunctionDef(_currentRange, _nextIp, end);
                _nextIp = end + 1;
            }
        }

        private void DefineInstruction(int opcode, ReadOnlySpan<byte> span)
        {
            if (_initialRange == TrueTypeCodeRange.Glyph)
            {
                Fail(TrueTypeError.DefInGlyphProgram);
                return;
            }

            if ((uint)opcode > 0xFF ||
                (!_instructionDefs.ContainsKey((byte)opcode) && _instructionDefs.Count >= _maxInstructionDefs))
            {
                Fail(TrueTypeError.TooManyDefs);
                return;
            }

            if (SkipDefinitionBody(span, _nextIp, out var end))
            {
                _instructionDefs[(byte)opcode] = new TrueTypeFunctionDef(_currentRange, _nextIp, end);
                _nextIp = end + 1;
            }
        }

        /// <summary>Scans a definition body for its ENDF; nested definitions are malformed.</summary>
        private bool SkipDefinitionBody(ReadOnlySpan<byte> span, int start, out int end)
        {
            var position = start;

            while (position < span.Length)
            {
                var opcode = span[position];

                switch (opcode)
                {
                    case 0x2C:  // FDEF
                    case 0x89:  // IDEF
                        end = 0;
                        return !Fail(TrueTypeError.NestedDefs);

                    case 0x2D:  // ENDF
                        end = position;
                        return true;
                }

                var length = InstructionLength(span, position);

                if (length < 0)
                {
                    break;
                }

                position += length;
            }

            end = 0;
            return !Fail(TrueTypeError.CodeOverflow);
        }

        private void CallFunction(int number, int count, bool countsTowardLoopBudget)
        {
            if (count <= 0)
            {
                return;
            }

            if (!_functions.TryGetValue(number, out var def))
            {
                Fail(TrueTypeError.InvalidReference);
                return;
            }

            // Only LOOPCALL iterations consume the loop budget; plain calls are bounded by
            // the call depth and the overall instruction budget, matching the reference.
            if (countsTowardLoopBudget)
            {
                _loopcallCounter += count;

                if (_loopcallCounter > _loopcallCounterMax)
                {
                    Fail(TrueTypeError.ExecutionTooLong);
                    return;
                }
            }

            EnterCall(def, count);
        }

        private void EnterCall(TrueTypeFunctionDef def, int count)
        {
            if (_callTop >= MaxCallDepth)
            {
                Fail(TrueTypeError.StackOverflow);
                return;
            }

            ref var record = ref _callStack[_callTop++];

            record.CallerRange = _currentRange;
            record.CallerIp = _nextIp;
            record.Count = count;
            record.Def = def;

            GotoRange(def.Range, def.Start);
        }

        private void EndFunction()
        {
            if (_callTop <= 0)
            {
                Fail(TrueTypeError.EndfInExecStream);
                return;
            }

            ref var record = ref _callStack[_callTop - 1];

            if (--record.Count > 0)
            {
                _nextIp = record.Def.Start;
                return;
            }

            _callTop--;
            GotoRange(record.CallerRange, record.CallerIp);
        }

        private void GotoRange(TrueTypeCodeRange range, int position)
        {
            _code = range switch
            {
                TrueTypeCodeRange.FontProgram => _fontProgram,
                TrueTypeCodeRange.ControlValueProgram => _cvtProgram,
                _ => _glyphProgram,
            };
            _currentRange = range;
            _nextIp = position;
        }

        /// <summary>IF with a false condition: skip to the matching ELSE or EIF.</summary>
        private void SkipFalseBranch(ReadOnlySpan<byte> span)
        {
            var depth = 1;
            var position = _nextIp;

            while (position < span.Length)
            {
                var opcode = span[position];
                var length = InstructionLength(span, position);

                if (length < 0)
                {
                    break;
                }

                switch (opcode)
                {
                    case 0x58:  // IF
                        depth++;
                        break;

                    case 0x1B:  // ELSE
                        if (depth == 1)
                        {
                            _nextIp = position + length;
                            return;
                        }
                        break;

                    case 0x59:  // EIF
                        if (--depth == 0)
                        {
                            _nextIp = position + length;
                            return;
                        }
                        break;
                }

                position += length;
            }

            Fail(TrueTypeError.CodeOverflow);
        }

        /// <summary>An executed ELSE terminates the taken branch: skip to the matching EIF.</summary>
        private void SkipToEndIf(ReadOnlySpan<byte> span)
        {
            var depth = 1;
            var position = _nextIp;

            while (position < span.Length)
            {
                var opcode = span[position];
                var length = InstructionLength(span, position);

                if (length < 0)
                {
                    break;
                }

                switch (opcode)
                {
                    case 0x58:
                        depth++;
                        break;

                    case 0x59:
                        if (--depth == 0)
                        {
                            _nextIp = position + length;
                            return;
                        }
                        break;
                }

                position += length;
            }

            Fail(TrueTypeError.CodeOverflow);
        }

        private void DeltaC(byte opcode)
        {
            if (!Pop(out var count))
            {
                return;
            }

            // The reference clamps a bad count to whatever the stack holds.
            count = Math.Clamp(count, 0, _top / 2);

            var basePpem = _ppem - GraphicsState.DeltaBase + opcode switch
            {
                0x74 => -16,
                0x75 => -32,
                _ => 0,
            };

            var magnitude = 1 << (6 - GraphicsState.DeltaShift);

            while (count-- > 0)
            {
                Pop(out var cvtIndex);
                Pop(out var arg);

                if ((basePpem & ~0xF) != 0)
                {
                    continue;
                }

                if ((arg & 0xF0) >> 4 != basePpem)
                {
                    continue;
                }

                var steps = (arg & 0xF) - 8;

                if (steps >= 0)
                {
                    steps++;
                }

                if ((uint)cvtIndex < (uint)_activeCvt.Length)
                {
                    EnsureCvtWritable();
                    _activeCvt[cvtIndex] = unchecked(_activeCvt[cvtIndex] + steps * magnitude);
                }
            }
        }

        private int GetInfo(int selector)
        {
            var result = 0;

            if ((selector & 1) != 0)
            {
                // The v40 engine identity: ClearType-era fonts take their modern branches.
                result = 40;
            }

            if ((selector & 8) != 0 && _isVariation)
            {
                result |= 1 << 10;
            }

            // Rotated (bit 8), stretched (bit 9) and the legacy grayscale flag (bit 12)
            // never apply: the mask tier is axis-aligned with one square ppem, and v40
            // forces the legacy flag off outside monochrome rendering.

            if (_renderClass != TrueTypeRenderClass.Aliased)
            {
                if ((selector & 64) != 0)
                {
                    result |= 1 << 13;      // subpixel hinting active
                }

                if ((selector & 1024) != 0)
                {
                    result |= 1 << 17;      // subpixel positioned
                }

                if ((selector & 2048) != 0)
                {
                    result |= 1 << 18;      // symmetric smoothing
                }

                if ((selector & 4096) != 0 && _renderClass == TrueTypeRenderClass.Grayscale)
                {
                    result |= 1 << 19;      // ClearType grayscale rendering
                }
            }

            return result;
        }

        private void ScanControl(int value)
        {
            var threshold = value & 0xFF;

            // Rotation and stretch conditions never apply on this engine's square,
            // axis-aligned transforms; 0xFF is the documented always-on threshold.
            if (threshold == 0xFF)
            {
                GraphicsState.ScanControl = true;
                return;
            }

            if ((value & 0x100) != 0 && _ppem <= threshold)
            {
                GraphicsState.ScanControl = true;
            }

            if ((value & 0x800) != 0 && _ppem > threshold)
            {
                GraphicsState.ScanControl = false;
            }

            if ((value & 0x1000) != 0)
            {
                GraphicsState.ScanControl = false;
            }

            if ((value & 0x2000) != 0)
            {
                GraphicsState.ScanControl = false;
            }
        }

        private void InstructControl(int value, int selector)
        {
            // Selectors are indices, not flags; the value must be zero or the matching flag.
            if (selector < 1 || selector > 3)
            {
                return;
            }

            var flag = 1 << (selector - 1);

            if (value != 0 && value != flag)
            {
                return;
            }

            if (_initialRange == TrueTypeCodeRange.ControlValueProgram)
            {
                GraphicsState.InstructControl = (byte)((GraphicsState.InstructControl & ~flag) | value);
            }
            else if (_initialRange == TrueTypeCodeRange.Glyph && selector == 3)
            {
                // The per-glyph native-ClearType waiver: compat off when bit 2 is set.
                BackwardCompatibility = (value & 4) ^ 4;
            }
        }
    }
}
