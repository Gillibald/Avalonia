using System;
using System.Collections.Generic;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Parsing;

namespace Avalonia.Svg.Compilation;

/// <summary>
/// Compiles SVG <c>&lt;filter&gt;</c> references and CSS filter functions into
/// <c>PushLayer(new LayerOptions {{ Bounds = region, Effect = … }})</c>.
/// Primitives build an effect graph: named <c>result</c>s feed <c>in</c>/
/// <c>in2</c> inputs, <c>SourceGraphic</c> is the layer source and
/// <c>SourceAlpha</c> derives from it through a color matrix. Unsupported
/// primitives render the element unfiltered with a one-shot warning.
/// </summary>
internal static class SvgFilters
{
    private static bool s_warnedAboutUnsupported;

    private static readonly ImmutableColorMatrixEffect s_sourceAlpha = new(new[]
    {
        0d, 0, 0, 0, 0,
        0, 0, 0, 0, 0,
        0, 0, 0, 0, 0,
        0, 0, 0, 1, 0,
    });

    /// <summary>
    /// The push states for an applied filter: a hard region clip (SVG filter
    /// regions clip; <c>LayerOptions.Bounds</c> only sizes the buffer) and the
    /// effect layer. <see cref="Hidden"/> signals that the filter produces no
    /// output and the element's content must not be compiled at all.
    /// </summary>
    internal struct SvgFilterScope : IDisposable
    {
        public DrawingContext.PushedState? ClipState;
        public DrawingContext.PushedState? LayerState;
        public bool Hidden;

        public void Dispose()
        {
            LayerState?.Dispose();
            ClipState?.Dispose();
        }
    }

    /// <summary>Applies a <c>filter="url(#id)"</c> reference.</summary>
    public static SvgFilterScope Push(
        DrawingContext context, SvgCompileContext compileContext, string id, Rect bounds, in SvgStyle style)
    {
        var scope = default(SvgFilterScope);

        if (!TryResolve(compileContext, id, bounds, style, out var region, out var effect))
            return scope;

        return PushResolved(context, region, effect);
    }

    /// <summary>
    /// Applies a CSS filter function list (<c>blur(2)</c>,
    /// <c>grayscale(50%)</c>, …). The region approximates the CSS unbounded
    /// filter with the SVG default outsets.
    /// </summary>
    public static SvgFilterScope PushFunctions(
        DrawingContext context, string value, Rect bounds, in SvgStyle style)
    {
        var scope = default(SvgFilterScope);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return scope;

        if (!TryParseFilterFunctions(value, style, out var effect))
            return scope;

        var region = bounds.Inflate(new Thickness(bounds.Width * 0.1, bounds.Height * 0.1));
        return PushResolved(context, region, effect);
    }

    private static SvgFilterScope PushResolved(DrawingContext context, Rect region, IImmutableEffect? effect)
    {
        var scope = default(SvgFilterScope);
        if (effect == null)
        {
            // A valid filter with no output (empty, or an empty region) hides
            // the element, per spec — prune at compile time.
            scope.Hidden = true;
            return scope;
        }

        scope.ClipState = context.PushClip(region);
        scope.LayerState = context.PushLayer(new LayerOptions { Bounds = region, Effect = effect });
        return scope;
    }

