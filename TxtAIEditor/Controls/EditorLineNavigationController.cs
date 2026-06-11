using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Editor;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Controls
{
    public sealed class EditorLineNavigationController
    {
        private readonly MainWindowViewModel _viewModel;
        private readonly Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> _tabBridges;

        public EditorLineNavigationController(
            MainWindowViewModel viewModel,
            Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges)
        {
            _viewModel = viewModel;
            _tabBridges = tabBridges;
        }

        public Task RevealFileLineAsync(
            string filePath,
            int lineNumber,
            int indexOfMatch = 0,
            int matchLength = 0,
            string query = "")
        {
            string? targetTabId = _viewModel.Tabs
                .FirstOrDefault(tab => string.Equals(tab.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                ?.Id;

            return targetTabId == null
                ? Task.CompletedTask
                : RevealTabLineAsync(targetTabId, lineNumber, indexOfMatch, matchLength, query);
        }

        public async Task RevealTabLineAsync(
            string tabId,
            int lineNumber,
            int indexOfMatch = 0,
            int matchLength = 0,
            string query = "")
        {
            if (!_tabBridges.TryGetValue(tabId, out var bridgeGroup))
            {
                return;
            }

            if (bridgeGroup.Bridge != null)
            {
                await bridgeGroup.Bridge.RevealLineAsync(lineNumber, indexOfMatch, matchLength, query);
                return;
            }

            if (bridgeGroup.WebView?.CoreWebView2 != null)
            {
                var message = new
                {
                    action = "revealLine",
                    lineNumber,
                    indexOfMatch,
                    matchLength,
                    query
                };
                bridgeGroup.WebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
            }
        }
    }
}
