using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentSessionSpill<T> : IDisposable where T : class
    {
        private readonly FileStream _file;

        private AgentSessionSpill(FileStream file) => _file = file;

        public static AgentSessionSpill<T> Write(T data)
        {
            string path = Path.Combine(Path.GetTempPath(), $"TxtAIEditor-agent-{Guid.NewGuid():N}.tmp");
            var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 4096, FileOptions.DeleteOnClose);
            try
            {
                using (var compressed = new GZipStream(file, CompressionLevel.Fastest, leaveOpen: true))
                {
                    JsonSerializer.Serialize(compressed, data);
                }
                return new AgentSessionSpill<T>(file);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        public T Read()
        {
            _file.Position = 0;
            using var compressed = new GZipStream(_file, CompressionMode.Decompress, leaveOpen: true);
            return JsonSerializer.Deserialize<T>(compressed) ?? throw new InvalidDataException(_file.Name);
        }

        public void Dispose() => _file.Dispose();
    }
}