    /// <summary>
    /// Resolves a filter reference. Returns false when the filter is missing or
    /// uses unsupported primitives/graphs (the element renders unfiltered);
    /// returns true with a null <paramref name="effect"/> when the filter is
    /// valid but produces nothing (the element is hidden).
    /// </summary>
    public static bool TryResolve(
        SvgCompileContext compileContext, string id, Rect bounds, in SvgStyle style,
        out Rect region, out IImmutableEffect? effect)
    {
        region = default;
        effect = null;

        if (compileContext.Document.GetElementById(id) is not { Name: "filter" } filter)
            return false;

        // Attributes and primitives inherit through filter href chains.
        var chain = new List<SvgElement> { filter };
        for (var depth = 0; depth < 8; depth++)
        {
            var href = chain[chain.Count - 1].Href;
            if (href is not { Length: > 1 } || href[0] != '#'
                || compileContext.Document.GetElementById(href.Substring(1)) is not { Name: "filter" } target
                || chain.Contains(target))
            {
                break;
            }

            chain.Add(target);
        }

        string? GetChained(string attribute)
        {
            foreach (var element in chain)
            {
                if (element.GetAttribute(attribute) is { } value)
                    return value;
            }

            return null;
        }

        // Filter region; filterUnits default to objectBoundingBox with the
        // spec's -10% / 120% defaults, which are percentages in either mode.
        var boxUnits = GetChained("filterUnits") != "userSpaceOnUse";
        if (boxUnits && (bounds.Width <= 0 || bounds.Height <= 0))
            return true; // hides the element

        var x = GetCoordinate(GetChained("x"), -10, boxUnits, SvgLengthAxis.Horizontal, compileContext.Viewport);
        var y = GetCoordinate(GetChained("y"), -10, boxUnits, SvgLengthAxis.Vertical, compileContext.Viewport);
        var width = GetCoordinate(GetChained("width"), 120, boxUnits, SvgLengthAxis.Horizontal, compileContext.Viewport);
        var height = GetCoordinate(GetChained("height"), 120, boxUnits, SvgLengthAxis.Vertical, compileContext.Viewport);

        region = boxUnits
            ? new Rect(
                bounds.X + x * bounds.Width,
                bounds.Y + y * bounds.Height,
                width * bounds.Width,
                height * bounds.Height)
            : new Rect(x, y, width, height);

        if (region.Width <= 0 || region.Height <= 0)
            return true; // hides the element

        // Primitives come from the first filter in the chain that has any.
        var primitiveSource = filter;
        foreach (var element in chain)
        {
            if (element.Children.Count > 0)
            {
                primitiveSource = element;
                break;
            }
        }

        var primitiveBoxUnits = GetChained("primitiveUnits") == "objectBoundingBox";
        return TryBuildEffectGraph(
            primitiveSource, style, bounds, region, compileContext.Viewport, primitiveBoxUnits, out effect);
    }

