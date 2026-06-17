using Avalonia.Metadata;

// SvgControl, SvgImage and SvgDocument fold into the core Avalonia.Controls /
// Avalonia.Media[.Imaging] namespaces (the Avalonia.Controls.ColorPicker pattern),
// so map them onto the default Avalonia XML namespace — no clr-namespace prefix
// needed to use <SvgControl> in XAML.
[assembly: XmlnsDefinition("https://github.com/avaloniaui", "Avalonia.Controls")]
[assembly: XmlnsDefinition("https://github.com/avaloniaui", "Avalonia.Media")]
[assembly: XmlnsDefinition("https://github.com/avaloniaui", "Avalonia.Media.Imaging")]
