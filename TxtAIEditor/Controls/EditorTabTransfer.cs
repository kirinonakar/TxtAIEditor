using TxtAIEditor.Core.Models;
using TxtAIEditor.Editor;

namespace TxtAIEditor.Controls
{
    public sealed class EditorTabTransfer
    {
        public EditorTabTransfer(
            OpenedTab tab,
            EditorDocumentSession? session,
            object? viewModeState)
        {
            Tab = tab;
            Session = session;
            ViewModeState = viewModeState;
        }

        public OpenedTab Tab { get; }
        public EditorDocumentSession? Session { get; }
        internal object? ViewModeState { get; }
    }
}
