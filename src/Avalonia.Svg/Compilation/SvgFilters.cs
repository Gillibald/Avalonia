using System;
using System.Collections.Generic;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
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

    /// <summary>
    /// True when the whole value is one <c>url(…)</c> reference, with no
    /// further entries — the plain SVG reference form.
    /// </summary>
    internal static bool TryParseSingleUrl(string value, out string id)
    {
        var trimmed = value.Trim();
        if (SvgClipPaths.TryParseUrlReference(trimmed, out id))
            return trimmed.IndexOf(')') == trimmed.Length - 1;

        return false;
    }

    /// <summary>Applies a <c>filter="url(#id)"</c> reference.</summary>
    public static SvgFilterScope Push(
        DrawingContext context, SvgCompileContext compileContext, string id, Rect bounds, in SvgStyle style)
    {
        var scope = default(SvgFilterScope);

        // A plain reference to a missing filter is an error: the element is
        // hidden (unlike in a function list, where it is skipped).
        if (compileContext.Document.GetElementById(id) is not { Name: "filter" })
        {
            scope.Hidden = true;
            return scope;
        }

        if (!TryResolve(compileContext, id, bounds, style, out var region, out var effect))
            return scope;

        return PushResolved(context, region, effect);
    }

    /// <summary>
    /// Applies a CSS filter function list (<c>blur(2)</c>,
    /// <c>grayscale(50%)</c>, <c>url(#f)</c>, …). The region covers the SVG
    /// default outsets, the effect's own extent and any referenced filter
    /// regions.
    /// </summary>
    public static SvgFilterScope PushFunctions(
        DrawingContext context, SvgCompileContext compileContext, string value, Rect bounds, in SvgStyle style)
    {
        var scope = default(SvgFilterScope);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return scope;

        if (!TryParseFilterFunctions(compileContext, value, bounds, style, out var effect, out var region))
            return scope;

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

        // Cap arbitrarily huge regions to what can possibly reach the canvas
        // (the viewport with a viewport-sized margin) so the layer buffer
        // stays allocatable.
        var cap = new Rect(compileContext.Viewport).Inflate(
            new Thickness(compileContext.Viewport.Width, compileContext.Viewport.Height));
        if (!cap.Contains(region))
            region = region.Intersect(cap);

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
            primitiveSource, compileContext, style, bounds, region, primitiveBoxUnits, out effect);
    }

    private static bool TryBuildEffectGraph(
        SvgElement filter, SvgCompileContext compileContext, in SvgStyle style, Rect bounds, Rect region,
        bool boxUnits, out IImmutableEffect? effect)
    {
        effect = null;
        var viewport = compileContext.Viewport;

        // primitiveUnits=objectBoundingBox makes primitive lengths and
        // subregions bounding-box fractions.
        var diagonal = Math.Sqrt((bounds.Width * bounds.Width + bounds.Height * bounds.Height) / 2);

        double ScaleX(double value) => boxUnits ? value * bounds.Width : value;
        double ScaleY(double value) => boxUnits ? value * bounds.Height : value;
        double ScaleOther(double value) => boxUnits ? value * diagonal : value;

        // Positions (unlike lengths) resolve from the bounding box origin.
        double PositionX(double value) => boxUnits ? bounds.X + value * bounds.Width : value;
        double PositionY(double value) => boxUnits ? bounds.Y + value * bounds.Height : value;

        // Named results; a null value is the unmodified source graphic. The
        // subregions feed feTile and crop each primitive's output. Primitives
        // operate in linearRGB unless color-interpolation-filters selects
        // sRGB; intermediates track their space and conversions happen at the
        // boundaries, with the final result always delivered back in sRGB.
        var results = new Dictionary<string, IImmutableEffect?>(StringComparer.Ordinal);
        var resultSubregions = new Dictionary<string, Rect?>(StringComparer.Ordinal);
        var resultLinear = new Dictionary<string, bool>(StringComparer.Ordinal);
        IImmutableEffect? last = null;
        Rect? lastSubregion = null;
        var lastLinear = false;
        var lastSet = false;
        var any = false;

        // The primitive subregion: per-component defaults come from the union
        // of the input subregions, falling back to the filter region, which
        // also hard-clips every subregion. The unclamped rect is feImage's
        // placement anchor.
        Rect? GetSubregionWithRaw(SvgElement primitive, Rect? inputUnion, out Rect? raw)
        {
            raw = null;
            var x = primitive.GetAttribute("x");
            var y = primitive.GetAttribute("y");
            var width = primitive.GetAttribute("width");
            var height = primitive.GetAttribute("height");
            if (x == null && y == null && width == null && height == null && inputUnion == null)
                return null;

            var defaults = inputUnion ?? region;

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

            var rx = Resolve(x, defaults.X, bounds.X, bounds.Width, SvgLengthAxis.Horizontal);
            var ry = Resolve(y, defaults.Y, bounds.Y, bounds.Height, SvgLengthAxis.Vertical);
            var rw = Resolve(width, defaults.Width, 0, bounds.Width, SvgLengthAxis.Horizontal);
            var rh = Resolve(height, defaults.Height, 0, bounds.Height, SvgLengthAxis.Vertical);
            var rect = new Rect(rx, ry, Math.Max(0, rw), Math.Max(0, rh));
            raw = rect;
            return rect.Intersect(region);
        }

        Rect? GetSubregion(SvgElement primitive, Rect? inputUnion) =>
            GetSubregionWithRaw(primitive, inputUnion, out _);

        // A null subregion means the whole region, which absorbs the union.
        static Rect? UnionSubregions(Rect? a, Rect? b) =>
            a == null || b == null ? null : a.Value.Union(b.Value);

        // Resolves an 'in'/'in2' reference: null = SourceGraphic. The source
        // graphic (and its alpha) is sRGB; named results carry the space of
        // the primitive that produced them.
        bool TryResolveInput(string? name, bool isFirstInput, out IImmutableEffect? input, out bool linear)
        {
            input = null;
            linear = false;
            switch (name)
            {
                case null:
                    // The first primitive defaults to SourceGraphic; later ones
                    // chain from the previous result.
                    if (isFirstInput && lastSet)
                    {
                        input = last;
                        linear = lastLinear;
                    }

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
                    {
                        resultLinear.TryGetValue(name, out linear);
                        return true;
                    }

                    // An unknown reference behaves like an unspecified one.
                    if (lastSet)
                    {
                        input = last;
                        linear = lastLinear;
                    }

                    return true;
            }
        }

        // Converts between the working spaces: an sRGB↔linear transfer table
        // on the color channels. Equal spaces pass through.
        static IImmutableEffect? ToSpace(IImmutableEffect? node, bool fromLinear, bool toLinear)
        {
            if (fromLinear == toLinear)
                return node;
            var table = SpaceTable(toLinear);
            return new ImmutableComponentTransferEffect(table, table, table, alphaTable: null, node);
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
            if (!TryResolveInput(primitive.GetAttribute("in"), isFirstInput: true, out var input, out var inputLinear))
                return Unsupported($"a '{primitive.GetAttribute("in")}' input");

            // feTile is excepted from the input-union default (its subregion
            // is the tile target) and generators reference no inputs; both
            // default to the whole region.
            Rect? rawSubregion = null;
            var subregion = primitive.Name is "feTile" or "feFlood" or "feImage" or "feTurbulence"
                ? GetSubregionWithRaw(primitive, inputUnion: null, out rawSubregion)
                : GetSubregion(primitive, GetInputSubregion(primitive.GetAttribute("in")));
            var linear = UsesLinearSpace(primitive);

            IImmutableEffect? node;
            var nodeLinear = linear;
            switch (primitive.Name)
            {
                case "feFlood":
                    // The flood color is an sRGB value whichever space the
                    // primitive nominally works in.
                    node = new ImmutableFloodEffect(
                        GetFloodColor(primitive, style), GetFloodOpacity(primitive));
                    nodeLinear = false;
                    break;

                case "feImage":
                {
                    // An unresolvable or cyclic feImage hides the element.
                    // Fragments anchor at the unclamped subregion origin.
                    var anchor = (rawSubregion ?? region).Position;
                    if (!TryCreateImage(primitive, compileContext, subregion, region, anchor, out var image))
                        return true;
                    node = image;
                    nodeLinear = false;
                    break;
                }

                case "feTurbulence":
                {
                    // Frequencies are per user unit; objectBoundingBox units
                    // divide by the box dimensions. Invalid values behave as
                    // unspecified.
                    var frequencyX = 0.0;
                    var frequencyY = 0.0;
                    if (primitive.GetAttribute("baseFrequency") is { } frequency)
                    {
                        var tokenizer = new SvgTokenizer(frequency.AsSpan());
                        if (tokenizer.TryReadNumber(out frequencyX))
                            frequencyY = tokenizer.TryReadNumber(out var second) ? second : frequencyX;
                        if (frequencyX < 0 || frequencyY < 0 || !tokenizer.IsAtEnd)
                        {
                            frequencyX = 0;
                            frequencyY = 0;
                        }
                    }

                    if (boxUnits)
                    {
                        frequencyX /= bounds.Width;
                        frequencyY /= bounds.Height;
                    }

                    var octaves = (int)GetStrictNumber(primitive, "numOctaves", 1);
                    if (frequencyX <= 0 || frequencyY <= 0 || octaves < 1)
                    {
                        // No noise is transparent black.
                        node = new ImmutableFloodEffect(Colors.Transparent, 0);
                        nodeLinear = false;
                        break;
                    }

                    // The noise generates directly in the working space.
                    node = new ImmutableTurbulenceEffect(
                        frequencyX, frequencyY, octaves,
                        GetStrictNumber(primitive, "seed", 0),
                        primitive.GetAttribute("type") == "fractalNoise",
                        primitive.GetAttribute("stitchTiles") == "stitch",
                        subregion ?? region);
                    break;
                }

                case "feTile":
                {
                    // Tiles the input's subregion across this primitive's
                    // subregion (or the filter region). Pixels only move, so
                    // the input's space passes through.
                    var source = GetInputSubregion(primitive.GetAttribute("in")) ?? region;
                    var destination = subregion ?? region;
                    node = new ImmutableTileEffect(source, destination, input);
                    nodeLinear = inputLinear;
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

                        // An invalid radius (negative, or more than two
                        // values) behaves as unspecified: a no-op.
                        if (radiusX < 0 || radiusY < 0 || !tokenizer.IsAtEnd)
                        {
                            radiusX = 0;
                            radiusY = 0;
                        }
                    }

                    radiusX = ScaleX(radiusX);
                    radiusY = ScaleY(radiusY);
                    // Per-channel min/max commutes with the monotone transfer
                    // curves, so morphology is space-invariant: no conversion.
                    if (radiusX > 0 || radiusY > 0)
                    {
                        node = Chain(input, new ImmutableMorphologyEffect(
                            radiusX, radiusY, primitive.GetAttribute("operator") == "dilate", input: null));
                    }
                    else
                    {
                        node = input;
                    }

                    nodeLinear = inputLinear;
                    break;
                }

                case "feComponentTransfer":
                {
                    var red = BuildTransferTable(primitive, "feFuncR");
                    var green = BuildTransferTable(primitive, "feFuncG");
                    var blue = BuildTransferTable(primitive, "feFuncB");
                    var alpha = BuildTransferTable(primitive, "feFuncA");
                    if (red == null && green == null && blue == null && alpha == null)
                    {
                        node = input;
                        nodeLinear = inputLinear;
                    }
                    else
                    {
                        node = Chain(ToSpace(input, inputLinear, linear),
                            new ImmutableComponentTransferEffect(red, green, blue, alpha, input: null));
                    }

                    break;
                }

                case "feConvolveMatrix":
                {
                    if (!TryCreateConvolveMatrix(primitive, out var convolve))
                        return true; // invalid values hide the element
                    node = Chain(ToSpace(input, inputLinear, linear), convolve);
                    break;
                }

                case "feDiffuseLighting":
                case "feSpecularLighting":
                {
                    // Lighting reads only the input's alpha (the height map),
                    // so the input needs no conversion; the light color is an
                    // sRGB value that linear-space lighting linearizes.
                    if (!TryCreateLighting(primitive, style, linear, PositionX, PositionY, ScaleOther, out var lighting))
                        return true; // invalid values hide the element
                    node = Chain(input, lighting);
                    break;
                }

                case "feMerge":
                {
                    var inputs = new List<IImmutableEffect?>();
                    var mergeUnion = default(Rect?);
                    foreach (var child in primitive.Children)
                    {
                        if (child.Name != "feMergeNode")
                            continue;
                        if (!TryResolveInput(child.GetAttribute("in"), isFirstInput: true, out var mergeInput, out var mergeLinear))
                            return Unsupported("a feMergeNode input");

                        var mergeSubregion = GetInputSubregion(child.GetAttribute("in"));
                        mergeUnion = inputs.Count == 0
                            ? mergeSubregion
                            : UnionSubregions(mergeUnion, mergeSubregion);
                        inputs.Add(ToSpace(mergeInput, mergeLinear, linear));
                    }

                    // An empty merge is invalid: the element is hidden.
                    if (inputs.Count == 0)
                        return true;
                    subregion = GetSubregion(primitive, mergeUnion);
                    node = new ImmutableMergeEffect(inputs);
                    break;
                }

                case "feBlend":
                case "feComposite":
                {
                    if (!TryResolveInput(primitive.GetAttribute("in2"), isFirstInput: false, out var input2, out var input2Linear))
                        return Unsupported("a feComposite/feBlend in2 input");

                    subregion = GetSubregion(primitive, UnionSubregions(
                        GetInputSubregion(primitive.GetAttribute("in")),
                        GetInputSubregion(primitive.GetAttribute("in2"))));

                    input = ToSpace(input, inputLinear, linear);
                    input2 = ToSpace(input2, input2Linear, linear);

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
                    // One or two sigmas; invalid values behave as unspecified
                    // (no blur). Arbitrarily huge deviations cap at 500
                    // (resvg parity) — the output dissipates far earlier.
                    var sigmaX = 0.0;
                    var sigmaY = 0.0;
                    if (primitive.GetAttribute("stdDeviation") is { } deviation)
                    {
                        var tokenizer = new SvgTokenizer(deviation.AsSpan());
                        if (tokenizer.TryReadNumber(out sigmaX))
                            sigmaY = tokenizer.TryReadNumber(out var second) ? second : sigmaX;
                        if (sigmaX < 0 || sigmaY < 0 || !tokenizer.IsAtEnd)
                        {
                            sigmaX = 0;
                            sigmaY = 0;
                        }
                    }

                    sigmaX = Math.Min(500, ScaleX(sigmaX));
                    sigmaY = Math.Min(500, ScaleY(sigmaY));

                    if (sigmaX <= 0 && sigmaY <= 0)
                    {
                        node = input;
                        nodeLinear = inputLinear;
                    }
                    else
                    {
                        var converted = ToSpace(input, inputLinear, linear);
                        // ReSharper disable once CompareOfFloatsByEqualityOperator
                        node = sigmaX == sigmaY
                            ? Chain(converted, new ImmutableBlurEffect(SigmaToBlurRadius(sigmaX)))
                            : Chain(converted, new ImmutableAnisotropicBlurEffect(
                                sigmaX > 0 ? SigmaToBlurRadius(sigmaX) : 0,
                                sigmaY > 0 ? SigmaToBlurRadius(sigmaY) : 0,
                                input: null));
                    }

                    break;
                }

                case "feOffset":
                    // Pixels only move; the input's space passes through.
                    // Offsets are plain numbers: units make them invalid.
                    node = Chain(input, new ImmutableOffsetEffect(
                        ScaleX(GetStrictNumber(primitive, "dx", 0)),
                        ScaleY(GetStrictNumber(primitive, "dy", 0))));
                    nodeLinear = inputLinear;
                    break;

                case "feColorMatrix":
                {
                    // An invalid type or values fall back to their defaults
                    // (an identity matrix), per the corpus references.
                    if (CreateColorMatrix(primitive) is { } colorMatrix)
                    {
                        node = Chain(ToSpace(input, inputLinear, linear), colorMatrix);
                    }
                    else
                    {
                        node = input;
                        nodeLinear = inputLinear;
                    }

                    break;
                }

                case "feDropShadow":
                {
                    var sigma = Math.Min(500, ScaleOther(GetNumber(primitive, "stdDeviation", 2)));
                    var shadowColor = GetFloodColor(primitive, style);
                    if (linear)
                        shadowColor = LinearizeColor(shadowColor);
                    node = Chain(ToSpace(input, inputLinear, linear), new ImmutableDropShadowEffect(
                        ScaleX(GetStrictNumber(primitive, "dx", 2)),
                        ScaleY(GetStrictNumber(primitive, "dy", 2)),
                        sigma > 0 ? SigmaToBlurRadius(sigma) : 0,
                        shadowColor,
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
                resultLinear[result] = nodeLinear;
            }

            last = node;
            lastSubregion = subregion;
            lastLinear = nodeLinear;
            lastSet = true;
        }

        if (!any)
            return true; // an empty filter hides the element

        // The final result converts back to sRGB; a null node is the
        // unmodified source: render through an identity layer.
        effect = ToSpace(last, lastLinear, toLinear: false) ?? new ImmutableOffsetEffect(0, 0);
        return true;
    }

    /// <summary>Sequences an input effect into a single-input stage.</summary>
    private static IImmutableEffect Chain(IImmutableEffect? input, IImmutableEffect stage) =>
        input == null ? stage : new ImmutableCompositeEffect(new IEffect[] { input, stage });

    private static byte[]? s_toLinearTable;
    private static byte[]? s_toSrgbTable;

    /// <summary>
    /// Resolves <c>color-interpolation-filters</c> for a primitive through its
    /// inheritance chain (primitive → filter → document ancestors). The
    /// initial value is linearRGB; <c>auto</c> selects sRGB.
    /// </summary>
    private static bool UsesLinearSpace(SvgElement primitive)
    {
        for (var element = primitive; element != null; element = element.Parent)
        {
            switch (element.GetStyleOrAttribute("color-interpolation-filters"))
            {
                case "sRGB":
                case "auto":
                    return false;
                case "linearRGB":
                    return true;
            }
        }

        return true;
    }

    /// <summary>The 256-entry sRGB↔linear transfer curve for one direction.</summary>
    private static byte[] SpaceTable(bool toLinear)
    {
        ref var cache = ref (toLinear ? ref s_toLinearTable : ref s_toSrgbTable);
        if (cache is { } existing)
            return existing;

        var table = new byte[256];
        for (var i = 0; i < table.Length; i++)
        {
            var c = i / 255.0;
            var converted = toLinear
                ? c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4)
                : c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
            table[i] = (byte)Math.Round(255 * Math.Clamp(converted, 0, 1));
        }

        return cache = table;
    }

    /// <summary>Converts an sRGB color's channels to linear values.</summary>
    private static Color LinearizeColor(Color color)
    {
        var table = SpaceTable(toLinear: true);
        return Color.FromArgb(color.A, table[color.R], table[color.G], table[color.B]);
    }

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

            // Trailing garbage invalidates the list: as if unspecified.
            if (!tokenizer.IsAtEnd)
                values.Clear();
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
                // Transfer-function attributes are plain numbers: units or
                // any other trailing garbage mean the default value.
                var slope = GetStrictNumber(function, "slope", 1);
                var intercept = GetStrictNumber(function, "intercept", 0);
                for (var i = 0; i < 256; i++)
                    table[i] = ToByte(slope * (i / 255.0) + intercept);
                return table;
            }
            case "gamma":
            {
                var amplitude = GetStrictNumber(function, "amplitude", 1);
                var exponent = GetStrictNumber(function, "exponent", 1);
                var offset = GetStrictNumber(function, "offset", 0);
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

    /// <summary>
    /// Builds the feImage source: a document fragment renders through its
    /// shared recording at its own position, translated by the subregion's
    /// offset; raster (or nested SVG) content scales into the subregion via
    /// preserveAspectRatio. False (unresolvable target, cycle, undecodable
    /// content) hides the element.
    /// </summary>
    private static bool TryCreateImage(
        SvgElement primitive, SvgCompileContext compileContext, Rect? subregion, Rect region,
        Point anchor, out IImmutableEffect effect)
    {
        effect = null!;

        if (primitive.Href is not { Length: > 0 } href)
            return false;

        if (href[0] == '#')
        {
            if (compileContext.Document.GetElementById(href.Substring(1)) is not { } target)
                return false;

            var recording = compileContext.GetSharedRecording(target, out _);
            if (recording == null)
                return false;

            // The fragment renders at its own document position, translated
            // by the unclamped subregion (or region) origin; the clamped
            // subregion still crops.
            if (anchor.X != 0 || anchor.Y != 0)
            {
                var inner = recording;
                recording = DrawingRecording.Create(ctx =>
                {
                    using (ctx.PushTransform(Matrix.CreateTranslation(anchor.X, anchor.Y)))
                        ctx.DrawRecording(inner);
                });
            }

            effect = new ImmutableRecordingEffect(recording, subregion ?? region);
            return true;
        }

        var content = compileContext.Document.GetImageContent(href, SvgImages.LoadContent);
        var destination = subregion ?? region;

        double intrinsicWidth;
        double intrinsicHeight;
        switch (content)
        {
            case Bitmap bitmap:
                intrinsicWidth = bitmap.PixelSize.Width;
                intrinsicHeight = bitmap.PixelSize.Height;
                break;
            case SvgDocument nested:
                var intrinsic = nested.GetIntrinsicSize();
                intrinsicWidth = intrinsic.Width;
                intrinsicHeight = intrinsic.Height;
                break;
            default:
                return false;
        }

        if (intrinsicWidth <= 0 || intrinsicHeight <= 0
            || destination.Width <= 0 || destination.Height <= 0)
        {
            return false;
        }

        var preserveAspectRatio = SvgPreserveAspectRatio.Default;
        if (primitive.GetAttribute("preserveAspectRatio") is { } par)
            SvgPreserveAspectRatio.TryParse(par.AsSpan(), out preserveAspectRatio);

        var contentMatrix = preserveAspectRatio.ComputeTransform(
            new SvgViewBox(0, 0, intrinsicWidth, intrinsicHeight), destination.Size);

        var imageRecording = DrawingRecording.Create(ctx =>
        {
            using (ctx.PushClip(destination))
            using (ctx.PushTransform(Matrix.CreateTranslation(destination.X, destination.Y)))
            {
                if (content is Bitmap image)
                {
                    var mapped = new Rect(
                        contentMatrix.M31,
                        contentMatrix.M32,
                        intrinsicWidth * contentMatrix.M11,
                        intrinsicHeight * contentMatrix.M22);
                    ctx.DrawImage(image, new Rect(image.Size), mapped);
                }
                else if (content is SvgDocument nestedDocument && SvgImages.EnterNested())
                {
                    try
                    {
                        using (ctx.PushTransform(contentMatrix))
                        {
                            SvgCompiler.CompileDocument(nestedDocument, ctx,
                                new Size(intrinsicWidth, intrinsicHeight));
                        }
                    }
                    finally
                    {
                        SvgImages.ExitNested();
                    }
                }
            }
        });

        effect = new ImmutableRecordingEffect(imageRecording, destination);
        return true;
    }

    private static bool TryCreateLighting(
        SvgElement primitive, in SvgStyle style, bool linear,
        Func<double, double> positionX, Func<double, double> positionY, Func<double, double> scaleOther,
        out ImmutableLightingEffect effect)
    {
        effect = null!;

        // The first light source child defines the light; without one the
        // primitive is invalid.
        SvgElement? light = null;
        foreach (var child in primitive.Children)
        {
            if (child.Name is "feDistantLight" or "fePointLight" or "feSpotLight")
            {
                light = child;
                break;
            }
        }

        if (light == null)
            return false;

        var lightColor = ResolveLightingColor(primitive, style);

        // Linear-space lighting computes with the linearized light color; the
        // final conversion back to sRGB restores it.
        if (linear)
            lightColor = LinearizeColor(lightColor);

        var specular = primitive.Name == "feSpecularLighting";

        // Negative constants clamp to zero (an unlit black result). The
        // primitive's specular exponent must be at least 1 — below that the
        // primitive is invalid and the element hides; larger values pass
        // through. The light's focus exponent rejects negatives.
        var constant = Math.Max(0, specular
            ? GetNumber(primitive, "specularConstant", 1)
            : GetNumber(primitive, "diffuseConstant", 1));

        var shininess = GetNumber(primitive, "specularExponent", 1);
        if (specular && shininess < 1)
            return false;

        var focus = GetNumber(light, "specularExponent", 1);
        if (focus < 0)
            focus = 1;

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
            new Point(positionX(GetNumber(light, "x", 0)), positionY(GetNumber(light, "y", 0))),
            scaleOther(GetNumber(light, "z", 0)),
            new Point(positionX(GetNumber(light, "pointsAtX", 0)), positionY(GetNumber(light, "pointsAtY", 0))),
            scaleOther(GetNumber(light, "pointsAtZ", 0)),
            focus,
            limitingConeAngle,
            GetNumber(light, "azimuth", 0),
            GetNumber(light, "elevation", 0),
            lightColor,
            GetNumber(primitive, "surfaceScale", 1),
            constant,
            shininess,
            specular,
            input: null);
        return true;
    }

    /// <summary>
    /// Resolves <c>lighting-color</c> on the primitive. <c>currentColor</c>
    /// resolves through the filter's own inheritance chain, not the
    /// referencing element's; <c>inherit</c> takes the parent's value.
    /// </summary>
    private static Color ResolveLightingColor(SvgElement primitive, in SvgStyle style) =>
        ResolveFilterColor(primitive, "lighting-color", Colors.White, style);

    private static Color ResolveFilterColor(
        SvgElement element, string property, Color initial, in SvgStyle style)
    {
        // The property is not inherited: an unset value is the initial one,
        // and an explicit "inherit" takes the parent's computed value — the
        // walk only continues through explicit inherits.
        for (var current = element; current != null; current = current.Parent)
        {
            switch (current.GetStyleOrAttribute(property))
            {
                case "inherit":
                    continue;
                case "currentColor":
                    return ResolveCurrentColor(current, style);
                case { } value:
                    return SvgColor.TryParse(value, out var parsed) ? parsed : initial;
                case null:
                    return initial;
            }
        }

        return initial;
    }

    /// <summary>
    /// Resolves the <c>color</c> property through a filter element's own
    /// inheritance chain, falling back to the referencing element's color.
    /// </summary>
    private static Color ResolveCurrentColor(SvgElement primitive, in SvgStyle style)
    {
        for (var element = primitive; element != null; element = element.Parent)
        {
            if (element.GetStyleOrAttribute("color") is { } value && value != "inherit"
                && SvgColor.TryParse(value, out var parsed))
            {
                return parsed;
            }
        }

        return style.Color;
    }

    private static Color GetFloodColor(SvgElement primitive, in SvgStyle style) =>
        ResolveFilterColor(primitive, "flood-color", Colors.Black, style);

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
    /// Parses the CSS filter function list: blur, drop-shadow, the color
    /// functions (which all reduce to color matrices) and <c>url(…)</c>
    /// references chained between them. A malformed function makes the whole
    /// list invalid (the element renders unfiltered); a url to a missing
    /// filter is skipped, matching the corpus references. False means
    /// unfiltered; true with a null <paramref name="effect"/> hides the
    /// element (a referenced filter produced no output).
    /// </summary>
    private static bool TryParseFilterFunctions(
        SvgCompileContext compileContext, string value, Rect bounds, in SvgStyle style,
        out IImmutableEffect? effect, out Rect region)
    {
        effect = null;
        region = bounds.Inflate(new Thickness(bounds.Width * 0.1, bounds.Height * 0.1));

        var stages = new List<IImmutableEffect>();
        var outsets = default(Thickness);
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
                case "url":
                {
                    var target = argument.Trim('\'', '"');
                    if (target.Length < 2 || target[0] != '#'
                        || compileContext.Document.GetElementById(target.Substring(1)) is not { Name: "filter" })
                    {
                        // A url to a missing filter is skipped within a list.
                        break;
                    }

                    if (!TryResolve(compileContext, target.Substring(1), bounds, style,
                            out var urlRegion, out var urlEffect))
                        return false;

                    // A valid filter with no output hides the element.
                    if (urlEffect == null)
                        return true;

                    // Each chained filter's output clips to its own region —
                    // a later blur softens those edges rather than the
                    // layer's.
                    stages.Add(new ImmutableCropEffect(urlRegion, urlEffect));
                    region = region.Union(urlRegion);
                    break;
                }
                case "blur":
                {
                    if (!TryParseCssLength(argument, style, out var sigma) || sigma < 0)
                        return false;
                    if (sigma > 0)
                    {
                        var radius = SigmaToBlurRadius(sigma);
                        stages.Add(new ImmutableBlurEffect(radius));
                        outsets = Accumulate(outsets, new Thickness(Math.Ceiling(radius) + 1));
                    }

                    break;
                }
                case "grayscale":
                {
                    if (!TryParseCssAmount(argument, max: 1, out var amount))
                        return false;
                    stages.Add(Saturate(1 - amount));
                    break;
                }
                case "saturate":
                {
                    if (!TryParseCssAmount(argument, max: double.PositiveInfinity, out var amount))
                        return false;
                    stages.Add(Saturate(amount));
                    break;
                }
                case "sepia":
                {
                    if (!TryParseCssAmount(argument, max: 1, out var amount))
                        return false;
                    stages.Add(Sepia(amount));
                    break;
                }
                case "hue-rotate":
                {
                    if (!TryParseCssAngle(argument, out var degrees))
                        return false;
                    stages.Add(HueRotate(degrees));
                    break;
                }
                case "invert":
                {
                    if (!TryParseCssAmount(argument, max: 1, out var amount))
                        return false;
                    stages.Add(ScaleOffsetMatrix(1 - 2 * amount, amount, alphaScale: 1, alphaOffset: 0));
                    break;
                }
                case "opacity":
                {
                    if (!TryParseCssAmount(argument, max: 1, out var amount))
                        return false;
                    stages.Add(ScaleOffsetMatrix(1, 0, alphaScale: amount, alphaOffset: 0));
                    break;
                }
                case "brightness":
                {
                    if (!TryParseCssAmount(argument, max: double.PositiveInfinity, out var amount))
                        return false;
                    stages.Add(ScaleOffsetMatrix(amount, 0, alphaScale: 1, alphaOffset: 0));
                    break;
                }
                case "contrast":
                {
                    if (!TryParseCssAmount(argument, max: double.PositiveInfinity, out var amount))
                        return false;
                    stages.Add(ScaleOffsetMatrix(amount, (1 - amount) / 2, alphaScale: 1, alphaOffset: 0));
                    break;
                }
                case "drop-shadow":
                {
                    if (!TryParseCssDropShadow(argument, style, out var dropShadow, out var shadowOutsets))
                        return false;
                    stages.Add(dropShadow);
                    outsets = Accumulate(outsets, shadowOutsets);
                    break;
                }
                default:
                    return false;
            }
        }

        if (stages.Count == 0)
            return false;

        // The region must cover whatever the effect paints outside the bounds.
        region = region.Union(bounds.Inflate(outsets));

        effect = stages.Count == 1 ? stages[0] : new ImmutableCompositeEffect(stages.ToArray());
        return true;

        static Thickness Accumulate(Thickness a, Thickness b) =>
            new(a.Left + b.Left, a.Top + b.Top, a.Right + b.Right, a.Bottom + b.Bottom);
    }

    private static bool TryParseCssDropShadow(
        string argument, in SvgStyle style, out IImmutableEffect effect, out Thickness outsets)
    {
        effect = null!;
        outsets = default;
        var color = style.Color;
        var lengths = new List<double>();

        foreach (var token in argument.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == "currentColor")
                color = style.Color;
            else if (SvgLength.TryParse(token.AsSpan(), out var length) && length.Unit != SvgLengthUnit.Percent)
                lengths.Add(length.Resolve(SvgLengthAxis.Other, default, style.FontSize));
            else if (SvgColor.TryParse(token, out var parsed))
                color = parsed;
            else
                return false;
        }

        if (lengths.Count is < 2 or > 3)
            return false;

        var sigma = lengths.Count == 3 ? lengths[2] : 0;
        if (sigma < 0)
            return false;

        var radius = sigma > 0 ? SigmaToBlurRadius(sigma) : 0;
        effect = new ImmutableDropShadowEffect(lengths[0], lengths[1], radius, color, 1);

        // The shadow paints up to blur + offset outside the source.
        var pad = radius > 0 ? Math.Ceiling(radius) + 1 : 0;
        outsets = new Thickness(
            Math.Max(0, pad - lengths[0]), Math.Max(0, pad - lengths[1]),
            Math.Max(0, pad + lengths[0]), Math.Max(0, pad + lengths[1]));
        return true;
    }

    private static bool TryParseCssAmount(string argument, double max, out double amount)
    {
        // An omitted argument is the function's default (1 everywhere).
        amount = Math.Min(1, max);
        if (argument.Length == 0)
            return true;

        double parsed;
        if (argument.EndsWith("%", StringComparison.Ordinal))
        {
            if (!double.TryParse(argument.Substring(0, argument.Length - 1),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                    out parsed))
            {
                return false;
            }

            parsed /= 100;
        }
        else if (!double.TryParse(argument, System.Globalization.NumberStyles.Float,
                     System.Globalization.CultureInfo.InvariantCulture, out parsed))
        {
            return false;
        }

        // Negative amounts are invalid; amounts over 1 clamp where the
        // function's range is [0, 1].
        if (parsed < 0)
            return false;

        amount = Math.Min(parsed, max);
        return true;
    }

    private static bool TryParseCssLength(string argument, in SvgStyle style, out double length)
    {
        length = 0;
        if (argument.Length == 0)
            return true;
        if (!SvgLength.TryParse(argument.AsSpan(), out var parsed) || parsed.Unit == SvgLengthUnit.Percent)
            return false;

        length = parsed.Resolve(SvgLengthAxis.Other, default, style.FontSize);
        return true;
    }

    private static bool TryParseCssAngle(string argument, out double degrees)
    {
        degrees = 0;
        if (argument.Length == 0)
            return true;

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
        else
        {
            // A bare number is only valid as the unitless zero.
            return double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var bare) && bare == 0;
        }

        if (!double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        degrees = value * factor;
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

    /// <summary>
    /// Builds a feColorMatrix effect. Invalid attributes fall back to their
    /// defaults — an unknown type behaves as the default "matrix", invalid
    /// values as unspecified ones — so null means an identity matrix (no-op).
    /// </summary>
    private static ImmutableColorMatrixEffect? CreateColorMatrix(SvgElement primitive)
    {
        var values = primitive.GetAttribute("values");

        switch (primitive.GetAttribute("type"))
        {
            case "saturate":
                return Saturate(values != null && TryParseNumber(values, out var s) ? s : 1);
            case "hueRotate":
                return HueRotate(values != null && TryParseNumber(values, out var degrees) ? degrees : 0);
            case "luminanceToAlpha":
                return new ImmutableColorMatrixEffect(new[]
                {
                    0d, 0, 0, 0, 0,
                    0, 0, 0, 0, 0,
                    0, 0, 0, 0, 0,
                    0.2125, 0.7154, 0.0721, 0, 0,
                });
            default:
            {
                if (values == null)
                    return null;
                var matrix = new double[ImmutableColorMatrixEffect.MatrixLength];
                var tokenizer = new SvgTokenizer(values.AsSpan());
                for (var i = 0; i < matrix.Length; i++)
                {
                    if (!tokenizer.TryReadNumber(out matrix[i]))
                        return null;
                }

                // Trailing values make the whole list invalid.
                if (tokenizer.TryReadNumber(out _))
                    return null;

                return new ImmutableColorMatrixEffect(matrix);
            }
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

    /// <summary>
    /// Like <see cref="GetNumber"/> but the whole attribute must be a single
    /// valid number — no trailing units or garbage.
    /// </summary>
    private static double GetStrictNumber(SvgElement element, string name, double fallback)
    {
        return element.GetAttribute(name) is { } value
            && double.TryParse(value.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
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
