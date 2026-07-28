using System;

namespace Avalonia.Input.TextInput
{
    public abstract class TextInputMethodClient
    {
        /// <summary>
        /// Fires when the text view visual has changed
        /// </summary>
        public event EventHandler? TextViewVisualChanged;

        /// <summary>
        /// Fires when the cursor rectangle has changed
        /// </summary>
        public event EventHandler? CursorRectangleChanged;

        /// <summary>
        /// Fires when the surrounding text has changed
        /// </summary>
        public event EventHandler? SurroundingTextChanged;

        /// <summary>
        /// Fires when the selection has changed
        /// </summary>
        public event EventHandler? SelectionChanged;
        
        /// <summary>
        /// Fires when client wants to reset IME state
        /// </summary>
        public event EventHandler? ResetRequested;
        
        /// <summary>
        /// Fires when client requests the input panel be opened.
        /// </summary>
        public event EventHandler? InputPaneActivationRequested;

        /// <summary>
        /// The visual that's showing the text
        /// </summary>
        public abstract Visual TextViewVisual { get; }

        /// <summary>
        /// Indicates if TextViewVisual is capable of displaying non-committed input on the cursor position
        /// </summary>
        public abstract bool SupportsPreedit { get; }

        /// <summary>
        /// Indicates if text input client is capable of providing the text around the cursor
        /// </summary>
        public abstract bool SupportsSurroundingText { get; }

        /// <summary>
        /// Returns the text around the cursor, usually the current paragraph
        /// </summary>
        public abstract string SurroundingText { get; }

        /// <summary>
        /// Gets the cursor rectangle relative to the TextViewVisual
        /// </summary>
        public abstract Rect CursorRectangle { get; }

        /// <summary>
        /// Gets or sets the curent selection range within current surrounding text.
        /// </summary>
        public abstract TextSelection Selection { get; set; }

        /// <summary>
        /// Sets the non-committed input string
        /// </summary>
        public virtual void SetPreeditText(string? preeditText) { }

        /// <summary>
        /// Execute specific context menu actions
        /// </summary>
        /// <param name="action">The <see cref="ContextMenuAction"/> to perform</param>
        public virtual void ExecuteContextMenuAction(ContextMenuAction action) { }

        /// <summary>
        /// Sets the non-committed input string and cursor offset in that string
        /// </summary>
        public virtual void SetPreeditText(string? preeditText, int? cursorPos)
        {
            SetPreeditText(preeditText);
        }

        /// <summary>
        /// Returns the index of the character nearest to <paramref name="point"/>, or -1 when the
        /// client has no text view. The point is expressed in <see cref="TextViewVisual"/>
        /// coordinates. Used by IMEs to hit-test pointer events against the active composition
        /// (e.g. to move the caret inside it).
        /// </summary>
        /// <remarks>
        /// The index is into the laid-out text, which includes any active preedit inserted at the
        /// caret. It therefore agrees with <see cref="SurroundingText"/> indices up to the start of
        /// the composition, and runs ahead of them by the preedit length after it; callers
        /// comparing against a composition range must work in this space. The nearest index is
        /// returned rather than requiring strict containment, matching the semantics of
        /// NSTextInputClient's characterIndexForPoint:.
        /// </remarks>
        public virtual int GetCharacterIndexFromPoint(Point point) => -1;

        /// <summary>
        /// Returns the bounding rectangle, in <see cref="TextViewVisual"/> coordinates, of the given
        /// character range, or null when geometry isn't available. The range is in the same index
        /// space as <see cref="GetCharacterIndexFromPoint"/>.
        /// </summary>
        public virtual Rect? GetTextRectForRange(int start, int end) => null;
        
        protected virtual void RaiseTextViewVisualChanged()
        {
            TextViewVisualChanged?.Invoke(this, EventArgs.Empty);
        }

        protected virtual void RaiseCursorRectangleChanged()
        {
            CursorRectangleChanged?.Invoke(this, EventArgs.Empty);
        }

        protected virtual void RaiseSurroundingTextChanged()
        {
            SurroundingTextChanged?.Invoke(this, EventArgs.Empty);
        }

        protected virtual void RaiseSelectionChanged()
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        protected virtual void RaiseInputPaneActivationRequested()
        {
            InputPaneActivationRequested?.Invoke(this, EventArgs.Empty);
        }
        
        protected virtual void RequestReset()
        {
            ResetRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public record struct TextSelection(int Start, int End);

    public enum ContextMenuAction
    {
        Copy,
        Cut,
        Paste,
        SelectAll
    }
}
