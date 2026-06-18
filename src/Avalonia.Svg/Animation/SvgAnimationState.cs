using System;
using System.Collections.Generic;

namespace Avalonia.Media.Svg.Animation;

/// <summary>
/// The animated attribute overrides for one animation instance (one
/// <see cref="SvgImage"/> / host). Kept off the shared <see cref="SvgElement"/>
/// so the same document can animate at different document times in several hosts.
/// The overrides are materialized onto the elements transactionally for the
/// duration of a single, synchronous compile and cleared again — see
/// <see cref="Materialize"/>.
/// </summary>
internal sealed class SvgAnimationState
{
    private readonly Dictionary<(SvgElement Element, string Attribute), string?> _values =
        new();

    /// <summary>The current override for an (element, attribute), or null.</summary>
    public string? Get(SvgElement element, string attribute) =>
        _values.TryGetValue((element, attribute), out var value) ? value : null;

    /// <summary>
    /// Sets an override, returning true when the value changed — the caller uses
    /// this for structural change detection.
    /// </summary>
    public bool Set(SvgElement element, string attribute, string? value)
    {
        if (_values.TryGetValue((element, attribute), out var current)
            && string.Equals(current, value, StringComparison.Ordinal))
        {
            return false;
        }

        _values[(element, attribute)] = value;
        return true;
    }

    /// <summary>
    /// Writes every override onto its element's animated-value slot for the
    /// lifetime of the returned scope, clearing them on dispose. Compilation is
    /// synchronous, so exactly one instance's overrides are live on the document
    /// at a time. Not re-entrant: compiles are sequenced, never nested.
    /// </summary>
    public Scope Materialize()
    {
        foreach (var pair in _values)
            pair.Key.Element.SetAnimatedValue(pair.Key.Attribute, pair.Value);
        return new Scope(this);
    }

    public readonly struct Scope : IDisposable
    {
        private readonly SvgAnimationState _state;

        internal Scope(SvgAnimationState state) => _state = state;

        public void Dispose()
        {
            foreach (var pair in _state._values)
                pair.Key.Element.SetAnimatedValue(pair.Key.Attribute, null);
        }
    }
}
