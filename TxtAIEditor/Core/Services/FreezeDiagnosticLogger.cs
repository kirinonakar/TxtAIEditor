using System;
using System.Diagnostics;
using System.IO;
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
        private static readonly TimeSpan ScrollLeadWindow = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan FreezeLogCooldown = TimeSpan.FromSeconds(1);

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
        private static long _lastScrollActivityTimestamp;
        private static long _lastFreezeLogTimestamp;
        private static long _lineRequestWindowStarted;
        private static int _lineRequestCount;
        private static int _lineRequestLines;
        private static int _lastLineResponseMilliseconds;

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
        }

        public static void MarkEditorScrollActivity()
        {
            lock (StateGate)
            {
                _lastScrollActivityTimestamp = Stopwatch.GetTimestamp();
            }
        }

        public static void RecordWebViewScrollFreeze(int gapMilliseconds)
        {
            long now = Stopwatch.GetTimestamp();
            if (!TryReserveFreezeLog(now))
            {
                return;
            }

            WriteScrollFreeze("webview", Math.Max(0, gapMilliseconds), now);
        }

        public static void RecordEditorLineRequest(
            int startLine,
            int lineCount,
            long sendElapsedMilliseconds)
        {
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
                _lastLineResponseMilliseconds = (int)Math.Max(0, sendElapsedMilliseconds);
            }
        }

        private static void Write(string eventName, string? details = null)
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

            long lastScrollActivity;
            lock (StateGate)
            {
                lastScrollActivity = _lastScrollActivityTimestamp;
            }

            bool scrollWasActiveAtStallStart = lastScrollActivity > 0 &&
                (lastScrollActivity >= previous ||
                 Stopwatch.GetElapsedTime(lastScrollActivity, previous) <= ScrollLeadWindow);
            if (!scrollWasActiveAtStallStart || !TryReserveFreezeLog(now))
            {
                return;
            }

            WriteScrollFreeze("xaml-ui", gapMilliseconds, now);
        }

        private static bool TryReserveFreezeLog(long now)
        {
            lock (StateGate)
            {
                if (_lastFreezeLogTimestamp > 0 &&
                    Stopwatch.GetElapsedTime(_lastFreezeLogTimestamp, now) < FreezeLogCooldown)
                {
                    return false;
                }

                _lastFreezeLogTimestamp = now;
                return true;
            }
        }

        private static void WriteScrollFreeze(string surface, long gapMilliseconds, long now)
        {
            int lineRequestCount;
            int requestedLines;
            int lastLineResponseMilliseconds;
            lock (StateGate)
            {
                bool hasRecentLineRequests = _lineRequestWindowStarted > 0 &&
                    Stopwatch.GetElapsedTime(_lineRequestWindowStarted, now) < TimeSpan.FromSeconds(2);
                lineRequestCount = hasRecentLineRequests ? _lineRequestCount : 0;
                requestedLines = hasRecentLineRequests ? _lineRequestLines : 0;
                lastLineResponseMilliseconds = hasRecentLineRequests ? _lastLineResponseMilliseconds : 0;
            }

            try
            {
                using Process process = Process.GetCurrentProcess();
                Write(
                    "scroll-freeze",
                    $"surface={surface}; gapMs={gapMilliseconds}; " +
                    $"lineRequests={lineRequestCount}; requestedLines={requestedLines}; lastLineResponseMs={lastLineResponseMilliseconds}; " +
                    $"workingSetMb={process.WorkingSet64 / (1024 * 1024)}; privateMb={process.PrivateMemorySize64 / (1024 * 1024)}; " +
                    $"managedMb={GC.GetTotalMemory(false) / (1024 * 1024)}; gc0={GC.CollectionCount(0)}; " +
                    $"gc1={GC.CollectionCount(1)}; gc2={GC.CollectionCount(2)}");
            }
            catch (Exception ex)
            {
                Write(
                    "scroll-freeze",
                    $"surface={surface}; gapMs={gapMilliseconds}; metricsError={ex.GetType().Name}");
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
