using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;

namespace TextTour.Chapters
{
    /// <summary>
    /// Font resolution for the string: the family list as a query, the collections asked, the
    /// faces of the family with the one chosen, synthesized styles, and the per-character fallback
    /// that colours the string by the face rendering each part.
    /// </summary>
    internal sealed class FontsChapter : Chapter
    {
        private static readonly (string Name, FontWeight Weight)[] s_weights =
        {
            ("Thin 100", FontWeight.Thin), ("Light 300", FontWeight.Light), ("Normal 400", FontWeight.Normal),
            ("Medium 500", FontWeight.Medium), ("SemiBold 600", FontWeight.SemiBold), ("Bold 700", FontWeight.Bold),
            ("Black 900", FontWeight.Black),
        };

        private static readonly FontStyle[] s_styles = { FontStyle.Normal, FontStyle.Italic, FontStyle.Oblique };

        private static readonly FontStretch[] s_stretches =
        {
            FontStretch.Condensed, FontStretch.SemiCondensed, FontStretch.Normal, FontStretch.SemiExpanded,
            FontStretch.Expanded,
        };

        private static readonly string[] s_cultures = { "", "en-US", "ja-JP", "zh-CN", "zh-TW", "ko-KR", "ar-EG", "hi-IN" };

        private readonly TextBox _family;
        private readonly ComboBox _weight;
        private readonly ComboBox _style;
        private readonly ComboBox _stretch;
        private readonly ComboBox _culture;
        private readonly StackPanel _panel;
        private readonly List<TextLayout> _layouts = new();
        private bool _syncing;
        private bool _exporting;
        private CultureInfo? _exportCulture;
        private Model? _model;

        public FontsChapter(TourState state) : base(state)
        {
            _family = new TextBox { Width = 320, FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
            _family.LostFocus += (_, _) => ApplyFamily();
            _family.KeyDown += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter)
                {
                    ApplyFamily();
                    Focus();
                    e.Handled = true;
                }
            };

            _weight = Combo(s_weights.Select(w => w.Name), 2, i => State.Weight = s_weights[i].Weight);
            _style = Combo(s_styles.Select(s => s.ToString()), 0, i => State.Style = s_styles[i]);
            _stretch = Combo(s_stretches.Select(s => s.ToString()), 2, i => State.Stretch = s_stretches[i]);
            _culture = Combo(s_cultures.Select(c => c.Length == 0 ? "current culture" : c), 0,
                i => State.Culture = s_cultures[i].Length == 0 ? null : CultureInfo.GetCultureInfo(s_cultures[i]));

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(Label("Family"));
            row.Children.Add(_family);
            row.Children.Add(_weight);
            row.Children.Add(_style);
            row.Children.Add(_stretch);
            row.Children.Add(_culture);

            var moves = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            moves.Children.Add(Label("Try"));

            foreach (var (label, apply) in DemoMoves())
            {
                var button = new Button { Content = label, Focusable = false, FontSize = 16 };
                button.Click += (_, _) => apply();
                moves.Children.Add(button);
            }

            _panel = new StackPanel
            {
                Spacing = 8,
                Margin = new Thickness(Pad, 12, Pad, 0),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            _panel.Children.Add(row);
            _panel.Children.Add(moves);
            Child = _panel;
            SyncControls();
        }

        public override string Title => "Fonts";

        public override int StepCount => 5;

        public override string StepName => Step switch
        {
            1 => "the query",
            2 => "collections",
            3 => "faces",
            4 => "simulations",
            _ => "fallback",
        };

        public override string Caption => Step switch
        {
            1 => $"new Typeface(new FontFamily(\"{State.FamilyList}\"), {State.Style}, {State.Weight}, {State.Stretch})",
            2 => "FontFamily.FamilyNames, FontFamily.Key  ->  IFontCollection (fonts:SystemFonts, avares://...)",
            3 => "IFontCollection.TryGetFamilyTypefaces; FontManager.TryGetGlyphTypeface(typeface)",
            4 => "GlyphTypeface.FontSimulations (Bold, Oblique) when the family lacks the face",
            _ => "FontManager.TryMatchCharacter(codepoint, style, weight, stretch, family, culture)",
        };

        public override int ExportVariantCount(int step) => step switch
        {
            2 => 2,
            3 => 2,
            4 => 2,
            5 => 3,
            _ => 1,
        };

