using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Editor;

namespace TxtAIEditor.Composition
{
    public sealed class MainWindowState
    {
        public string CurrentFolderPath { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        public string CurrentRepoPath { get; set; } = string.Empty;
        public bool ScrollSyncEnabled { get; set; } = true;
        public Dictionary<string, (WebView2 WebView, CustomEditorBridge Bridge)> TabBridges { get; } =
            new Dictionary<string, (WebView2 WebView, CustomEditorBridge Bridge)>();
        public Dictionary<string, EditorDocumentSession> EditorSessions { get; } =
            new Dictionary<string, EditorDocumentSession>();
    }
}