    private static bool TryBuildEffectGraph(
        SvgElement filter, in SvgStyle style, Rect bounds, Rect region, Size viewport, bool boxUnits,
        out IImmutableEffect? effect)
    {
        effect = null;

        // primitiveUnits=objectBoundingBox makes primitive lengths and
        // subregions bounding-box fractions.
        var diagonal = Math.Sqrt((bounds.Width * bounds.Width + bounds.Height * bounds.Height) / 2);

        double ScaleX(double value) => boxUnits ? value * bounds.Width : value;
        double ScaleY(double value) => boxUnits ? value * bounds.Height : value;
        double ScaleOther(double value) => boxUnits ? value * diagonal : value;

        // Named results; a null value is the unmodified source graphic. The
        // subregions feed feTile and crop each primitive's output.
        var results = new Dictionary<string, IImmutableEffect?>(StringComparer.Ordinal);
        var resultSubregions = new Dictionary<string, Rect?>(StringComparer.Ordinal);
        IImmutableEffect? last = null;
        Rect? lastSubregion = null;
        var lastSet = false;
        var any = false;

        // The primitive subregion: absent attributes default to the filter
        // region (no crop).
        Rect? GetSubregion(SvgElement primitive)
        {
            var x = primitive.GetAttribute("x");
            var y = primitive.GetAttribute("y");
            var width = primitive.GetAttribute("width");
            var height = primitive.GetAttribute("height");
            if (x == null && y == null && width == null && height == null)
                return null;

            double Resolve(string? value, double fallback, double origin, double size, SvgLengthAxis axis)
            {
                if (value == null || !SvgLength.TryParse(value.AsSpan(), out var length))
                    return fallback;
                if (boxUnits)
                {
                    var fraction = length.Unit == SvgLengthUnit.Percent ? length.Value / 100 : length.Value;
                    return origin + fraction * size;
                }

                return length.Resolve(axis, viewport);
            }

            var rx = Resolve(x, region.X, bounds.X, bounds.Width, SvgLengthAxis.Horizontal);
            var ry = Resolve(y, region.Y, bounds.Y, bounds.Height, SvgLengthAxis.Vertical);
            var rw = Resolve(width, region.Right - rx, 0, bounds.Width, SvgLengthAxis.Horizontal);
            var rh = Resolve(height, region.Bottom - ry, 0, bounds.Height, SvgLengthAxis.Vertical);
            return new Rect(rx, ry, Math.Max(0, rw), Math.Max(0, rh));
        }

        // Resolves an 'in'/'in2' reference: null = SourceGraphic.
        bool TryResolveInput(string? name, bool isFirstInput, out IImmutableEffect? input)
        {
            input = null;
            switch (name)
            {
                case null:
                    // The first primitive defaults to SourceGraphic; later ones
                    // chain from the previous result.
                    if (isFirstInput && lastSet)
                        input = last;
                    return true;
                case "SourceGraphic":
                    return true;
                case "SourceAlpha":
                    input = s_sourceAlpha;
                    return true;
                case "BackgroundImage":
                case "BackgroundAlpha":
                case "FillPaint":
                case "StrokePaint":
                    return false;
                default:
                    if (results.TryGetValue(name, out input))
                        return true;
                    // An unknown reference behaves like an unspecified one.
                    if (lastSet)
                        input = last;
                    return true;
            }
        }

        // The subregion of an input, for feTile's source tile.
        Rect? GetInputSubregion(string? name)
        {
            if (name == null)
                return lastSet ? lastSubregion : null;
            return resultSubregions.TryGetValue(name, out var subregion) ? subregion : null;
        }

        foreach (var primitive in filter.Children)
        {
            if (primitive.Name is "title" or "desc" or "metadata")
                continue;

            any = true;
            if (!TryResolveInput(primitive.GetAttribute("in"), isFirstInput: true, out var input))
                return Unsupported($"a '{primitive.GetAttribute("in")}' input");

            var subregion = GetSubregion(primitive);

            IImmutableEffect? node;
            switch (primitive.Name)
            {
                case "feFlood":
                    node = new ImmutableFloodEffect(
                        GetFloodColor(primitive, style), GetFloodOpacity(primitive));
                    break;

                case "feTile":
                {
                    // Tiles the input's subregion across this primitive's
                    // subregion (or the filter region).
                    var source = GetInputSubregion(primitive.GetAttribute("in")) ?? region;
                    var destination = subregion ?? region;
                    node = new ImmutableTileEffect(source, destination, input);
                    break;
                }

                case "feMorphology":
                {
                    var radius = primitive.GetAttribute("radius");
                    var radiusX = 0.0;
                    var radiusY = 0.0;
                    if (radius != null)
                    {
                        var tokenizer = new SvgTokenizer(radius.AsSpan());
                        if (tokenizer.TryReadNumber(out radiusX))
                            radiusY = tokenizer.TryReadNumber(out var second) ? second : radiusX;
                    }

                    // A negative radius is an error (no rendering); zero is a no-op.
                    if (radiusX < 0 || radiusY < 0)
                    {
                        node = new ImmutableFloodEffect(Colors.Transparent, 0);
                        break;
                    }

                    radiusX = ScaleX(radiusX);
                    radiusY = ScaleY(radiusY);
                    node = radiusX > 0 || radiusY > 0
                        ? Chain(input, new ImmutableMorphologyEffect(
                            radiusX, radiusY, primitive.GetAttribute("operator") == "dilate", input: null))
                        : input;
                    break;
                }

                case "feComponentTransfer":
                {
                    var red = BuildTransferTable(primitive, "feFuncR");
                    var green = BuildTransferTable(primitive, "feFuncG");
                    var blue = BuildTransferTable(primitive, "feFuncB");
                    var alpha = BuildTransferTable(primitive, "feFuncA");
                    node = red == null && green == null && blue == null && alpha == null
                        ? input
                        : Chain(input, new ImmutableComponentTransferEffect(red, green, blue, alpha, input: null));
                    break;
                }

                case "feConvolveMatrix":
                {
                    if (!TryCreateConvolveMatrix(primitive, out var convolve))
                        return Unsupported("an invalid feConvolveMatrix");
                    node = Chain(input, convolve);
                    break;
                }

                case "feDiffuseLighting":
                case "feSpecularLighting":
                {
                    if (!TryCreateLighting(primitive, style, ScaleX, ScaleY, ScaleOther, out var lighting))
                        return Unsupported("an invalid lighting primitive");
                    node = Chain(input, lighting);
                    break;
                }

                case "feMerge":
                {
                    var inputs = new List<IImmutableEffect?>();
                    foreach (var child in primitive.Children)
                    {
                        if (child.Name != "feMergeNode")
                            continue;
                        if (!TryResolveInput(child.GetAttribute("in"), isFirstInput: true, out var mergeInput))
                            return Unsupported("a feMergeNode input");
                        inputs.Add(mergeInput);
                    }

                    node = inputs.Count > 0 ? new ImmutableMergeEffect(inputs) : null;
                    break;
                }

                case "feBlend":
                case "feComposite":
                {
                    if (!TryResolveInput(primitive.GetAttribute("in2"), isFirstInput: false, out var input2))
                        return Unsupported("a feComposite/feBlend in2 input");

                    if (primitive.Name == "feComposite"
                        && primitive.GetAttribute("operator") == "arithmetic")
                    {
                        node = new ImmutableArithmeticCompositeEffect(
                            GetNumber(primitive, "k1", 0), GetNumber(primitive, "k2", 0),
                            GetNumber(primitive, "k3", 0), GetNumber(primitive, "k4", 0),
                            background: input2, foreground: input);
                        break;
                    }

                    BitmapBlendingMode? mode = primitive.Name == "feComposite"
                        ? primitive.GetAttribute("operator") switch
                        {
                            null or "over" => BitmapBlendingMode.SourceOver,
                            "in" => BitmapBlendingMode.SourceIn,
                            "out" => BitmapBlendingMode.SourceOut,
                            "atop" => BitmapBlendingMode.SourceAtop,
                            "xor" => BitmapBlendingMode.Xor,
                            _ => null,
                        }
                        : primitive.GetAttribute("mode") switch
                        {
                            null or "normal" => BitmapBlendingMode.SourceOver,
                            "multiply" => BitmapBlendingMode.Multiply,
                            "screen" => BitmapBlendingMode.Screen,
                            "overlay" => BitmapBlendingMode.Overlay,
                            "darken" => BitmapBlendingMode.Darken,
                            "lighten" => BitmapBlendingMode.Lighten,
                            "color-dodge" => BitmapBlendingMode.ColorDodge,
                            "color-burn" => BitmapBlendingMode.ColorBurn,
                            "hard-light" => BitmapBlendingMode.HardLight,
                            "soft-light" => BitmapBlendingMode.SoftLight,
                            "difference" => BitmapBlendingMode.Difference,
                            "exclusion" => BitmapBlendingMode.Exclusion,
                            "hue" => BitmapBlendingMode.Hue,
                            "saturation" => BitmapBlendingMode.Saturation,
                            "color" => BitmapBlendingMode.Color,
                            "luminosity" => BitmapBlendingMode.Luminosity,
                            _ => null,
                        };

                    if (mode is not { } blendMode)
                        return Unsupported($"a '{primitive.Name}' mode/operator");

                    node = new ImmutableBlendEffect(blendMode, background: input2, foreground: input);
                    break;
                }

                case "feGaussianBlur":
                {
                    var sigma = ScaleOther(GetNumber(primitive, "stdDeviation", 0));
                    node = sigma > 0 ? Chain(input, new ImmutableBlurEffect(SigmaToBlurRadius(sigma))) : input;
                    break;
                }

                case "feOffset":
                    node = Chain(input, new ImmutableOffsetEffect(
                        ScaleX(GetNumber(primitive, "dx", 0)),
                        ScaleY(GetNumber(primitive, "dy", 0))));
                    break;

                case "feColorMatrix":
                {
                    if (!TryCreateColorMatrix(primitive, out var colorMatrix))
                        return Unsupported("an invalid feColorMatrix");
                    node = Chain(input, colorMatrix);
                    break;
                }

                case "feDropShadow":
                {
                    var sigma = ScaleOther(GetNumber(primitive, "stdDeviation", 2));
                    node = Chain(input, new ImmutableDropShadowEffect(
                        ScaleX(GetNumber(primitive, "dx", 2)),
                        ScaleY(GetNumber(primitive, "dy", 2)),
                        sigma > 0 ? SigmaToBlurRadius(sigma) : 0,
                        GetFloodColor(primitive, style),
                        GetFloodOpacity(primitive)));
                    break;
                }

                default:
                    return Unsupported($"the '{primitive.Name}' primitive");
            }

            // The primitive subregion crops the node's output (feTile already
            // fills its destination).
            if (subregion is { } crop && primitive.Name != "feTile")
                node = new ImmutableCropEffect(crop, node);

            if (primitive.GetAttribute("result") is { } result)
            {
                results[result] = node;
                resultSubregions[result] = subregion;
            }

            last = node;
            lastSubregion = subregion;
            lastSet = true;
        }

        if (!any)
            return true; // an empty filter hides the element

        // A null final node is the unmodified source: render through an
        // identity layer.
        effect = last ?? new ImmutableOffsetEffect(0, 0);
        return true;
    }

