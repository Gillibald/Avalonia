using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts.Rasterization;
using Avalonia.Media.Fonts.Rasterization.TrueType;
using Avalonia.Media.Fonts.Tables;

namespace TextLab
{
    /// <summary>
    /// A private bytecode-hinting run for the inspector: its own size state and hinter (the
    /// render path's cached instances are never touched), executed once with the interpreter
    /// trace attached, capturing the point state at every top-level glyph instruction so the
    /// view can scrub through the program watching points move.
    /// </summary>
    internal sealed class TrueTypeHintingProbe
    {
        internal readonly record struct Step(string Instruction, int[] CurX, int[] CurY, byte[] Tags);

        private TrueTypeHintingProbe(TrueTypeZone zone, List<Step> steps, bool canScrub,
            int instructionsExecuted, bool fullInterpretation)
        {
            Zone = zone;
            Steps = steps;
            CanScrub = canScrub;
            InstructionsExecuted = instructionsExecuted;
            FullInterpretation = fullInterpretation;
        }

        /// <summary>The final hinted zone (26.6 device pixels, y-up, phantoms last).</summary>
        public TrueTypeZone Zone { get; }

        /// <summary>Per-instruction snapshots of the glyph stream: the state BEFORE each
        /// top-level instruction. Position k of the scrubber shows Steps[k] (k &lt; Count)
        /// or the final zone (k == Count).</summary>
        public List<Step> Steps { get; }

        /// <summary>False for composites, whose zone changes per component - the scrubber
        /// then stays at the final assembly.</summary>
        public bool CanScrub { get; }

        public int InstructionsExecuted { get; }

        /// <summary>Full both-axes interpretation (Strong/Aliased) vs the v40 y-only class.</summary>
        public bool FullInterpretation { get; }

        public int StepCount => CanScrub ? Steps.Count : 0;

        /// <summary>The instruction label for a scrubber position (position k executed
        /// instruction k-1 last; position 0 is the loaded, un-instructed state).</summary>
        public string StepLabel(int position)
        {
            if (!CanScrub || Steps.Count == 0)
            {
                return "final state";
            }

            position = Math.Clamp(position, 0, Steps.Count);
            return position == 0
                ? FormattableString.Invariant($"loaded outline, {Steps.Count} instructions ahead")
                : Steps[position - 1].Instruction;
        }

        /// <summary>Cur coordinates at a scrubber position (the final zone past the end).</summary>
        public (int[] CurX, int[] CurY, byte[] Tags) StateAt(int position)
        {
            if (!CanScrub || position >= Steps.Count || Steps.Count == 0)
            {
                return (Zone.CurX, Zone.CurY, Zone.Tags);
            }

            var step = Steps[Math.Max(position, 0)];

            return (step.CurX, step.CurY, step.Tags);
        }

        /// <summary>Emits the outline at a scrubber position through the real emitter by
        /// temporarily restoring that snapshot into the zone.</summary>
        public void EmitAt(int position, Matrix transform, GlyphPathBuilder sink)
        {
            var (curX, curY, _) = StateAt(position);

            if (ReferenceEquals(curX, Zone.CurX))
            {
                TrueTypeGlyphEmitter.Emit(Zone, transform, sink);
                return;
            }

            var finalX = new int[Zone.PointCount];
            var finalY = new int[Zone.PointCount];

            Array.Copy(Zone.CurX, finalX, Zone.PointCount);
            Array.Copy(Zone.CurY, finalY, Zone.PointCount);
            Array.Copy(curX, Zone.CurX, Zone.PointCount);
            Array.Copy(curY, Zone.CurY, Zone.PointCount);

            try
            {
                TrueTypeGlyphEmitter.Emit(Zone, transform, sink);
            }
            finally
            {
                Array.Copy(finalX, Zone.CurX, Zone.PointCount);
                Array.Copy(finalY, Zone.CurY, Zone.PointCount);
            }
        }

