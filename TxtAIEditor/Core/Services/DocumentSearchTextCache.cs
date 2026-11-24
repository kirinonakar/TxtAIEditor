using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services
{
    internal sealed class DocumentSearchTextCache(string folderPath) : IDisposable
    {
        private const int EntryLimit = 1024;
        private const long ByteLimit = 256L * 1024 * 1024;
        private const int BufferSize = 16 * 1024;
        private const long LargeExtractionAllocation = 64L * 1024 * 1024;
        private const string TempDirectoryPrefix = "TxtAIEditor-Search-";
        private static int _reclaimingSearchMemory;

        private readonly object _gate = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Dictionary<CacheKey, LinkedListNode<Entry>> _entries = new();
        private readonly LinkedList<Entry> _recentEntries = new();
        private string? _directory;
        private long _retainedBytes;
        private int _activeSearches;
        private bool _retired;
        private bool _reclaimMemoryOnRetirement;

        internal string FolderPath { get; } = folderPath;

        internal static bool IsSearchTempDirectory(string directory, string name) =>
            name.StartsWith(TempDirectoryPrefix, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetDirectoryName(directory), Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
                StringComparison.OrdinalIgnoreCase);

        internal CancellationTokenSource BeginSearch(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
                _activeSearches++;
                return linked;
            }
        }

        internal void EndSearch()
        {
            lock (_gate)
            {
                _activeSearches--;
                ScheduleCleanupIfIdle();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_retired) return;
                _retired = true;
                _entries.Clear();
                _recentEntries.Clear();
                ScheduleCleanupIfIdle();
            }

            _lifetime.Cancel();
            _lifetime.Dispose();
        }

        internal async Task<TextReader?> OpenTextAsync(
            string filePath, int maxChars, string? converterPath, SemaphoreSlim extractionSlots,
            Func<string, TextWriter, CancellationToken, Task<bool>> extractTextAsync, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            filePath = Path.GetFullPath(filePath);
            if (ReadVersion(filePath, converterPath) is not InputVersion version)
            {
                return null;
            }

            var key = new CacheKey(filePath, maxChars, converterPath);
            if (TryOpenCachedText(key, version, out TextReader? reader))
            {
                return reader;
            }

            await extractionSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            string? tempPath = null;
            long allocationAtStart = -1;
            try
            {
                if (ReadVersion(filePath, converterPath) is not InputVersion extractionVersion)
                {
                    return null;
                }

                if (TryOpenCachedText(key, extractionVersion, out reader))
                {
                    return reader;
                }

                cancellationToken.ThrowIfCancellationRequested();
                tempPath = CreateTempPath();
                bool hasText;
                long length;
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false), BufferSize, leaveOpen: true))
                    {
                        if (converterPath is null) allocationAtStart = GC.GetTotalAllocatedBytes();
                        hasText = await extractTextAsync(filePath, writer, cancellationToken).ConfigureAwait(false);
                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    length = stream.Length;
                }

                cancellationToken.ThrowIfCancellationRequested();
                bool cacheable = (hasText || converterPath is null) && length <= ByteLimit &&
                    ReadVersion(filePath, converterPath) == extractionVersion;
                if (!hasText)
                {
                    if (cacheable) CacheText(key, new Entry(key, extractionVersion, null, 0));
                    return null;
                }

                reader = OpenReader(tempPath);
                if (cacheable && CacheText(key, new Entry(key, extractionVersion, tempPath, length)))
                {
                    tempPath = null;
                }

                return reader;
            }
            finally
            {
                if (allocationAtStart >= 0 && GC.GetTotalAllocatedBytes() - allocationAtStart >= LargeExtractionAllocation)
                {
                    Volatile.Write(ref _reclaimMemoryOnRetirement, true);
                }
                if (tempPath is not null) TryDeleteFile(tempPath);
                extractionSlots.Release();
            }
        }

        private string CreateTempPath()
        {
            lock (_gate)
            {
                if (_retired) throw new OperationCanceledException();
                _directory ??= Directory.CreateTempSubdirectory(TempDirectoryPrefix).FullName;
                return Path.Combine(_directory, Path.GetRandomFileName());
            }
        }

        internal FileStream CreateScratchStream() => new(CreateTempPath(), FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, BufferSize, FileOptions.DeleteOnClose | FileOptions.SequentialScan);

        private bool TryOpenCachedText(CacheKey key, InputVersion version, out TextReader? reader)
        {
            reader = null;
            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out LinkedListNode<Entry>? node)) return false;
                if (node.Value.Version != version)
                {
                    RemoveEntry(node);
                    return false;
                }

                if (node.Value.TempPath is string tempPath)
                {
                    try
                    {
                        reader = OpenReader(tempPath);
                    }
                    catch (IOException)
                    {
                        RemoveEntry(node);
                        return false;
                    }
                }

                _recentEntries.Remove(node);
                _recentEntries.AddFirst(node);
                return true;
            }
        }

        private bool CacheText(CacheKey key, Entry entry)
        {
            lock (_gate)
            {
                if (_retired) return false;
                if (_entries.TryGetValue(key, out LinkedListNode<Entry>? existing)) RemoveEntry(existing);

                while (_entries.Count >= EntryLimit || _retainedBytes + entry.ByteCount > ByteLimit)
                {
                    RemoveEntry(_recentEntries.Last!);
                }

                _entries.Add(key, _recentEntries.AddFirst(entry));
                _retainedBytes += entry.ByteCount;
                return true;
            }
        }

        private void RemoveEntry(LinkedListNode<Entry> node)
        {
            _entries.Remove(node.Value.Key);
            _recentEntries.Remove(node);
            _retainedBytes -= node.Value.ByteCount;
            if (node.Value.TempPath is string tempPath) TryDeleteFile(tempPath);
        }

        private void ScheduleCleanupIfIdle()
        {
            if (!_retired || _activeSearches != 0 || _directory is not string directory) return;
            _directory = null;
            bool reclaimMemory = Volatile.Read(ref _reclaimMemoryOnRetirement);
            _ = Task.Run(() =>
            {
                try
                {
                    foreach (string file in Directory.EnumerateFiles(directory)) TryDeleteFile(file);
                    Directory.Delete(directory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine($"Could not remove folder search temp directory: {ex.Message}");
                }

                if (reclaimMemory) ReclaimSearchMemory();
            });
        }

        private static void ReclaimSearchMemory()
        {
            if (Interlocked.Exchange(ref _reclaimingSearchMemory, 1) != 0) return;
            try
            {
                // Whole-document parsers can leave large GC segments committed after their objects are gone.
                // Compact once after the last search stopped using the retired folder, off the UI thread.
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            }
            finally
            {
                Volatile.Write(ref _reclaimingSearchMemory, 0);
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Could not remove folder search temp file: {ex.Message}");
            }
        }

        private static StreamReader OpenReader(string path) => new(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BufferSize, FileOptions.SequentialScan),
            Encoding.UTF8, detectEncodingFromByteOrderMarks: false, BufferSize);

        private static InputVersion? ReadVersion(string filePath, string? converterPath)
        {
            FileStamp? document = FileStamp.Read(filePath);
            FileStamp? converter = converterPath is null ? null : FileStamp.Read(converterPath);
            return document.HasValue && (converterPath is null || converter.HasValue)
                ? new InputVersion(document.Value, converter)
                : null;
        }

        private readonly record struct CacheKey(string Path, int MaxChars, string? ConverterPath);
        private readonly record struct Entry(CacheKey Key, InputVersion Version, string? TempPath, long ByteCount);
        private readonly record struct InputVersion(FileStamp Document, FileStamp? Converter);
        private readonly record struct FileStamp(long Length, DateTime LastWriteTimeUtc, DateTime CreationTimeUtc)
        {
            internal static FileStamp? Read(string path)
            {
                var file = new FileInfo(path);
                return file.Exists ? new FileStamp(file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc) : null;
            }
        }
    }
}
