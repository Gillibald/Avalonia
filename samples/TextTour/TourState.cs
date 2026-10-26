using System;
using System.Globalization;
using Avalonia.Media;

namespace TextTour
{
    /// <summary>What a change to <see cref="TourState"/> touched, so observers redo only what depends on it.</summary>
    [Flags]
    internal enum TourChange
    {
        None = 0,

        /// <summary>The string, its style span or the font query: the analysis is rebuilt.</summary>
        Text = 1,

        /// <summary>Theme or content scale.</summary>
        View = 2,

        /// <summary>The wrapping width shared by the Layout and Hits chapters.</summary>
        Width = 4,
    }

    /// <summary>
    /// The state every chapter observes: one string, the font query that renders it and the
    /// presenter's view settings. Owned by the window (or by the exporter), never static.
    /// </summary>
    internal sealed class TourState : IDisposable
    {
        private string _text;
        private Preset _preset;
        private string _familyList;
        private FontWeight _weight = FontWeight.Normal;
        private FontStyle _style = FontStyle.Normal;
        private FontStretch _stretch = FontStretch.Normal;
        private CultureInfo? _culture;
        private bool _dark;
        private double _contentScale = 1;
        private double _wrapFraction = 0.62;
        private TextAnalysis? _analysis;

        public TourState(Preset preset)
        {
            _preset = preset;
            _text = preset.Text;
            _familyList = DefaultFamilyList;
        }

        /// <summary>Raised after any setter changed a value.</summary>
        public event Action<TourChange>? Changed;

        /// <summary>The platform's default family, so the string starts with the system font plus fallback.</summary>
        public static string DefaultFamilyList => FontManager.Current.DefaultFontFamily.Name;

        /// <summary>Visualised string size in device independent pixels at 100 % content scale.</summary>
        public double FontSize => 56;

        /// <summary>False while exporting, so every step renders in its final state.</summary>
        public bool Animate { get; set; } = true;

        public string Text
        {
            get => _text;
            set
            {
                if (_text == value)
                {
                    return;
                }

                _text = value;
                Raise(TourChange.Text);
            }
        }

        public Preset Preset
        {
            get => _preset;
            set
            {
                _preset = value;
                _text = value.Text;
                Raise(TourChange.Text);
            }
        }

        /// <summary>The style span of the text source: the preset's span while the text is the preset's own.</summary>
        public StyleSpan? StyleSpan => _text == _preset.Text ? _preset.Span : null;

        /// <summary>The family list as typed: comma separated names, the first that resolves wins.</summary>
        public string FamilyList
        {
            get => _familyList;
            set
            {
                if (_familyList == value)
                {
                    return;
                }

                _familyList = value;
                Raise(TourChange.Text);
            }
        }

        public FontWeight Weight
        {
            get => _weight;
            set
            {
                _weight = value;
                Raise(TourChange.Text);
            }
        }

        public FontStyle Style
        {
            get => _style;
            set
            {
                _style = value;
                Raise(TourChange.Text);
            }
        }

        public FontStretch Stretch
        {
            get => _stretch;
            set
            {
                _stretch = value;
                Raise(TourChange.Text);
            }
        }

        /// <summary>The culture handed to the text runs and the fallback request; null uses the current culture.</summary>
        public CultureInfo? Culture
        {
            get => _culture;
            set
            {
                _culture = value;
                Raise(TourChange.Text);
            }
        }

        public bool Dark
        {
            get => _dark;
            set
            {
                if (_dark == value)
                {
                    return;
                }

                _dark = value;
                Raise(TourChange.View);
            }
        }

        public double ContentScale
        {
            get => _contentScale;
            set
            {
                var clamped = Math.Clamp(Math.Round(value * 4) / 4, 0.75, 2);

                if (_contentScale == clamped)
                {
                    return;
                }

                _contentScale = clamped;
                Raise(TourChange.View);
            }
        }

        /// <summary>The wrapping width of the Layout and Hits chapters as a fraction of their content width.</summary>
        public double WrapFraction
        {
            get => _wrapFraction;
            set
            {
                var clamped = Math.Clamp(value, 0.12, 1);

                if (_wrapFraction == clamped)
                {
                    return;
                }

                _wrapFraction = clamped;
                Raise(TourChange.Width);
            }
        }

        public Typeface Typeface => new(new FontFamily(_familyList), _style, _weight, _stretch);

        public Palette Palette => _dark ? Palette.Dark : Palette.Light;

        /// <summary>The pipeline's view of the current string, rebuilt lazily after a text change.</summary>
        public TextAnalysis Analysis => _analysis ??= new TextAnalysis(_text, StyleSpan, Typeface, FontSize, _culture, Palette);

        private void Raise(TourChange change)
        {
            if ((change & (TourChange.Text | TourChange.View)) != 0)
            {
                // The analysis layout carries the style span's brush, which follows the theme.
                _analysis?.Dispose();
                _analysis = null;
            }

            Changed?.Invoke(change);
        }

        public void Dispose()
        {
            _analysis?.Dispose();
            _analysis = null;
        }
    }
}