        public override string ExportVariantName(int step, int variant) => (step, variant) switch
        {
            (2, 1) => "-missing",
            (3, 1) or (4, 0) => "-bolditalic",
            (4, 1) => "-synthetic",
            (5, 1) => "-ja",
            (5, 2) => "-zh",
            _ => string.Empty,
        };

        public override void PrepareExport(int step, int variant)
        {
            // Variants without a culture of their own keep the culture the export started with.
            if (!_exporting)
            {
                _exporting = true;
                _exportCulture = State.Culture;
            }

            var defaultFamily = TourState.DefaultFamilyList;
            var bold = SystemFamilies.FirstOf("Avenir Next", "Segoe UI", "Arial") ?? defaultFamily;
            var cjk = SystemFamilies.FirstOf("PingFang SC", "Microsoft YaHei", "Yu Gothic UI", "Hiragino Sans") ?? defaultFamily;

            (string Family, FontWeight Weight, FontStyle Style, string? Culture) query = (step, variant) switch
            {
                (2, 1) => ("No Such Font, " + defaultFamily, FontWeight.Normal, FontStyle.Normal, null),
                (3, 1) or (4, 0) => (bold, FontWeight.Bold, FontStyle.Italic, null),
                (4, 1) => (cjk, FontWeight.Normal, FontStyle.Italic, null),
                (5, 1) => (defaultFamily, FontWeight.Normal, FontStyle.Normal, "ja-JP"),
                (5, 2) => (defaultFamily, FontWeight.Normal, FontStyle.Normal, "zh-CN"),
                _ => (defaultFamily, FontWeight.Normal, FontStyle.Normal, null),
            };

            State.FamilyList = query.Family;
            State.Weight = query.Weight;
            State.Style = query.Style;
            State.Culture = query.Culture is null ? _exportCulture : CultureInfo.GetCultureInfo(query.Culture);
        }

        public override void EndExport()
        {
            PrepareExport(1, 0);
            _exporting = false;
        }

        protected override void OnStateChanged(TourChange change)
        {
            if ((change & TourChange.Text) != 0)
            {
                SyncControls();
            }

            base.OnStateChanged(change);
        }

        protected override void InvalidateModel()
        {
            _model = null;
            base.InvalidateModel();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            foreach (var layout in _layouts)
            {
                layout.Dispose();
            }

            _layouts.Clear();

            var model = _model ??= new Model(State);
            var top = Math.Max(_panel.Bounds.Bottom, 100) + 24;
            var area = new Rect(Pad, top, Bounds.Width - 2 * Pad, Bounds.Height - top - 8);

            switch (Step)
            {
                case 1:
                    RenderQuery(context, model, area);
                    break;
                case 2:
                    RenderCollections(context, model, area);
                    break;
                case 3:
                    RenderFaces(context, model, area);
                    break;
                case 4:
                    RenderSimulations(context, model, area);
                    break;
                default:
                    RenderFallback(context, model, area);
                    break;
            }
        }

        private void RenderQuery(DrawingContext context, Model model, Rect area)
        {
            var p = Palette;
            var y = area.Y;
            Draw.Text(context, "A family name is a query, not a file:", area.X, y, 24, p.Muted);
            y += 44;

            var names = model.Family.FamilyNames.ToArray();

            for (var i = 0; i < names.Length; i++)
            {
                var box = new Rect(area.X + i * 260, y, 240, 56);
                Draw.Segment(context, box, p.SeriesFill(i), p.Series(i), $"{i + 1}. {names[i]}", 22, p.Foreground);
            }

            y += 80;
            var rows = new[]
            {
                ("Weight", $"{State.Weight} ({(int)State.Weight})"),
                ("Style", State.Style.ToString()),
                ("Stretch", $"{State.Stretch} ({(int)State.Stretch})"),
                ("Culture", State.Culture?.Name ?? $"current ({CultureInfo.CurrentCulture.Name})"),
            };

            foreach (var (key, value) in rows)
            {
                Draw.Text(context, key, area.X, y, 22, p.Muted);
                Draw.Text(context, value, area.X + 140, y, 22, p.Foreground, typeface: Draw.UiBold);
                y += 34;
            }

            y += 20;
            DrawSample(context, State.Typeface, State.Text, new Point(area.X, y), 56, area.Width);
        }

