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

        // The twilight zone is size state like the CVT: prep builds it, and every glyph run
        // works on a fresh copy so no run can leak points into another.
        private readonly TrueTypeZone _twilight;
        private TrueTypeZone? _workingTwilight;
        private TrueTypeZone _activeTwilight;
        private TrueTypeZone? _glyphZone;

        // The movement vector: freedom scaled by 1/(freedom . projection), in 16.16, zero
        // when the vectors are prohibitively orthogonal. Recomputed on every vector change.
        private int _moveX;
        private int _moveY;

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
            int maxTwilightPoints,
            int ppem,
            int pointSize26Dot6,
            int scale16Dot16,
            TrueTypeRenderClass renderClass,
            bool isVariation)
        {
            _twilight = new TrueTypeZone(Math.Clamp(maxTwilightPoints, 1, 0xFFFF), 1)
            {
                PointCount = Math.Clamp(maxTwilightPoints, 1, 0xFFFF),
            };
            _activeTwilight = _twilight;
            _fontProgram = fontProgram;
            _cvtProgram = cvtProgram;
            _cvt = cvt;
            _storage = storage;
            _activeCvt = cvt;
            _activeStorage = storage;
            _maxFunctionDefs = maxFunctionDefs;
            _maxInstructionDefs = maxInstructionDefs;

            // Shipped fonts routinely push past their declared maxStackElements (the
            // reference cites a prep pushing 255 against a declared 153), so the stack takes
            // the reference's headroom: half again the declaration, at least 128 slots.
            _stack = new int[Math.Clamp(
                maxStackElements + Math.Max(maxStackElements / 2, 128), 128, MaxStackSize)];
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

        /// <summary>Instructions dispatched by the most recent run, for shared budgeting.</summary>
        public int InstructionsExecuted => _instructionCount;

        public ReadOnlySpan<int> Stack => _stack.AsSpan(0, _top);

        public ReadOnlySpan<int> ActiveCvt => _activeCvt;

        public ReadOnlySpan<int> ActiveStorage => _activeStorage;

        public ReadOnlySpan<int> PristineCvt => _cvt;

        public ReadOnlySpan<int> PristineStorage => _storage;

        /// <summary>The size-owned twilight zone as prep left it.</summary>
        public TrueTypeZone PristineTwilight => _twilight;

        /// <summary>The twilight zone the current run works on.</summary>
        public TrueTypeZone ActiveTwilight => _activeTwilight;

        /// <summary>The outline zone of the current glyph run; installed by the loader.</summary>
        public TrueTypeZone? GlyphZone => _glyphZone;

        /// <summary>Composite glyph programs get wider DELTAP/SHPIX exceptions under v40.</summary>
        public bool IsCompositeGlyph;

        /// <summary>
        /// Diagnostic hook: receives one line per dispatched instruction when set. Costs a
        /// null check when unset; string building happens only while tracing.
        /// </summary>
        public Action<string>? Trace;

        public void SetGlyphZone(TrueTypeZone? zone) => _glyphZone = zone;

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

            // Twilight is small and glyph programs routinely move its points, so every run
            // simply works on a fresh copy of the post-prep zone. It stays observable after
            // the run; the next Execute reselects the zone for its range.
            _workingTwilight ??= new TrueTypeZone(_twilight.PointCount, 1);
            _workingTwilight.CopyFrom(_twilight);
            _activeTwilight = _workingTwilight;

            return Execute(TrueTypeCodeRange.Glyph, code);
        }

        private bool Execute(TrueTypeCodeRange range, ReadOnlyMemory<byte> code)
        {
            if (range != TrueTypeCodeRange.Glyph)
            {
                // The control programs build the size-owned twilight zone directly.
                _activeTwilight = _twilight;
            }

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

            // The reference heuristic: sized from the outline when hinting a glyph, from the
            // control value count for the control programs.
            _loopcallCounterMax = _glyphZone is { PointCount: > 0 } zone && range == TrueTypeCodeRange.Glyph
                ? Math.Max(50, 10L * zone.PointCount) + Math.Max(50, _cvt.Length / 10)
                : 300 + 22L * _cvt.Length;

            RefreshVectors();

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

                if (Trace is { } trace)
                {
                    trace($"{_currentRange}:{_ip:D4} {TrueTypeOpcodeName.Of(opcode)} stack={_top}");
                }

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
                    RefreshVectors();
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
                    RefreshVectors();
                    break;
                }

                case 0x04:  // SFVTCA[y]
                case 0x05:  // SFVTCA[x]
                    gs.FreedomX = (short)(opcode == 0x05 ? 0x4000 : 0);
                    gs.FreedomY = (short)(opcode == 0x05 ? 0 : 0x4000);
                    RefreshVectors();
                    break;

                case 0x06 or 0x07:  // SPVTL[a]: pops point1 (top), point2
                    if (Pop2(out a, out b) && SetVectorToLine(b, a, (opcode & 1) != 0, out var lpx, out var lpy))
                    {
                        gs.ProjectionX = lpx;
                        gs.ProjectionY = lpy;
                        gs.DualX = lpx;
                        gs.DualY = lpy;
                        RefreshVectors();
                    }
                    break;

                case 0x08 or 0x09:  // SFVTL[a]
                    if (Pop2(out a, out b) && SetVectorToLine(b, a, (opcode & 1) != 0, out var lfx, out var lfy))
                    {
                        gs.FreedomX = lfx;
                        gs.FreedomY = lfy;
                        RefreshVectors();
                    }
                    break;

                case 0x0A:  // SPVFS: pops y (top), x
                    if (Pop2(out a, out b) && Normalize((short)a, (short)b, out var px, out var py))
                    {
                        gs.ProjectionX = px;
                        gs.ProjectionY = py;
                        gs.DualX = px;
                        gs.DualY = py;
                        RefreshVectors();
                    }
                    break;

                case 0x0B:  // SFVFS
                    if (Pop2(out a, out b) && Normalize((short)a, (short)b, out var fx, out var fy))
                    {
                        gs.FreedomX = fx;
                        gs.FreedomY = fy;
                        RefreshVectors();
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
                    RefreshVectors();
                    break;

                case 0x86 or 0x87:  // SDPVTL[a]: dual from originals, projection from currents
                    if (Pop2(out a, out b))
                        SetDualVectorsToLine(p1: b, p2: a, (opcode & 1) != 0);
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

                // ---- the point engine ----------------------------------------------------

                case 0x0F:  // ISECT
                    Isect();
                    break;

                case 0x27:  // ALIGNPTS
                    AlignPoints();
                    break;

                case 0x29:  // UTP
                    if (Pop(out a))
                        UntouchPoint(a);
                    break;

                case 0x2E or 0x2F:  // MDAP[a]
                    if (Pop(out a))
                        MoveDirectAbsolutePoint(a, round: (opcode & 1) != 0);
                    break;

                case 0x30 or 0x31:  // IUP[a]
                    InterpolateUntouchedPoints(opcode);
                    break;

                case 0x32 or 0x33:  // SHP[a]
                    ShiftPoints(opcode);
                    break;

                case 0x34 or 0x35:  // SHC[a]
                    if (Pop(out a))
                        ShiftContour(opcode, a);
                    break;

                case 0x36 or 0x37:  // SHZ[a]
                    if (Pop(out a))
                        ShiftZone(opcode, a);
                    break;

                case 0x38:  // SHPIX
                    ShiftPointsByPixels();
                    break;

                case 0x39:  // IP
                    InterpolatePoints();
                    break;

                case 0x3A or 0x3B:  // MSIRP[a]: pops distance (top), point
                    if (Pop2(out a, out b))
                        MoveStackIndirectRelativePoint(point: a, distance: b, setRp0: (opcode & 1) != 0);
                    break;

                case 0x3C:  // ALIGNRP
                    AlignToReferencePoint();
                    break;

                case 0x3E or 0x3F:  // MIAP[a]: pops cvt entry (top), point
                    if (Pop2(out a, out b))
                        MoveIndirectAbsolutePoint(point: a, cvtEntry: b, roundAndCutIn: (opcode & 1) != 0);
                    break;

                case 0x46 or 0x47:  // GC[a]
                    if (Pop(out a))
                        Push(GetCoordinate(a, original: (opcode & 1) != 0));
                    break;

                case 0x48:  // SCFS: pops value (top), point
                    if (Pop2(out a, out b))
                        SetCoordinateFromStack(point: a, value: b);
                    break;

                case 0x49 or 0x4A:  // MD[a]: pops point K (top), point L; flag inverted per the reference
                    if (Pop2(out a, out b))
                        Push(MeasureDistance(pointL: a, pointK: b, current: (opcode & 1) != 0));
                    break;

                case 0x5D or 0x71 or 0x72:  // DELTAP1..3
                    DeltaP(opcode);
                    break;

                case 0x80:  // FLIPPT
                    FlipPoints();
                    break;

                case 0x81 or 0x82:  // FLIPRGON, FLIPRGOFF: pops high (top), low
                    if (Pop2(out a, out b))
                        FlipRange(low: a, high: b, on: opcode == 0x81);
                    break;

                case >= 0xC0 and <= 0xDF:  // MDRP[abcde]
                    if (Pop(out a))
                        MoveDirectRelativePoint(opcode, a);
                    break;

                case >= 0xE0:  // MIRP[abcde]: pops cvt index (top), point
                    if (Pop2(out a, out b))
                        MoveIndirectRelativePoint(opcode, point: a, cvtIndex: b);
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

        private bool Normalize(long x, long y, out short unitX, out short unitY)
        {
            unitX = 0;
            unitY = 0;

            if (x == 0 && y == 0)
            {
                return !Fail(TrueTypeError.BadArgument);
            }

            // Math.Sqrt is IEEE-correctly-rounded, so this stays bit-deterministic.
            var length = Math.Sqrt((double)x * x + (double)y * y);

            unitX = (short)Math.Floor(x * 16384.0 / length + 0.5);
            unitY = (short)Math.Floor(y * 16384.0 / length + 0.5);
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

        // ---- point-engine machinery ----------------------------------------------------

        private static readonly TrueTypeZone s_emptyZone = new(0, 0);

        private TrueTypeZone ZoneOf(byte zonePointer) =>
            zonePointer == 0 ? _activeTwilight : _glyphZone ?? s_emptyZone;

        private TrueTypeZone Zp0 => ZoneOf(GraphicsState.Zp0);

        private TrueTypeZone Zp1 => ZoneOf(GraphicsState.Zp1);

        private TrueTypeZone Zp2 => ZoneOf(GraphicsState.Zp2);

        /// <summary>
        /// Recomputes the movement vector from the graphics state, the reference formula:
        /// collinear vectors move by the plain distance, near-orthogonal pairs move nothing,
        /// everything else scales freedom by the inverse projection of itself.
        /// </summary>
        private void RefreshVectors()
        {
            ref var gs = ref GraphicsState;

            var fDotP =
                ((long)gs.ProjectionX * gs.FreedomX +
                 (long)gs.ProjectionY * gs.FreedomY + 0x2000) >> 14;

            if (fDotP >= 0x3FFE)
            {
                _moveX = gs.FreedomX * 4;
                _moveY = gs.FreedomY * 4;
            }
            else if (fDotP > -0x400 && fDotP < 0x400)
            {
                _moveX = 0;
                _moveY = 0;
            }
            else
            {
                _moveX = (int)(gs.FreedomX * 0x10000L / fDotP);
                _moveY = (int)(gs.FreedomY * 0x10000L / fDotP);
            }
        }

        /// <summary>(ax*bx + ay*by) / 2^14 with the reference rounding phase.</summary>
        private static int DotFix14(long ax, long ay, int bx, int by)
        {
            var c = ax * bx + ay * by;

            c += 0x2000 + (c >> 63);

            return (int)(c >> 14);
        }

        private int Project(long dx, long dy)
        {
            ref var gs = ref GraphicsState;

            if (gs.ProjectionX == 0x4000)
                return (int)dx;
            if (gs.ProjectionY == 0x4000)
                return (int)dy;

            return DotFix14(dx, dy, gs.ProjectionX, gs.ProjectionY);
        }

        private int DualProject(long dx, long dy)
        {
            ref var gs = ref GraphicsState;

            if (gs.DualX == 0x4000)
                return (int)dx;
            if (gs.DualY == 0x4000)
                return (int)dy;

            return DotFix14(dx, dy, gs.DualX, gs.DualY);
        }

        /// <summary>
        /// Moves a point along the freedom vector, applying the v40 gates: x never moves in
        /// compatibility mode, y freezes post-IUP, and the touch flags always land so IUP
        /// still treats the point as instructed.
        /// </summary>
        private void MovePoint(TrueTypeZone zone, int point, int distance)
        {
            if (_moveX != 0)
            {
                if (BackwardCompatibility == 0)
                {
                    zone.CurX[point] = unchecked(zone.CurX[point] + F26Dot6.MulFix(distance, _moveX));
                }

                zone.Tags[point] |= TrueTypeZone.TouchX;
            }

            if (_moveY != 0)
            {
                if (BackwardCompatibility != 0x7)
                {
                    zone.CurY[point] = unchecked(zone.CurY[point] + F26Dot6.MulFix(distance, _moveY));
                }

                zone.Tags[point] |= TrueTypeZone.TouchY;
            }
        }

        /// <summary>Moves a point's original position; no gates, no touch.</summary>
        private void MoveOriginal(TrueTypeZone zone, int point, int distance)
        {
            if (_moveX != 0)
            {
                zone.OrgX[point] = unchecked(zone.OrgX[point] + F26Dot6.MulFix(distance, _moveX));
            }

            if (_moveY != 0)
            {
                zone.OrgY[point] = unchecked(zone.OrgY[point] + F26Dot6.MulFix(distance, _moveY));
            }
        }

        /// <summary>The zp2 displacement move SHP/SHC/SHPIX share, gated like MovePoint.</summary>
        private void MoveZp2Point(int point, int dx, int dy)
        {
            var zone = Zp2;
            ref var gs = ref GraphicsState;

            if (gs.FreedomX != 0)
            {
                if (BackwardCompatibility == 0)
                {
                    zone.CurX[point] = unchecked(zone.CurX[point] + dx);
                }

                zone.Tags[point] |= TrueTypeZone.TouchX;
            }

            if (gs.FreedomY != 0)
            {
                if (BackwardCompatibility != 0x7)
                {
                    zone.CurY[point] = unchecked(zone.CurY[point] + dy);
                }

                zone.Tags[point] |= TrueTypeZone.TouchY;
            }
        }

        /// <summary>
        /// The displacement of the reference point selected by the shift opcode's flag bit,
        /// decomposed along the freedom vector. Returns false when the reference is out of
        /// bounds; <paramref name="excludeReference"/> reports the reference index when it
        /// lives in the zone the caller is about to shift.
        /// </summary>
        private bool ComputePointDisplacement(byte opcode, TrueTypeZone? excludeIn, out int dx, out int dy, out int excludeReference)
        {
            TrueTypeZone zone;
            int p;

            if ((opcode & 1) != 0)
            {
                zone = Zp0;
                p = GraphicsState.Rp1;
            }
            else
            {
                zone = Zp1;
                p = GraphicsState.Rp2;
            }

            if ((uint)p >= (uint)zone.PointCount)
            {
                dx = 0;
                dy = 0;
                excludeReference = -1;
                return false;
            }

            excludeReference = ReferenceEquals(zone, excludeIn) ? p : -1;

            var d = Project(zone.CurX[p] - (long)zone.OrgX[p], zone.CurY[p] - (long)zone.OrgY[p]);

            dx = F26Dot6.MulFix(d, _moveX);
            dy = F26Dot6.MulFix(d, _moveY);
            return true;
        }

        private bool SetVectorToLine(int aIdx1, int aIdx2, bool perpendicular, out short unitX, out short unitY)
        {
            unitX = 0;
            unitY = 0;

            var zp1 = Zp1;
            var zp2 = Zp2;

            if ((uint)aIdx2 >= (uint)zp1.PointCount || (uint)aIdx1 >= (uint)zp2.PointCount)
            {
                return false;
            }

            long a = zp1.CurX[aIdx2] - (long)zp2.CurX[aIdx1];
            long b = zp1.CurY[aIdx2] - (long)zp2.CurY[aIdx1];

            // Identical points behave like the x-axis without rotation, per the reference.
            if (a == 0 && b == 0)
            {
                a = 0x4000;
                perpendicular = false;
            }

            if (perpendicular)
            {
                (a, b) = (-b, a);
            }

            return Normalize(a, b, out unitX, out unitY);
        }

        private void SetDualVectorsToLine(int p1, int p2, bool perpendicular)
        {
            ref var gs = ref GraphicsState;
            var zp1 = Zp1;
            var zp2 = Zp2;

            if ((uint)p2 >= (uint)zp1.PointCount || (uint)p1 >= (uint)zp2.PointCount)
            {
                return;
            }

            // The dual vector measures the original outline, the projection the current one.
            long a = zp1.OrgX[p2] - (long)zp2.OrgX[p1];
            long b = zp1.OrgY[p2] - (long)zp2.OrgY[p1];
            var rotate = perpendicular;

            if (a == 0 && b == 0)
            {
                a = 0x4000;
                rotate = false;
                perpendicular = false;
            }

            if (rotate)
            {
                (a, b) = (-b, a);
            }

            if (!Normalize(a, b, out var dualX, out var dualY))
            {
                return;
            }

            gs.DualX = dualX;
            gs.DualY = dualY;

            a = zp1.CurX[p2] - (long)zp2.CurX[p1];
            b = zp1.CurY[p2] - (long)zp2.CurY[p1];
            rotate = perpendicular;

            if (a == 0 && b == 0)
            {
                a = 0x4000;
                rotate = false;
            }

            if (rotate)
            {
                (a, b) = (-b, a);
            }

            if (Normalize(a, b, out var projX, out var projY))
            {
                gs.ProjectionX = projX;
                gs.ProjectionY = projY;
                RefreshVectors();
            }
        }

        // ---- geometric instructions ----------------------------------------------------

        private void MoveDirectAbsolutePoint(int point, bool round)
        {
            var zone = Zp0;

            if ((uint)point >= (uint)zone.PointCount)
            {
                return;
            }

            var distance = 0;

            if (round)
            {
                var current = Project(zone.CurX[point], zone.CurY[point]);

                distance = RoundValue(current, 0) - current;
            }

            MovePoint(zone, point, distance);

            GraphicsState.Rp0 = point;
            GraphicsState.Rp1 = point;
        }

        private void MoveIndirectAbsolutePoint(int point, int cvtEntry, bool roundAndCutIn)
        {
            var zone = Zp0;

            if ((uint)point < (uint)zone.PointCount && (uint)cvtEntry < (uint)_activeCvt.Length)
            {
                var distance = _activeCvt[cvtEntry];

                // Twilight points are created here: the original position becomes the
                // unrounded control value along the freedom vector, which is what lets IP
                // work in the twilight zone (the Arial/Times prep idiom).
                if (GraphicsState.Zp0 == 0)
                {
                    zone.OrgX[point] = DotFix14(distance, 0, GraphicsState.FreedomX, 0);
                    zone.OrgY[point] = DotFix14(distance, 0, GraphicsState.FreedomY, 0);
                    zone.CurX[point] = zone.OrgX[point];
                    zone.CurY[point] = zone.OrgY[point];
                }

                var orgDist = Project(zone.CurX[point], zone.CurY[point]);

                if (roundAndCutIn)
                {
                    var delta = distance - orgDist;

                    if (delta < 0)
                    {
                        delta = -delta;
                    }

                    if (delta > GraphicsState.ControlValueCutIn)
                    {
                        distance = orgDist;
                    }

                    distance = RoundValue(distance, 0);
                }

                MovePoint(zone, point, distance - orgDist);
            }

            GraphicsState.Rp0 = point;
            GraphicsState.Rp1 = point;
        }

        private void MoveDirectRelativePoint(byte opcode, int point)
        {
            ref var gs = ref GraphicsState;
            var zp0 = Zp0;
            var zp1 = Zp1;

            if ((uint)point < (uint)zp1.PointCount && (uint)gs.Rp0 < (uint)zp0.PointCount)
            {
                int orgDist;

                // Original distances come from the unscaled outline except in twilight,
                // where only scaled originals exist.
                if (gs.Zp0 == 0 || gs.Zp1 == 0)
                {
                    orgDist = DualProject(
                        zp1.OrgX[point] - (long)zp0.OrgX[gs.Rp0],
                        zp1.OrgY[point] - (long)zp0.OrgY[gs.Rp0]);
                }
                else
                {
                    orgDist = F26Dot6.MulFix(
                        DualProject(
                            zp1.OrusX[point] - (long)zp0.OrusX[gs.Rp0],
                            zp1.OrusY[point] - (long)zp0.OrusY[gs.Rp0]),
                        _scale);
                }

                // Single-width cut-in.
                if (gs.SingleWidthCutIn > 0 &&
                    orgDist < gs.SingleWidthValue + gs.SingleWidthCutIn &&
                    orgDist > gs.SingleWidthValue - gs.SingleWidthCutIn)
                {
                    orgDist = orgDist >= 0 ? gs.SingleWidthValue : -gs.SingleWidthValue;
                }

                var distance = (opcode & 4) != 0
                    ? RoundValue(orgDist, 0)
                    : RoundNone(orgDist);

                if ((opcode & 8) != 0)
                {
                    if (orgDist >= 0)
                    {
                        if (distance < gs.MinimumDistance)
                        {
                            distance = gs.MinimumDistance;
                        }
                    }
                    else if (distance > -gs.MinimumDistance)
                    {
                        distance = -gs.MinimumDistance;
                    }
                }

                var currentDist = Project(
                    zp1.CurX[point] - (long)zp0.CurX[gs.Rp0],
                    zp1.CurY[point] - (long)zp0.CurY[gs.Rp0]);

                MovePoint(zp1, point, distance - currentDist);
            }

            gs.Rp1 = gs.Rp0;
            gs.Rp2 = point;

            if ((opcode & 16) != 0)
            {
                gs.Rp0 = point;
            }
        }

        private void MoveIndirectRelativePoint(byte opcode, int point, int cvtIndex)
        {
            ref var gs = ref GraphicsState;
            var zp0 = Zp0;
            var zp1 = Zp1;

            // cvt[-1] reads zero by long-standing rasterizer convention.
            var cvtEntry = cvtIndex + 1;

            if ((uint)point < (uint)zp1.PointCount &&
                (uint)cvtEntry < (uint)_activeCvt.Length + 1 &&
                (uint)gs.Rp0 < (uint)zp0.PointCount)
            {
                var cvtDist = cvtEntry == 0 ? 0 : _activeCvt[cvtEntry - 1];

                // Single-width cut-in applies to the control value here.
                var delta = cvtDist - gs.SingleWidthValue;

                if (delta < 0)
                {
                    delta = -delta;
                }

                if (delta < gs.SingleWidthCutIn)
                {
                    cvtDist = cvtDist >= 0 ? gs.SingleWidthValue : -gs.SingleWidthValue;
                }

                // Twilight points spring into being relative to the reference point.
                if (gs.Zp1 == 0)
                {
                    zp1.OrgX[point] = zp0.OrgX[gs.Rp0] + DotFix14(cvtDist, 0, gs.FreedomX, 0);
                    zp1.OrgY[point] = zp0.OrgY[gs.Rp0] + DotFix14(cvtDist, 0, gs.FreedomY, 0);
                    zp1.CurX[point] = zp1.OrgX[point];
                    zp1.CurY[point] = zp1.OrgY[point];
                }

                var orgDist = DualProject(
                    zp1.OrgX[point] - (long)zp0.OrgX[gs.Rp0],
                    zp1.OrgY[point] - (long)zp0.OrgY[gs.Rp0]);
                var currentDist = Project(
                    zp1.CurX[point] - (long)zp0.CurX[gs.Rp0],
                    zp1.CurY[point] - (long)zp0.CurY[gs.Rp0]);

                if (gs.AutoFlip && (orgDist ^ cvtDist) < 0)
                {
                    cvtDist = -cvtDist;
                }

                int distance;

                if ((opcode & 4) != 0)
                {
                    // The cut-in only applies when both points live in the same zone.
                    if (gs.Zp0 == gs.Zp1)
                    {
                        delta = cvtDist - orgDist;

                        if (delta < 0)
                        {
                            delta = -delta;
                        }

                        if (delta > gs.ControlValueCutIn)
                        {
                            cvtDist = orgDist;
                        }
                    }

                    distance = RoundValue(cvtDist, 0);
                }
                else
                {
                    distance = RoundNone(cvtDist);
                }

                if ((opcode & 8) != 0)
                {
                    if (orgDist >= 0)
                    {
                        if (distance < gs.MinimumDistance)
                        {
                            distance = gs.MinimumDistance;
                        }
                    }
                    else if (distance > -gs.MinimumDistance)
                    {
                        distance = -gs.MinimumDistance;
                    }
                }

                MovePoint(zp1, point, distance - currentDist);
            }

            gs.Rp1 = gs.Rp0;
            gs.Rp2 = point;

            if ((opcode & 16) != 0)
            {
                gs.Rp0 = point;
            }
        }

        private void MoveStackIndirectRelativePoint(int point, int distance, bool setRp0)
        {
            ref var gs = ref GraphicsState;
            var zp0 = Zp0;
            var zp1 = Zp1;

            if ((uint)point < (uint)zp1.PointCount && (uint)gs.Rp0 < (uint)zp0.PointCount)
            {
                if (gs.Zp1 == 0)
                {
                    zp1.OrgX[point] = zp0.OrgX[gs.Rp0];
                    zp1.OrgY[point] = zp0.OrgY[gs.Rp0];
                    MoveOriginal(zp1, point, distance);
                    zp1.CurX[point] = zp1.OrgX[point];
                    zp1.CurY[point] = zp1.OrgY[point];
                }

                var currentDist = Project(
                    zp1.CurX[point] - (long)zp0.CurX[gs.Rp0],
                    zp1.CurY[point] - (long)zp0.CurY[gs.Rp0]);

                MovePoint(zp1, point, distance - currentDist);
            }

            gs.Rp1 = gs.Rp0;
            gs.Rp2 = point;

            if (setRp0)
            {
                gs.Rp0 = point;
            }
        }

        private void AlignToReferencePoint()
        {
            var loop = GraphicsState.Loop;

            GraphicsState.Loop = 1;

            if (_top < loop)
            {
                return;
            }

            var zp0 = Zp0;
            var zp1 = Zp1;
            var valid = (uint)GraphicsState.Rp0 < (uint)zp0.PointCount;

            while (loop-- > 0)
            {
                Pop(out var point);

                if (!valid || (uint)point >= (uint)zp1.PointCount)
                {
                    continue;
                }

                var distance = Project(
                    zp1.CurX[point] - (long)zp0.CurX[GraphicsState.Rp0],
                    zp1.CurY[point] - (long)zp0.CurY[GraphicsState.Rp0]);

                MovePoint(zp1, point, -distance);
            }
        }

        private void AlignPoints()
        {
            if (!Pop2(out var p1, out var p2))
            {
                return;
            }

            var zp0 = Zp0;
            var zp1 = Zp1;

            if ((uint)p1 >= (uint)zp1.PointCount || (uint)p2 >= (uint)zp0.PointCount)
            {
                return;
            }

            var distance = Project(
                zp0.CurX[p2] - (long)zp1.CurX[p1],
                zp0.CurY[p2] - (long)zp1.CurY[p1]) / 2;

            MovePoint(zp1, p1, distance);
            MovePoint(zp0, p2, -distance);
        }

        private void Isect()
        {
            if (_top < 5)
            {
                Fail(TrueTypeError.TooFewArguments);
                return;
            }

            Pop(out var b1);
            Pop(out var b0);
            Pop(out var a1);
            Pop(out var a0);
            Pop(out var point);

            var zp0 = Zp0;
            var zp1 = Zp1;
            var zp2 = Zp2;

            if ((uint)b0 >= (uint)zp0.PointCount || (uint)b1 >= (uint)zp0.PointCount ||
                (uint)a0 >= (uint)zp1.PointCount || (uint)a1 >= (uint)zp1.PointCount ||
                (uint)point >= (uint)zp2.PointCount)
            {
                return;
            }

            var dbx = zp0.CurX[b1] - zp0.CurX[b0];
            var dby = zp0.CurY[b1] - zp0.CurY[b0];
            var dax = zp1.CurX[a1] - zp1.CurX[a0];
            var day = zp1.CurY[a1] - zp1.CurY[a0];
            var dx = zp0.CurX[b0] - zp1.CurX[a0];
            var dy = zp0.CurY[b0] - zp1.CurY[a0];

            var discriminant = F26Dot6.MulDivRounded(dax, -dby, 0x40) +
                               F26Dot6.MulDivRounded(day, dbx, 0x40);
            var dotProduct = F26Dot6.MulDivRounded(dax, dbx, 0x40) +
                             F26Dot6.MulDivRounded(day, dby, 0x40);

            // Grazing intersections (under about 3 degrees) snap to the middle of the four
            // ends instead, the reference's stability rule.
            if (19L * Math.Abs(discriminant) > Math.Abs((long)dotProduct))
            {
                var val = F26Dot6.MulDivRounded(dx, -dby, 0x40) +
                          F26Dot6.MulDivRounded(dy, dbx, 0x40);

                zp2.CurX[point] = unchecked(zp1.CurX[a0] + F26Dot6.MulDivRounded(val, dax, discriminant));
                zp2.CurY[point] = unchecked(zp1.CurY[a0] + F26Dot6.MulDivRounded(val, day, discriminant));
            }
            else
            {
                zp2.CurX[point] = (int)(((long)zp1.CurX[a0] + zp1.CurX[a1] + zp0.CurX[b0] + zp0.CurX[b1]) / 4);
                zp2.CurY[point] = (int)(((long)zp1.CurY[a0] + zp1.CurY[a1] + zp0.CurY[b0] + zp0.CurY[b1]) / 4);
            }

            zp2.Tags[point] |= TrueTypeZone.TouchBoth;
        }

        private void ShiftPoints(byte opcode)
        {
            var loop = GraphicsState.Loop;

            GraphicsState.Loop = 1;

            if (_top < loop)
            {
                return;
            }

            var valid = ComputePointDisplacement(opcode, excludeIn: null, out var dx, out var dy, out _);
            var zp2 = Zp2;

            while (loop-- > 0)
            {
                Pop(out var point);

                if (valid && (uint)point < (uint)zp2.PointCount)
                {
                    MoveZp2Point(point, dx, dy);
                }
            }
        }

        private void ShiftContour(byte opcode, int contour)
        {
            var zp2 = Zp2;
            var contourBound = GraphicsState.Zp2 == 0 ? 1 : zp2.ContourCount;

            if ((uint)contour >= (uint)contourBound ||
                !ComputePointDisplacement(opcode, excludeIn: zp2, out var dx, out var dy, out var reference))
            {
                return;
            }

            var start = contour == 0 ? 0 : zp2.ContourEnds[contour - 1] + 1 - zp2.FirstPoint;

            // The twilight zone has one virtual contour spanning every point.
            var limit = GraphicsState.Zp2 == 0
                ? zp2.PointCount
                : zp2.ContourEnds[contour] + 1 - zp2.FirstPoint;

            for (var i = start; i < limit; i++)
            {
                if (i != reference)
                {
                    MoveZp2Point(i, dx, dy);
                }
            }
        }

        private void ShiftZone(byte opcode, int zoneNumber)
        {
            if ((uint)zoneNumber > 1)
            {
                return;
            }

            TrueTypeZone zone;
            int limit;

            if (zoneNumber == 0)
            {
                zone = _activeTwilight;
                limit = zone.PointCount;
            }
            else
            {
                zone = _glyphZone ?? s_emptyZone;

                // The phantom points never shift with the zone.
                limit = zone.PointCount > 4 ? zone.PointCount - 4 : 0;
            }

            if (!ComputePointDisplacement(opcode, excludeIn: zone, out var dx, out var dy, out var reference))
            {
                return;
            }

            // Zone shifts move without touching, with the usual per-axis v40 gates.
            if (dx != 0 && BackwardCompatibility == 0)
            {
                for (var i = 0; i < limit; i++)
                {
                    if (i != reference)
                    {
                        zone.CurX[i] = unchecked(zone.CurX[i] + dx);
                    }
                }
            }

            if (dy != 0 && BackwardCompatibility != 0x7)
            {
                for (var i = 0; i < limit; i++)
                {
                    if (i != reference)
                    {
                        zone.CurY[i] = unchecked(zone.CurY[i] + dy);
                    }
                }
            }
        }

        private void ShiftPointsByPixels()
        {
            if (!Pop(out var amount))
            {
                return;
            }

            var loop = GraphicsState.Loop;

            GraphicsState.Loop = 1;

            if (_top < loop)
            {
                return;
            }

            ref var gs = ref GraphicsState;
            var zp2 = Zp2;
            var inTwilight = gs.Zp0 == 0 || gs.Zp1 == 0 || gs.Zp2 == 0;
            var dx = DotFix14(amount, 0, gs.FreedomX, 0);
            var dy = DotFix14(amount, 0, gs.FreedomY, 0);

            while (loop-- > 0)
            {
                Pop(out var point);

                if ((uint)point >= (uint)zp2.PointCount)
                {
                    continue;
                }

                if (BackwardCompatibility != 0)
                {
                    // Twilight moves always pass; outline moves only pre-IUP for composite
                    // y adjustment or already y-touched points, and then y-only. This is
                    // the reference's unbreak-Rokkitt rule.
                    if (inTwilight ||
                        (BackwardCompatibility != 0x7 &&
                         ((IsCompositeGlyph && gs.FreedomY != 0) ||
                          (zp2.Tags[point] & TrueTypeZone.TouchY) != 0)))
                    {
                        MoveZp2Point(point, 0, dy);
                    }
                }
                else
                {
                    MoveZp2Point(point, dx, dy);
                }
            }
        }

        private void InterpolatePoints()
        {
            var loop = GraphicsState.Loop;

            GraphicsState.Loop = 1;

            if (_top < loop)
            {
                return;
            }

            ref var gs = ref GraphicsState;
            var zp0 = Zp0;
            var zp1 = Zp1;
            var zp2 = Zp2;

            if ((uint)gs.Rp1 >= (uint)zp0.PointCount)
            {
                // The reference still consumes the points when rp1 is invalid.
                while (loop-- > 0)
                {
                    Pop(out _);
                }

                return;
            }

            // Twilight orus values are all zero by definition, so scaled originals stand in;
            // for outline points the unscaled originals measure the old range, and because
            // the interpolation is a pure ratio the scale cancels.
            var twilight = gs.Zp0 == 0 || gs.Zp1 == 0 || gs.Zp2 == 0;

            long orusBaseX, orusBaseY;

            if (twilight)
            {
                orusBaseX = zp0.OrgX[gs.Rp1];
                orusBaseY = zp0.OrgY[gs.Rp1];
            }
            else
            {
                orusBaseX = zp0.OrusX[gs.Rp1];
                orusBaseY = zp0.OrusY[gs.Rp1];
            }

            long curBaseX = zp0.CurX[gs.Rp1];
            long curBaseY = zp0.CurY[gs.Rp1];

            var oldRange = 0;
            var curRange = 0;

            if ((uint)gs.Rp2 < (uint)zp1.PointCount)
            {
                oldRange = twilight
                    ? DualProject(zp1.OrgX[gs.Rp2] - orusBaseX, zp1.OrgY[gs.Rp2] - orusBaseY)
                    : DualProject(zp1.OrusX[gs.Rp2] - orusBaseX, zp1.OrusY[gs.Rp2] - orusBaseY);
                curRange = Project(zp1.CurX[gs.Rp2] - curBaseX, zp1.CurY[gs.Rp2] - curBaseY);
            }

            while (loop-- > 0)
            {
                Pop(out var point);

                if ((uint)point >= (uint)zp2.PointCount)
                {
                    continue;
                }

                var orgDist = twilight
                    ? DualProject(zp2.OrgX[point] - orusBaseX, zp2.OrgY[point] - orusBaseY)
                    : DualProject(zp2.OrusX[point] - orusBaseX, zp2.OrusY[point] - orusBaseY);
                var curDist = Project(zp2.CurX[point] - curBaseX, zp2.CurY[point] - curBaseY);
                int newDist;

                if (orgDist != 0)
                {
                    newDist = oldRange != 0
                        ? F26Dot6.MulDivRounded(orgDist, curRange, oldRange)
                        : orgDist;
                }
                else
                {
                    newDist = 0;
                }

                MovePoint(zp2, point, newDist - curDist);
            }
        }

        private int GetCoordinate(int point, bool original)
        {
            var zone = Zp2;

            if ((uint)point >= (uint)zone.PointCount)
            {
                return 0;
            }

            return original
                ? DualProject(zone.OrgX[point], zone.OrgY[point])
                : Project(zone.CurX[point], zone.CurY[point]);
        }

        private void SetCoordinateFromStack(int point, int value)
        {
            var zone = Zp2;

            if ((uint)point >= (uint)zone.PointCount)
            {
                return;
            }

            var current = Project(zone.CurX[point], zone.CurY[point]);

            MovePoint(zone, point, value - current);

            // Twilight points remember the write as their original position too.
            if (GraphicsState.Zp2 == 0)
            {
                zone.OrgX[point] = zone.CurX[point];
                zone.OrgY[point] = zone.CurY[point];
            }
        }

        private int MeasureDistance(int pointL, int pointK, bool current)
        {
            var zp0 = Zp0;
            var zp1 = Zp1;

            if ((uint)pointL >= (uint)zp0.PointCount || (uint)pointK >= (uint)zp1.PointCount)
            {
                return 0;
            }

            if (current)
            {
                return Project(
                    zp0.CurX[pointL] - (long)zp1.CurX[pointK],
                    zp0.CurY[pointL] - (long)zp1.CurY[pointK]);
            }

            if (GraphicsState.Zp0 == 0 || GraphicsState.Zp1 == 0)
            {
                return DualProject(
                    zp0.OrgX[pointL] - (long)zp1.OrgX[pointK],
                    zp0.OrgY[pointL] - (long)zp1.OrgY[pointK]);
            }

            return F26Dot6.MulFix(
                DualProject(
                    zp0.OrusX[pointL] - (long)zp1.OrusX[pointK],
                    zp0.OrusY[pointL] - (long)zp1.OrusY[pointK]),
                _scale);
        }

        private void UntouchPoint(int point)
        {
            var zone = Zp0;

            if ((uint)point >= (uint)zone.PointCount)
            {
                return;
            }

            var mask = (byte)0xFF;

            if (GraphicsState.FreedomX != 0)
            {
                mask &= unchecked((byte)~TrueTypeZone.TouchX);
            }

            if (GraphicsState.FreedomY != 0)
            {
                mask &= unchecked((byte)~TrueTypeZone.TouchY);
            }

            zone.Tags[point] &= mask;
        }

        private void FlipPoints()
        {
            var loop = GraphicsState.Loop;

            GraphicsState.Loop = 1;

            if (_top < loop)
            {
                return;
            }

            // Post-IUP flips fix monochrome pixel patterns and only dent AA rendering; the
            // arguments are still consumed.
            var blocked = BackwardCompatibility == 0x7;
            var zone = _glyphZone ?? s_emptyZone;

            while (loop-- > 0)
            {
                Pop(out var point);

                if (!blocked && (uint)point < (uint)zone.PointCount)
                {
                    zone.Tags[point] ^= TrueTypeZone.OnCurve;
                }
            }
        }

        private void FlipRange(int low, int high, bool on)
        {
            if (BackwardCompatibility == 0x7)
            {
                return;
            }

            var zone = _glyphZone ?? s_emptyZone;

            if ((uint)low >= (uint)zone.PointCount || (uint)high >= (uint)zone.PointCount)
            {
                return;
            }

            for (var i = low; i <= high; i++)
            {
                if (on)
                {
                    zone.Tags[i] |= TrueTypeZone.OnCurve;
                }
                else
                {
                    zone.Tags[i] &= unchecked((byte)~TrueTypeZone.OnCurve);
                }
            }
        }

        private void DeltaP(byte opcode)
        {
            if (!Pop(out var count))
            {
                return;
            }

            count = Math.Clamp(count, 0, _top / 2);

            ref var gs = ref GraphicsState;
            var zone = Zp0;

            var basePpem = _ppem - gs.DeltaBase + opcode switch
            {
                0x71 => -16,
                0x72 => -32,
                _ => 0,
            };

            var magnitude = 1 << (6 - gs.DeltaShift);

            while (count-- > 0)
            {
                Pop(out var point);
                Pop(out var arg);

                if ((basePpem & ~0xF) != 0 ||
                    (arg & 0xF0) >> 4 != basePpem ||
                    (uint)point >= (uint)zone.PointCount)
                {
                    continue;
                }

                var steps = (arg & 0xF) - 8;

                if (steps >= 0)
                {
                    steps++;
                }

                steps *= magnitude;

                if (BackwardCompatibility != 0)
                {
                    if (BackwardCompatibility != 0x7 &&
                        ((IsCompositeGlyph && gs.FreedomY != 0) ||
                         (zone.Tags[point] & TrueTypeZone.TouchY) != 0))
                    {
                        MovePoint(zone, point, steps);
                    }
                }
                else
                {
                    MovePoint(zone, point, steps);
                }
            }
        }

        private void InterpolateUntouchedPoints(byte opcode)
        {
            // Track the per-axis IUP state for the v40 curfews; the second call on an axis
            // pair completes the outline and later ones are no-ops.
            if (BackwardCompatibility == 0x7)
            {
                return;
            }

            if (BackwardCompatibility != 0)
            {
                BackwardCompatibility |= 1 << (opcode & 1);
            }

            if (_glyphZone is not { ContourCount: > 0 } zone)
            {
                return;
            }

            var xAxis = (opcode & 1) != 0;
            var mask = xAxis ? TrueTypeZone.TouchX : TrueTypeZone.TouchY;
            var point = 0;

            for (var contour = 0; contour < zone.ContourCount; contour++)
            {
                var endPoint = zone.ContourEnds[contour] - zone.FirstPoint;
                var firstPoint = point;

                if (endPoint >= zone.PointCount)
                {
                    endPoint = zone.PointCount - 1;
                }

                while (point <= endPoint && (zone.Tags[point] & mask) == 0)
                {
                    point++;
                }

                if (point <= endPoint)
                {
                    var firstTouched = point;
                    var currentTouched = point;

                    point++;

                    while (point <= endPoint)
                    {
                        if ((zone.Tags[point] & mask) != 0)
                        {
                            IupInterpolate(zone, xAxis, currentTouched + 1, point - 1, currentTouched, point);
                            currentTouched = point;
                        }

                        point++;
                    }

                    if (currentTouched == firstTouched)
                    {
                        IupShift(zone, xAxis, firstPoint, endPoint, currentTouched);
                    }
                    else
                    {
                        IupInterpolate(zone, xAxis, currentTouched + 1, endPoint, currentTouched, firstTouched);

                        if (firstTouched > 0)
                        {
                            IupInterpolate(zone, xAxis, firstPoint, firstTouched - 1, currentTouched, firstTouched);
                        }
                    }
                }
            }
        }

        private static void IupShift(TrueTypeZone zone, bool xAxis, int p1, int p2, int p)
        {
            var cur = xAxis ? zone.CurX : zone.CurY;
            var org = xAxis ? zone.OrgX : zone.OrgY;
            var delta = cur[p] - org[p];

            if (delta == 0)
            {
                return;
            }

            for (var i = p1; i < p; i++)
            {
                cur[i] = unchecked(cur[i] + delta);
            }

            for (var i = p + 1; i <= p2; i++)
            {
                cur[i] = unchecked(cur[i] + delta);
            }
        }

        private static void IupInterpolate(TrueTypeZone zone, bool xAxis, int p1, int p2, int ref1, int ref2)
        {
            if (p1 > p2 ||
                (uint)ref1 >= (uint)zone.PointCount ||
                (uint)ref2 >= (uint)zone.PointCount)
            {
                return;
            }

            var cur = xAxis ? zone.CurX : zone.CurY;
            var org = xAxis ? zone.OrgX : zone.OrgY;
            var orus = xAxis ? zone.OrusX : zone.OrusY;

            var orus1 = orus[ref1];
            var orus2 = orus[ref2];

            if (orus1 > orus2)
            {
                (orus1, orus2) = (orus2, orus1);
                (ref1, ref2) = (ref2, ref1);
            }

            var org1 = org[ref1];
            var org2 = org[ref2];
            var cur1 = cur[ref1];
            var cur2 = cur[ref2];
            var delta1 = cur1 - org1;
            var delta2 = cur2 - org2;

            if (cur1 == cur2 || orus1 == orus2)
            {
                for (var i = p1; i <= p2; i++)
                {
                    var x = org[i];

                    if (x <= org1)
                    {
                        x = unchecked(x + delta1);
                    }
                    else if (x >= org2)
                    {
                        x = unchecked(x + delta2);
                    }
                    else
                    {
                        x = cur1;
                    }

                    cur[i] = x;
                }
            }
            else
            {
                var scale = 0L;
                var scaleValid = false;

                for (var i = p1; i <= p2; i++)
                {
                    var x = org[i];

                    if (x <= org1)
                    {
                        x = unchecked(x + delta1);
                    }
                    else if (x >= org2)
                    {
                        x = unchecked(x + delta2);
                    }
                    else
                    {
                        if (!scaleValid)
                        {
                            scaleValid = true;
                            scale = Math.Clamp(
                                ((long)(cur2 - cur1) << 16) / (orus2 - orus1),
                                int.MinValue, int.MaxValue);
                        }

                        x = unchecked(cur1 + F26Dot6.MulFix(orus[i] - orus1, (int)scale));
                    }

                    cur[i] = x;
                }
            }
        }

        /// <summary>Engine compensation without rounding, the NROUND/unrounded-MDRP form.</summary>
        private static int RoundNone(int distance) => distance;
    }
}
