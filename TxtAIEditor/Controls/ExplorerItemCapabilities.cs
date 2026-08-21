using System.IO;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;

namespace TxtAIEditor.Controls
{
    internal static class ExplorerItemCapabilities
    {
        public static bool CanUseLocalStorage(ExplorerItem item)
        {
            return !item.IsRemote &&
                   !item.IsArchiveEntry &&
                   !string.IsNullOrWhiteSpace(item.Path) &&
                   (item.IsFolder ? Directory.Exists(item.Path) : File.Exists(item.Path));
        }

        public static bool IsRemote(ExplorerItem? item)
        {
            return item != null && item.IsRemote && RemotePath.IsRemote(item.Path);
        }

        public static bool CanDelete(ExplorerItem item)
        {
            return !item.IsArchiveEntry &&
                   !string.IsNullOrWhiteSpace(item.Path) &&
                   (item.IsRemote || (item.IsFolder ? Directory.Exists(item.Path) : File.Exists(item.Path)));
        }

        public static bool CanCompress(ExplorerItem item)
        {
            return CanUseLocalStorage(item);
        }

        public static bool CanConvertImage(ExplorerItem item)
        {
            return !item.IsRemote &&
                   !item.IsArchiveEntry &&
                   !item.IsFolder &&
                   !string.IsNullOrWhiteSpace(item.Path) &&
                   File.Exists(item.Path) &&
                   IsSupportedImage(item.Path);
        }

        public static bool CanOpenFile(ExplorerItem? item)
        {
            return item != null &&
                   !item.IsFolder &&
                   !item.IsArchiveEntry &&
                   !string.IsNullOrWhiteSpace(item.Path) &&
                   (item.IsRemote || File.Exists(item.Path));
        }

        public static bool IsSupportedArchive(ExplorerItem? item)
        {
            return item != null &&
                   !item.IsFolder &&
                   !item.IsArchiveEntry &&
                   !string.IsNullOrWhiteSpace(item.Path) &&
                   File.Exists(item.Path) &&
                   ArchiveExplorerService.IsSupportedArchivePath(item.Path);
        }

        public static string GetArchiveExtractFolderName(string archivePath)
        {
            string folderName = Path.GetFileNameWithoutExtension(archivePath);
            return string.IsNullOrWhiteSpace(folderName)
                ? "archive"
                : folderName;
        }

        public static bool IsSupportedImage(string filePath)
        {
            return SupportedFileTypes.IsImageFile(filePath);
        }
    }
}