    /// <summary>Sequences an input effect into a single-input stage.</summary>
    private static IImmutableEffect Chain(IImmutableEffect? input, IImmutableEffect stage) =>
        input == null ? stage : new ImmutableCompositeEffect(new IEffect[] { input, stage });

    /// <summary>
    /// Builds the 256-entry lookup table of one transfer function child, or
    /// null for identity (absent child or <c>type="identity"</c>).
    /// </summary>
    private static byte[]? BuildTransferTable(SvgElement transfer, string childName)
    {
        SvgElement? function = null;
        foreach (var child in transfer.Children)
        {
            if (child.Name == childName)
                function = child;
        }

        if (function == null)
            return null;

        var type = function.GetAttribute("type");
        var values = new List<double>();
        if (function.GetAttribute("tableValues") is { } tableValues)
        {
            var tokenizer = new SvgTokenizer(tableValues.AsSpan());
            while (tokenizer.TryReadNumber(out var v))
                values.Add(v);
        }

        var table = new byte[256];
        switch (type)
        {
            case "table":
            {
                if (values.Count == 0)
                    return null;
                if (values.Count == 1)
                {
                    var constant = ToByte(values[0]);
                    for (var i = 0; i < 256; i++)
                        table[i] = constant;
                    return table;
                }

                var n = values.Count - 1;
                for (var i = 0; i < 256; i++)
                {
                    var c = i / 255.0;
                    var k = Math.Min(n - 1, (int)(c * n));
                    var v = values[k] + (c * n - k) * (values[k + 1] - values[k]);
                    table[i] = ToByte(v);
                }

                return table;
            }
            case "discrete":
            {
                if (values.Count == 0)
                    return null;
                var n = values.Count;
                for (var i = 0; i < 256; i++)
                {
                    var k = Math.Min(n - 1, (int)(i / 255.0 * n));
                    table[i] = ToByte(values[k]);
                }

                return table;
            }
            case "linear":
            {
                var slope = GetNumber(function, "slope", 1);
                var intercept = GetNumber(function, "intercept", 0);
                for (var i = 0; i < 256; i++)
                    table[i] = ToByte(slope * (i / 255.0) + intercept);
                return table;
            }
            case "gamma":
            {
                var amplitude = GetNumber(function, "amplitude", 1);
                var exponent = GetNumber(function, "exponent", 1);
                var offset = GetNumber(function, "offset", 0);
                for (var i = 0; i < 256; i++)
                    table[i] = ToByte(amplitude * Math.Pow(i / 255.0, exponent) + offset);
                return table;
            }
            default:
                return null; // identity (or unknown, treated as identity)
        }

        static byte ToByte(double value) =>
            (byte)Math.Max(0, Math.Min(255, Math.Round(value * 255)));
    }

