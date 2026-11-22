using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace TxtAIEditor.Controls
{
    internal sealed class ExplorerItemSorter
    {
        internal enum SortMode
        {
            Name,
            NameReverse,
            Newest,
            Oldest,
            Size,
            SizeReverse,
            Extension
        }

        public SortMode Mode { get; private set; } = SortMode.Name;

        public void SetMode(SortMode mode) => Mode = mode;

        public void CycleMode()
        {
            Mode = Mode switch
            {
                SortMode.Name => SortMode.Newest,
                SortMode.Newest => SortMode.Oldest,
                SortMode.Oldest => SortMode.Name,
                _ => SortMode.Name
            };
        }

        public IEnumerable<ExplorerItem> Sort(
            IEnumerable<ExplorerItem> items,
            SortMode? sortMode = null)
        {
            var folders = new List<ExplorerItem>();
            var files = new List<ExplorerItem>();

            foreach (ExplorerItem item in items)
            {
                if (item.IsFolder)
                {
                    folders.Add(item);
                }
                else
                {
                    files.Add(item);
                }
            }

            switch (sortMode ?? Mode)
            {
                case SortMode.Name:
                    folders.Sort((left, right) => StrCmpLogicalW(left.Name, right.Name));
                    files.Sort((left, right) => StrCmpLogicalW(left.Name, right.Name));
                    break;
                case SortMode.NameReverse:
                    folders.Sort((left, right) => StrCmpLogicalW(right.Name, left.Name));
                    files.Sort((left, right) => StrCmpLogicalW(right.Name, left.Name));
                    break;
                case SortMode.Newest:
                    folders.Sort((left, right) => right.ModifiedTime.CompareTo(left.ModifiedTime));
                    files.Sort((left, right) => right.ModifiedTime.CompareTo(left.ModifiedTime));
                    break;
                case SortMode.Oldest:
                    folders.Sort((left, right) => left.ModifiedTime.CompareTo(right.ModifiedTime));
                    files.Sort((left, right) => left.ModifiedTime.CompareTo(right.ModifiedTime));
                    break;
                case SortMode.Size:
                    folders.Sort((left, right) => StrCmpLogicalW(left.Name, right.Name));
                    files.Sort((left, right) => CompareFileSize(left, right, descending: false));
                    break;
                case SortMode.SizeReverse:
                    folders.Sort((left, right) => StrCmpLogicalW(left.Name, right.Name));
                    files.Sort((left, right) => CompareFileSize(left, right, descending: true));
                    break;
                case SortMode.Extension:
                    folders.Sort((left, right) => StrCmpLogicalW(left.Name, right.Name));
                    files.Sort(CompareExtension);
                    break;
            }

            var sorted = new List<ExplorerItem>(folders.Count + files.Count);
            sorted.AddRange(folders);
            sorted.AddRange(files);
            return sorted;
        }

        private static int CompareFileSize(ExplorerItem left, ExplorerItem right, bool descending)
        {
            int result = (left.FileSize ?? 0).CompareTo(right.FileSize ?? 0);
            if (result == 0)
            {
                return StrCmpLogicalW(left.Name, right.Name);
            }

            return descending ? -result : result;
        }

        private static int CompareExtension(ExplorerItem left, ExplorerItem right)
        {
            int result = StrCmpLogicalW(Path.GetExtension(left.Name), Path.GetExtension(right.Name));
            return result != 0 ? result : StrCmpLogicalW(left.Name, right.Name);
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int StrCmpLogicalW(string x, string y);
    }
}
