using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Avalonia.Platform;

namespace Avalonia.Media.Imaging;

/// <summary>
/// Converts raw metadata values to and from the CLR values the query API exposes, in the
/// shapes WIC uses: integers by width and sign, rationals packed into 64 bits with the
/// numerator in the low half, text as strings, opaque data as byte arrays.
/// </summary>
internal static class MetadataValueConverter
{
    public static object? ToObject(MetadataValue value)
    {
        var data = value.Data.Span;

        switch (value.Type)
        {
            case MetadataValueType.Ascii:
                return Encoding.Latin1.GetString(data);
            case MetadataValueType.Unicode:
                return Encoding.Unicode.GetString(data);
            case MetadataValueType.Undefined:
                return data.ToArray();
            case MetadataValueType.List:
                return ListToObject(value.Items!);
        }

        var count = value.Count;

        return value.Type switch
        {
            MetadataValueType.Byte => count == 1 ? (object)data[0] : data.ToArray(),
            MetadataValueType.SByte => count == 1 ? (object)(sbyte)data[0] : ToArray(data, 1, (s, i) => (sbyte)s[i]),
            MetadataValueType.Short => count == 1 ? (object)BinaryPrimitives.ReadUInt16LittleEndian(data)
                : ToArray(data, 2, (s, i) => BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(i))),
            MetadataValueType.SShort => count == 1 ? (object)BinaryPrimitives.ReadInt16LittleEndian(data)
                : ToArray(data, 2, (s, i) => BinaryPrimitives.ReadInt16LittleEndian(s.Slice(i))),
            MetadataValueType.Long => count == 1 ? (object)BinaryPrimitives.ReadUInt32LittleEndian(data)
                : ToArray(data, 4, (s, i) => BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(i))),
            MetadataValueType.SLong => count == 1 ? (object)BinaryPrimitives.ReadInt32LittleEndian(data)
                : ToArray(data, 4, (s, i) => BinaryPrimitives.ReadInt32LittleEndian(s.Slice(i))),
            MetadataValueType.Rational => count == 1 ? (object)BinaryPrimitives.ReadUInt64LittleEndian(data)
                : ToArray(data, 8, (s, i) => BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(i))),
            MetadataValueType.SRational => count == 1 ? (object)BinaryPrimitives.ReadInt64LittleEndian(data)
                : ToArray(data, 8, (s, i) => BinaryPrimitives.ReadInt64LittleEndian(s.Slice(i))),
            MetadataValueType.Float => count == 1 ? (object)BinaryPrimitives.ReadSingleLittleEndian(data)
                : ToArray(data, 4, (s, i) => BinaryPrimitives.ReadSingleLittleEndian(s.Slice(i))),
            MetadataValueType.Double => count == 1 ? (object)BinaryPrimitives.ReadDoubleLittleEndian(data)
                : ToArray(data, 8, (s, i) => BinaryPrimitives.ReadDoubleLittleEndian(s.Slice(i))),
            _ => throw new NotSupportedException($"Metadata values of type {value.Type} are not supported.")
        };
    }

    /// <summary>
    /// Converts a CLR value for storage. When the path already holds a numeric value, the
    /// stored type is kept if the new value converts to it without loss.
    /// </summary>
    public static MetadataValue FromObject(object value, MetadataValue? existing)
    {
        if (existing is { Type: not (MetadataValueType.List or MetadataValueType.Ascii or MetadataValueType.Unicode or MetadataValueType.Undefined) } previous &&
            TryConvertScalar(value, previous.Type) is { } kept)
        {
            return kept;
        }

        switch (value)
        {
            case bool flag:
                return new MetadataValue(MetadataValueType.Byte, new[] { flag ? (byte)1 : (byte)0 });
            case string text:
                return FromText(text);
            case string[] texts:
            {
                var items = new List<MetadataValue>(texts.Length);

                foreach (var text in texts)
                    items.Add(FromText(text));

                return new MetadataValue(items);
            }
            case byte[] bytes:
                return new MetadataValue(MetadataValueType.Undefined, (byte[])bytes.Clone());
            case byte scalar:
                return new MetadataValue(MetadataValueType.Byte, new[] { scalar });
            case sbyte scalar:
                return new MetadataValue(MetadataValueType.SByte, new[] { (byte)scalar });
            case ushort scalar:
                return Pack(MetadataValueType.Short, 2, s => BinaryPrimitives.WriteUInt16LittleEndian(s, scalar));
            case short scalar:
                return Pack(MetadataValueType.SShort, 2, s => BinaryPrimitives.WriteInt16LittleEndian(s, scalar));
            case uint scalar:
                return Pack(MetadataValueType.Long, 4, s => BinaryPrimitives.WriteUInt32LittleEndian(s, scalar));
            case int scalar:
                return Pack(MetadataValueType.SLong, 4, s => BinaryPrimitives.WriteInt32LittleEndian(s, scalar));
            case ulong scalar:
                return Pack(MetadataValueType.Rational, 8, s => BinaryPrimitives.WriteUInt64LittleEndian(s, scalar));
            case long scalar:
                return Pack(MetadataValueType.SRational, 8, s => BinaryPrimitives.WriteInt64LittleEndian(s, scalar));
            case float scalar:
                return Pack(MetadataValueType.Float, 4, s => BinaryPrimitives.WriteSingleLittleEndian(s, scalar));
            case double scalar:
                return Pack(MetadataValueType.Double, 8, s => BinaryPrimitives.WriteDoubleLittleEndian(s, scalar));
            case ushort[] values:
                return PackArray(MetadataValueType.Short, values.Length, 2, (s, i) => BinaryPrimitives.WriteUInt16LittleEndian(s, values[i]));
            case uint[] values:
                return PackArray(MetadataValueType.Long, values.Length, 4, (s, i) => BinaryPrimitives.WriteUInt32LittleEndian(s, values[i]));
            case ulong[] values:
                return PackArray(MetadataValueType.Rational, values.Length, 8, (s, i) => BinaryPrimitives.WriteUInt64LittleEndian(s, values[i]));
            default:
                throw new NotSupportedException($"Metadata values of type {value.GetType().Name} are not supported.");
        }
    }

    // Latin-1 text keeps the single-byte form tags such as EXIF ASCII and PNG tEXt use.
    private static MetadataValue FromText(string text)
    {
        foreach (var c in text)
        {
            if (c > 0xFF)
                return new MetadataValue(MetadataValueType.Unicode, Encoding.Unicode.GetBytes(text));
        }

        return new MetadataValue(MetadataValueType.Ascii, Encoding.Latin1.GetBytes(text));
    }

    private static MetadataValue? TryConvertScalar(object value, MetadataValueType type)
    {
        long number;

        switch (value)
        {
            case byte b: number = b; break;
            case sbyte b: number = b; break;
            case ushort s: number = s; break;
            case short s: number = s; break;
            case uint i: number = i; break;
            case int i: number = i; break;
            default: return null;
        }

        return type switch
        {
            MetadataValueType.Byte when number is >= 0 and <= byte.MaxValue => new MetadataValue(type, new[] { (byte)number }),
            MetadataValueType.SByte when number is >= sbyte.MinValue and <= sbyte.MaxValue => new MetadataValue(type, new[] { (byte)(sbyte)number }),
            MetadataValueType.Short when number is >= 0 and <= ushort.MaxValue => Pack(type, 2, s => BinaryPrimitives.WriteUInt16LittleEndian(s, (ushort)number)),
            MetadataValueType.SShort when number is >= short.MinValue and <= short.MaxValue => Pack(type, 2, s => BinaryPrimitives.WriteInt16LittleEndian(s, (short)number)),
            MetadataValueType.Long when number is >= 0 and <= uint.MaxValue => Pack(type, 4, s => BinaryPrimitives.WriteUInt32LittleEndian(s, (uint)number)),
            MetadataValueType.SLong when number is >= int.MinValue and <= int.MaxValue => Pack(type, 4, s => BinaryPrimitives.WriteInt32LittleEndian(s, (int)number)),
            _ => null
        };
    }

    private static object ListToObject(IReadOnlyList<MetadataValue> items)
    {
        var allText = true;

        foreach (var item in items)
            allText &= item.Type is MetadataValueType.Ascii or MetadataValueType.Unicode;

        if (allText)
        {
            var texts = new string[items.Count];

            for (var i = 0; i < texts.Length; i++)
                texts[i] = (string)ToObject(items[i])!;

            return texts;
        }

        var values = new object?[items.Count];

        for (var i = 0; i < values.Length; i++)
            values[i] = ToObject(items[i]);

        return values;
    }

    private delegate T ElementReader<out T>(ReadOnlySpan<byte> data, int offset);

    private static T[] ToArray<T>(ReadOnlySpan<byte> data, int size, ElementReader<T> read)
    {
        var result = new T[data.Length / size];

        for (var i = 0; i < result.Length; i++)
            result[i] = read(data, i * size);

        return result;
    }

    private delegate void ElementWriter(Span<byte> destination);

    private delegate void IndexedElementWriter(Span<byte> destination, int index);

    private static MetadataValue Pack(MetadataValueType type, int size, ElementWriter write)
    {
        var data = new byte[size];

        write(data);

        return new MetadataValue(type, data);
    }

    private static MetadataValue PackArray(MetadataValueType type, int count, int size, IndexedElementWriter write)
    {
        var data = new byte[count * size];

        for (var i = 0; i < count; i++)
            write(data.AsSpan(i * size), i);

        return new MetadataValue(type, data);
    }
}