    private static bool TryCreateConvolveMatrix(SvgElement primitive, out ImmutableConvolveMatrixEffect effect)
    {
        effect = null!;

        var orderX = 3;
        var orderY = 3;
        if (primitive.GetAttribute("order") is { } order)
        {
            var tokenizer = new SvgTokenizer(order.AsSpan());
            if (!tokenizer.TryReadNumber(out var first))
                return false;
            orderX = (int)first;
            orderY = tokenizer.TryReadNumber(out var second) ? (int)second : orderX;
        }

        if (orderX <= 0 || orderY <= 0 || orderX * orderY > 1024)
            return false;

        if (primitive.GetAttribute("kernelMatrix") is not { } kernelMatrix)
            return false;

        var kernel = new List<double>();
        var kernelTokenizer = new SvgTokenizer(kernelMatrix.AsSpan());
        while (kernelTokenizer.TryReadNumber(out var value))
            kernel.Add(value);
        if (kernel.Count != orderX * orderY)
            return false;

        // The default divisor is the kernel sum, or 1 when that is zero.
        var divisor = GetNumber(primitive, "divisor", double.NaN);
        if (double.IsNaN(divisor))
        {
            divisor = 0;
            foreach (var value in kernel)
                divisor += value;
            if (divisor == 0)
                divisor = 1;
        }
        else if (divisor == 0)
        {
            return false;
        }

        var targetX = (int)GetNumber(primitive, "targetX", orderX / 2);
        var targetY = (int)GetNumber(primitive, "targetY", orderY / 2);
        if (targetX < 0 || targetX >= orderX || targetY < 0 || targetY >= orderY)
            return false;

        var edgeMode = primitive.GetAttribute("edgeMode") switch
        {
            "wrap" => ConvolveMatrixEdgeMode.Wrap,
            "none" => ConvolveMatrixEdgeMode.None,
            _ => ConvolveMatrixEdgeMode.Duplicate,
        };

        effect = new ImmutableConvolveMatrixEffect(
            orderX, orderY, kernel,
            divisor,
            GetNumber(primitive, "bias", 0),
            targetX, targetY,
            edgeMode,
            primitive.GetAttribute("preserveAlpha") == "true",
            input: null);
        return true;
    }

