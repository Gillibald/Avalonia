using System;
using System.Buffers.Binary;
using Avalonia.Media.Fonts.Tables.Glyf;
using Avalonia.Media.Fonts.Tables.Variation;

namespace Avalonia.Media.Fonts.Rasterization.TrueType
{
    /// <summary>Per-glyph horizontal metrics in font units, for phantom-point synthesis.</summary>
    internal delegate bool TrueTypeGlyphMetricsProvider(int glyphIndex, out int leftSideBearing, out int advanceWidth);

    /// <summary>
    /// Hints one glyph through the size state: simple glyphs load and run their program;
    /// composites recurse per component (each component fully hinted first, per the
    /// reference), transform and offset the assembled current points, then run the
    /// composite's own program over the assembly with the touch flags cleared. Any fault,
    /// cycle, depth excess or budget excess vetoes the whole glyph - the caller falls back
    /// to the auto-hinter, and a partially executed program never renders. That last rule is
    /// deliberately stricter than the reference, which keeps a faulted glyph's partial
    /// result; this pipeline's charter is that ambiguity degrades, never misfits.
    ///
    /// Assembly semantics mirror the reference exactly: component transforms and offsets
    /// apply to the current points only, so the assembled originals stay component-local.
    /// Contours never span components, which keeps per-contour interpolation coherent, and
    /// composite programs measure current positions for everything cross-component.
    /// </summary>
    internal sealed class TrueTypeGlyphHinter
    {
        private const int MaxDepth = 8;

        private readonly TrueTypeSizeState _state;
        private readonly GlyfTable _glyfTable;
        private readonly GvarTable? _gvarTable;
        private readonly float[]? _activeCoords;
        private readonly TrueTypeGlyphMetricsProvider _metrics;
        private readonly int _verticalAdvance;
        private readonly TrueTypeGlyphLoader _loader = new();
        private readonly int[] _chain = new int[MaxDepth];
        private TrueTypeZone?[] _assemblies = new TrueTypeZone?[MaxDepth];

        private int _backwardCompatibility;
        private long _instructionsUsed;

        public TrueTypeGlyphHinter(
            TrueTypeSizeState state,
            GlyfTable glyfTable,
            GvarTable? gvarTable,
            float[]? activeCoords,
            TrueTypeGlyphMetricsProvider metrics,
            int verticalAdvance)
        {
            _state = state;
            _glyfTable = glyfTable;
            _gvarTable = gvarTable;
            _activeCoords = activeCoords;
            _metrics = metrics;
            _verticalAdvance = verticalAdvance;
        }

        /// <summary>The hinted zone of the last successful <see cref="TryHint"/>.</summary>
        public TrueTypeZone? Zone { get; private set; }

        /// <summary>The size context this hinter runs against.</summary>
        public TrueTypeSizeState State => _state;

        public bool TryHint(int glyphIndex, int backwardCompatibility)
        {
            Zone = null;

            if (!_state.IsValid)
            {
                return false;
            }

            _backwardCompatibility = backwardCompatibility;
            _instructionsUsed = 0;

            if (!HintRecursive(glyphIndex, 0, out var zone))
            {
                return false;
            }

            Zone = zone;
            return true;
        }

        private bool HintRecursive(int glyphIndex, int depth, out TrueTypeZone zone)
        {
            zone = null!;

            if (depth >= MaxDepth)
            {
                return false;
            }

            for (var i = 0; i < depth; i++)
            {
                if (_chain[i] == glyphIndex)
                {
                    // A component cycle can only be malicious or corrupt.
                    return false;
                }
            }

            _chain[depth] = glyphIndex;

            if (!_glyfTable.TryGetGlyphData(glyphIndex, out var glyphData))
            {
                return false;
            }

            if (glyphData.Length >= 10 && BinaryPrimitives.ReadInt16BigEndian(glyphData.Span) < 0)
            {
                return HintComposite(glyphIndex, glyphData, depth, out zone);
            }

            return HintSimple(glyphIndex, depth, out zone);
        }

