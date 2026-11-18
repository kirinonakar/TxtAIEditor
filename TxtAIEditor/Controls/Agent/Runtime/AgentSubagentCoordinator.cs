using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Services.LLM;
using static TxtAIEditor.Controls.AgentToolHelpers;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentSubagentCoordinator : IDisposable
    {
        public const string ProgressMarker = "[Orchestrator progress] ";
        public const string ReportProgressTool = "report_progress";
        public const string WaitTool = "wait_subagent";
        private const int ResultPreviewLength = 1200;
        private const int ProgressPreviewLength = 600;
        private const int ResultPageLength = 1600;
        private const int MaxEventBatchLength = 4800;
        private const int MaxEventsPerBatch = 8;
        private const int MaxPendingReports = 32;
        private const int MaxPendingReportChars = 16000;
        private const int MaxArchivedReports = 512;
        private const int MaxArchivedReportChars = 256000;
        private static readonly TimeSpan ProgressBatchInterval = TimeSpan.FromMilliseconds(350);
        private readonly TimeSpan _stopTimeout;
        private Task? _shutdown;
        public const string WaitInterruptedResult =
            "Subagent wait interrupted by new user instructions. Running subagents continue. Consider the new instructions before waiting again.";

        public static IReadOnlyList<LlmTool> Tools { get; } = (LlmTool[])
        [
            CreateTool("spawn_subagent", "Start a background task and return its agent ID.",
                new { title = Text("Short session title"), instruction = Text("Task, scope, and expected result") }, "title", "instruction"),
            CreateTool("send_subagent_message", "Message a running subagent or continue an existing session with its conversation history.",
                new { agentId = Text("Subagent ID"), instruction = Text("Message or follow-up task") }, "agentId", "instruction"),
            CreateTool("cancel_subagent", "Request subagent cancellation.", new { agentId = Text("Subagent ID") }, "agentId"),
            CreateTool("subagent_status", "List subagent IDs and statuses, including sessions from earlier turns.", new { }),
            CreateTool(WaitTool, "Wait for a completion. Omit agentId for the next available result.",
                new
                {
                    agentId = Text("Subagent ID"),
                    timeoutSeconds = new { type = "integer", minimum = 1, maximum = 86400, description = "Timeout leaves the subagent running." }
                }),
            CreateTool("read_subagent_result", "Read a page of a result or report from this run.",
                new
                {
                    resultId = Text("Result or report ID"),
                    offset = new { type = "integer", minimum = 0, description = "Use nextOffset to continue." },
                    limit = new { type = "integer", minimum = 2, maximum = ResultPageLength }
                }, "resultId"),
            CreateTool("list_subagent_reports", "List archived finding and blocker IDs, including undelivered reports.",
                new { offset = new { type = "integer", minimum = 0 }, agentId = Text("Filter by subagent ID") }),
            CreateTool(ReportProgressTool, "Report progress in the user's language.",
                new
                {
                    message = Text("Brief update"),
                    kind = new { type = "string", @enum = new[] { "progress", "finding", "blocker" }, description = "Defaults to UI progress. Findings and blockers go to the orchestrator." }
                }, "message")
        ];

        public static LlmTool ProgressTool => Tools[^1];

        private readonly int _limit;
        private readonly Func<string, string, string, AgentFollowUpQueue, Func<string, string, Task<string>>, CancellationToken, Task<AgentRunContext>> _run;
        private readonly Func<string, Task> _publishProgress;
        private readonly Func<string, string, string> _getString;
        private readonly CancellationTokenSource _cancellation;
        private readonly Dictionary<string, Worker> _workers = new(StringComparer.Ordinal);
        private readonly List<WorkerTurn> _turns = new();
        private readonly bool _hasExistingWorkers;
        private int _assignedWorkerCount;
        private readonly ConcurrentQueue<string> _progressLog = new();
        private readonly object _eventGate = new();
        private readonly List<WorkerReport> _pendingReports = new();
        private readonly Dictionary<string, WorkerReport> _reports = new(StringComparer.Ordinal);
        private readonly HashSet<(string ResultId, string Kind, string Message)> _reportKeys = new();
        private int _pendingReportChars;
        private int _archivedReportChars;
        private int _overflowReportCount;
        private TaskCompletionSource _eventAvailable = NewSignal();
        private TaskCompletionSource _completionAvailable = NewSignal();

        public AgentSubagentCoordinator(
            int limit,
            Func<string, string, string, AgentFollowUpQueue, Func<string, string, Task<string>>, CancellationToken, Task<AgentRunContext>> run,
            Func<string, Task> publishProgress,
            Func<string, string, string> getString,
            CancellationToken cancellationToken,
            IEnumerable<(string Id, string Title)> existingWorkers,
            TimeSpan? stopTimeout = null)
        {
            _limit = limit;
            _run = run;
            _publishProgress = publishProgress;
            _getString = getString;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(10);
            foreach (var (id, title) in existingWorkers)
            {
                _workers.Add(id, new Worker(id, title, isExisting: true));
            }
            _hasExistingWorkers = _workers.Count > 0;
        }

        public bool HasUncollectedResults => _turns.Any(turn => !turn.Delivered);
        public bool NeedsModelReview
        {
            get
            {
                lock (_eventGate)
                {
                    return HasUncollectedResults || HasPendingReports;
                }
            }
        }
        public static bool IsCoordinationTool(string name) => Tools.Any(tool => tool.Name == name);

        public static bool IsWorkerToolAvailable(string toolName, bool isPlanResearch)
        {
            if (toolName is "make_plan" or "open_file" or "insert_text" or "create_tab" or "edit_tab" or "save_tab")
            {
                return false;
            }

            return !isPlanResearch || toolName is "list_files" or "search_text" or "run_rg" or "run_rga"
                or "read_file" or "read_image" or "web_search" or "web_fetch" or "web_fetch_exa" or "skill_use";
        }

        public string BuildInstructions(bool planningMode) => $$"""
            [Orchestrator]
            Own the task and verify delegated results before finalizing. Assign up to {{_limit}} distinct subagents per turn. Give them distinct scopes, relevant context, and clear deliverables. Coordinate shared edits and launch independent tasks before waiting.
            Act on [Subagent events] while continuing available work. When blocked on workers, yield a brief response to wait for an event or call wait_subagent for a completion. Address user updates that interrupt a wait.
            Text calls ([arg] is optional): spawn_subagent(title, instruction), send_subagent_message(agentId, instruction), cancel_subagent(agentId), subagent_status(), wait_subagent([agentId], [timeoutSeconds]), read_subagent_result(resultId, [offset], [limit]), list_subagent_reports([agentId], [offset]), report_progress(message).
            """ + (_hasExistingWorkers
                ? "\nSubagent sessions from earlier turns are available. Use subagent_status to find their IDs and send_subagent_message to continue related work with the same subagent."
                : "") + (planningMode ? "\nDelegate only investigation and plan review." : "");

        public static string BuildWorkerInstructions(bool planningMode) => """
            [Subagent]
            Follow [Orchestrator]: assignments and stay within scope. Coordinate shared edits through the orchestrator.
            Use report_progress(message, [kind]) for brief updates. Send actionable findings or blockers with kind=finding or kind=blocker.
            Return concise findings, changes, verification, and open issues to the orchestrator.
            """ + (planningMode ? "\nPlan research is read-only. Do not change files, tabs, or external state." : "");

        public async Task<string> ExecuteAsync(string name, JsonElement arguments, Task followUpRequested)
        {
            _cancellation.Token.ThrowIfCancellationRequested();
            string id = GetStringArgument(arguments, "agentId").Trim();
            string instruction = GetStringArgument(arguments, "instruction").Trim();
            string limitReached = $"Tool failed: the {_limit}-subagent limit for this turn has been reached. Reuse a subagent already assigned in this turn.";
            switch (name)
            {
                case "spawn_subagent":
                    string title = GetStringArgument(arguments, "title").Trim();
                    if (title.Length == 0 || instruction.Length == 0)
                    {
                        return "Tool failed: title and instruction are required.";
                    }
                    if (_assignedWorkerCount >= _limit)
                    {
                        return limitReached;
                    }

                    var worker = new Worker(Guid.NewGuid().ToString(), AgentRunTextFormatter.TruncateUserPrompt(title), isExisting: false);
                    _workers.Add(worker.Id, worker);
                    Start(worker, instruction);
                    return $"Started subagent {worker.Id} ({worker.Title}).";

                case "send_subagent_message":
                    if (!_workers.TryGetValue(id, out Worker? recipient) || instruction.Length == 0)
                    {
                        return "Tool failed: a valid agentId and an instruction are required.";
                    }
                    if (recipient.Turn is { } current && !current.Cancellation.IsCancellationRequested && current.Inbox.TryQueue(instruction))
                    {
                        return $"Queued a message for subagent {recipient.Id} ({recipient.Title}).";
                    }
                    if (recipient.Turn == null && _assignedWorkerCount >= _limit)
                    {
                        return limitReached;
                    }

                    WorkerTurn followUp = Start(recipient, instruction);
                    return followUp.WaitingForPreviousTurn
                        ? $"Subagent {recipient.Id} ({recipient.Title}) is WaitingForPreviousTurn. The follow-up will fail if the previous turn does not exit in {_stopTimeout.TotalSeconds:g} seconds."
                        : $"Scheduled a follow-up for subagent {recipient.Id} ({recipient.Title}).";

                case "cancel_subagent":
                    if (!_workers.TryGetValue(id, out Worker? target))
                    {
                        return "Tool failed: unknown subagent ID.";
                    }

                    WorkerTurn[] active = _turns.Where(turn => turn.Worker == target && !turn.Run.IsCompleted).ToArray();
                    foreach (WorkerTurn turn in active)
                    {
                        RequestCancellation(turn);
                    }
                    return active.Length == 0 ? $"Subagent {id} has already finished." : $"Cancellation requested for subagent {id}.";

                case "subagent_status":
                    return _workers.Count == 0
                        ? "No subagents have been started."
                        : string.Join("\n", _workers.Values.Select(item =>
                        {
                            if (item.Turn is not { } turn)
                            {
                                return $"{item.Id} ({item.Title}): Idle. Available for follow-up.";
                            }
                            WorkerResult? result = turn.Result;
                            string status = result?.Status.ToString() ??
                                (turn.Cancellation.IsCancellationRequested ? "Cancelling" :
                                    turn.WaitingForPreviousTurn ? "WaitingForPreviousTurn" : "Running");
                            if (result != null && _turns.Any(previous => previous.Worker == item && previous != turn && !previous.Run.IsCompleted))
                            {
                                status += " (previous turn still exiting)";
                            }
                            string resultReference = result != null ? $" Result ID: {turn.ResultId}." : string.Empty;
                            return $"{item.Id} ({item.Title}): {status}.{resultReference}";
                        }));

                case WaitTool:
                    if (id.Length > 0)
                    {
                        if (!_workers.TryGetValue(id, out Worker? awaitedWorker))
                        {
                            return "Tool failed: unknown subagent ID.";
                        }
                        if (awaitedWorker.Turn == null)
                        {
                            return "This subagent has no assignment in the current turn.";
                        }
                    }
                    TimeSpan timeout = Timeout.InfiniteTimeSpan;
                    if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("timeoutSeconds", out _))
                    {
                        if (!TryGetIntArgument(arguments, "timeoutSeconds", out int seconds) || seconds is < 1 or > 86400)
                        {
                            return "Tool failed: timeoutSeconds must be an integer from 1 to 86400.";
                        }
                        timeout = TimeSpan.FromSeconds(seconds);
                    }
                    return await CollectAsync(id, timeout, followUpRequested);

                case "read_subagent_result":
                    return ReadResult(arguments);

                case "list_subagent_reports":
                    return ListReports(arguments);

                case ReportProgressTool:
                    string message = GetStringArgument(arguments, "message").Trim();
                    if (message.Length == 0)
                    {
                        return "Tool failed: message is required.";
                    }
                    Publish(message);
                    return "Progress recorded.";

                default:
                    return $"Unknown tool: {name}";
            }
        }

        public async Task WaitForEventAsync(Task followUpRequested)
        {
            Task? signal = null;
            lock (_eventGate)
            {
                if (!HasPendingReports && !HasReadyCompletion)
                {
                    if (!HasUncollectedResults)
                    {
                        return;
                    }
                    if (_eventAvailable.Task.IsCompleted)
                    {
                        _eventAvailable = NewSignal();
                    }
                    signal = _eventAvailable.Task;
                }
            }

            if (signal != null && !followUpRequested.IsCompleted)
            {
                PublishResource("AgentSubagentWaiting", "서브에이전트 결과를 기다리는 중");
                await Task.WhenAny(signal, followUpRequested).WaitAsync(_cancellation.Token);
            }

            if (!followUpRequested.IsCompleted)
            {
                Task? completion = null;
                lock (_eventGate)
                {
                    if (!HasReadyCompletion)
                    {
                        if (_completionAvailable.Task.IsCompleted)
                        {
                            _completionAvailable = NewSignal();
                        }
                        completion = _completionAvailable.Task;
                    }
                }
                if (completion != null)
                {
                    await Task.WhenAny(completion, followUpRequested, Task.Delay(ProgressBatchInterval, _cancellation.Token))
                        .WaitAsync(_cancellation.Token);
                }
            }

            if (followUpRequested.IsCompleted)
            {
                PublishResource("AgentSubagentWaitInterrupted", "추가 지시를 받아 서브에이전트 대기를 해제했습니다.");
            }
        }

        public string DrainModelEvents()
        {
            const string header = "\n\n[Subagent events]\n";
            int length = header.Length;
            var messages = new List<string>();
            bool Add(string text)
            {
                string indented = "  " + text.ReplaceLineEndings("\n").Replace("\n", "\n  ", StringComparison.Ordinal);
                int nextLength = length + indented.Length + (messages.Count == 0 ? 0 : 2);
                if (nextLength > MaxEventBatchLength || messages.Count >= MaxEventsPerBatch)
                {
                    return false;
                }
                messages.Add(indented);
                length = nextLength;
                return true;
            }

            lock (_eventGate)
            {
                foreach (WorkerTurn turn in _turns.Where(turn => !turn.Delivered && turn.Result != null)
                    .OrderByDescending(turn => turn.Result!.Status == AgentRunStatus.Failed))
                {
                    if (!Add(FormatResultPreview(turn, turn.Result!))) break;
                    turn.Delivered = true;
                }

                foreach (WorkerReport report in _pendingReports.OrderByDescending(report => report.Kind == "blocker").ToArray())
                {
                    if (!Add(FormatReportPreview(report))) break;
                    _pendingReports.Remove(report);
                    _pendingReportChars -= report.Message.Length;
                }

                if (_overflowReportCount > 0 &&
                    Add($"{_overflowReportCount} additional reports were archived. Use list_subagent_reports to find them."))
                {
                    _overflowReportCount = 0;
                }
            }

            return messages.Count == 0 ? string.Empty : header + string.Join("\n\n", messages);
        }

        private bool HasPendingReports => _pendingReports.Count > 0 || _overflowReportCount > 0;
        private bool HasReadyCompletion => _turns.Any(turn => !turn.Delivered && turn.Result != null);

        private async Task<string> CollectAsync(string id, TimeSpan timeout, Task followUpRequested)
        {
            WorkerTurn[] pending = _turns.Where(turn => !turn.Delivered && (id.Length == 0 || turn.Worker.Id == id)).ToArray();
            if (pending.Length == 0)
            {
                return id.Length == 0
                    ? "No outstanding subagent results."
                    : "The latest result has already been delivered in the conversation.";
            }

            if (!followUpRequested.IsCompleted && pending.All(turn => !turn.Run.IsCompleted))
            {
                PublishResource("AgentSubagentWaiting", "서브에이전트 결과를 기다리는 중");
            }

            Task completed;
            try
            {
                completed = await Task.WhenAny(pending.Select(turn => (Task)turn.Run).Append(followUpRequested))
                    .WaitAsync(timeout, _cancellation.Token);
            }
            catch (TimeoutException)
            {
                return "Subagent wait timed out. Running subagents continue and their results will be delivered automatically.";
            }

            _cancellation.Token.ThrowIfCancellationRequested();
            if (followUpRequested.IsCompleted)
            {
                PublishResource("AgentSubagentWaitInterrupted", "추가 지시를 받아 서브에이전트 대기를 해제했습니다.");
                return WaitInterruptedResult;
            }

            WorkerTurn turn = pending.First(item => item.Run == completed);
            WorkerResult result = await turn.Run;
            turn.Delivered = true;
            return FormatResultPreview(turn, result);
        }

        private WorkerTurn Start(Worker worker, string instruction)
        {
            bool followUp = worker.IsExisting || worker.Turn != null;
            if (worker.Turn == null)
            {
                _assignedWorkerCount++;
            }
            Task[] pending = _turns.Where(turn => turn.Worker == worker && !turn.Run.IsCompleted).Select(turn => (Task)turn.Run).ToArray();
            var turn = new WorkerTurn(worker, CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token))
            {
                WaitingForPreviousTurn = pending.Length > 0
            };
            worker.Turn = turn;
            _turns.Add(turn);
            turn.Run = RunWorkerAsync(turn, pending, followUp, instruction);
            return turn;
        }

        private async Task<WorkerResult> RunWorkerAsync(WorkerTurn turn, Task[] pending, bool followUp, string instruction)
        {
            await Task.Yield();
            WorkerResult result;
            try
            {
                if (pending.Length > 0)
                {
                    try
                    {
                        await Task.WhenAll(pending).WaitAsync(_stopTimeout, turn.Cancellation.Token);
                    }
                    catch (TimeoutException ex)
                    {
                        throw new InvalidOperationException(
                            "The follow-up did not start because a previous turn is still exiting. Use a new subagent session for independent work.", ex);
                    }
                }
                turn.WaitingForPreviousTurn = false;
                turn.Cancellation.Token.ThrowIfCancellationRequested();
                PublishResource(followUp ? "AgentSubagentFollowUpFormat" : "AgentSubagentStartedFormat",
                    followUp ? "서브에이전트 후속 작업 시작 · {0}" : "서브에이전트 작업 시작 · {0}", turn.Worker.Title);

                AgentRunContext context = await _run(turn.Worker.Id, turn.Worker.Title, instruction, turn.Inbox, (message, kind) =>
                {
                    turn.Cancellation.Token.ThrowIfCancellationRequested();
                    string outcome = kind is "finding" or "blocker"
                        ? ArchiveReport(turn, kind, message)
                        : "Progress recorded in the UI.";
                    PublishResource("AgentSubagentProgressFormat", "{0} · {1}", turn.Worker.Title, TakeText(message, 0, 4000));
                    return Task.FromResult(outcome);
                }, turn.Cancellation.Token);

                string[] editedPaths = context.SessionEdits.Skip(context.InitialEditCount)
                    .Select(edit => edit.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                string changes = editedPaths.Length > 0 ? "Changed files: " + string.Join(", ", editedPaths) + "\n\n" : string.Empty;
                string answer = context.Status == AgentRunStatus.Failed ? context.FailureReason : context.LastAnswerText;
                AgentRunStatus statusAfterRun = turn.Cancellation.IsCancellationRequested ? AgentRunStatus.Stopped : context.Status;
                result = new WorkerResult(statusAfterRun, changes + answer);
            }
            catch (OperationCanceledException) when (turn.Cancellation.IsCancellationRequested)
            {
                result = new WorkerResult(AgentRunStatus.Stopped, string.Empty);
            }
            catch (Exception ex)
            {
                result = new WorkerResult(AgentRunStatus.Failed, ex.Message);
            }

            if (turn.Inbox.Close() is { } undelivered)
            {
                result = result with { Text = result.Text + "\nFollow-up messages pending when this assignment ended:\n" + undelivered };
            }
            lock (_eventGate)
            {
                turn.Result = result;
                _eventAvailable.TrySetResult();
                _completionAvailable.TrySetResult();
            }

            (string resource, string fallback) = result.Status switch
            {
                AgentRunStatus.Completed => ("AgentSubagentCompletedFormat", "서브에이전트 작업 완료 · {0}"),
                AgentRunStatus.Stopped => ("AgentSubagentStoppedFormat", "서브에이전트 작업 중단 · {0}"),
                _ => ("AgentSubagentFailedFormat", "서브에이전트 작업 실패 · {0}")
            };
            PublishResource(resource, fallback, turn.Worker.Title);
            return result;
        }

        private string ArchiveReport(WorkerTurn turn, string kind, string message)
        {
            lock (_eventGate)
            {
                var key = (turn.ResultId, kind, message);
                if (_reportKeys.Contains(key))
                {
                    return "Duplicate report already recorded.";
                }
                if (message.Length > 4000 || _reports.Count >= MaxArchivedReports ||
                    message.Length > MaxArchivedReportChars - _archivedReportChars)
                {
                    return "Tool failed: the report exceeds the archive limit. Include the finding in your final answer.";
                }

                var report = new WorkerReport(Guid.NewGuid().ToString("N"), turn, kind, message);
                _reports.Add(report.Id, report);
                _reportKeys.Add(key);
                _archivedReportChars += message.Length;
                bool CanQueue() => _pendingReports.Count < MaxPendingReports &&
                    message.Length <= MaxPendingReportChars - _pendingReportChars;
                if (kind == "blocker")
                {
                    while (!CanQueue() && _pendingReports.FirstOrDefault(item => item.Kind == "finding") is { } finding)
                    {
                        _pendingReports.Remove(finding);
                        _pendingReportChars -= finding.Message.Length;
                        _overflowReportCount++;
                    }
                }
                bool queued = CanQueue();
                if (queued)
                {
                    _pendingReports.Add(report);
                    _pendingReportChars += message.Length;
                }
                else
                {
                    _overflowReportCount++;
                }
                _eventAvailable.TrySetResult();
                return queued ? "Update queued for the orchestrator." : "Update archived for the orchestrator.";
            }
        }

        private static string FormatReportPreview(WorkerReport report) =>
            $"{Describe(report.Turn)} · {report.Kind} (resultId {report.Id})\n{TakeText(report.Message, 0, ProgressPreviewLength)}";

        private string ListReports(JsonElement arguments)
        {
            string agentId = GetStringArgument(arguments, "agentId").Trim();
            lock (_eventGate)
            {
                WorkerReport[] reports = _reports.Values.Where(report => agentId.Length == 0 || report.Turn.Worker.Id == agentId).ToArray();
                if (!ReadPageInteger(arguments, "offset", 0, 0, reports.Length, out int offset))
                {
                    return "Tool failed: invalid report offset.";
                }
                WorkerReport[] page = reports.Skip(offset).Take(MaxEventsPerBatch).ToArray();
                int end = offset + page.Length;
                string next = end < reports.Length ? $"\nnextOffset={end}" : "\nend of reports";
                return string.Join("\n", page.Select(report => $"{report.Id} · {Describe(report.Turn)} · {report.Kind}: " +
                    FormatProgressLine(TakeText(report.Message, 0, 120)))) + next;
            }
        }

        private static string Describe(WorkerTurn turn) => $"{turn.Worker.Id} ({turn.Worker.Title})";
        private static string FormatResultPreview(WorkerTurn turn, WorkerResult result)
        {
            string preview = TakeText(result.Text, 0, ResultPreviewLength);
            string reference = preview.Length < result.Text.Length
                ? $"\nPreview ends at offset {preview.Length}. Use read_subagent_result with resultId {turn.ResultId} for more."
                : string.Empty;
            return $"{Describe(turn)} · {result.Status}\n{preview}{reference}";
        }

        private string ReadResult(JsonElement arguments)
        {
            string id = GetStringArgument(arguments, "resultId").Trim();
            string? text;
            lock (_eventGate)
            {
                text = _reports.GetValueOrDefault(id)?.Message ?? _turns.FirstOrDefault(turn => turn.ResultId == id)?.Result?.Text;
            }
            if (text == null)
            {
                return "Tool failed: unknown subagent result or report ID.";
            }
            if (!ReadPageInteger(arguments, "offset", 0, 0, text.Length, out int offset) ||
                !ReadPageInteger(arguments, "limit", ResultPageLength, 2, ResultPageLength, out int limit))
            {
                return $"Tool failed: offset must be between 0 and {text.Length}; limit must be between 2 and {ResultPageLength}.";
            }
            if (offset > 0 && offset < text.Length && char.IsLowSurrogate(text[offset]) &&
                char.IsHighSurrogate(text[offset - 1]))
            {
                offset--;
            }

            string page = TakeText(text, offset, limit);
            int end = offset + page.Length;
            string next = end < text.Length ? $"nextOffset={end}" : "end of result";
            return $"Subagent result {id}, offset={offset}, {next}\n" + page;
        }

        private static bool ReadPageInteger(JsonElement arguments, string name, int fallback, int min, int max, out int value)
        {
            value = fallback;
            return !arguments.TryGetProperty(name, out _) ||
                (TryGetIntArgument(arguments, name, out value) && value >= min && value <= max);
        }

        private static string TakeText(string text, int offset, int limit)
        {
            int length = Math.Min(limit, text.Length - offset);
            if (length > 0 && offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1]))
            {
                length--;
            }
            return text.Substring(offset, length);
        }

        private void PublishResource(string resource, string fallback, params object[] arguments)
        {
            try
            {
                Publish(string.Format(_getString(resource, fallback), arguments));
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        private void Publish(string message)
        {
            string line = FormatProgressLine(message);
            _progressLog.Enqueue(ProgressMarker + line);
            _ = PublishBestEffortAsync(line);
        }

        private async Task PublishBestEffortAsync(string line)
        {
            try
            {
                await _publishProgress(line);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        public static string FormatProgressLine(string message) =>
            string.Join(" ", message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

        public string DrainProgressLog()
        {
            var messages = new List<string>();
            while (_progressLog.TryDequeue(out string? message))
            {
                messages.Add(message);
            }
            return messages.Count == 0 ? string.Empty :
                Environment.NewLine + string.Join(Environment.NewLine, messages) + Environment.NewLine;
        }

        public async Task<PendingShutdown[]> StopAsync()
        {
            if (_shutdown == null)
            {
                foreach (WorkerTurn turn in _turns.Where(turn => !turn.Run.IsCompleted))
                {
                    RequestCancellation(turn);
                }
                _shutdown = Task.WhenAll(_turns.SelectMany(turn => new Task[] { turn.Run, turn.CancelTask ?? Task.CompletedTask })
                    .Append(CancelBestEffortAsync(_cancellation)));
            }
            try
            {
                await _shutdown.WaitAsync(_stopTimeout);
                return [];
            }
            catch (TimeoutException)
            {
                var delayed = _turns.Where(turn => !turn.Run.IsCompleted || turn.CancelTask is { IsCompleted: false })
                    .GroupBy(turn => turn.Worker).ToArray();
                var pending = new List<PendingShutdown>(delayed.Length);
                foreach (var worker in delayed)
                {
                    Task completion = Task.WhenAll(worker.SelectMany(turn => new Task[] { turn.Run, turn.CancelTask ?? Task.CompletedTask }));
                    pending.Add(new PendingShutdown(worker.Key.Id, completion));
                    PublishResource("AgentSubagentShutdownDelayedFormat", "서브에이전트 종료 지연 · {0}", worker.Key.Title);
                }
                return pending.ToArray();
            }
        }

        private static void RequestCancellation(WorkerTurn turn) =>
            turn.CancelTask ??= CancelBestEffortAsync(turn.Cancellation);

        private static async Task CancelBestEffortAsync(CancellationTokenSource cancellation)
        {
            try
            {
                await cancellation.CancelAsync();
            }
            catch (AggregateException ex)
            {
                Debug.WriteLine(ex);
            }
        }

        public void Dispose()
        {
            if (_shutdown is { IsCompleted: false })
            {
                _ = DisposeAfterShutdownAsync(_shutdown);
                return;
            }
            DisposeCancellation();
        }

        private async Task DisposeAfterShutdownAsync(Task shutdown)
        {
            try { await shutdown; }
            catch (Exception ex) { Debug.WriteLine(ex); }
            finally { DisposeCancellation(); }
        }

        private void DisposeCancellation()
        {
            foreach (WorkerTurn turn in _turns)
            {
                turn.Cancellation.Dispose();
            }
            _cancellation.Dispose();
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static object Text(string description) => new { type = "string", description };
        private static LlmTool CreateTool(string name, string description, object properties, params string[] required) =>
            new() { Name = name, Description = description, Parameters = new { type = "object", properties, required } };

        private sealed record WorkerResult(AgentRunStatus Status, string Text);
        private sealed record WorkerReport(string Id, WorkerTurn Turn, string Kind, string Message);
        public sealed record PendingShutdown(string AgentId, Task Completion);

        private sealed class Worker(string id, string title, bool isExisting)
        {
            public string Id { get; } = id;
            public string Title { get; } = title;
            public bool IsExisting { get; } = isExisting;
            public WorkerTurn? Turn { get; set; }
        }

        private sealed class WorkerTurn(Worker worker, CancellationTokenSource cancellation)
        {
            public Worker Worker { get; } = worker;
            public CancellationTokenSource Cancellation { get; } = cancellation;
            public AgentFollowUpQueue Inbox { get; } = new();
            public string ResultId { get; } = Guid.NewGuid().ToString("N");
            public volatile WorkerResult? Result;
            public volatile bool WaitingForPreviousTurn;
            public Task? CancelTask { get; set; }
            public bool Delivered { get; set; }
            public Task<WorkerResult> Run { get; set; } = null!;
        }
    }
}