        private void RenderCollections(DrawingContext context, Model model, Rect area)
        {
            var p = Palette;
            var y = area.Y;

            for (var i = 0; i < model.Lookups.Count; i++)
            {
                var lookup = model.Lookups[i];
                var box = new Rect(area.X, y, Math.Min(area.Width, 980), 64);
                Draw.Segment(context, box, lookup.Found ? p.SeriesFill(2) : p.Surface, lookup.Found ? p.Series(2) : p.Grid,
                    null, 20, p.Foreground);
                Draw.Text(context, $"{i + 1}. '{lookup.Name}'", box.X + 16, box.Y + 8, 22, p.Foreground, typeface: Draw.UiBold);
                Draw.Text(context, $"asked in {lookup.Collection}", box.X + 16, box.Y + 36, 18, p.Muted);
                Draw.Text(context, lookup.Found ? $"found, {lookup.FaceCount} faces" : "not found, next name",
                    box.Right - 16, box.Y + 20, 22, lookup.Found ? p.Series(2) : p.Warning, HAlign.Right, Draw.UiBold);
                y += 76;
            }

            y += 16;
            var winner = model.Winner;
            Draw.Text(context, "FontManager.TryGetGlyphTypeface(typeface) returned:", area.X, y, 20, p.Muted);
            y += 32;
            Draw.Text(context, winner is null ? "nothing" : $"{winner.FamilyName}  {FaceName(winner)}", area.X, y, 30,
                p.Foreground, typeface: Draw.UiBold);

            if (winner is not null && !model.Lookups.Any(l => l.Found))
            {
                y += 48;
                Draw.Text(context, "No name in the list resolved, so the default family answered.", area.X, y, 20, p.Warning);
            }
        }

        private void RenderFaces(DrawingContext context, Model model, Rect area)
        {
            var p = Palette;

            if (model.Faces.Count == 0)
            {
                Draw.Text(context, "The family's faces could not be listed.", area.X, area.Y, 24, p.Muted);
                return;
            }

            Draw.Text(context, $"Faces of '{model.FaceFamily}', ranked by CSS font matching distance (explanatory, computed by this app); " +
                               "the highlighted face is FontManager's answer", area.X, area.Y, 18, p.Muted, maxWidth: area.Width);

            var columns = Math.Max(1, (int)(area.Width / 250));
            var cardWidth = area.Width / columns - 12;
            var cardHeight = 118.0;
            var y0 = area.Y + 36;
            var maxRows = Math.Max(1, (int)((area.Bottom - y0) / (cardHeight + 12)));

            for (var i = 0; i < model.Faces.Count && i < columns * maxRows; i++)
            {
                var face = model.Faces[i];
                var x = area.X + (i % columns) * (cardWidth + 12);
                var y = y0 + (i / columns) * (cardHeight + 12);
                var card = new Rect(x, y, cardWidth, cardHeight);
                var chosen = face.IsWinner;
                context.DrawRectangle(chosen ? p.AccentFill : p.Surface, Draw.Pen(chosen ? p.Accent : p.Grid, chosen ? 3 : 1.5), card, 8, 8);

                Draw.Text(context, $"#{i + 1}", card.X + 10, card.Y + 8, 16, chosen ? p.Accent : p.Muted, typeface: Draw.UiBold);
                Draw.Text(context, face.Name, card.X + 48, card.Y + 6, 20, p.Foreground, typeface: Draw.UiBold, maxWidth: cardWidth - 56);
                Draw.Text(context, $"{(int)face.Weight}  {face.Style}  {face.Stretch}", card.X + 10, card.Y + 36, 16, p.Muted,
                    maxWidth: cardWidth - 20);

                using var sample = new TextLayout("Office", new Typeface(new FontFamily(model.FaceFamily), face.Style, face.Weight, face.Stretch),
                    32, p.Foreground, maxWidth: cardWidth - 20, textTrimming: TextTrimming.CharacterEllipsis);
                sample.Draw(context, new Point(card.X + 10, card.Y + 60));
            }

            if (model.Faces.Count > columns * maxRows)
            {
                Draw.Text(context, $"+ {model.Faces.Count - columns * maxRows} more", area.Right, area.Bottom - 24, 18, p.Muted, HAlign.Right);
            }
        }