        private bool HintSimple(int glyphIndex, int depth, out TrueTypeZone zone)
        {
            zone = null!;

            if (!_metrics(glyphIndex, out var leftSideBearing, out var advanceWidth))
            {
                return false;
            }

            // Deeper levels reuse the shared loader: the child's points are appended into
            // the parent's assembly before the next sibling loads.
            if (!_loader.TryLoadSimple(
                    _glyfTable, glyphIndex, _gvarTable, _activeCoords,
                    leftSideBearing, advanceWidth, _verticalAdvance, _state.Scale))
            {
                return false;
            }

            zone = _loader.Zone;
            return RunProgram(zone, _loader.Instructions, isComposite: false);
        }

        private bool HintComposite(int glyphIndex, ReadOnlyMemory<byte> glyphData, int depth, out TrueTypeZone zone)
        {
            zone = null!;

            var span = glyphData.Span;
            var xMin = BinaryPrimitives.ReadInt16BigEndian(span.Slice(2, 2));
            var yMax = BinaryPrimitives.ReadInt16BigEndian(span.Slice(8, 2));

            if (!_metrics(glyphIndex, out var leftSideBearing, out var advanceWidth))
            {
                return false;
            }

            var assembly = _assemblies[depth] ??= new TrueTypeZone(64, 8);

            assembly.PointCount = 0;
            assembly.ContourCount = 0;
            assembly.FirstPoint = 0;

            // The composite's own phantom points; a USE_MY_METRICS component replaces them
            // with its already-hinted ones.
            Span<int> ppX = stackalloc int[4];
            Span<int> ppY = stackalloc int[4];

            ComputeOwnPhantoms(ppX, ppY, xMin, yMax, leftSideBearing, advanceWidth);

            int instructionsOffset;
            int instructionsLength;

            try
            {
                var composite = CompositeGlyph.Create(span.Slice(10));

                try
                {
                    instructionsOffset = composite.InstructionsOffset;
                    instructionsLength = composite.Instructions.Length;

                    foreach (var component in composite.Components)
                    {
                        if (!AppendComponent(assembly, component, depth, ppX, ppY))
                        {
                            return false;
                        }
                    }
                }
                finally
                {
                    composite.Dispose();
                }
            }
            catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                return false;
            }

            var outlinePoints = assembly.PointCount;

            if (outlinePoints + 4 > 0xFFFF)
            {
                return false;
            }

            // Append the phantom points; they round only when a composite program actually
            // runs, matching the reference flow.
            assembly.EnsureCapacity(outlinePoints + 4, Math.Max(assembly.ContourCount, 1));

            for (var i = 0; i < 4; i++)
            {
                assembly.CurX[outlinePoints + i] = ppX[i];
                assembly.CurY[outlinePoints + i] = ppY[i];
                assembly.OrgX[outlinePoints + i] = ppX[i];
                assembly.OrgY[outlinePoints + i] = ppY[i];
                assembly.OrusX[outlinePoints + i] = 0;
                assembly.OrusY[outlinePoints + i] = 0;
                assembly.Tags[outlinePoints + i] = 0;
            }

            assembly.PointCount = outlinePoints + 4;

            if (instructionsLength > 0 && outlinePoints > 0)
            {
                assembly.CurX[outlinePoints + 0] = F26Dot6.Round(assembly.CurX[outlinePoints + 0]);
                assembly.CurX[outlinePoints + 1] = F26Dot6.Round(assembly.CurX[outlinePoints + 1]);
                assembly.CurY[outlinePoints + 2] = F26Dot6.Round(assembly.CurY[outlinePoints + 2]);
                assembly.CurY[outlinePoints + 3] = F26Dot6.Round(assembly.CurY[outlinePoints + 3]);

                // Component programs touched their points; the composite program starts on
                // an untouched outline, per the reference.
                for (var i = 0; i < outlinePoints; i++)
                {
                    assembly.Tags[i] &= unchecked((byte)~TrueTypeZone.TouchBoth);
                }

                var instructions = glyphData.Slice(10 + instructionsOffset + 2, instructionsLength);

                if (!RunProgram(assembly, instructions, isComposite: true))
                {
                    return false;
                }
            }

            zone = assembly;
            return true;
        }

