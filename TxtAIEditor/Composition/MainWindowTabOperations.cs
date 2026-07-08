using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Controls;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Composition
{
    internal static class MainWindowTabOperations
    {
        public static async Task ReloadAsync(
            OpenedTab tab,
            TabViewItem tabItem,
            StatusBarController statusBar,
            PdfViewerController pdfViewer,
            OfficeDocumentViewerController officeDocumentViewer,
            TabReloadController tabReload,
            Action<OpenedTab> updateLanguageUi,
            Action updateWindowTitle)
        {
            if (tab.IsImageViewer)
            {
                await EditorTabViewItemFactory.ReloadImageAsync(tabItem, tab.FilePath);
                UpdateViewerStatus(tab, statusBar, updateLanguageUi, updateWindowTitle);
                return;
            }

            if (tab.IsMediaViewer)
            {
                await EditorTabViewItemFactory.ReloadMediaAsync(tabItem, tab.FilePath);
                UpdateViewerStatus(tab, statusBar, updateLanguageUi, updateWindowTitle);
                return;
            }

            if (pdfViewer.Reload(tab) || officeDocumentViewer.Reload(tab))
            {
                UpdateViewerStatus(tab, statusBar, updateLanguageUi, updateWindowTitle);
                return;
            }

            await tabReload.ReloadFromDiskAsync(tab);
        }

        private static void UpdateViewerStatus(
            OpenedTab tab,
            StatusBarController statusBar,
            Action<OpenedTab> updateLanguageUi,
            Action updateWindowTitle)
        {
            statusBar.UpdateFileStats(tab);
            statusBar.UpdateTotalLines(tab);
            updateLanguageUi(tab);
            updateWindowTitle();
        }
    }
}
