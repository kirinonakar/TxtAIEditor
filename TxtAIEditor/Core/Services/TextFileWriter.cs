using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services
{
    internal static class TextFileWriter
    {
        // Callers capture immutable strings before entering the worker. Even an
        // asynchronous FileStream opens, replaces and closes files synchronously.
        public static Task SaveAsync(
            string filePath,
            IReadOnlyList<string> lines,
            string lineEnding,
            Encoding encoding,
            CancellationToken cancellationToken = default,
            Action<int, int>? reportProgress = null) =>
            Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                string temp = Path.Combine(directory ?? Path.GetTempPath(), $"._{Path.GetFileName(filePath)}.tmp");
                string backup = filePath + ".bak";
                try
                {
                    await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write,
                        FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await using (var writer = new StreamWriter(stream, encoding, 128 * 1024, false))
                    {
                        for (int i = 0; i < lines.Count; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (i % 512 == 0) reportProgress?.Invoke(i, lines.Count);
                            if (i > 0) await writer.WriteAsync(lineEnding.AsMemory(), cancellationToken).ConfigureAwait(false);
                            await writer.WriteAsync(lines[i].AsMemory(), cancellationToken).ConfigureAwait(false);
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (File.Exists(filePath))
                    {
                        File.Replace(temp, filePath, backup);
                        if (File.Exists(backup)) File.Delete(backup);
                    }
                    else File.Move(temp, filePath);
                    reportProgress?.Invoke(lines.Count, lines.Count);
                }
                catch (OperationCanceledException)
                {
                    TryDelete(temp);
                    throw;
                }
                catch (Exception ex)
                {
                    TryDelete(temp);
                    throw new IOException($"파일 저장 실패 (안전 복구 완료): {ex.Message}", ex);
                }
            }, cancellationToken);

        private static void TryDelete(string filePath)
        {
            try { File.Delete(filePath); } catch { }
        }
    }
}