    private static bool TryCreateLighting(
        SvgElement primitive, in SvgStyle style,
        Func<double, double> scaleX, Func<double, double> scaleY, Func<double, double> scaleOther,
        out ImmutableLightingEffect effect)
    {
        effect = null!;

        // Exactly one light source child defines the light.
        SvgElement? light = null;
        foreach (var child in primitive.Children)
        {
            if (child.Name is "feDistantLight" or "fePointLight" or "feSpotLight")
            {
                if (light != null)
                    return false;
                light = child;
            }
        }

        if (light == null)
            return false;

        var lightColor = Colors.White;
        if (primitive.GetStyleOrAttribute("lighting-color") is { } lightingColor)
        {
            if (lightingColor == "currentColor")
                lightColor = style.Color;
            else if (SvgColor.TryParse(lightingColor, out var parsed))
                lightColor = parsed;
        }

        var specular = primitive.Name == "feSpecularLighting";
        var constant = specular
            ? GetNumber(primitive, "specularConstant", 1)
            : GetNumber(primitive, "diffuseConstant", 1);

        // Negative constants and exponents are errors.
        if (constant < 0
            || GetNumber(primitive, "specularExponent", 1) < 0
            || GetNumber(light, "specularExponent", 1) < 0)
        {
            return false;
        }

        var kind = light.Name switch
        {
            "fePointLight" => LightSourceKind.Point,
            "feSpotLight" => LightSourceKind.Spot,
            _ => LightSourceKind.Distant,
        };

        double? limitingConeAngle = null;
        if (kind == LightSourceKind.Spot && light.GetAttribute("limitingConeAngle") is { } cone
            && TryParseNumber(cone, out var coneAngle))
        {
            limitingConeAngle = coneAngle;
        }

        effect = new ImmutableLightingEffect(
            kind,
            new Point(scaleX(GetNumber(light, "x", 0)), scaleY(GetNumber(light, "y", 0))),
            scaleOther(GetNumber(light, "z", 0)),
            new Point(scaleX(GetNumber(light, "pointsAtX", 0)), scaleY(GetNumber(light, "pointsAtY", 0))),
            scaleOther(GetNumber(light, "pointsAtZ", 0)),
            GetNumber(light, "specularExponent", 1),
            limitingConeAngle,
            GetNumber(light, "azimuth", 0),
            GetNumber(light, "elevation", 0),
            lightColor,
            GetNumber(primitive, "surfaceScale", 1),
            constant,
            GetNumber(primitive, "specularExponent", 1),
            specular,
            input: null);
        return true;
    }

    private static Color GetFloodColor(SvgElement primitive, in SvgStyle style)
    {
        if (primitive.GetStyleOrAttribute("flood-color") is { } floodColor)
        {
            if (floodColor == "currentColor")
                return style.Color;
            if (SvgColor.TryParse(floodColor, out var parsed))
                return parsed;
        }

        return Colors.Black;
    }

    private static double GetFloodOpacity(SvgElement primitive)
    {
        if (primitive.GetStyleOrAttribute("flood-opacity") is { } floodOpacity
            && SvgStyle.TryParseOpacity(floodOpacity, out var parsed))
        {
            return parsed;
        }

        return 1;
    }

    /// <summary>
    /// Parses the CSS filter function list: blur, drop-shadow and the color
    /// functions, which all reduce to color matrices.
    /// </summary>
    private static bool TryParseFilterFunctions(string value, in SvgStyle style, out IImmutableEffect? effect)
    {
        effect = null;
        var stages = new List<IImmutableEffect>();
        var position = 0;

        while (position < value.Length)
        {
            while (position < value.Length && char.IsWhiteSpace(value[position]))
                position++;
            if (position >= value.Length)
                break;

            var open = value.IndexOf('(', position);
            if (open < 0)
                return false;
            var close = value.IndexOf(')', open + 1);
            if (close < 0)
                return false;

            var name = value.Substring(position, open - position).Trim();
            var argument = value.Substring(open + 1, close - open - 1).Trim();
            position = close + 1;

            switch (name)
            {
                case "blur":
                {
                    var sigma = ParseCssLength(argument, 0);
                    if (sigma > 0)
                        stages.Add(new ImmutableBlurEffect(SigmaToBlurRadius(sigma)));
                    break;
                }
                case "grayscale":
                    stages.Add(Saturate(1 - ParseCssAmount(argument, 1)));
                    break;
                case "saturate":
                    stages.Add(Saturate(ParseCssAmount(argument, 1)));
                    break;
                case "sepia":
                    stages.Add(Sepia(ParseCssAmount(argument, 1)));
                    break;
                case "hue-rotate":
                    stages.Add(HueRotate(ParseCssAngle(argument)));
                    break;
                case "invert":
                {
                    var amount = ParseCssAmount(argument, 1);
                    stages.Add(ScaleOffsetMatrix(1 - 2 * amount, amount, alphaScale: 1, alphaOffset: 0));
                    break;
                }
                case "opacity":
                    stages.Add(ScaleOffsetMatrix(1, 0, alphaScale: ParseCssAmount(argument, 1), alphaOffset: 0));
                    break;
                case "brightness":
                    stages.Add(ScaleOffsetMatrix(ParseCssAmount(argument, 1), 0, alphaScale: 1, alphaOffset: 0));
                    break;
                case "contrast":
                {
                    var amount = ParseCssAmount(argument, 1);
                    stages.Add(ScaleOffsetMatrix(amount, (1 - amount) / 2, alphaScale: 1, alphaOffset: 0));
                    break;
                }
                case "drop-shadow":
                {
                    if (!TryParseCssDropShadow(argument, style, out var dropShadow))
                        return false;
                    stages.Add(dropShadow);
                    break;
                }
                default:
                    return false;
            }
        }

        if (stages.Count == 0)
            return false;

        effect = stages.Count == 1 ? stages[0] : new ImmutableCompositeEffect(stages.ToArray());
        return true;
    }

