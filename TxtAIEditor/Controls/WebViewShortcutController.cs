using System;

namespace TxtAIEditor.Controls
{
    public sealed class WebViewShortcutController
    {
        private readonly Action _find;
        private readonly Action _toggleLivePreview;
        private readonly Action _toggleTopMost;
        private readonly Action _toggleTheme;
        private readonly Action _toggleMaximize;
        private readonly Action _toggleStickyNote;
        private readonly Action _print;
        private readonly Action _togglePreviewWidth;

        public WebViewShortcutController(
            Action find,
            Action toggleLivePreview,
            Action toggleTopMost,
            Action toggleTheme,
            Action toggleMaximize,
            Action toggleStickyNote,
            Action print,
            Action togglePreviewWidth)
        {
            _find = find;
            _toggleLivePreview = toggleLivePreview;
            _toggleTopMost = toggleTopMost;
            _toggleTheme = toggleTheme;
            _toggleMaximize = toggleMaximize;
            _toggleStickyNote = toggleStickyNote;
            _print = print;
            _togglePreviewWidth = togglePreviewWidth;
        }

        public void Handle(string name)
        {
            switch (name)
            {
                case "find":
                    _find();
                    break;
                case "f4":
                    _toggleLivePreview();
                    break;
                case "f9":
                    _toggleTopMost();
                    break;
                case "f10":
                    _toggleTheme();
                    break;
                case "f11":
                    _toggleMaximize();
                    break;
                case "f12":
                    _toggleStickyNote();
                    break;
                case "print":
                    _print();
                    break;
                case "expandRightPanel":
                    _togglePreviewWidth();
                    break;
            }
        }
    }
}
