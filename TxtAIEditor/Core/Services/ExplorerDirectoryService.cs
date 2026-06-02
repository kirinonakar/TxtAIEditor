using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace TxtAIEditor.Core.Services
{
    public sealed class ExplorerDirectoryService
    {
        public IEnumerable<ExplorerItem> CreateDirectoryItems(string parentPath)
        {
            var items = new List<ExplorerItem>();
            try
            {
                var dirInfo = new DirectoryInfo(parentPath);
                var enumerationOptions = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false
                };

                foreach (var dir in dirInfo.EnumerateDirectories("*", enumerationOptions))
                {
                    if (dir.Attributes.HasFlag(FileAttributes.Hidden) || dir.Name.StartsWith("."))
                    {
                        continue;
                    }

                    items.Add(new ExplorerItem { Name = dir.Name, Path = dir.FullName, IsFolder = true });
                }

                foreach (var file in dirInfo.EnumerateFiles("*", enumerationOptions))
                {
                    if (file.Attributes.HasFlag(FileAttributes.Hidden))
                    {
                        continue;
                    }

                    items.Add(new ExplorerItem { Name = file.Name, Path = file.FullName, IsFolder = false });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed reading folder hierarchy: {ex.Message}");
            }

            return items;
        }
    }
}