    private static bool TryParseCssDropShadow(string argument, in SvgStyle style, out IImmutableEffect effect)
    {
        effect = null!;
        var color = style.Color;
        var lengths = new List<double>();

        foreach (var token in argument.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (SvgLength.TryParse(token.AsSpan(), out var length) && length.Unit != SvgLengthUnit.Percent)
                lengths.Add(length.Resolve(SvgLengthAxis.Other, default));
            else if (SvgColor.TryParse(token, out var parsed))
                color = parsed;
            else
                return false;
        }

        if (lengths.Count is < 2 or > 3)
            return false;

        var sigma = lengths.Count == 3 ? lengths[2] : 0;
        effect = new ImmutableDropShadowEffect(
            lengths[0], lengths[1], sigma > 0 ? SigmaToBlurRadius(sigma) : 0, color, 1);
        return true;
    }

    private static ImmutableColorMatrixEffect Saturate(double s) => new(new[]
    {
        0.213 + 0.787 * s, 0.715 - 0.715 * s, 0.072 - 0.072 * s, 0, 0,
        0.213 - 0.213 * s, 0.715 + 0.285 * s, 0.072 - 0.072 * s, 0, 0,
        0.213 - 0.213 * s, 0.715 - 0.715 * s, 0.072 + 0.928 * s, 0, 0,
        0, 0, 0, 1, 0,
    });

    private static ImmutableColorMatrixEffect Sepia(double p) => new(new[]
    {
        1 - 0.607 * p, 0.769 * p, 0.189 * p, 0, 0,
        0.349 * p, 1 - 0.314 * p, 0.168 * p, 0, 0,
        0.272 * p, 0.534 * p, 1 - 0.869 * p, 0, 0,
        0, 0, 0, 1, 0,
    });

    private static ImmutableColorMatrixEffect HueRotate(double degrees)
    {
        var cos = Math.Cos(Matrix.ToRadians(degrees));
        var sin = Math.Sin(Matrix.ToRadians(degrees));
        return new ImmutableColorMatrixEffect(new[]
        {
            0.213 + cos * 0.787 - sin * 0.213, 0.715 - cos * 0.715 - sin * 0.715, 0.072 - cos * 0.072 + sin * 0.928, 0, 0,
            0.213 - cos * 0.213 + sin * 0.143, 0.715 + cos * 0.285 + sin * 0.140, 0.072 - cos * 0.072 - sin * 0.283, 0, 0,
            0.213 - cos * 0.213 - sin * 0.787, 0.715 - cos * 0.715 + sin * 0.715, 0.072 + cos * 0.928 + sin * 0.072, 0, 0,
            0, 0, 0, 1, 0,
        });
    }

    private static ImmutableColorMatrixEffect ScaleOffsetMatrix(
        double scale, double offset, double alphaScale, double alphaOffset) => new(new[]
    {
        scale, 0, 0, 0, offset,
        0, scale, 0, 0, offset,
        0, 0, scale, 0, offset,
        0, 0, 0, alphaScale, alphaOffset,
    });