        private void RenderSimulations(DrawingContext context, Model model, Rect area)
        {
            var p = Palette;
            var winner = model.Winner;

            if (winner is null)
            {
                return;
            }

            var simulations = winner.FontSimulations;
            var y = area.Y;
            Draw.Text(context, "Requested", area.X, y, 20, p.Muted);
            Draw.Text(context, $"{State.Weight} {State.Style} {State.Stretch}", area.X + 180, y, 22, p.Foreground, typeface: Draw.UiBold);
            y += 34;
            Draw.Text(context, "Answer", area.X, y, 20, p.Muted);
            Draw.Text(context, $"{winner.FamilyName} {FaceName(winner)}, {(int)winner.Weight} {winner.Style}", area.X + 180, y, 22,
                p.Foreground, typeface: Draw.UiBold);
            y += 34;
            Draw.Text(context, "FontSimulations", area.X, y, 20, p.Muted);
            Draw.Text(context, simulations == FontSimulations.None ? "None: a real face" : simulations.ToString(), area.X + 180, y, 22,
                simulations == FontSimulations.None ? p.Series(2) : p.Warning, typeface: Draw.UiBold);
            y += 60;

            var nearest = model.Faces.FirstOrDefault();
            var half = (area.Width - 24) / 2;

            if (nearest is not null)
            {
                Draw.Text(context, $"nearest real face: {nearest.Name}", area.X, y, 18, p.Muted);
                DrawSample(context, new Typeface(new FontFamily(model.FaceFamily), nearest.Style, nearest.Weight, nearest.Stretch),
                    "Office \u6F22\u5B57", new Point(area.X, y + 28), 64, half);
            }

            Draw.Text(context, simulations == FontSimulations.None ? "what the request renders" : "what the request renders (synthesized)",
                area.X + half + 24, y, 18, p.Muted);
            DrawSample(context, State.Typeface, "Office \u6F22\u5B57", new Point(area.X + half + 24, y + 28), 64, half);
        }

        private void RenderFallback(DrawingContext context, Model model, Rect area)
        {
            var p = Palette;
            var analysis = State.Analysis;
            var faces = analysis.Items.Select(i => i.Typeface.FamilyName).Distinct().ToList();

            // The string coloured by the face of each shaped item: the same layout as the other
            // chapters, with one foreground per face.
            var overrides = analysis.Items
                .Select(i => new Avalonia.Utilities.ValueSpan<TextRunProperties>(i.Range.Start, i.Range.Length,
                    new GenericTextRunProperties(State.Typeface, 64, foregroundBrush: p.Series(faces.IndexOf(i.Typeface.FamilyName)),
                        cultureInfo: State.Culture)))
                .ToArray();
            var colored = new TextLayout(State.Text, State.Typeface, 64, p.Foreground, maxWidth: area.Width,
                textWrapping: TextWrapping.Wrap, textStyleOverrides: overrides);
            _layouts.Add(colored);
            colored.Draw(context, area.TopLeft);

            var y = area.Y + colored.Height + 16;
            var x = area.X;

            for (var i = 0; i < faces.Count; i++)
            {
                var size = Draw.Text(context, faces[i], x + 26, y, 20, p.Foreground, typeface: Draw.UiBold);
                context.DrawRectangle(p.Series(i), null, new Rect(x, y + 4, 18, 18), 3, 3);
                x += size.Width + 60;
            }

            y += 44;
            Draw.Text(context, "Fallback requests for characters the first face does not cover:", area.X, y, 18, p.Muted);
            y += 30;

            foreach (var request in model.Fallbacks.Take(Math.Max(1, (int)((area.Bottom - y) / 32))))
            {
                Draw.Text(context, request, area.X, y, 18, p.Foreground, typeface: Draw.UiMono, maxWidth: area.Width);
                y += 30;
            }
        }

        private void DrawSample(DrawingContext context, Typeface typeface, string text, Point origin, double size, double width)
        {
            var layout = new TextLayout(text, typeface, size, Palette.Foreground, maxWidth: width, textWrapping: TextWrapping.Wrap);
            _layouts.Add(layout);
            layout.Draw(context, origin);
        }

        private static string FaceName(GlyphTypeface face)
        {
            if (face.FaceNames.TryGetValue(CultureInfo.GetCultureInfo("en-US"), out var name) ||
                face.FaceNames.TryGetValue(CultureInfo.InvariantCulture, out name))
            {
                return name;
            }

            return face.FaceNames.Values.FirstOrDefault() ?? string.Empty;
        }

        private void ApplyFamily()
        {
            if (!_syncing && !string.IsNullOrWhiteSpace(_family.Text))
            {
                State.FamilyList = _family.Text!;
            }
        }

