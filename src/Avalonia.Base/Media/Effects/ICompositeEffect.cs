// ReSharper disable once CheckNamespace

using System;
using System.Collections.Generic;
using Avalonia.Animation.Animators;

namespace Avalonia.Media;

/// <summary>
/// An effect that applies a linear chain of child effects in sequence: the
/// first child processes the input, each subsequent child processes the
/// previous child's output.
/// </summary>
public interface ICompositeEffect : IEffect
{
    IReadOnlyList<IEffect> Children { get; }
}

public class ImmutableCompositeEffect : ICompositeEffect, IImmutableEffect
{
    private readonly IImmutableEffect[] _children;

    static ImmutableCompositeEffect()
    {
        EffectAnimator.EnsureRegistered();
    }

    public ImmutableCompositeEffect(IReadOnlyList<IEffect> children)
    {
        _ = children ?? throw new ArgumentNullException(nameof(children));
        if (children.Count == 0)
            throw new ArgumentException("A composite effect needs at least one child.", nameof(children));

        var copy = new IImmutableEffect[children.Count];
        for (var i = 0; i < children.Count; i++)
            copy[i] = children[i].ToImmutable();
        _children = copy;
    }

    public IReadOnlyList<IEffect> Children => _children;

    public bool Equals(IEffect? other)
    {
        if (other is not ICompositeEffect composite || composite.Children.Count != _children.Length)
            return false;

        for (var i = 0; i < _children.Length; i++)
        {
            if (!_children[i].EffectEquals(composite.Children[i]))
                return false;
        }

        return true;
    }
}
