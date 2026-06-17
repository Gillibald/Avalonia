using Avalonia.Metadata;

// SvgControl lands in Avalonia.Controls and SvgImage in Avalonia.Media (the SVG
// document model and internals live under Avalonia.Media.Svg). Map the two
// namespaces that expose XAML-usable types onto the default Avalonia XML
// namespace, so <SvgControl> needs no clr-namespace prefix.
[assembly: XmlnsDefinition("https://github.com/avaloniaui", "Avalonia.Controls")]
[assembly: XmlnsDefinition("https://github.com/avaloniaui", "Avalonia.Media")]
