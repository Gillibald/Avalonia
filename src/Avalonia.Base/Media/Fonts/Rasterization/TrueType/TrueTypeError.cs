namespace Avalonia.Media.Fonts.Rasterization.TrueType
{
    /// <summary>
    /// Why an instruction run stopped. Any value other than <see cref="None"/> vetoes the
    /// run: the caller falls back (auto-hinter or unhinted), never renders a half-executed
    /// program. Mirrors the FreeType error taxonomy where a distinction is observable.
    /// </summary>
    internal enum TrueTypeError
    {
        None = 0,

        /// <summary>An opcode outside the implemented set with no IDEF covering it.</summary>
        InvalidOpcode,

        /// <summary>A recognized opcode whose engine support has not been built yet.</summary>
        UnsupportedOpcode,

        /// <summary>An instruction needed more stack values than were present.</summary>
        TooFewArguments,

        /// <summary>The value stack or the call stack exceeded its limit.</summary>
        StackOverflow,

        /// <summary>An argument outside its instruction's domain (SLOOP &lt; 0, SDS &gt; 6, zero-offset jump).</summary>
        BadArgument,

        /// <summary>A reference that does not resolve: unknown function, inactive definition, bad jump target.</summary>
        InvalidReference,

        /// <summary>DIV with a zero divisor.</summary>
        DivideByZero,

        /// <summary>Instruction, LOOPCALL or backward-jump budget exhausted.</summary>
        ExecutionTooLong,

        /// <summary>FDEF or IDEF encountered inside a glyph program.</summary>
        DefInGlyphProgram,

        /// <summary>FDEF or IDEF nested inside another definition.</summary>
        NestedDefs,

        /// <summary>ENDF with no active call.</summary>
        EndfInExecStream,

        /// <summary>More function or instruction definitions than the declared budget.</summary>
        TooManyDefs,

        /// <summary>Instruction data or a definition ran past the end of its code range.</summary>
        CodeOverflow,

        /// <summary>The DEBUG opcode, which production fonts must not execute.</summary>
        DebugOpcode,
    }
}
