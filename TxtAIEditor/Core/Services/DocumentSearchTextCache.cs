using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services
{
    internal sealed class DocumentSearchTextCache
    {
        private const int EntryLimit = 1024;
        // Strings use two bytes per UTF-16 code unit. Limit retained text to 64 MiB in total and 4 MiB per document.
        private const int CharacterLimit = 32 * 1024 * 1024;
        private const int EntryCharacterLimit = 2 * 1024 * 1024;

        private readonly object _cacheLock = new();
        private readonly Dictionary<CacheKey, LinkedListNode<Entry>> _entries = new();
        private readonly LinkedList<Entry> _recentEntries = new();
        private int _retainedCharacters;

        internal async Task<string> GetTextAsync(
            string filePath, int maxChars, string? converterPath, SemaphoreSlim extractionSlots,
            Func<string, CancellationToken, Task<string>> extractTextAsync, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            filePath = Path.GetFullPath(filePath);
            if (ReadVersion(filePath, converterPath) is not InputVersion version)
            {
                return string.Empty;
            }

            var key = new CacheKey(filePath, maxChars, converterPath);
            string? cached = GetCachedText(key, version);
            if (cached is not null)
            {
                return cached;
            }

            await extractionSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // A queued extraction may have finished, or either file may have changed while waiting.
                if (ReadVersion(filePath, converterPath) is not InputVersion extractionVersion)
                {
                    return string.Empty;
                }

                cached = GetCachedText(key, extractionVersion);
                if (cached is not null)
                {
                    return cached;
                }

                cancellationToken.ThrowIfCancellationRequested();
                string extracted = await extractTextAsync(filePath, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                // Empty built-in results also let future searches go straight to the next extractor.
                if (extracted.Length <= EntryCharacterLimit && (converterPath is null || !string.IsNullOrWhiteSpace(extracted)) &&
                    ReadVersion(filePath, converterPath) == extractionVersion)
                {
                    CacheText(key, extractionVersion, extracted);
                }

                return extracted;
            }
            finally
            {
                extractionSlots.Release();
            }
        }

        private string? GetCachedText(CacheKey key, InputVersion version)
        {
            lock (_cacheLock)
            {
                if (!_entries.TryGetValue(key, out LinkedListNode<Entry>? node) || node.Value.Version != version)
                {
                    return null;
                }

                _recentEntries.Remove(node);
                _recentEntries.AddFirst(node);
                return node.Value.Text;
            }
        }

        private void CacheText(CacheKey key, InputVersion version, string text)
        {
            lock (_cacheLock)
            {
                if (_entries.TryGetValue(key, out LinkedListNode<Entry>? existing))
                {
                    RemoveEntry(existing);
                }

                while (_entries.Count >= EntryLimit || _retainedCharacters + text.Length > CharacterLimit)
                {
                    RemoveEntry(_recentEntries.Last!);
                }

                _entries.Add(key, _recentEntries.AddFirst(new Entry(key, version, text)));
                _retainedCharacters += text.Length;
            }
        }

        private void RemoveEntry(LinkedListNode<Entry> node)
        {
            _entries.Remove(node.Value.Key);
            _recentEntries.Remove(node);
            _retainedCharacters -= node.Value.Text.Length;
        }

        private static InputVersion? ReadVersion(string filePath, string? converterPath)
        {
            FileStamp? document = FileStamp.Read(filePath);
            FileStamp? converter = converterPath is null ? null : FileStamp.Read(converterPath);
            return document.HasValue && (converterPath is null || converter.HasValue)
                ? new InputVersion(document.Value, converter)
                : null;
        }

        private readonly record struct CacheKey(string Path, int MaxChars, string? ConverterPath);
        private readonly record struct Entry(CacheKey Key, InputVersion Version, string Text);
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
