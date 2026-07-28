using System;
using Avalonia.Input.TextInput;
using Avalonia.Native.Interop;

namespace Avalonia.Native
{
    internal class AvaloniaNativeTextInputMethod : ITextInputMethodImpl, IDisposable
    {
        private TextInputMethodClient? _client;
        private IAvnTextInputMethodClient? _nativeClient;
        private readonly IAvnTextInputMethod _inputMethod;
        
        public AvaloniaNativeTextInputMethod(IAvnTopLevel topLevel)
        {
            _inputMethod = topLevel.InputMethod;
        }

        public void Dispose()
        {
            _inputMethod.Dispose();
            _nativeClient?.Dispose();
        }

        public void Reset()
        {
            _inputMethod.Reset();
        }

        public void SetClient(TextInputMethodClient? client)
        {
            if (_client is { SupportsSurroundingText: true })
            {
                _client.SurroundingTextChanged -= OnSurroundingTextChanged;
                _client.CursorRectangleChanged -= OnCursorRectangleChanged;
                _client.SelectionChanged -= OnSelectionChanged;

                _nativeClient?.Dispose();
            }

            _nativeClient = null;
            _client = client;

            if (_client != null)
            {
                _nativeClient = new AvnTextInputMethodClient(_client);

                OnSurroundingTextChanged(this, EventArgs.Empty);
                OnCursorRectangleChanged(this, EventArgs.Empty);
                // Note: OnSelectionChanged isn't called, it's already up-to-date thanks to OnSurroundingTextChanged

                _client.SurroundingTextChanged += OnSurroundingTextChanged;
                _client.CursorRectangleChanged += OnCursorRectangleChanged;
                _client.SelectionChanged += OnSelectionChanged;
            }

            _inputMethod.SetClient(_nativeClient);
        }

        private void OnCursorRectangleChanged(object? sender, EventArgs e)
        {
            if (_client == null)
            {
                return;
            }

            var textViewVisual = _client.TextViewVisual;

            if(textViewVisual is null )
            {
                return;
            }

            var visualRoot = textViewVisual.VisualRoot;

            if(visualRoot is null)
            {
                return;
            }

            var transform = textViewVisual.TransformToVisual((Visual)visualRoot);

            if (transform == null)
            {
                return;
            }

            var rect = _client.CursorRectangle.TransformToAABB(transform.Value);         

            _inputMethod.SetCursorRect(rect.ToAvnRect());
        }

        private void OnSurroundingTextChanged(object? sender, EventArgs e)
        {
            if (_client == null)
            {
                return;
            }

            var surroundingText = _client.SurroundingText;
            var selection = _client.Selection;

            _inputMethod.SetSurroundingText(
                surroundingText ?? "",
                selection.Start,
                selection.End
            );
        }

        private void OnSelectionChanged(object? sender, EventArgs e)
        {
            if (_client is null)
            {
                return;
            }

            var selection = _client.Selection;
            _inputMethod.SetSelectionInSurroundingText(selection.Start, selection.End);
        }

        public void SetCursorRect(Rect rect)
        {
            _inputMethod.SetCursorRect(rect.ToAvnRect());
        }

        public void SetOptions(TextInputOptions options)
        {
           
        }

        private class AvnTextInputMethodClient : NativeCallbackBase, IAvnTextInputMethodClient
        {
            private readonly TextInputMethodClient _client;

            public AvnTextInputMethodClient(TextInputMethodClient client)
            {
                _client = client;
            }

            public void SetPreeditText(string preeditText, int cursorPos)
            {
                if (_client.SupportsPreedit)
                {
                    _client.SetPreeditText(preeditText, cursorPos < 0 ? (int?)null : cursorPos);
                }
            }

            public void SelectInSurroundingText(int start, int end)
            {
                if (_client.SupportsSurroundingText)
                {
                    _client.Selection = new TextSelection(start, end);
                }
            }

            public int GetCharacterIndexFromPoint(AvnPoint point)
            {
                var visual = _client.TextViewVisual;

                if (visual?.VisualRoot is not Visual root)
                {
                    return -1;
                }

                // point arrives in the top level (root) coordinate space; map it to the text visual.
                var transform = root.TransformToVisual(visual);

                if (transform == null)
                {
                    return -1;
                }

                var localPoint = point.ToAvaloniaPoint().Transform(transform.Value);

                return _client.GetCharacterIndexFromPoint(localPoint);
            }

            public unsafe void GetTextRectForRange(int start, int end, AvnRect* rect)
            {
                var local = _client.GetTextRectForRange(start, end);
                var visual = _client.TextViewVisual;

                if (local is null || visual?.VisualRoot is not Visual root)
                {
                    *rect = default;
                    return;
                }

                var transform = visual.TransformToVisual(root);

                if (transform == null)
                {
                    *rect = default;
                    return;
                }

                *rect = local.Value.TransformToAABB(transform.Value).ToAvnRect();
            }
        }
    }
}
