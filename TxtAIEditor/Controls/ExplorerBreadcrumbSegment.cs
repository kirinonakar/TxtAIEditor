namespace TxtAIEditor.Controls
{
    public sealed class ExplorerBreadcrumbSegment
    {
        public ExplorerBreadcrumbSegment(
            string name,
            string path,
            bool isArchive = false,
            string archivePath = "",
            string entryDirectory = "")
        {
            Name = name;
            Path = path;
            IsArchive = isArchive;
            ArchivePath = archivePath;
            EntryDirectory = entryDirectory;
        }

        public string Name { get; }
        public string Path { get; }
        public bool IsArchive { get; }
        public string ArchivePath { get; }
        public string EntryDirectory { get; }
    }
}
