using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TxtAIEditor.Controls
{
    internal sealed record AgentHistoryEntry(string Id, DateTime Timestamp, string Title, AgentSessionRole Role,
        string? OrchestratorSessionId, string WorkspaceRoot, Guid TranscriptId, Guid? EditsId,
        AgentSessionSelections? Selections)
    {
        [JsonIgnore]
        public string GroupId => Role == AgentSessionRole.Subagent && !string.IsNullOrWhiteSpace(OrchestratorSessionId)
            ? OrchestratorSessionId
            : Id;
    }

    internal sealed class AgentHistoryStore(string settingsDirectory)
    {
        private sealed record HistoryIndex(List<AgentHistoryEntry> Entries);
        private sealed record Transcript(string SessionHistoryText, string ModelSessionHistoryText,
            string LastAnswerText, double SessionHistoryTokenCount);

        public sealed class OpenHistorySession(AgentHistoryEntry entry, Stream transcript, Stream? edits) : IDisposable
        {
            public AgentHistoryEntry Entry { get; } = entry;

            public AgentHistoryItem ReadSession(List<AgentFileEditPreview> sessionEdits)
            {
                transcript.Position = 0;
                var body = JsonSerializer.Deserialize<Transcript>(transcript) ?? throw new InvalidDataException(Entry.Id);
                return new AgentHistoryItem
                {
                    Id = Entry.Id,
                    Timestamp = Entry.Timestamp,
                    Title = Entry.Title,
                    Role = Entry.Role,
                    Selections = Entry.Selections?.Copy(),
                    OrchestratorSessionId = Entry.OrchestratorSessionId,
                    WorkspaceRoot = Entry.WorkspaceRoot,
                    SessionHistoryText = body.SessionHistoryText,
                    ModelSessionHistoryText = body.ModelSessionHistoryText,
                    LastAnswerText = body.LastAnswerText,
                    SessionHistoryTokenCount = body.SessionHistoryTokenCount,
                    SessionEdits = sessionEdits
                };
            }

            public List<AgentFileEditPreview> ReadEdits()
            {
                if (edits == null)
                {
                    return new();
                }
                edits.Position = 0;
                return JsonSerializer.Deserialize<List<AgentFileEditPreview>>(edits) ?? throw new InvalidDataException(Entry.Id);
            }

            public void Dispose()
            {
                transcript.Dispose();
                edits?.Dispose();
            }
        }

        public const int MaxGroups = 30;
        public static AgentHistoryStore Shared { get; } = new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".TxtAIEditor"));

        private readonly string _indexPath = Path.Combine(settingsDirectory, "agent-history.json");
        private readonly string _itemsDirectory = Path.Combine(settingsDirectory, "agent-history-items");
        private readonly object _gate = new();
        private volatile IReadOnlyList<AgentHistoryEntry> _entries = Array.Empty<AgentHistoryEntry>();
        private bool _loaded;

        public IReadOnlyList<AgentHistoryEntry> Entries => _entries;

        public static IEnumerable<IGrouping<string, AgentHistoryEntry>> GroupEntries(IEnumerable<AgentHistoryEntry> entries) =>
            entries.OrderByDescending(entry => entry.Timestamp).GroupBy(entry => entry.GroupId, StringComparer.Ordinal);

        public void Load()
        {
            lock (_gate)
            {
                EnsureLoaded();
            }
        }

        private void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }

            if (File.Exists(_indexPath))
            {
                if (HasLegacyHistory())
                {
                    MigrateLegacyHistoryAsync().GetAwaiter().GetResult();
                }
                else
                {
                    var entries = Read<HistoryIndex>(_indexPath).Entries ?? throw new InvalidDataException(_indexPath);
                    _entries = entries;
                    if (GroupEntries(entries).Skip(MaxGroups).Any())
                    {
                        Commit(entries, new());
                    }
                }
            }
            _loaded = true;
        }

        public AgentHistoryItem? ReadSession(string id)
        {
            using var session = OpenSession(id);
            return session?.ReadSession(session.ReadEdits());
        }

        public OpenHistorySession? OpenSession(string id)
        {
            lock (_gate)
            {
                var entry = _entries.FirstOrDefault(item => item.Id == id);
                if (entry == null)
                {
                    return null;
                }

                Stream transcript = OpenItem(entry.TranscriptId);
                try
                {
                    return new OpenHistorySession(entry, transcript, entry.EditsId is Guid edits ? OpenItem(edits) : null);
                }
                catch
                {
                    transcript.Dispose();
                    throw;
                }
            }
        }

        // A session already opened from history can still read its body after the history is trimmed or deleted.
        private FileStream OpenItem(Guid id) => new(GetItemPath(id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

        public List<AgentFileEditPreview> ReadSessionEdits(string id)
        {
            lock (_gate)
            {
                return ReadEdits(_entries.FirstOrDefault(item => item.Id == id));
            }
        }

        private List<AgentFileEditPreview> ReadEdits(AgentHistoryEntry? entry) =>
            entry?.EditsId is Guid id ? Read<List<AgentFileEditPreview>>(GetItemPath(id)) : new();

        public void SaveSession(AgentHistoryItem item)
        {
            lock (_gate)
            {
                EnsureLoaded();
                var staged = new List<Guid>();
                try
                {
                    var entry = WriteSession(item, staged);
                    Commit(_entries.Where(current => current.Id != item.Id).Prepend(entry), staged);
                }
                finally
                {
                    DeleteStagedFiles(staged);
                }
            }
        }

        public void UpdateSessionEdits(IReadOnlyDictionary<string, List<AgentFileEditPreview>> updates)
        {
            lock (_gate)
            {
                EnsureLoaded();
                var staged = new List<Guid>();
                try
                {
                    var updated = _entries.Select(entry => updates.TryGetValue(entry.Id, out var edits)
                        ? entry with { EditsId = WriteEdits(edits, staged) }
                        : entry).ToList();
                    if (!updated.SequenceEqual(_entries))
                    {
                        Commit(updated, staged);
                    }
                }
                finally
                {
                    DeleteStagedFiles(staged);
                }
            }
        }

        public void UpdateSelections(string id, AgentSessionSelections selections)
        {
            lock (_gate)
            {
                EnsureLoaded();
                var entry = _entries.FirstOrDefault(item => item.Id == id);
                if (entry != null && !selections.EquivalentTo(entry.Selections))
                {
                    Commit(_entries.Select(item => item == entry ? item with { Selections = selections } : item), new());
                }
            }
        }

        public void Delete(string? id)
        {
            lock (_gate)
            {
                EnsureLoaded();
                if (id == null || _entries.Any(entry => entry.Id == id))
                {
                    Commit(_entries.Where(entry => id != null && entry.Id != id), new());
                }
            }
        }

        private bool HasLegacyHistory()
        {
            using var reader = File.OpenText(_indexPath);
            int value;
            do
            {
                value = reader.Read();
            } while (value >= 0 && char.IsWhiteSpace((char)value));
            return value == '[';
        }

        private async Task MigrateLegacyHistoryAsync()
        {
            var entries = new List<AgentHistoryEntry>();
            var parentsByWorker = new Dictionary<string, string>(StringComparer.Ordinal);
            var staged = new List<Guid>();
            try
            {
                using (var stream = File.OpenRead(_indexPath))
                {
                    await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<AgentHistoryItem>(stream).ConfigureAwait(false))
                    {
                        if (item == null || string.IsNullOrWhiteSpace(item.Id))
                        {
                            continue;
                        }

                        entries.Add(WriteSession(item, staged));
                        if (item.Role == AgentSessionRole.Orchestrator)
                        {
                            // Older versions recorded the worker ID in the spawn tool result.
                            var matches = Regex.Matches(item.SessionHistoryText ?? string.Empty,
                                @"^\[tool: spawn[_ -]subagent result\]\r?\nStarted subagent ([0-9a-f-]{36}) \(",
                                RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                            foreach (Match match in matches)
                            {
                                parentsByWorker.TryAdd(match.Groups[1].Value, item.Id);
                            }
                        }
                    }
                }

                Commit(entries.Select(entry => entry.Role == AgentSessionRole.Subagent &&
                    string.IsNullOrWhiteSpace(entry.OrchestratorSessionId) && parentsByWorker.TryGetValue(entry.Id, out var parent)
                    ? entry with { OrchestratorSessionId = parent }
                    : entry), staged);
            }
            finally
            {
                DeleteStagedFiles(staged);
            }
        }

        private AgentHistoryEntry WriteSession(AgentHistoryItem item, List<Guid> staged) => new(
            item.Id, item.Timestamp, item.Title, item.Role, item.OrchestratorSessionId, item.WorkspaceRoot,
            WriteItem(new Transcript(item.SessionHistoryText, item.ModelSessionHistoryText, item.LastAnswerText,
                item.SessionHistoryTokenCount), staged), WriteEdits(item.SessionEdits, staged), item.Selections?.Copy());

        private Guid? WriteEdits(List<AgentFileEditPreview> edits, List<Guid> staged) =>
            edits.Count > 0 ? WriteItem(edits, staged) : null;

        private Guid WriteItem<T>(T item, List<Guid> staged)
        {
            Directory.CreateDirectory(_itemsDirectory);
            Guid id = Guid.NewGuid();
            staged.Add(id);
            Write(GetItemPath(id), item);
            return id;
        }

        private void Commit(IEnumerable<AgentHistoryEntry> candidates, List<Guid> staged)
        {
            var kept = GroupEntries(candidates).Take(MaxGroups).SelectMany(group => group).ToList();
            var retainedFiles = GetItemIds(kept).ToHashSet();
            var obsoleteFiles = GetItemIds(_entries).Concat(staged).Where(id => !retainedFiles.Contains(id)).ToArray();
            Directory.CreateDirectory(settingsDirectory);
            string temporaryIndex = $"{_indexPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                Write(temporaryIndex, new HistoryIndex(kept));
                File.Move(temporaryIndex, _indexPath, overwrite: true);
                _entries = kept;
                staged.Clear();
            }
            finally
            {
                TryDelete(temporaryIndex);
            }

            DeleteStagedFiles(obsoleteFiles);
        }

        private string GetItemPath(Guid id) => Path.Combine(_itemsDirectory, $"{id:N}.json");

        private static IEnumerable<Guid> GetItemIds(IEnumerable<AgentHistoryEntry> entries)
        {
            foreach (var entry in entries)
            {
                yield return entry.TranscriptId;
                if (entry.EditsId is Guid id)
                {
                    yield return id;
                }
            }
        }

        private void DeleteStagedFiles(IEnumerable<Guid> ids)
        {
            foreach (Guid id in ids)
            {
                TryDelete(GetItemPath(id));
            }
        }

        private static T Read<T>(string path)
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream) ?? throw new InvalidDataException(path);
        }

        private static void Write<T>(string path, T value)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, value);
            stream.Flush(flushToDisk: true);
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
            catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }
    }
}
