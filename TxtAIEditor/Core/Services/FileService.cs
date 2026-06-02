using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;

namespace TxtAIEditor.Core.Services
{
    public class FileService : IFileService
    {
        public async Task<string> ReadTextFileAsync(string filePath)
        {
            var result = await ReadTextFileAsync(filePath, "Auto");
            return result.Content;
        }

        public async Task<TextFileReadResult> ReadTextFileAsync(string filePath, string encodingName)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("파일을 찾을 수 없습니다.", filePath);

            byte[] bytes = await File.ReadAllBytesAsync(filePath);
            Encoding encoding = TextEncodingService.GetTextEncoding(bytes, encodingName);
            bool hasUtf8Bom = TextEncodingService.HasUtf8Bom(bytes);

            using (var stream = new MemoryStream(bytes))
            using (var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true))
            {
                bool isAuto = string.IsNullOrWhiteSpace(encodingName) || encodingName.Equals("Auto", StringComparison.OrdinalIgnoreCase);
                return new TextFileReadResult
                {
                    Content = await reader.ReadToEndAsync(),
                    EncodingName = isAuto ? TextEncodingService.GetDisplayName(encoding, hasUtf8Bom) : encodingName,
                    WasAutoDetected = isAuto
                };
            }
        }

        public async Task SaveTextFileAsync(string filePath, string content)
        {
            await SaveTextFileAsync(filePath, content, "UTF-8");
        }

        public async Task SaveTextFileAsync(string filePath, string content, string encodingName)
        {
            // Fail-safe writing: Write to temporary file first, then atomically replace
            string? directory = Path.GetDirectoryName(filePath);
            if (directory != null && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempFilePath = Path.Combine(directory ?? Path.GetTempPath(), $"._{Path.GetFileName(filePath)}.tmp");
            string backupFilePath = filePath + ".bak";

            try
            {
                Encoding encoding = TextEncodingService.GetEncodingByName(encodingName);
                await File.WriteAllTextAsync(tempFilePath, content, encoding);

                // 2. Perform atomic replace
                if (File.Exists(filePath))
                {
                    // Create backup and replace
                    File.Replace(tempFilePath, filePath, backupFilePath);
                    
                    // Cleanup backup if everything went flawlessly
                    if (File.Exists(backupFilePath))
                    {
                        File.Delete(backupFilePath);
                    }
                }
                else
                {
                    // Direct move if new file
                    File.Move(tempFilePath, filePath);
                }
            }
            catch (Exception ex)
            {
                // Safe recover
                if (File.Exists(tempFilePath))
                {
                    try { File.Delete(tempFilePath); } catch { }
                }
                throw new IOException($"파일 저장 실패 (안전 복구 완료): {ex.Message}", ex);
            }
        }

        public Task<LargeFileInfo> GetLargeFileInfoAsync(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("파일을 찾을 수 없습니다.", filePath);

            var info = new FileInfo(filePath);
            return Task.FromResult(new LargeFileInfo
            {
                FilePath = filePath,
                FileSize = info.Length
            });
        }

        public async Task<string> ReadChunkAsync(string filePath, long offset, int length)
        {
            // Placeholder chunk read for Phase 2
            if (!File.Exists(filePath))
                return string.Empty;

            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (offset >= stream.Length)
                    return string.Empty;

                stream.Seek(offset, SeekOrigin.Begin);
                byte[] buffer = new byte[Math.Min(length, stream.Length - offset)];
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                
                // Return UTF-8 string representation
                return Encoding.UTF8.GetString(buffer, 0, bytesRead);
            }
        }

        private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<long>> _largeFileIndexes = 
            new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<long>>();

        public async Task InitializeLargeFileAsync(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("파일을 찾을 수 없습니다.", filePath);

            if (_largeFileIndexes.ContainsKey(filePath))
                return; // Already indexed

            var offsets = new System.Collections.Generic.List<long> { 0 }; // First line starts at 0

            await Task.Run(() =>
            {
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] buffer = new byte[64 * 1024]; // 64KB buffers
                    int bytesRead;
                    long totalBytesRead = 0;

                    while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        for (int i = 0; i < bytesRead; i++)
                        {
                            if (buffer[i] == 10) // LF (\n) character code
                            {
                                offsets.Add(totalBytesRead + i + 1);
                            }
                        }
                        totalBytesRead += bytesRead;
                    }
                    
                    // Add end of file offset
                    if (totalBytesRead > 0 && offsets[offsets.Count - 1] != totalBytesRead)
                    {
                        offsets.Add(totalBytesRead);
                    }
                }
            });

            _largeFileIndexes[filePath] = offsets;
        }

        public Task<int> GetLargeFileLineCountAsync(string filePath)
        {
            if (_largeFileIndexes.TryGetValue(filePath, out var offsets))
            {
                return Task.FromResult(offsets.Count - 1);
            }
            return Task.FromResult(0);
        }

        public async Task<System.Collections.Generic.List<string>> GetLargeFileLinesAsync(string filePath, int startLine, int count)
        {
            var lines = new System.Collections.Generic.List<string>();
            if (!File.Exists(filePath) || !_largeFileIndexes.TryGetValue(filePath, out var offsets))
                return lines;

            int totalLines = offsets.Count - 1;
            if (startLine < 1 || startLine > totalLines)
                return lines;

            int endLine = Math.Min(startLine + count - 1, totalLines);
            long startOffset = offsets[startLine - 1];

            // Auto-detect encoding once or fall back to UTF-8
            Encoding encoding = DetectEncoding(filePath);

            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Seek(startOffset, SeekOrigin.Begin);
                using (var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true))
                {
                    for (int i = startLine; i <= endLine; i++)
                    {
                        string? line = await reader.ReadLineAsync();
                        if (line == null) break;
                        lines.Add(line);
                    }
                }
            }

            return lines;
        }

        public async Task<System.Collections.Generic.List<LargeFileSearchResult>> SearchLargeFileAsync(string filePath, string query, bool isRegex, bool matchCase = false, bool wholeWord = false)
        {
            var results = new System.Collections.Generic.List<LargeFileSearchResult>();
            if (!File.Exists(filePath) || string.IsNullOrEmpty(query))
                return results;

            // Ensure indexing is ready
            await InitializeLargeFileAsync(filePath);
            if (!_largeFileIndexes.TryGetValue(filePath, out var offsets))
                return results;

            Encoding encoding = DetectEncoding(filePath);

            await Task.Run(() =>
            {
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, encoding))
                {
                    string? line;
                    int lineNumber = 1;
                    System.Text.RegularExpressions.Regex? regex = null;

                    if (isRegex || wholeWord)
                    {
                        try
                        {
                            string pattern = isRegex ? query : System.Text.RegularExpressions.Regex.Escape(query);
                            if (wholeWord)
                            {
                                pattern = $"\\b{pattern}\\b";
                            }

                            var options = matchCase
                                ? System.Text.RegularExpressions.RegexOptions.None
                                : System.Text.RegularExpressions.RegexOptions.IgnoreCase;
                            regex = new System.Text.RegularExpressions.Regex(pattern, options);
                        }
                        catch
                        {
                            return;
                        }
                    }

                    while ((line = reader.ReadLine()) != null)
                    {
                        if (regex != null)
                        {
                            var match = regex.Match(line);
                            if (match.Success)
                            {
                                results.Add(new LargeFileSearchResult
                                {
                                    LineNumber = lineNumber,
                                    LineContent = line,
                                    IndexOfMatch = match.Index,
                                    MatchLength = match.Length
                                });
                            }
                        }
                        else
                        {
                            var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                            int idx = line.IndexOf(query, comparison);
                            if (idx >= 0)
                            {
                                results.Add(new LargeFileSearchResult
                                {
                                    LineNumber = lineNumber,
                                    LineContent = line,
                                    IndexOfMatch = idx,
                                    MatchLength = query.Length
                                });
                            }
                        }

                        if (results.Count >= 1000) // Cap results for stability
                            break;

                        lineNumber++;
                    }
                }
            });

            return results;
        }

        public async Task SaveLargeFileWithPatchesAsync(string filePath, System.Collections.Generic.Dictionary<int, string> patches)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("파일을 찾을 수 없습니다.", filePath);

            if (patches == null || patches.Count == 0)
                return; // Nothing to change

            string? directory = Path.GetDirectoryName(filePath);
            string tempFilePath = Path.Combine(directory ?? Path.GetTempPath(), $"._{Path.GetFileName(filePath)}.tmp");
            string backupFilePath = filePath + ".bak";

            Encoding encoding = DetectEncoding(filePath);

            try
            {
                await Task.Run(async () =>
                {
                    using (var reader = new StreamReader(filePath, encoding))
                    using (var writer = new StreamWriter(tempFilePath, false, Encoding.UTF8))
                    {
                        string? line;
                        int lineNumber = 1;
                        while ((line = await reader.ReadLineAsync()) != null)
                        {
                            if (patches.TryGetValue(lineNumber, out string? patchText))
                            {
                                await writer.WriteLineAsync(patchText);
                            }
                            else
                            {
                                await writer.WriteLineAsync(line);
                            }
                            lineNumber++;
                        }
                    }
                });

                // Atomic replace
                if (File.Exists(filePath))
                {
                    File.Replace(tempFilePath, filePath, backupFilePath);
                    if (File.Exists(backupFilePath))
                    {
                        File.Delete(backupFilePath);
                    }
                }
                else
                {
                    File.Move(tempFilePath, filePath);
                }

                // Invalidate cache and re-index
                _largeFileIndexes.Remove(filePath);
                await InitializeLargeFileAsync(filePath);
            }
            catch (Exception ex)
            {
                if (File.Exists(tempFilePath))
                {
                    try { File.Delete(tempFilePath); } catch { }
                }
                throw new IOException($"대용량 파일 패치 병합 저장 실패 (안전 복구 완료): {ex.Message}", ex);
            }
        }

        private Encoding DetectEncoding(string filePath)
        {
            byte[] bytes = File.ReadAllBytes(filePath);
            return TextEncodingService.DetectEncoding(bytes);
        }
    }
}
