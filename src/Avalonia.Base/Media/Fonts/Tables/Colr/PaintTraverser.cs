namespace Avalonia.Media.Fonts.Tables.Colr
{
    /// <summary>
    /// Traverses a resolved paint tree and calls the appropriate methods on the painter.
    /// </summary>
    internal static class PaintTraverser
    {
        public static void Traverse(Paint paint, IColorPainter painter, Matrix currentMatrix)
        {
            switch (paint)
            {
                case ResolvedSolid fill:
                    painter.FillSolid(fill.Color);
                    break;

                case ResolvedClipBox clip:
                    painter.PushClip(clip.Box);
                    Traverse(clip.Inner, painter, currentMatrix);
                    painter.PopClip();
                    break;

                case ResolvedLinearGradient linearGrad:
                    painter.FillLinearGradient(
                        linearGrad.P0, 
                        linearGrad.P1, 
                        linearGrad.Stops, 
                        linearGrad.Extend);
                    break;

                case ResolvedRadialGradient radialGrad:
                    painter.FillRadialGradient(
                        radialGrad.C0, 
                        radialGrad.R0, 
                        radialGrad.C1, 
                        radialGrad.R1, 
                        radialGrad.Stops, 
                        radialGrad.Extend);
                    break;

                case ResolvedConicGradient conicGrad:
                    painter.FillConicGradient(
                        conicGrad.Center, 
                        conicGrad.StartAngle, 
                        conicGrad.EndAngle, 
                        conicGrad.Stops, 
                        conicGrad.Extend);
                    break;

                case ResolvedTransform t:
                    painter.PushTransform(t.Matrix);
                    Traverse(t.Inner, painter, t.Matrix * currentMatrix);
                    painter.PopTransform();
                    break;

                case ColrLayers layers:
                    foreach (var child in layers.Layers)
                        Traverse(child, painter, currentMatrix);
                    break;

                case Glyph glyph:
                    painter.Glyph(glyph.GlyphIndex);
                    Traverse(glyph.Paint, painter, currentMatrix);
                    break;

                case Composite comp:
                    // A COLR composite is an isolated group: backdrop and source render into it,
                    // the source blends onto the backdrop with the composite mode, and the finished
                    // group composites onto whatever lies outside as a single unit.
                    painter.PushLayer(CompositeMode.SrcOver);
                    Traverse(comp.Backdrop, painter, currentMatrix);
                    painter.PushLayer(comp.Mode);
                    Traverse(comp.Source, painter, currentMatrix);
                    painter.PopLayer();
                    painter.PopLayer();
                    break;
            }
        }
    }
}