    private static double ParseCssAmount(string argument, double fallback)
    {
        if (argument.Length == 0)
            return fallback;
        if (argument.EndsWith("%", StringComparison.Ordinal)
            && double.TryParse(argument.Substring(0, argument.Length - 1),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                out var percent))
        {
            return percent / 100;
        }

        return double.TryParse(argument, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number
            : fallback;
    }

    private static double ParseCssLength(string argument, double fallback)
    {
        if (argument.Length == 0)
            return fallback;
        return SvgLength.TryParse(argument.AsSpan(), out var length) && length.Unit != SvgLengthUnit.Percent
            ? length.Resolve(SvgLengthAxis.Other, default)
            : fallback;
    }

    private static double ParseCssAngle(string argument)
    {
        if (argument.Length == 0)
            return 0;
        var trimmed = argument;
        var factor = 1.0;
        if (trimmed.EndsWith("deg", StringComparison.Ordinal))
            trimmed = trimmed.Substring(0, trimmed.Length - 3);
        else if (trimmed.EndsWith("grad", StringComparison.Ordinal))
        {
            trimmed = trimmed.Substring(0, trimmed.Length - 4);
            factor = 0.9;
        }
        else if (trimmed.EndsWith("rad", StringComparison.Ordinal))
        {
            trimmed = trimmed.Substring(0, trimmed.Length - 3);
            factor = 180 / Math.PI;
        }
        else if (trimmed.EndsWith("turn", StringComparison.Ordinal))
        {
            trimmed = trimmed.Substring(0, trimmed.Length - 4);
            factor = 360;
        }

        return double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value * factor
            : 0;
    }

    private static bool TryCreateColorMatrix(SvgElement primitive, out ImmutableColorMatrixEffect effect)
    {
        effect = null!;
        var type = primitive.GetAttribute("type") ?? "matrix";
        var values = primitive.GetAttribute("values");

        switch (type)
        {
            case "matrix":
            {
                if (values == null)
                    return false;
                var matrix = new double[ImmutableColorMatrixEffect.MatrixLength];
                var tokenizer = new SvgTokenizer(values.AsSpan());
                for (var i = 0; i < matrix.Length; i++)
                {
                    if (!tokenizer.TryReadNumber(out matrix[i]))
                        return false;
                }

                effect = new ImmutableColorMatrixEffect(matrix);
                return true;
            }
            case "saturate":
            {
                var s = 1.0;
                if (values != null && !TryParseNumber(values, out s))
                    return false;
                effect = Saturate(s);
                return true;
            }
            case "hueRotate":
            {
                var degrees = 0.0;
                if (values != null && !TryParseNumber(values, out degrees))
                    return false;
                effect = HueRotate(degrees);
                return true;
            }
            case "luminanceToAlpha":
            {
                effect = new ImmutableColorMatrixEffect(new[]
                {
                    0d, 0, 0, 0, 0,
                    0, 0, 0, 0, 0,
                    0, 0, 0, 0, 0,
                    0.2125, 0.7154, 0.0721, 0, 0,
                });
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>
    /// SVG <c>stdDeviation</c> is the Gaussian sigma; Avalonia blur radii lower
    /// to Skia via <c>sigma = 0.288675 · radius + 0.5</c>, so invert that.
    /// </summary>
    private static double SigmaToBlurRadius(double sigma) =>
        Math.Max(0, (sigma - 0.5) / 0.288675);

    private static bool Unsupported(string what)
    {
        if (!s_warnedAboutUnsupported)
        {
            s_warnedAboutUnsupported = true;
            Logger.TryGet(LogEventLevel.Warning, "SVG")?.Log(
                typeof(SvgFilters),
                "An SVG filter uses " + what + ", which is not supported; the element renders unfiltered.");
        }

        return false;
    }

    private static double GetNumber(SvgElement element, string name, double fallback)
    {
        var value = element.GetAttribute(name);
        return value != null && TryParseNumber(value, out var parsed) ? parsed : fallback;
    }

    private static bool TryParseNumber(string value, out double result)
    {
        // The first number of a possibly two-value list (e.g. stdDeviation
        // "2 3"); Avalonia effects are symmetric, so the first value wins.
        var tokenizer = new SvgTokenizer(value.AsSpan());
        return tokenizer.TryReadNumber(out result);
    }

    private static double GetCoordinate(
        string? value, double percentFallback,
        bool boxUnits, SvgLengthAxis axis, Size viewport)
    {
        if (value == null || !SvgLength.TryParse(value.AsSpan(), out var length))
        {
            // The spec defaults are percentages, sensitive to the units mode.
            length = new SvgLength(percentFallback, SvgLengthUnit.Percent);
        }

        if (boxUnits)
            return length.Unit == SvgLengthUnit.Percent ? length.Value / 100.0 : length.Value;

        return length.Resolve(axis, viewport);
    }
}