        private void SyncControls()
        {
            _syncing = true;
            _family.Text = State.FamilyList;
            _weight.SelectedIndex = Math.Max(0, Array.FindIndex(s_weights, w => w.Weight == State.Weight));
            _style.SelectedIndex = Math.Max(0, Array.IndexOf(s_styles, State.Style));
            _stretch.SelectedIndex = Math.Max(0, Array.IndexOf(s_stretches, State.Stretch));
            _culture.SelectedIndex = Math.Max(0, Array.IndexOf(s_cultures, State.Culture?.Name ?? string.Empty));
            _syncing = false;
        }

        private ComboBox Combo(IEnumerable<string> items, int selected, Action<int> apply)
        {
            var combo = new ComboBox { ItemsSource = items.ToArray(), SelectedIndex = selected, FontSize = 16, MinWidth = 120 };
            combo.SelectionChanged += (_, _) =>
            {
                if (!_syncing && combo.SelectedIndex >= 0)
                {
                    apply(combo.SelectedIndex);

                    // Step keys belong to the presenter again once a value is picked.
                    Focus();
                }
            };

            return combo;
        }

        private static TextBlock Label(string text) =>
            new() { Text = text, FontSize = 18, VerticalAlignment = VerticalAlignment.Center, MinWidth = 60 };

        // Moves that show matching at work with fonts present on the machine: a real bold italic
        // face, an italic the family lacks, and the default again.
        private IEnumerable<(string, Action)> DemoMoves()
        {
            yield return ("Default", () => Apply(TourState.DefaultFamilyList, FontWeight.Normal, FontStyle.Normal));

            if (SystemFamilies.FirstOf("Avenir Next", "Segoe UI", "Arial") is { } bold)
            {
                yield return ($"{bold} bold italic", () => Apply(bold, FontWeight.Bold, FontStyle.Italic));
            }

            if (SystemFamilies.FirstOf("PingFang SC", "Microsoft YaHei", "Yu Gothic UI", "Hiragino Sans") is { } cjk)
            {
                yield return ($"{cjk} italic", () => Apply(cjk, FontWeight.Normal, FontStyle.Italic));
            }

            yield return ("Missing family", () => Apply("No Such Font, " + TourState.DefaultFamilyList, FontWeight.Normal, FontStyle.Normal));

            void Apply(string family, FontWeight weight, FontStyle style)
            {
                State.FamilyList = family;
                State.Weight = weight;
                State.Style = style;
            }
        }

        public override void Dispose()
        {
            foreach (var layout in _layouts)
            {
                layout.Dispose();
            }

            _layouts.Clear();
        }

        private sealed record Lookup(string Name, string Collection, bool Found, int FaceCount);

        private sealed record Face(string Name, FontWeight Weight, FontStyle Style, FontStretch Stretch, bool IsWinner);

        /// <summary>The resolution of the current query, read from the font manager once per change.</summary>
        private sealed class Model
        {
            public Model(TourState state)
            {
                var manager = FontManager.Current;
                Family = new FontFamily(state.FamilyList);
                var typeface = state.Typeface;
                Winner = manager.TryGetGlyphTypeface(typeface, out var winner) ? winner : null;

                var lookups = new List<Lookup>();
                var names = Family.FamilyNames.ToArray();

                for (var i = 0; i < names.Length; i++)
                {
                    var name = names[i];
                    // A list of names carries one key per name; a name without a source is a system font.
                    var key = Family.Key is CompositeFontFamilyKey composite ? composite.Keys[i] : Family.Key;
                    var uri = key is null ? FontManager.SystemFontsKey : key.Source.EnsureAbsolute(key.BaseUri);

                    if (name == FontFamily.DefaultFontFamilyName)
                    {
                        lookups.Add(new Lookup(name, "the default family: " + manager.DefaultFontFamily.Name, true, 0));
                        continue;
                    }

                    var found = manager.TryGetFontCollection(uri, out var collection) &&
                                collection.TryGetFamilyTypefaces(name, out var faces) && faces.Count > 0;
                    var count = found && collection!.TryGetFamilyTypefaces(name, out var list) ? list.Count : 0;
                    lookups.Add(new Lookup(name, uri.ToString(), found, count));
                }

                Lookups = lookups;

                FaceFamily = Winner?.FamilyName ?? manager.DefaultFontFamily.Name;
                Faces = ReadFaces(manager, FaceFamily, state, Winner);
                Fallbacks = ReadFallbacks(state);
            }

