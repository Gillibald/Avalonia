// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

//
// Description: The arguments sent when a Change event is fired in a TextContainer.
//

using System;

namespace Avalonia.Documents
{
    /// <summary>
    ///  The TextContainerChangeEventArgs defines the event arguments sent when a 
    ///  TextContainer is changed.
    /// </summary>
    internal class TextContainerChangeEventArgs : EventArgs
    {
        //------------------------------------------------------
        //
        //  Constructors
        //
        //------------------------------------------------------

        #region Constructors

        internal TextContainerChangeEventArgs(ITextPointer textPosition, int count, int charCount, TextChangeType textChange) :
            this(textPosition, count, charCount, textChange, null, false)
        {
        }

        internal TextContainerChangeEventArgs(ITextPointer textPosition, int count, int charCount, TextChangeType textChange, AvaloniaProperty property, bool affectsRenderOnly)
        {
            ITextPosition = textPosition.GetFrozenPointer(LogicalDirection.Forward);
            _count = count;
            IMECharCount = charCount;
            _textChange = textChange;
            _property = property;
            AffectsRenderOnly = affectsRenderOnly;
        }

        #endregion Constructors

        //------------------------------------------------------
        //
        //  Internal Properties
        //
        //------------------------------------------------------

        #region Internal Properties

        // Position of the segment start, expressed as an ITextPointer.
        internal ITextPointer ITextPosition { get; }

        // Number of chars covered by this segment.
        internal int IMECharCount { get; }

        internal bool AffectsRenderOnly { get; }

        /// <summary>
        /// 
        /// </summary>
        internal int Count
        {
            get
            {
                return _count;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        internal TextChangeType TextChange
        {
            get
            {
                return _textChange;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        internal AvaloniaProperty Property
        {
            get
            {
                return _property;
            }
        }

        #endregion Internal Properties

        //------------------------------------------------------
        //
        //  Private Fields
        //
        //------------------------------------------------------

        #region Private Fields

        // Position of the segment start, expressed as an ITextPointer.

        // Number of symbols covered by this segment.
        private readonly int _count;

        // Type of change.
        private readonly TextChangeType _textChange;

        private readonly AvaloniaProperty _property;

        #endregion Private Fields
    }
}
