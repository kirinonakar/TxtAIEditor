using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace TxtAIEditor.Core.Services
{
    public static class FreezeDiagnosticLogger
    {
        private const long MaximumLogBytes = 5L * 1024 * 1024;
        private const int HeartbeatIntervalMilliseconds = 250;
        private const int UiStallThresholdMilliseconds = 700;

        private static readonly object StateGate = new();
        private static readonly UTF8Encoding Utf8WithoutBom = new(false);
        private static readonly Channel<string> LogChannel = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
        private static readonly string SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".TxtAIEditor");
        private static readonly string LogsDirectory = Path.Combine(SettingsDirectory, "Logs");
        private static readonly string CurrentLogPath = Path.Combine(LogsDirectory, "freeze-diagnostics.log");
        private static readonly string PreviousLogPath = Path.Combine(LogsDirectory, "freeze-diagnostics.previous.log");
        private static readonly Task WriterTask = Task.Run(ProcessLogQueueAsync);

        private static DispatcherQueueTimer? _heartbeatTimer;
        private static long _lastHeartbeatTimestamp;
        private static string _activeDocumentName = string.Empty;
        private static int _activeDocumentLineCount;
        private static long _lineRequestWindowStarted;
        private static int _lineRequestCount;
        private static int _lineRequestLines;

        public static string LogFilePath => CurrentLogPath;

        public static void StartUiFreezeMonitor(DispatcherQueue dispatcherQueue)
        {
            lock (StateGate)
            {
                if (_heartbeatTimer != null)
                {
                    return;
                }

                _lastHeartbeatTimestamp = Stopwatch.GetTimestamp();
                _heartbeatTimer = dispatcherQueue.CreateTimer();
                _heartbeatTimer.Interval = TimeSpan.FromMilliseconds(HeartbeatIntervalMilliseconds);
                _heartbeatTimer.IsRepeating = true;
                _heartbeatTimer.Tick += OnHeartbeatTick;
                _heartbeatTimer.Start();
            }

            Version? appVersion = Assembly.GetExecutingAssembly().GetName().Version;
            Write(
                "session-start",
                $"appVersion={appVersion}; runtime={Environment.Version}; os={Environment.OSVersion}; log={CurrentLogPath}");
        }

        public static void SetActiveDocument(string? filePath, int lineCount)
        {
            string documentName = string.IsNullOrWhiteSpace(filePath)
                ? "(untitled)"
                : Path.GetFileName(filePath);

            lock (StateGate)
            {
                _activeDocumentName = documentName;
                _activeDocumentLineCount = Math.Max(0, lineCount);
            }

            Write("active-document", $"name={documentName}; lines={Math.Max(0, lineCount)}");
        }

        public static void LogSlowOperation(
            string operation,
            long elapsedMilliseconds,
            int thresholdMilliseconds,
            string? details = null)
        {
            if (elapsedMilliseconds < thresholdMilliseconds)
            {
                return;
            }

            Write(
                "slow-operation",
                $"operation={operation}; elapsedMs={elapsedMilliseconds}; {details}");
        }

        public static void RecordEditorLineRequest(
            int startLine,
            int lineCount,
            long sendElapsedMilliseconds)
        {
            bool shouldLogBurst = false;
            int requestCount;
            int requestedLines;
            long now = Stopwatch.GetTimestamp();

            lock (StateGate)
            {
                if (_lineRequestWindowStarted == 0 ||
                    Stopwatch.GetElapsedTime(_lineRequestWindowStarted, now) >= TimeSpan.FromSeconds(1))
                {
                    _lineRequestWindowStarted = now;
                    _lineRequestCount = 0;
                    _lineRequestLines = 0;
                }

                _lineRequestCount++;
                _lineRequestLines += Math.Max(0, lineCount);
                requestCount = _lineRequestCount;
                requestedLines = _lineRequestLines;
                shouldLogBurst = requestCount >= 8 && (requestCount & (requestCount - 1)) == 0;
            }

            if (shouldLogBurst)
            {
                Write(
                    "editor-line-request-burst",
                    $"requests={requestCount}; lines={requestedLines}; windowMs=1000; latestStartLine={startLine}");
            }

            LogSlowOperation(
                "editor-line-response",
                sendElapsedMilliseconds,
                thresholdMilliseconds: 25,
                $"startLine={startLine}; lines={lineCount}");
        }

        public static void Write(string eventName, string? details = null)
        {
            _ = WriterTask;
            string safeEventName = Sanitize(eventName);
            string safeDetails = Sanitize(details);
            string line = $"{DateTimeOffset.Now:O} | pid={Environment.ProcessId} | event={safeEventName}";
            if (!string.IsNullOrWhiteSpace(safeDetails))
            {
                line += $" | {safeDetails}";
            }

            LogChannel.Writer.TryWrite(line);
        }

        private static void OnHeartbeatTick(DispatcherQueueTimer sender, object args)
        {
            long now = Stopwatch.GetTimestamp();
            long previous = _lastHeartbeatTimestamp;
            _lastHeartbeatTimestamp = now;
            if (previous == 0)
            {
                return;
            }

            long gapMilliseconds = (long)Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds;
            if (gapMilliseconds < UiStallThresholdMilliseconds)
            {
                return;
            }

            string documentName;
            int lineCount;
            lock (StateGate)
            {
                documentName = _activeDocumentName;
                lineCount = _activeDocumentLineCount;
            }

            try
            {
                using Process process = Process.GetCurrentProcess();
                Write(
                    "ui-stall",
                    $"gapMs={gapMilliseconds}; estimatedBlockedMs={Math.Max(0, gapMilliseconds - HeartbeatIntervalMilliseconds)}; " +
                    $"document={documentName}; lines={lineCount}; workingSetMb={process.WorkingSet64 / (1024 * 1024)}; " +
                    $"privateMb={process.PrivateMemorySize64 / (1024 * 1024)}; managedMb={GC.GetTotalMemory(false) / (1024 * 1024)}; " +
                    $"gc0={GC.CollectionCount(0)}; gc1={GC.CollectionCount(1)}; gc2={GC.CollectionCount(2)}");
            }
            catch (Exception ex)
            {
                Write(
                    "ui-stall",
                    $"gapMs={gapMilliseconds}; estimatedBlockedMs={Math.Max(0, gapMilliseconds - HeartbeatIntervalMilliseconds)}; " +
                    $"document={documentName}; lines={lineCount}; metricsError={ex.GetType().Name}");
            }
        }

        private static async Task ProcessLogQueueAsync()
        {
            await foreach (string line in LogChannel.Reader.ReadAllAsync())
            {
                try
                {
                    Directory.CreateDirectory(LogsDirectory);
                    RotateLogIfNeeded();
                    await File.AppendAllTextAsync(
                        CurrentLogPath,
                        line + Environment.NewLine,
                        Utf8WithoutBom).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to write freeze diagnostics: {ex.Message}");
                }
            }
        }

        private static void RotateLogIfNeeded()
        {
            if (!File.Exists(CurrentLogPath) ||
                new FileInfo(CurrentLogPath).Length < MaximumLogBytes)
            {
                return;
            }

            File.Move(CurrentLogPath, PreviousLogPath, overwrite: true);
        }

        private static string Sanitize(string? value)
        {
            return (value ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('|', ';');
        }
    }
}
