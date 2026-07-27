namespace TxtAIEditor.Core.Models
{
    public enum TabContentKind
    {
        Text,
        Image,
        Media,
        Pdf,
        OfficeDocument,
        ExtractedDocumentText,
        Notebook,
        Hex,
        CsvTable
    }

    public abstract record DocumentOrigin;

    public sealed record UntitledOrigin : DocumentOrigin
    {
        public static UntitledOrigin Instance { get; } = new();

        private UntitledOrigin()
        {
        }
    }

    public sealed record LocalFileOrigin(string Path) : DocumentOrigin;

    public sealed record RemoteFileOrigin(
        string RemotePath,
        string? CachePath) : DocumentOrigin;

    public sealed record ArchiveEntryOrigin(
        string ArchivePath,
        string EntryPath) : DocumentOrigin;
}