            public FontFamily Family { get; }

            public GlyphTypeface? Winner { get; }

            public IReadOnlyList<Lookup> Lookups { get; }

            public string FaceFamily { get; }

            /// <summary>The family's real faces, nearest to the request first.</summary>
            public IReadOnlyList<Face> Faces { get; }

            public IReadOnlyList<string> Fallbacks { get; }

            private static IReadOnlyList<Face> ReadFaces(FontManager manager, string family, TourState state, GlyphTypeface? winner)
            {
                if (!manager.SystemFonts.TryGetFamilyTypefaces(family, out var typefaces))
                {
                    return Array.Empty<Face>();
                }

                var faces = new List<Face>();

                foreach (var typeface in typefaces)
                {
                    if (!manager.SystemFonts.TryGetGlyphTypeface(family, typeface.Style, typeface.Weight, typeface.Stretch,
                            out var glyphTypeface) || glyphTypeface.FontSimulations != FontSimulations.None)
                    {
                        continue;
                    }

                    var isWinner = winner is not null && winner.FontSimulations == FontSimulations.None &&
                                   winner.Weight == glyphTypeface.Weight && winner.Style == glyphTypeface.Style &&
                                   winner.Stretch == glyphTypeface.Stretch;

                    if (faces.Any(f => f.Weight == glyphTypeface.Weight && f.Style == glyphTypeface.Style &&
                                       f.Stretch == glyphTypeface.Stretch))
                    {
                        continue;
                    }

                    faces.Add(new Face(FaceName(glyphTypeface), glyphTypeface.Weight, glyphTypeface.Style,
                        glyphTypeface.Stretch, isWinner));
                }

                return faces
                    .OrderBy(f => StyleRank(f.Style, state.Style))
                    .ThenBy(f => StretchRank(f.Stretch, state.Stretch))
                    .ThenBy(f => WeightRank((int)f.Weight, (int)state.Weight))
                    .ToList();
            }

            private static IReadOnlyList<string> ReadFallbacks(TourState state)
            {
                var analysis = state.Analysis;
                var primary = analysis.Items.FirstOrDefault()?.Typeface.FamilyName;
                var result = new List<string>();

                foreach (var item in analysis.Items)
                {
                    if (item.Typeface.FamilyName == primary)
                    {
                        continue;
                    }

                    var cp = analysis.Codepoints.First(c => item.Range.Contains(c.Offset)).Codepoint;
                    var answer = FontManager.Current.TryMatchCharacter((int)cp.Value, state.Style, state.Weight, state.Stretch,
                        new FontFamily(state.FamilyList), state.Culture, out var match) ?
                        match.FontFamily.Name :
                        "no match";
                    result.Add($"{Draw.Hex(cp.Value),-8} {state.Weight} {state.Style} {state.Culture?.Name ?? CultureInfo.CurrentCulture.Name,-6} " +
                               $"-> {answer}   (item uses {item.Typeface.FamilyName})");
                }

                return result;
            }

            // CSS Fonts 4 matching, used only to order the cards: style first, then stretch, then weight.
            private static int StyleRank(FontStyle face, FontStyle wanted)
            {
                if (face == wanted)
                {
                    return 0;
                }

                return wanted == FontStyle.Normal ? (face == FontStyle.Oblique ? 1 : 2) :
                    face == FontStyle.Normal ? 2 : 1;
            }

            private static int StretchRank(FontStretch face, FontStretch wanted)
            {
                var d = (int)face - (int)wanted;

                // Narrower faces are preferred for a condensed request, wider ones otherwise.
                return (int)wanted <= (int)FontStretch.Normal ? (d <= 0 ? -d * 2 : d * 2 + 1) : (d >= 0 ? d * 2 : -d * 2 + 1);
            }

            private static int WeightRank(int face, int wanted)
            {
                if (face == wanted)
                {
                    return 0;
                }

                if (wanted is >= 400 and <= 500)
                {
                    if (face >= wanted && face <= 500)
                    {
                        return face - wanted;
                    }

                    return face < wanted ? 1000 + (wanted - face) : 2000 + face;
                }

                if (wanted < 400)
                {
                    return face < wanted ? wanted - face : 1000 + face;
                }

                return face > wanted ? face - wanted : 1000 + (wanted - face);
            }
        }
    }
}
