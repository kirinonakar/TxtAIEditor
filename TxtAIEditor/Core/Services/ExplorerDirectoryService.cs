using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace TxtAIEditor.Core.Services
{
    public sealed class ExplorerDirectoryService
    {
        public const string LocalRootPath = "txtaieditor://local-root";

        public IEnumerable<ExplorerItem> CreateDirectoryItems(string parentPath)
        {
            var items = new List<ExplorerItem>();
            try
            {
                if (parentPath == LocalRootPath)
                {
                    foreach (DriveInfo drive in DriveInfo.GetDrives())
                    {
                        items.Add(new ExplorerItem
                        {
                            Name = drive.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                            Path = drive.Name,
                            IsFolder = true
                        });
                    }

                    return items;
                }

                var dirInfo = new DirectoryInfo(parentPath);
                var enumerationOptions = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false
                };

                foreach (var dir in dirInfo.EnumerateDirectories("*", enumerationOptions))
                {
                    if (dir.Attributes.HasFlag(FileAttributes.Hidden))
                    {
                        continue;
                    }

                    items.Add(new ExplorerItem { Name = dir.Name, Path = dir.FullName, IsFolder = true, ModifiedTime = dir.LastWriteTime });
                }

                foreach (var file in dirInfo.EnumerateFiles("*", enumerationOptions))
                {
                    if (file.Attributes.HasFlag(FileAttributes.Hidden))
                    {
                        continue;
                    }

                    items.Add(new ExplorerItem { Name = file.Name, Path = file.FullName, IsFolder = false, ModifiedTime = file.LastWriteTime });
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
