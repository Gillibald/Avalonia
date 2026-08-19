using System;
using System.Buffers;
using System.Buffers.Binary;

namespace Avalonia.Media.Fonts.Tables.Glyf
{
    /// <summary>
    /// Represents a composite glyph that references one or more component glyphs.
    /// </summary>
    /// <remarks>This struct holds references to rented arrays that must be returned via Dispose.
    /// The struct should be disposed after consuming the component data.</remarks>
    internal readonly ref struct CompositeGlyph
    {
        // Most composite glyphs have fewer than 8 components
        private const int EstimatedComponentCount = 8;

        // Rented buffer for glyph components
        private readonly GlyphComponent[]? _rentedBuffer;

        /// <summary>
        /// Gets the span of glyph components that make up this composite glyph.
        /// </summary>
        public ReadOnlySpan<GlyphComponent> Components { get; }

        /// <summary>
        /// Gets the composite instruction stream that follows the last component. Empty when
        /// the last component's WE_HAVE_INSTRUCTIONS flag is clear, or when the declared
        /// stream overruns the glyph record (half a program must not execute, so a truncated
        /// stream reads as absent rather than clamped).
        /// </summary>
        public ReadOnlySpan<byte> Instructions { get; }

        /// <summary>
        /// Offset of the instruction stream's length field within the post-header component
        /// data, or -1 when no stream is present. Lets a caller holding the glyph's memory
        /// re-slice the stream with a stable lifetime, since <see cref="Instructions"/> is
        /// span-bound to this parse.
        /// </summary>
        public int InstructionsOffset { get; }

        /// <summary>
        /// Gets a value indicating whether any component is positioned by point matching
        /// (ARGS_ARE_XY_VALUES is clear) rather than by an x/y offset.
        /// </summary>
        /// <remarks>Computed while parsing the components so the caller does not have to make a
        /// second pass to decide which build path to use.</remarks>
        public bool UsesPointMatching { get; }

        /// <summary>
        /// Initializes a new instance of the CompositeGlyph class using the specified glyph components and an optional
        /// rented buffer.
        /// </summary>
        /// <remarks>The rented buffer, if supplied, is managed internally and should not be accessed or
        /// modified by the caller after construction. The CompositeGlyph instance does not take ownership of the
        /// buffer's lifetime.</remarks>
        /// <param name="components">A read-only span containing the glyph components that make up the composite glyph. The span must remain
        /// valid for the lifetime of the CompositeGlyph instance.</param>
        /// <param name="instructions">The composite instruction stream following the last component, or empty when absent.</param>
        /// <param name="instructionsOffset">The instruction stream's offset from the start of the component data, so callers holding the stable glyph memory can re-slice it.</param>
        /// <param name="usesPointMatching">Indicates whether any component is positioned by point matching rather than by an x/y offset.</param>
        /// <param name="rentedBuffer">An optional array used as a rented buffer for internal storage. If provided, the buffer may be used to
        /// optimize memory usage.</param>
        private CompositeGlyph(ReadOnlySpan<GlyphComponent> components, ReadOnlySpan<byte> instructions,
            int instructionsOffset, bool usesPointMatching, GlyphComponent[]? rentedBuffer)
        {
            Components = components;
            Instructions = instructions;
            InstructionsOffset = instructionsOffset;
            UsesPointMatching = usesPointMatching;
            _rentedBuffer = rentedBuffer;
        }

        /// <summary>
        /// Creates a CompositeGlyph from the raw glyph data.
        /// </summary>
        /// <param name="data">The raw glyph data from the glyf table.</param>
        /// <returns>A CompositeGlyph instance with components backed by a rented buffer.</returns>
        /// <remarks>The caller must call Dispose() to return the rented buffer to the pool.</remarks>
        public static CompositeGlyph Create(ReadOnlySpan<byte> data)
        {
            // Rent a buffer for components (most composite glyphs have < 8 components)
            var componentsBuffer = ArrayPool<GlyphComponent>.Shared.Rent(EstimatedComponentCount);
            int componentCount = 0;

            var usesPointMatching = false;

            try
            {
                int offset = 0;
                bool moreComponents;

                do
                {
                    // Read flags and glyph index
                    CompositeFlags flags = (CompositeFlags)BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
                    offset += 2;

                    // When ARGS_ARE_XY_VALUES is clear the component is placed by point matching.
                    if ((flags & CompositeFlags.ArgsAreXYValues) == 0)
                    {
                        usesPointMatching = true;
                    }

                    ushort glyphIndex = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
                    offset += 2;

                    short arg1 = 0, arg2 = 0;

                    // Read arguments
                    if ((flags & CompositeFlags.ArgsAreWords) != 0)
                    {
                        arg1 = BinaryPrimitives.ReadInt16BigEndian(data.Slice(offset, 2));
                        offset += 2;
                        arg2 = BinaryPrimitives.ReadInt16BigEndian(data.Slice(offset, 2));
                        offset += 2;
                    }
                    else
                    {
                        // Arguments are indices
                        arg1 = (sbyte)data[offset++];
                        arg2 = (sbyte)data[offset++];
                    }

                    // Optional transformation
                    float scale = 1.0f;
                    float scaleX = 1.0f, scaleY = 1.0f;
                    float scale01 = 0.0f, scale10 = 0.0f;

                    // Uniform scale
                    if ((flags & CompositeFlags.WeHaveAScale) != 0)
                    {
                        scale = BinaryPrimitives.ReadInt16BigEndian(data.Slice(offset, 2)) / 16384f;
                        offset += 2;
                    }
                    // Separate x and y scales
                    else if ((flags & CompositeFlags.WeHaveAnXAndYScale) != 0)
                    {
                        scaleX = BinaryPrimitives.ReadInt16BigEndian(data.Slice(offset, 2)) / 16384f;
                        offset += 2;
                        scaleY = BinaryPrimitives.ReadInt16BigEndian(data.Slice(offset, 2)) / 16384f;
                        offset += 2;
                    }
                    // Two by two transformation matrix
                    else if ((flags & CompositeFlags.WeHaveATwoByTwo) != 0)
                    {
                        scaleX = BinaryPrimitives.ReadInt16BigEndian(data.Slice(offset, 2)) / 16384f;
                        offset += 2;
                        scale01 = BinaryPrimitives.ReadInt16BigEndian(data.Slice(offset, 2)) / 16384f;
                        offset += 2;
                        scale10 = BinaryPrimitives.ReadInt16BigEndian(data.Slice(offset, 2)) / 16384f;
                        offset += 2;
                        scaleY = BinaryPrimitives.ReadInt16BigEndian(data.Slice(offset, 2)) / 16384f;
                        offset += 2;
                    }

                    // Grow buffer if needed
                    if (componentCount >= componentsBuffer.Length)
                    {
                        var oldBuffer = componentsBuffer;
                        var newSize = componentsBuffer.Length * 2;

                        componentsBuffer = ArrayPool<GlyphComponent>.Shared.Rent(newSize);

                        oldBuffer.AsSpan(0, componentCount).CopyTo(componentsBuffer);

                        ArrayPool<GlyphComponent>.Shared.Return(oldBuffer);
                    }

                    componentsBuffer[componentCount++] = new GlyphComponent
                    {
                        Flags = flags,
                        GlyphIndex = glyphIndex,
                        Arg1 = arg1,
                        Arg2 = arg2,
                        Scale = scale,
                        ScaleX = scaleX,
                        ScaleY = scaleY,
                        Scale01 = scale01,
                        Scale10 = scale10
                    };

                    moreComponents = (flags & CompositeFlags.MoreComponents) != 0;
                } while (moreComponents);

                // The instruction stream follows the last component when that component's
                // flags announce one (the spec puts the flag on the last record). A stream
                // whose declared length overruns the glyph record is malformed and reads as
                // absent: executing half a program would move points arbitrarily, while a
                // missing one merely renders unhinted.
                ReadOnlySpan<byte> instructions = default;
                var instructionsOffset = -1;

                if (componentCount > 0 &&
                    (componentsBuffer[componentCount - 1].Flags & CompositeFlags.WeHaveInstructions) != 0 &&
                    data.Length - offset >= 2)
                {
                    int instructionLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));

                    if (instructionLength <= data.Length - offset - 2)
                    {
                        instructions = data.Slice(offset + 2, instructionLength);
                        instructionsOffset = offset;
                    }
                }

                // Return a CompositeGlyph with the rented buffer
                // The caller is responsible for calling Dispose() to return the buffer
                return new CompositeGlyph(
                    componentsBuffer.AsSpan(0, componentCount),
                    instructions,
                    instructionsOffset,
                    usesPointMatching,
                    componentsBuffer
                );
            }
            catch
            {
                // On exception, return the buffer immediately
                ArrayPool<GlyphComponent>.Shared.Return(componentsBuffer);
                throw;
            }
        }

        /// <summary>
        /// Returns the rented buffer to the ArrayPool.
        /// </summary>
        /// <remarks>This method should be called when the CompositeGlyph is no longer needed
        /// to ensure the rented buffer is returned to the pool.</remarks>
        public void Dispose()
        {
            if (_rentedBuffer != null)
            {
                ArrayPool<GlyphComponent>.Shared.Return(_rentedBuffer);
            }
        }
    }
}
