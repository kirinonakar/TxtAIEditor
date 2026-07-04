using System.Collections.Generic;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Interfaces
{
    public interface IRecentFilesService
    {
        void LoadInto(List<RecentFileItem> recentFiles);
        void Save(IEnumerable<RecentFileItem> recentFiles);
        void Add(List<RecentFileItem> recentFiles, string filePath, bool isFolder);
        bool Remove(List<RecentFileItem> recentFiles, string path);
    }
}
