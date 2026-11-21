using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentSessionRewindStack
    {
        private const int RecentMetadataCount = 2;
        private sealed class Entry(AgentSessionRewindSnapshot snapshot)
        {
            public AgentSessionRewindSnapshot? Live { get; set; } = snapshot;
            public AgentSessionSpill<AgentSessionRewindSnapshot>? Disk { get; set; }
            public List<string>? RecentSubagentIds { get; set; }
        }

        private readonly List<Entry> _entries = new();
        private readonly HashSet<string> _olderSubagentIds = new(StringComparer.Ordinal);

        public int Count => _entries.Count;

        public void Add(AgentSessionRewindSnapshot snapshot) => _entries.Add(new Entry(snapshot));

        public async Task ArchiveAsync(AgentSessionRewindSnapshot snapshot)
        {
            if (_entries.Count == 0 || _entries[^1].Live != snapshot)
            {
                return;
            }

            var entry = _entries[^1];
            try
            {
                entry.Disk = await Task.Run(() => AgentSessionSpill<AgentSessionRewindSnapshot>.Write(snapshot));
                entry.RecentSubagentIds = snapshot.SubagentSessionIds;
                entry.Live = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // A failed archive must leave the rewind point usable.
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }

            if (_entries.Count > RecentMetadataCount)
            {
                var older = _entries[^(RecentMetadataCount + 1)];
                if (older.RecentSubagentIds is { } ids)
                {
                    _olderSubagentIds.UnionWith(ids);
                    older.RecentSubagentIds = null;
                }
            }
        }

        public AgentSessionRewindSnapshot ReadLatest()
        {
            var entry = _entries[^1];
            return entry.Live ?? entry.Disk!.Read();
        }

        public AgentSessionRewindSnapshot ReadAt(int index)
        {
            var entry = _entries[index];
            return entry.Live ?? entry.Disk!.Read();
        }

        public void RemoveLatest(AgentSessionRewindSnapshot snapshot)
        {
            _entries[^1].Disk?.Dispose();
            _entries.RemoveAt(_entries.Count - 1);
            _olderSubagentIds.ExceptWith(snapshot.SubagentSessionIds);
        }

        public IEnumerable<string> GetSubagentIds() => _olderSubagentIds.Concat(_entries.SelectMany(entry =>
            entry.Live?.SubagentSessionIds ?? entry.RecentSubagentIds ?? Enumerable.Empty<string>()));

        public void Clear()
        {
            foreach (var entry in _entries)
            {
                entry.Disk?.Dispose();
            }
            _entries.Clear();
            _olderSubagentIds.Clear();
        }
    }
}
