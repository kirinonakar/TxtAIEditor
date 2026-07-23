using System;

namespace TxtAIEditor.Core.Models
{
    public enum RemoteServerType
    {
        Ssh,
        Sftp,
        Ftps,
        WebDav
    }

    public sealed class RemoteServerProfile
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public RemoteServerType ServerType { get; set; } = RemoteServerType.Sftp;
        public int Port { get; set; } = 22;
        public string UserName { get; set; } = string.Empty;

        public string ProtocolLabel => ServerType switch
        {
            RemoteServerType.Ssh => "SSH",
            RemoteServerType.Sftp => "SFTP",
            RemoteServerType.Ftps => "FTPS",
            RemoteServerType.WebDav => "WebDAV",
            _ => ServerType.ToString()
        };

        public string Summary => $"{ProtocolLabel} · {UserName} · :{Port}";
    }

    public sealed class RemoteDirectoryEntry
    {
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public DateTimeOffset? ModifiedTime { get; set; }

        public string IconGlyph => IsDirectory ? "\uE8B7" : "\uE7C3";
        public string Detail => IsDirectory
            ? string.Empty
            : FormatSize(Size);

        private static string FormatSize(long size)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = Math.Max(0, size);
            int unitIndex = 0;
            while (value >= 1024 && unitIndex < units.Length - 1)
            {
                value /= 1024;
                unitIndex++;
            }

            return unitIndex == 0 ? $"{value:0} {units[unitIndex]}" : $"{value:0.#} {units[unitIndex]}";
        }
    }

    public sealed class RemoteConnectionSettings
    {
        public required RemoteServerProfile Profile { get; init; }
        public required string Address { get; init; }
        public required string Password { get; init; }
    }

    public sealed class RemoteFileOpenedEventArgs : EventArgs
    {
        public RemoteFileOpenedEventArgs(string localPath)
        {
            LocalPath = localPath;
        }

        public string LocalPath { get; }
    }
}
