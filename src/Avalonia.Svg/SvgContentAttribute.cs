using System;

namespace Avalonia.Svg;

/// <summary>
/// Marks a string property as containing SVG markup. The XAML compiler
/// validates literal values of marked properties at compile time — malformed
/// markup becomes a build error at the XAML position — and minifies them
/// (comments, editor metadata and insignificant whitespace are removed, a
/// missing root <c>xmlns</c> is injected) so the runtime parses a compact,
/// pre-checked string.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SvgContentAttribute : Attribute
{
}
