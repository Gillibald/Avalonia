using System;
using System.Collections.Generic;
using Avalonia.Platform;

namespace Avalonia.Media.Imaging;

/// <summary>
/// A metadata store held in memory: the store behind metadata authored from scratch and
/// behind writable copies of metadata whose backend store cannot be copied.
/// </summary>
internal sealed class MemoryMetadataStore : IBitmapMetadataStore
{
    private readonly SortedDictionary<string, MetadataValue> _values;

    public MemoryMetadataStore()
        : this(new SortedDictionary<string, MetadataValue>(StringComparer.Ordinal), false)
    {
    }

    private MemoryMetadataStore(SortedDictionary<string, MetadataValue> values, bool isReadOnly)
    {
        _values = values;
        IsReadOnly = isReadOnly;
    }

    public bool IsReadOnly { get; }

    /// <summary>
    /// Copies every value of another store.
    /// </summary>
    public static MemoryMetadataStore CopyOf(IBitmapMetadataStore source, bool isReadOnly = false)
    {
        var values = new SortedDictionary<string, MetadataValue>(StringComparer.Ordinal);

        foreach (var path in source.EnumeratePaths())
        {
            if (source.TryRead(path, out var value))
                values[path] = value;
        }

        return new MemoryMetadataStore(values, isReadOnly);
    }

    public bool TryRead(string path, out MetadataValue value) => _values.TryGetValue(path, out value);

    public void Write(string path, MetadataValue value)
    {
        ThrowIfReadOnly();
        _values[path] = value;
    }

    public bool Remove(string path)
    {
        ThrowIfReadOnly();
        return _values.Remove(path);
    }

    public IEnumerable<string> EnumeratePaths() => _values.Keys;

    public IBitmapMetadataStore Clone() => new MemoryMetadataStore(new SortedDictionary<string, MetadataValue>(_values, StringComparer.Ordinal), false);

    private void ThrowIfReadOnly()
    {
        if (IsReadOnly)
            throw new InvalidOperationException("The metadata is read-only; clone it to make changes.");
    }
}
