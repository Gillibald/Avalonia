// Copyright (c) The Avalonia Project. All rights reserved.
// Licensed under the MIT license. See licence.md file in the project root for full license information.

using System;

using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
using SharpDX.Mathematics.Interop;

namespace Avalonia.Direct2D1.Media
{
    internal class AvaloniaTextRenderer : TextRenderer
    {
        private readonly DrawingContextImpl _context;

        private readonly SharpDX.Direct2D1.RenderTarget _renderTarget;

        private readonly Brush _foreground;

        public AvaloniaTextRenderer(
            DrawingContextImpl context,
            SharpDX.Direct2D1.RenderTarget target,
            Brush foreground)
        {
            _context = context;
            _renderTarget = target;
            _foreground = foreground;
        }

        public IDisposable Shadow { get; set; }

        public Result DrawGlyphRun(
            object clientDrawingContext,
            float baselineOriginX,
            float baselineOriginY,
            MeasuringMode measuringMode,
            GlyphRun glyphRun,
            GlyphRunDescription glyphRunDescription,
            ComObject clientDrawingEffect)
        {
            using (var brush = CreateEffectBrush(clientDrawingEffect))
            {
                _renderTarget.DrawGlyphRun(
                    new RawVector2 { X = baselineOriginX, Y = baselineOriginY },
                    glyphRun,
                    brush ?? _foreground,
                    measuringMode);
            }

            return Result.Ok;
        }

        public Result DrawInlineObject(object clientDrawingContext, float originX, float originY, InlineObject inlineObject, bool isSideways, bool isRightToLeft, ComObject clientDrawingEffect)
        {
            throw new NotImplementedException();
        }

        public Result DrawStrikethrough(object clientDrawingContext, float baselineOriginX, float baselineOriginY, ref Strikethrough strikethrough, ComObject clientDrawingEffect)
        {
            return DrawTextDecoration(
                baselineOriginX,
                baselineOriginY,
                strikethrough.Offset,
                strikethrough.Width,
                strikethrough.Thickness,
                clientDrawingEffect);
        }

        public Result DrawUnderline(object clientDrawingContext, float baselineOriginX, float baselineOriginY, ref Underline underline, ComObject clientDrawingEffect)
        {
            return DrawTextDecoration(
                baselineOriginX,
                baselineOriginY,
                underline.Offset,
                underline.Width,
                underline.Thickness,
                clientDrawingEffect);
        }

        public RawMatrix3x2 GetCurrentTransform(object clientDrawingContext)
        {
            return _renderTarget.Transform;
        }

        public float GetPixelsPerDip(object clientDrawingContext)
        {
            return _renderTarget.DotsPerInch.Width / 96;
        }

        public bool IsPixelSnappingDisabled(object clientDrawingContext)
        {
            return false;
        }

        public void Dispose()
        {
            Shadow?.Dispose();
        }

        private Brush CreateEffectBrush(ComObject clientDrawingEffect)
        {
            if (clientDrawingEffect is BrushWrapper brushWrapper)
            {
                return _context.CreateBrush(brushWrapper.Brush, new Size()).PlatformBrush;
            }

            return null;
        }

        private Result DrawTextDecoration(float baselineOriginX, float baselineOriginY, float offset, float width, float thickness, ComObject clientDrawingEffect)
        {
            try
            {
                var rect = new RawRectangleF(0, offset, width, offset + thickness);

                var factory = AvaloniaLocator.Current.GetService<SharpDX.Direct2D1.Factory>();

                var transform = new Matrix(
                    1.0f,
                    0.0f,
                    0.0f,
                    1.0f,
                    baselineOriginX,
                    baselineOriginY);

                using (var rectangleGeometry = new RectangleGeometry(factory, rect))
                using (var transformedGeometry = new TransformedGeometry(factory, rectangleGeometry, transform.ToDirect2D()))
                using (var brush = CreateEffectBrush(clientDrawingEffect))
                {
                    _renderTarget.DrawGeometry(transformedGeometry, brush);

                    _renderTarget.FillGeometry(transformedGeometry, brush);
                }
            }
            catch
            {
                return Result.Fail;
            }

            return Result.Ok;
        }
    }
}
