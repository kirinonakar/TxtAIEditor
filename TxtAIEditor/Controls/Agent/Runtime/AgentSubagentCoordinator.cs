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
        public const string WaitInterruptedResult =
            "Subagent wait interrupted by new user instructions. Running subagents continue. Consider the new instructions before waiting again.";

        public static IReadOnlyList<LlmTool> Tools { get; } = (LlmTool[])
        [
            CreateTool("spawn_subagent", "Start an independent task in the background and return its agent ID immediately.",
                new { title = Text("Short session title"), instruction = Text("Task, scope, and expected result") }, "title", "instruction"),
            CreateTool("send_subagent_message", "Queue a message for a running subagent or start a follow-up in its conversation.",
                new { agentId = Text("ID returned by spawn_subagent"), instruction = Text("Question or follow-up task") }, "agentId", "instruction"),
            CreateTool("cancel_subagent", "Request cancellation of one subagent.", new { agentId = Text("ID returned by spawn_subagent") }, "agentId"),
            CreateTool("subagent_status", "Get the status and latest progress of every subagent without waiting.", new { }),
            CreateTool(WaitTool, "Wait for one completion when work depends on it. Omit agentId to wait for the next completion.",
                new
                {
                    agentId = Text("Optional subagent ID"),
                    timeoutSeconds = new { type = "integer", minimum = 1, maximum = 86400, description = "Optional wait limit in seconds. The subagent keeps running on timeout." }
                }),
            CreateTool(ReportProgressTool, "Report a concise milestone, finding, or blocker.",
                new { message = Text("Progress update in the user's language") }, "message")
        ];

        public static LlmTool ProgressTool => Tools[^1];

        private readonly int _limit;
        private readonly Func<string, string, string, AgentFollowUpQueue, Func<string, Task>, CancellationToken, Task<AgentRunContext>> _run;
        private readonly Func<string, Task> _publishProgress;
        private readonly Func<string, string, string> _getString;
        private readonly CancellationTokenSource _cancellation;
        private readonly Dictionary<string, Worker> _workers = new(StringComparer.Ordinal);
        private readonly List<WorkerTurn> _turns = new();
        private readonly ConcurrentQueue<string> _progressLog = new();
        private readonly Queue<WorkerEvent> _events = new();
        private TaskCompletionSource _eventAvailable = NewSignal();

        public AgentSubagentCoordinator(
            int limit,
            Func<string, string, string, AgentFollowUpQueue, Func<string, Task>, CancellationToken, Task<AgentRunContext>> run,
            Func<string, Task> publishProgress,
            Func<string, string, string> getString,
            CancellationToken cancellationToken)
        {
            _limit = limit;
            _run = run;
            _publishProgress = publishProgress;
            _getString = getString;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        public bool HasUncollectedResults => _turns.Any(turn => !turn.Delivered);
        public static bool IsCoordinationTool(string name) => Tools.Any(tool => tool.Name == name);

        public string BuildInstructions(bool planningMode) => $$"""
            [Orchestrator]
            You own the user's task, integration, verification, and final answer. Delegate tasks that benefit from independent work.
            You may create up to {{_limit}} subagent sessions for this run. Launch independent assignments before waiting so they can run concurrently.
            Give each subagent the needed context, expected result, and a distinct scope. Coordinate changes to shared files and check their results.
            Subagent findings, failures, and completed results arrive as [Subagent events] in your conversation. Act on them and continue any work that is ready.
            Use send_subagent_message to steer a running subagent or assign a follow-up. Use cancel_subagent to stop one subagent and subagent_status to inspect current work.
            If no work is ready, return a brief answer and the runtime will wait asynchronously for the next event. Use wait_subagent only when you specifically need a completion, optionally with a timeout.
            Consider new user instructions if they interrupt a wait. Review the delegated results before finalizing or submitting a plan. {{(planningMode ? "Delegate investigation and plan review only." : "")}}
            For text tool calls, use the normal tool_call syntax with these names and JSON keys:
            spawn_subagent(title, instruction), send_subagent_message(agentId, instruction), cancel_subagent(agentId), subagent_status(), wait_subagent(optional agentId, optional timeoutSeconds), report_progress(message).
            """;

        public static string BuildWorkerInstructions(bool planningMode) => """
            [Subagent]
            Your counterpart is the orchestrator. Treat each [Orchestrator]: message as an assignment or follow-up from that orchestrator.
            Work within its assigned scope and use report_progress(message) for important findings, milestones, or blockers the orchestrator can act on.
            Return your answer to the orchestrator, including findings, changes, verified results, and remaining questions.
            Avoid unrelated changes and coordinate overlapping work through the orchestrator.
            """ + (planningMode ? "\nThis is plan research. Investigate and report findings without changing files, tabs, or external state." : "");

        public async Task<string> ExecuteAsync(string name, JsonElement arguments, Task followUpRequested)
        {
            _cancellation.Token.ThrowIfCancellationRequested();
            string id = GetStringArgument(arguments, "agentId").Trim();
            string instruction = GetStringArgument(arguments, "instruction").Trim();
            switch (name)
            {
                case "spawn_subagent":
                    string title = GetStringArgument(arguments, "title").Trim();
                    if (title.Length == 0 || instruction.Length == 0)
                    {
                        return "Tool failed: title and instruction are required.";
                    }
                    if (_workers.Count >= _limit)
                    {
                        return $"Tool failed: the {_limit}-subagent limit has been reached. Reuse an existing subagent for follow-up work.";
                    }

                    var worker = new Worker(Guid.NewGuid().ToString(), AgentRunTextFormatter.TruncateUserPrompt(title));
                    _workers.Add(worker.Id, worker);
                    Start(worker, instruction);
                    return $"Started subagent {worker.Id} ({worker.Title}).";

                case "send_subagent_message":
                    if (!_workers.TryGetValue(id, out Worker? recipient) || instruction.Length == 0)
                    {
                        return "Tool failed: a valid agentId and an instruction are required.";
                    }
                    if (!recipient.Turn!.Cancellation.IsCancellationRequested && recipient.Turn.Inbox.TryQueue(instruction))
                    {
                        return $"Queued a message for subagent {recipient.Id} ({recipient.Title}).";
                    }

                    Start(recipient, instruction);
                    return $"Scheduled a follow-up for subagent {recipient.Id} ({recipient.Title}).";

                case "cancel_subagent":
                    if (!_workers.TryGetValue(id, out Worker? target))
                    {
                        return "Tool failed: unknown subagent ID.";
                    }

                    WorkerTurn[] active = _turns.Where(turn => turn.Worker == target && !turn.Run.IsCompleted).ToArray();
                    foreach (WorkerTurn turn in active)
                    {
                        await CancelBestEffortAsync(turn.Cancellation);
                    }
                    return active.Length == 0 ? $"Subagent {id} has already finished." : $"Cancellation requested for subagent {id}.";

                case "subagent_status":
                    return _workers.Count == 0
                        ? "No subagents have been started."
                        : string.Join("\n", _workers.Values.Select(item =>
                        {
                            WorkerTurn turn = item.Turn!;
                            string status = turn.Run.IsCompletedSuccessfully
                                ? turn.Run.Result.Status.ToString()
                                : turn.Cancellation.IsCancellationRequested ? "Cancelling" : "Running";
                            return $"{item.Id} ({item.Title}): {status}. {turn.LatestProgress}";
                        }));

                case WaitTool:
                    if (id.Length > 0 && !_workers.ContainsKey(id))
                    {
                        return "Tool failed: unknown subagent ID.";
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
            Task signal;
            lock (_events)
            {
                if (_events.Any(item => !item.IsCompletion || !item.Turn.Delivered) || !HasUncollectedResults)
                {
                    return;
                }
                signal = _eventAvailable.Task;
            }

            if (!followUpRequested.IsCompleted)
            {
                PublishResource("AgentSubagentWaiting", "서브에이전트 결과를 기다리는 중");
                await Task.WhenAny(signal, followUpRequested).WaitAsync(_cancellation.Token);
            }
            if (followUpRequested.IsCompleted)
            {
                PublishResource("AgentSubagentWaitInterrupted", "추가 지시를 받아 서브에이전트 대기를 해제했습니다.");
            }
        }

        public string DrainModelEvents()
        {
            var messages = new List<string>();
            lock (_events)
            {
                while (_events.TryDequeue(out WorkerEvent? item))
                {
                    if (item.IsCompletion)
                    {
                        if (item.Turn.Delivered)
                        {
                            continue;
                        }
                        item.Turn.Delivered = true;
                    }
                    messages.Add(string.Join("\n", item.Text.ReplaceLineEndings("\n").Split('\n').Select(line => "  " + line)));
                }
                _eventAvailable = NewSignal();
            }

            return messages.Count == 0 ? string.Empty :
                "\n\n[Subagent events]\n" + string.Join("\n\n", messages);
        }

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
            return FormatResult(turn, result);
        }

        private void Start(Worker worker, string instruction)
        {
            WorkerTurn? previous = worker.Turn;
            var turn = new WorkerTurn(worker, CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token));
            worker.Turn = turn;
            _turns.Add(turn);
            turn.Run = RunWorkerAsync(turn, previous?.Run, instruction);
        }

        private async Task<WorkerResult> RunWorkerAsync(WorkerTurn turn, Task<WorkerResult>? previous, string instruction)
        {
            await Task.Yield();
            WorkerResult result;
            try
            {
                if (previous != null)
                {
                    await previous.WaitAsync(turn.Cancellation.Token);
                }
                turn.Cancellation.Token.ThrowIfCancellationRequested();
                PublishResource(previous == null ? "AgentSubagentStartedFormat" : "AgentSubagentFollowUpFormat",
                    previous == null ? "서브에이전트 작업 시작 · {0}" : "서브에이전트 후속 작업 시작 · {0}", turn.Worker.Title);

                AgentRunContext context = await _run(turn.Worker.Id, turn.Worker.Title, instruction, turn.Inbox, message =>
                {
                    turn.LatestProgress = message;
                    EnqueueEvent(new WorkerEvent(turn, $"{Describe(turn)} · Progress\n{message}", IsCompletion: false));
                    PublishResource("AgentSubagentProgressFormat", "{0} · {1}", turn.Worker.Title, message);
                    return Task.CompletedTask;
                }, turn.Cancellation.Token);

                string[] editedPaths = context.SessionEdits.Skip(context.InitialEditCount)
                    .Select(edit => edit.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                string changes = editedPaths.Length > 0 ? "\nChanged files: " + string.Join(", ", editedPaths) : string.Empty;
                string answer = context.Status == AgentRunStatus.Failed ? context.FailureReason : context.LastAnswerText;
                result = new WorkerResult(context.Status, answer + changes);
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
            EnqueueEvent(new WorkerEvent(turn, FormatResult(turn, result), IsCompletion: true));

            (string resource, string fallback) = result.Status switch
            {
                AgentRunStatus.Completed => ("AgentSubagentCompletedFormat", "서브에이전트 작업 완료 · {0}"),
                AgentRunStatus.Stopped => ("AgentSubagentStoppedFormat", "서브에이전트 작업 중단 · {0}"),
                _ => ("AgentSubagentFailedFormat", "서브에이전트 작업 실패 · {0}")
            };
            PublishResource(resource, fallback, turn.Worker.Title);
            return result;
        }

        private void EnqueueEvent(WorkerEvent item)
        {
            lock (_events)
            {
                _events.Enqueue(item);
                _eventAvailable.TrySetResult();
            }
        }

        private static string Describe(WorkerTurn turn) => $"{turn.Worker.Id} ({turn.Worker.Title})";
        private static string FormatResult(WorkerTurn turn, WorkerResult result) =>
            $"{Describe(turn)} · {result.Status}\n{result.Text}";

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

        public async Task StopAsync()
        {
            await CancelBestEffortAsync(_cancellation);
            await Task.WhenAll(_turns.Select(turn => turn.Run));
        }

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
        private sealed record WorkerEvent(WorkerTurn Turn, string Text, bool IsCompletion);

        private sealed class Worker(string id, string title)
        {
            public string Id { get; } = id;
            public string Title { get; } = title;
            public WorkerTurn? Turn { get; set; }
        }

        private sealed class WorkerTurn(Worker worker, CancellationTokenSource cancellation)
        {
            public Worker Worker { get; } = worker;
            public CancellationTokenSource Cancellation { get; } = cancellation;
            public AgentFollowUpQueue Inbox { get; } = new();
            public volatile string LatestProgress = string.Empty;
            public bool Delivered { get; set; }
            public Task<WorkerResult> Run { get; set; } = null!;
        }
    }
}