        private bool AppendComponent(TrueTypeZone assembly, in GlyphComponent component, int depth, Span<int> ppX, Span<int> ppY)
        {
            var numBasePoints = assembly.PointCount;
            var numBaseContours = assembly.ContourCount;

            if (!HintRecursive(component.GlyphIndex, depth + 1, out var child))
            {
                return false;
            }

            // The component's hinted phantoms become the composite's under USE_MY_METRICS.
            if ((component.Flags & CompositeFlags.UseMyMetrics) != 0)
            {
                var childOutline = child.PointCount - 4;

                for (var i = 0; i < 4; i++)
                {
                    ppX[i] = child.CurX[childOutline + i];
                    ppY[i] = child.CurY[childOutline + i];
                }
            }

            var added = child.PointCount - 4;

            if (added <= 0)
            {
                return true;
            }

            if (numBasePoints + added > 0xFFFF)
            {
                return false;
            }

            assembly.EnsureCapacity(numBasePoints + added + 4, numBaseContours + child.ContourCount);

            for (var i = 0; i < added; i++)
            {
                assembly.CurX[numBasePoints + i] = child.CurX[i];
                assembly.CurY[numBasePoints + i] = child.CurY[i];
                assembly.OrgX[numBasePoints + i] = child.OrgX[i];
                assembly.OrgY[numBasePoints + i] = child.OrgY[i];
                assembly.OrusX[numBasePoints + i] = child.OrusX[i];
                assembly.OrusY[numBasePoints + i] = child.OrusY[i];
                assembly.Tags[numBasePoints + i] = child.Tags[i];
            }

            for (var i = 0; i < child.ContourCount; i++)
            {
                assembly.ContourEnds[numBaseContours + i] = (ushort)(child.ContourEnds[i] + numBasePoints);
            }

            assembly.PointCount = numBasePoints + added;
            assembly.ContourCount = numBaseContours + child.ContourCount;

            // Transform, then offset, current positions only - the assembled originals stay
            // component-local per the reference.
            var hasScale = (component.Flags & (CompositeFlags.WeHaveAScale |
                                               CompositeFlags.WeHaveAnXAndYScale |
                                               CompositeFlags.WeHaveATwoByTwo)) != 0;

            if (hasScale)
            {
                TransformRange(assembly, numBasePoints, added, component);
            }

            int offsetX;
            int offsetY;

            if ((component.Flags & CompositeFlags.ArgsAreXYValues) == 0)
            {
                // Point matching: align the l-th point of the new component onto the k-th
                // point of what was assembled before it, unrounded.
                int k = (ushort)component.Arg1;
                var l = (ushort)component.Arg2 + numBasePoints;

                if (k >= numBasePoints || l >= assembly.PointCount)
                {
                    return false;
                }

                offsetX = assembly.CurX[k] - assembly.CurX[l];
                offsetY = assembly.CurY[k] - assembly.CurY[l];
            }
            else
            {
                long x = component.Arg1;
                long y = component.Arg2;

                if (x == 0 && y == 0)
                {
                    return true;
                }

                // The default engine behavior: offsets scale with the component transform
                // only when the font asks for it explicitly, using the reference's
                // hypotenuse approximation of the per-axis magnitudes.
                if (hasScale && (component.Flags & CompositeFlags.ScaledComponentOffset) != 0)
                {
                    var m11 = GetMatrix(component, out var m12, out var m21, out var m22);

                    x = F26Dot6.MulFix((int)x, Hypot(m11, m21));
                    y = F26Dot6.MulFix((int)y, Hypot(m22, m12));
                }

                offsetX = F26Dot6.MulFix((int)x, _state.Scale);
                offsetY = F26Dot6.MulFix((int)y, _state.Scale);

                if ((component.Flags & CompositeFlags.RoundXYToGrid) != 0)
                {
                    if (_backwardCompatibility == 0)
                    {
                        offsetX = F26Dot6.Round(offsetX);
                    }

                    offsetY = F26Dot6.Round(offsetY);
                }
            }

            if (offsetX != 0 || offsetY != 0)
            {
                for (var i = numBasePoints; i < assembly.PointCount; i++)
                {
                    assembly.CurX[i] = unchecked(assembly.CurX[i] + offsetX);
                    assembly.CurY[i] = unchecked(assembly.CurY[i] + offsetY);
                }
            }

            return true;
        }