        /// <summary>
        /// Runs the glyph through a fresh hinter. Null with a reason when the bytecode
        /// branch would not run in the pipeline (ineligible, faulted programs, INSTCTRL
        /// disable, or a glyph-level veto) - the caller shows the fallback engine instead.
        /// </summary>
        public static TrueTypeHintingProbe? TryCreate(GlyphTypeface typeface, ushort glyph,
            float size, GlyphMaskMode mode, bool stemSnap, out string? reason)
        {
            reason = null;

            if (!typeface.HasTrueTypeHinting)
            {
                return null;
            }

            var scaleQ = GlyphMaskKey.QuantizeScale(size);
            var maxp = MaxpTable.Load(typeface);

            var renderClass = mode switch
            {
                GlyphMaskMode.Subpixel => TrueTypeRenderClass.Subpixel,
                GlyphMaskMode.Aliased => TrueTypeRenderClass.Aliased,
                _ => TrueTypeRenderClass.Grayscale,
            };

            var coords = typeface.ActiveVariationCoordinates;
            var activeCoords = coords.IsEmpty ? null : coords.ToArray();

            var state = TrueTypeSizeState.Create(
                typeface.ProgramTables,
                typeface.Metrics.DesignEmHeight,
                scaleQ * 8,
                maxp.MaxStorage,
                maxp.MaxFunctionDefs,
                maxp.MaxInstructionDefs,
                maxp.MaxStackElements,
                maxp.MaxTwilightPoints,
                renderClass,
                isVariation: activeCoords is not null);

            if (!state.IsValid)
            {
                reason = FormattableString.Invariant($"programs faulted at this size ({state.Error})");
                return null;
            }

            if (state.GlyphHintingDisabled)
            {
                reason = "the font disables its instructions at this size (INSTCTRL) - renders unfitted";
                return null;
            }

            var hinter = new TrueTypeGlyphHinter(
                state,
                typeface.GlyfTable!,
                typeface.GvarTable,
                activeCoords,
                (int glyphIndex, out int lsb, out int advance) =>
                {
                    typeface.TryGetGlyphMetrics((ushort)glyphIndex, out var metrics);
                    lsb = metrics.XBearing;
                    advance = metrics.AdvanceWidth;
                    return true;
                },
                verticalAdvance: typeface.Metrics.DesignEmHeight);

            var interpreter = state.Interpreter!;
            var steps = new List<Step>();
            TrueTypeZone? tracedZone = null;
            var zoneSwitched = false;
            var totalOps = 0;

            interpreter.Trace = line =>
            {
                totalOps++;

                if (!line.StartsWith("Glyph:", StringComparison.Ordinal))
                {
                    return;
                }

                var zone = interpreter.GlyphZone;

                if (zone is null)
                {
                    return;
                }

                if (tracedZone is null)
                {
                    tracedZone = zone;
                }
                else if (!ReferenceEquals(tracedZone, zone))
                {
                    zoneSwitched = true;
                    return;
                }

                var curX = new int[zone.PointCount];
                var curY = new int[zone.PointCount];
                var tags = new byte[zone.PointCount];

                Array.Copy(zone.CurX, curX, zone.PointCount);
                Array.Copy(zone.CurY, curY, zone.PointCount);
                Array.Copy(zone.Tags, tags, zone.PointCount);
                steps.Add(new Step(line, curX, curY, tags));
            };

            var compat = stemSnap || mode == GlyphMaskMode.Aliased ? 0 : 4;
            var hinted = hinter.TryHint(glyph, compat);

            interpreter.Trace = null;

            if (!hinted)
            {
                reason = FormattableString.Invariant(
                    $"glyph program vetoed ({interpreter.Error}) - falls back to the auto-hinter");
                return null;
            }

            // A composite's snapshots mix component zones; the final assembly is still shown,
            // just without the scrubber.
            var canScrub = !zoneSwitched && steps.Count > 0 &&
                           steps[^1].CurX.Length == hinter.Zone!.PointCount;

            return new TrueTypeHintingProbe(hinter.Zone!, canScrub ? steps : new List<Step>(),
                canScrub, totalOps, compat == 0);
        }
    }
}
