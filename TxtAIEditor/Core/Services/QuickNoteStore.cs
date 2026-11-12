using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TxtAIEditor.Core.Services
{
    internal sealed class QuickNote
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public string Title { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string? Background { get; set; }
        public string? Foreground { get; set; }
        public int Opacity { get; set; } = 100;
        public bool IsCollapsed { get; set; }
        public bool IsPreview { get; set; }
        public int? X { get; set; }
        public int? Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    internal sealed class QuickNoteStore(string path)
    {
        public List<QuickNote> Load()
        {
            try
            {
                using var file = File.OpenRead(path);
                var notes = JsonSerializer.Deserialize<List<QuickNote>>(file)
                    ?? throw new JsonException("Expected a list of quick notes.");
                var ids = new HashSet<Guid>();
                foreach (QuickNote note in notes)
                {
                    if (note == null || note.Id == Guid.Empty || !ids.Add(note.Id) ||
                        note.Title == null || note.Text == null)
                    {
                        throw new JsonException("Invalid quick note entry.");
                    }
                }
                return notes;
            }
            catch (FileNotFoundException)
            {
                return new List<QuickNote>();
            }
            catch (DirectoryNotFoundException)
            {
                return new List<QuickNote>();
            }
        }

        public void Save(IReadOnlyList<QuickNote> notes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string pendingPath = path + ".tmp";
            using (var file = File.Create(pendingPath))
            {
                JsonSerializer.Serialize(file, notes);
                file.Flush(flushToDisk: true);
            }
            File.Move(pendingPath, path, overwrite: true);
        }
    }
}
