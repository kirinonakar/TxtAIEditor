using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TxtAIEditor.Controls
{
    internal sealed class ExplorerItemSorter
    {
        internal enum SortMode
        {
            Name,
            Newest,
            Oldest
        }

        public SortMode Mode { get; private set; } = SortMode.Name;

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
                case SortMode.Newest:
                    folders.Sort((left, right) => right.ModifiedTime.CompareTo(left.ModifiedTime));
                    files.Sort((left, right) => right.ModifiedTime.CompareTo(left.ModifiedTime));
                    break;
                case SortMode.Oldest:
                    folders.Sort((left, right) => left.ModifiedTime.CompareTo(right.ModifiedTime));
                    files.Sort((left, right) => left.ModifiedTime.CompareTo(right.ModifiedTime));
                    break;
            }

            var sorted = new List<ExplorerItem>(folders.Count + files.Count);
            sorted.AddRange(folders);
            sorted.AddRange(files);
            return sorted;
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int StrCmpLogicalW(string x, string y);
    }
}
