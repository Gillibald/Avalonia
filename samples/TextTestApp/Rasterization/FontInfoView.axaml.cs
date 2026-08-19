using System;
using System.Collections.Generic;
using System.Text;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Fonts.Tables;
using Avalonia.Media.Fonts.Tables.Name;

namespace TextTestApp
{
    /// <summary>
    /// The font's passport, read from its own tables: name-table identity and legal
    /// strings, resolved line metrics with their provenance, cmap coverage, the SFNT
    /// table inventory with sizes, GSUB/GPOS feature tags, the variation summary
    /// (linking into the Variable fonts view) and the color-glyph capabilities.
    /// </summary>
    public partial class FontInfoView : UserControl
    {
        /// <summary>Well-known SFNT tags probed for the inventory - IFontMemory offers
        /// lookup by tag, not enumeration, so this lists what a text stack cares about.</summary>
        private static readonly string[] s_knownTables =
        {
            "head", "hhea", "maxp", "hmtx", "cmap", "name", "OS/2", "post",
            "glyf", "loca", "cvt ", "fpgm", "prep", "gasp", "CFF ", "CFF2",
            "GSUB", "GPOS", "GDEF", "BASE", "kern", "MATH", "meta",
            "fvar", "avar", "gvar", "cvar", "HVAR", "VVAR", "MVAR", "STAT",
            "COLR", "CPAL", "CBDT", "CBLC", "EBDT", "EBLC", "sbix", "SVG ",
            "vhea", "vmtx", "hdmx", "LTSH", "VDMX", "DSIG",
        };

        private const ushort EnglishUs = 1033;

        private TextBlock _titleText = null!;
        private TextBlock _identityText = null!;
        private TextBlock _legalText = null!;
        private TextBlock _metricsText = null!;
        private TextBlock _coverageText = null!;
        private TextBlock _tablesText = null!;
        private TextBlock _featuresText = null!;
        private TextBlock _variationText = null!;
        private Button _exploreAxesButton = null!;
        private TextBlock _colorText = null!;

        private GlyphTypeface? _typeface;

        /// <summary>Raised by "Explore the axes"; the host navigates to Variable fonts.</summary>
        public event Action? VariableFontsRequested;

        public FontInfoView()
        {
            AvaloniaXamlLoader.Load(this);

            _titleText = this.FindControl<TextBlock>("TitleText")!;
            _identityText = this.FindControl<TextBlock>("IdentityText")!;
            _legalText = this.FindControl<TextBlock>("LegalText")!;
            _metricsText = this.FindControl<TextBlock>("MetricsText")!;
            _coverageText = this.FindControl<TextBlock>("CoverageText")!;
            _tablesText = this.FindControl<TextBlock>("TablesText")!;
            _featuresText = this.FindControl<TextBlock>("FeaturesText")!;
            _variationText = this.FindControl<TextBlock>("VariationText")!;
            _exploreAxesButton = this.FindControl<Button>("ExploreAxesButton")!;
            _colorText = this.FindControl<TextBlock>("ColorText")!;

            _exploreAxesButton.Click += (_, _) => VariableFontsRequested?.Invoke();
            this.FindControl<Button>("CopyButton")!.Click += (_, _) => ClipboardHelper.Copy(this,
                string.Join(Environment.NewLine + Environment.NewLine,
                    _titleText.Text, _identityText.Text, _legalText.Text, _metricsText.Text,
                    _coverageText.Text, _tablesText.Text, _featuresText.Text, _variationText.Text,
                    _colorText.Text));
        }

        public void SetContext(GlyphTypeface? typeface)
        {
            if (ReferenceEquals(_typeface, typeface))
            {
                return;
            }

            _typeface = typeface;
            Rebuild();
        }

