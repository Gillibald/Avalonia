// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

//
// Description: 
//

namespace Avalonia.Documents
{
    /// <summary>
    /// Specifies the changes applied to TextContainer content.
    /// </summary>
    public class TextChange
    {
        //------------------------------------------------------
        //
        //  Constructors
        //
        //------------------------------------------------------

        #region Constructors

        internal TextChange()
        {
        }

        #endregion Constructors

        //------------------------------------------------------
        //
        //  Public Members
        //
        //------------------------------------------------------

        #region Public Members

        /// <summary>
        /// 0-based character offset for this change
        /// </summary>
        public int Offset { get; internal set; }

        /// <summary>
        /// Number of characters added
        /// </summary>
        public int AddedLength { get; internal set; }

        /// <summary>
        /// Number of characters removed
        /// </summary>
        public int RemovedLength { get; internal set; }

        #endregion Public Members
    }
}