        private void ComputeOwnPhantoms(Span<int> ppX, Span<int> ppY, int xMin, int yMax, int leftSideBearing, int advanceWidth)
        {
            ppX[0] = F26Dot6.MulFix(xMin - leftSideBearing, _state.Scale);
            ppY[0] = 0;
            ppX[1] = F26Dot6.MulFix(xMin - leftSideBearing + advanceWidth, _state.Scale);
            ppY[1] = 0;
            ppX[2] = 0;
            ppY[2] = F26Dot6.MulFix(yMax, _state.Scale);
            ppX[3] = 0;
            ppY[3] = F26Dot6.MulFix(yMax - _verticalAdvance, _state.Scale);
        }

        private bool RunProgram(TrueTypeZone zone, ReadOnlyMemory<byte> instructions, bool isComposite)
        {
            if (instructions.IsEmpty)
            {
                return true;
            }

            var interpreter = _state.Interpreter!;

            interpreter.SetGlyphZone(zone);
            interpreter.IsCompositeGlyph = isComposite;

            // Under compatibility mode the reference discards phantom modifications made by
            // the program; restoring the pre-run values keeps parents reading stable pps.
            var outline = zone.PointCount - 4;
            Span<int> savedX = stackalloc int[4];
            Span<int> savedY = stackalloc int[4];

            for (var i = 0; i < 4; i++)
            {
                savedX[i] = zone.CurX[outline + i];
                savedY[i] = zone.CurY[outline + i];
            }

            var ok = _state.RunGlyphProgram(instructions, _backwardCompatibility);

            interpreter.SetGlyphZone(null);

            if (!ok)
            {
                return false;
            }

            _instructionsUsed += interpreter.InstructionsExecuted;

            if (_instructionsUsed > TrueTypeInterpreter.MaxRunnableOpcodes)
            {
                return false;
            }

            if (interpreter.BackwardCompatibility != 0)
            {
                for (var i = 0; i < 4; i++)
                {
                    zone.CurX[outline + i] = savedX[i];
                    zone.CurY[outline + i] = savedY[i];
                }
            }

            return true;
        }

        private static void TransformRange(TrueTypeZone zone, int start, int count, in GlyphComponent component)
        {
            // 16.16 fixed transform per the reference: x' = a*x + c*y, y' = b*x + d*y with
            // the spec's [a b c d] = [ScaleX Scale01 Scale10 ScaleY].
            var m11 = GetMatrix(component, out var m12, out var m21, out var m22);

            for (var i = start; i < start + count; i++)
            {
                var x = zone.CurX[i];
                var y = zone.CurY[i];

                zone.CurX[i] = unchecked(F26Dot6.MulFix(x, m11) + F26Dot6.MulFix(y, m21));
                zone.CurY[i] = unchecked(F26Dot6.MulFix(x, m12) + F26Dot6.MulFix(y, m22));
            }
        }

        private static int GetMatrix(in GlyphComponent component, out int m12, out int m21, out int m22)
        {
            int m11;

            if ((component.Flags & CompositeFlags.WeHaveAScale) != 0)
            {
                m11 = m22 = ToFixed(component.Scale);
                m12 = m21 = 0;
            }
            else if ((component.Flags & CompositeFlags.WeHaveAnXAndYScale) != 0)
            {
                m11 = ToFixed(component.ScaleX);
                m22 = ToFixed(component.ScaleY);
                m12 = m21 = 0;
            }
            else
            {
                m11 = ToFixed(component.ScaleX);
                m12 = ToFixed(component.Scale01);
                m21 = ToFixed(component.Scale10);
                m22 = ToFixed(component.ScaleY);
            }

            return m11;
        }

        private static int ToFixed(float value) => (int)Math.Round(value * 65536.0);

        private static int Hypot(int a16Dot16, int b16Dot16)
        {
            // IEEE sqrt keeps this deterministic; inputs are small fixed values.
            var a = a16Dot16 / 65536.0;
            var b = b16Dot16 / 65536.0;

            return (int)Math.Round(Math.Sqrt(a * a + b * b) * 65536.0);
        }
    }
}