        private void Rebuild()
        {
            if (_typeface is not { } typeface)
            {
                _titleText.Text = "no typeface";
                return;
            }

            _titleText.Text = typeface.FamilyName;

            var name = NameTable.Load(typeface);

            _identityText.Text = JoinLines(
                Line("full name", name?.GetNameById(EnglishUs, KnownNameIds.FullFontName)),
                Line("typographic family", typeface.TypographicFamilyName),
                Line("subfamily", name?.GetNameById(EnglishUs, KnownNameIds.FontSubfamilyName)),
                Line("version", name?.GetNameById(EnglishUs, KnownNameIds.Version)),
                Line("PostScript name", name?.GetNameById(EnglishUs, KnownNameIds.PostscriptName)),
                Line("manufacturer", name?.GetNameById(EnglishUs, KnownNameIds.Manufacturer)),
                Line("designer", name?.GetNameById(EnglishUs, KnownNameIds.Designer)),
                Line("vendor URL", name?.GetNameById(EnglishUs, KnownNameIds.VendorUrl)),
                Line("style", $"{typeface.Weight}, {typeface.Style}, {typeface.Stretch}"));

            _legalText.Text = JoinLines(
                Line("copyright", name?.GetNameById(EnglishUs, KnownNameIds.CopyrightNotice)),
                Line("trademark", name?.GetNameById(EnglishUs, KnownNameIds.Trademark)),
                Line("license", Truncate(name?.GetNameById(EnglishUs, KnownNameIds.LicenseDescription), 300)),
                Line("license URL", name?.GetNameById(EnglishUs, KnownNameIds.LicenseInfoUrl)));

            var metrics = typeface.Metrics;

            _metricsText.Text = JoinLines(
                FormattableString.Invariant($"em            {metrics.DesignEmHeight} units"),
                FormattableString.Invariant($"ascent        {-metrics.Ascent}"),
                FormattableString.Invariant($"descent       {metrics.Descent}"),
                FormattableString.Invariant($"line gap      {metrics.LineGap}"),
                FormattableString.Invariant($"underline     {metrics.UnderlinePosition} / {metrics.UnderlineThickness}"),
                FormattableString.Invariant($"strikethrough {metrics.StrikethroughPosition} / {metrics.StrikethroughThickness}"),
                $"fixed pitch   {(metrics.IsFixedPitch ? "yes" : "no")}",
                $"provenance    {typeface.MetricsProvenance}");

            var encoded = 0;

            foreach (var _ in new Avalonia.Media.Fonts.Tables.Cmap.CharacterToGlyphMapDictionary(
                         typeface.CharacterToGlyphMap))
            {
                encoded++;
            }

            _coverageText.Text =
                $"{typeface.GlyphCount} glyphs, {encoded} encoded codepoints " +
                $"({typeface.GlyphCount - encoded} unencoded: ligatures, alternates, components).";

            var inventory = new List<string>();
            long totalBytes = 0;
            var tableCount = 0;

            foreach (var tag in s_knownTables)
            {
                if (typeface.PlatformTypeface.TryGetTable(OpenTypeTag.Parse(tag), out var data))
                {
                    inventory.Add($"{tag.TrimEnd()} ({FormatSize(data.Length)})");
                    totalBytes += data.Length;
                    tableCount++;
                }
            }

            _tablesText.Text = $"{tableCount} of {s_knownTables.Length} known tables present, {FormatSize(totalBytes)} total:"
                + Environment.NewLine + string.Join("  ", inventory);

            var gsub = FeatureListTable.LoadGSub(typeface)?.Features;
            var gpos = FeatureListTable.LoadGPos(typeface)?.Features;

            _featuresText.Text = JoinLines(
                Line("GSUB", FormatTags(gsub)),
                Line("GPOS", FormatTags(gpos)))
                is { Length: > 0 } features ? features : "no GSUB or GPOS layout tables";

            var axes = typeface.VariationAxes;

            if (axes.Count > 0)
            {
                var tags = new StringBuilder();

                foreach (var axis in axes)
                {
                    if (tags.Length > 0)
                    {
                        tags.Append(", ");
                    }

                    tags.Append(FormattableString.Invariant(
                        $"{axis.Tag} {Fmt.N(axis.MinimumValue)}..{Fmt.N(axis.DefaultValue)}..{Fmt.N(axis.MaximumValue)}"));
                }

                _variationText.Text =
                    $"{axes.Count} axes ({tags}), {typeface.NamedInstances.Count} named instances.";
                _exploreAxesButton.IsVisible = true;
            }
            else
            {
                _variationText.Text = "static font - no fvar table.";
                _exploreAxesButton.IsVisible = false;
            }

            var colorParts = new List<string>();

            if (typeface.ColorTable is not null)
            {
                colorParts.Add("COLR color glyphs");
            }

            if (typeface.ColorPaletteTable is { } cpal)
            {
                colorParts.Add($"CPAL with {cpal.PaletteCount} palettes");
            }

            if (typeface.BitmapSource is not null)
            {
                colorParts.Add("bitmap strikes");
            }

            _colorText.Text = colorParts.Count > 0
                ? string.Join(", ", colorParts) + "."
                : "no color glyph representations.";
        }

        private static string? Line(string label, string? value)
            => string.IsNullOrWhiteSpace(value) ? null : $"{label}: {value}";

        private static string JoinLines(params string?[] lines)
        {
            var builder = new StringBuilder();

            foreach (var line in lines)
            {
                if (line is null)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.Append(line);
            }

            return builder.ToString();
        }

        private static string? Truncate(string? value, int max)
            => value is { Length: > 0 } && value.Length > max ? value.Substring(0, max) + "..." : value;

        private static string? FormatTags(IReadOnlyList<OpenTypeTag>? tags)
        {
            if (tags is not { Count: > 0 })
            {
                return null;
            }

            var seen = new SortedSet<string>();

            foreach (var tag in tags)
            {
                seen.Add(tag.ToString());
            }

            return $"{seen.Count} features: " + string.Join(" ", seen);
        }

        private static string FormatSize(long bytes) => bytes switch
        {
            < 1024 => FormattableString.Invariant($"{bytes} B"),
            < 1024 * 1024 => FormattableString.Invariant($"{bytes / 1024.0:0.#} KB"),
            _ => FormattableString.Invariant($"{bytes / (1024.0 * 1024.0):0.##} MB"),
        };
    }
}
